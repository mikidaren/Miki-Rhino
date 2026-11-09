"""Validated metre-based IFC4 export and independent OpenCASCADE readback.

The public entry points are validate_project, block_specs, export_project and
inspect_project. CLI output is a JSON report, with no source or IFC payload.
"""

import argparse
from contextlib import contextmanager
import copy
import errno
from importlib.metadata import version
import json
import math
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import uuid


IFCOPENSHELL_VERSION = "0.9.0.post1"
TOLERANCE = 1e-6
MAX_ENTRIES = 10000
MAX_GENERATED_PARTS = 5000
COLLECTION_FIELDS = {
    "levels": ({"id", "elevation", "height"}, set()),
    "grids": ({"id", "start", "end"}, set()),
    "spaces": ({"id", "level", "name", "boundary"}, set()),
    "walls": ({"id", "level", "start", "end", "thickness"}, {"height"}),
    "slabs": ({"id", "level", "boundary", "thickness"}, {"offset", "voids"}),
    "types": ({"id", "kind", "form", "width", "height"}, {"depth"}),
    "openings": ({"id", "wall", "type", "offset", "sill"}, set()),
    "stairs": ({"id", "from", "to", "origin", "direction", "width", "run", "risers"}, set()),
}


class WorkerError(Exception):
    def __init__(self, code, message, report=None):
        super().__init__(message)
        self.code = code
        self.report = report


def require_runtime():
    try:
        import ifcopenshell
        import ifcopenshell.geom
        import ifcopenshell.validate
        import shapely.geometry
        installed = version("ifcopenshell")
    except (ImportError, OSError) as error:
        raise WorkerError(
            "runtime_missing",
            "Use an explicitly prepared Python 3.12 environment. Run that Python with "
            "-m pip install -r python/requirements.txt, then invoke this worker with it.",
        ) from error
    if installed != IFCOPENSHELL_VERSION:
        raise WorkerError(
            "runtime_version",
            f"Expected ifcopenshell=={IFCOPENSHELL_VERSION}, found {installed}. "
            "Install the pinned python/requirements.txt in the selected environment.",
        )
    return ifcopenshell


def invalid(message):
    raise WorkerError("invalid_project", message)


def fields(value, required, optional, label):
    if not isinstance(value, dict):
        invalid(f"{label} must be an object")
    missing = required - value.keys()
    unknown = value.keys() - required - optional
    if missing or unknown:
        invalid(f"{label} has missing fields {sorted(missing)} or unknown fields {sorted(unknown)}")


def number(value, label, positive=False):
    if type(value) not in (int, float) or abs(value) > 1e6 or not math.isfinite(value):
        invalid(f"{label} must be a finite number within +/-1000000 metres")
    if positive and value <= 0:
        invalid(f"{label} must be positive")
    return float(value)


def point(value, label):
    if not isinstance(value, list) or len(value) != 2:
        invalid(f"{label} must have two coordinates")
    return [number(coordinate, label) for coordinate in value]


def ring_points(value):
    points = list(value)
    if len(points) > 1 and points[0] == points[-1]:
        points.pop()
    return points


def polygon(value, label):
    from shapely.geometry import Polygon
    if not isinstance(value, list) or not 3 <= len(value) <= 2048:
        invalid(f"{label} needs 3 to 2048 polygon vertices")
    coordinates = [point(vertex, label) for vertex in value]
    coordinates = ring_points(coordinates)
    if len(coordinates) < 3 or len({tuple(vertex) for vertex in coordinates}) != len(coordinates):
        invalid(f"{label} has repeated or missing polygon vertices")
    result = Polygon(coordinates)
    if not result.is_valid or result.area <= 0:
        invalid(f"{label} must be a simple polygon with positive area")
    return result


def duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            invalid(f"Duplicate JSON property {key}")
        result[key] = value
    return result


def load_project(source):
    if isinstance(source, dict):
        return copy.deepcopy(source)
    try:
        path = Path(source)
        if path.stat().st_size > 10 * 1024 * 1024:
            invalid("Project JSON exceeds the 10 MiB input limit")
        return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=duplicate_keys)
    except (OSError, ValueError, TypeError, RecursionError) as error:
        raise WorkerError("invalid_project", "Could not read a valid project JSON file") from error


def index_by_id(project, collection):
    return {entry["id"]: entry for entry in project[collection]}


def wall_frame(wall):
    delta = [wall["end"][axis] - wall["start"][axis] for axis in range(2)]
    length = math.hypot(*delta)
    return length, [delta[0] / length, delta[1] / length]


def transform_point(origin, direction, along, normal):
    return [origin[0] + along * direction[0] - normal * direction[1],
            origin[1] + along * direction[1] + normal * direction[0]]


def rectangle_points(origin, direction, start, end, low, high):
    return [transform_point(origin, direction, along, normal)
            for along, normal in ((start, low), (end, low), (end, high), (start, high))]


def wall_section(project, wall):
    from shapely.geometry import box
    levels = index_by_id(project, "levels")
    types = index_by_id(project, "types")
    length, direction = wall_frame(wall)
    height = wall.get("height", levels[wall["level"]]["height"])
    section = box(0, 0, length, height)
    for opening in project["openings"]:
        if opening["wall"] == wall["id"]:
            definition = types[opening["type"]]
            section = section.difference(box(opening["offset"], opening["sill"],
                                             opening["offset"] + definition["width"],
                                             opening["sill"] + definition["height"]))
    return section


def validate_project(source):
    require_runtime()
    project = load_project(source)
    required = {"schemaVersion", "id", "name", "revision", "units", *COLLECTION_FIELDS}
    fields(project, required, set(), "project")
    if type(project["schemaVersion"]) is not int or project["schemaVersion"] != 1 or project["units"] != "m":
        invalid("Only schemaVersion 1 and metre units 'm' are supported")
    if type(project["revision"]) is not int or not 0 <= project["revision"] <= 2147483647:
        invalid("revision must be a nonnegative 32-bit integer")
    if not isinstance(project["name"], str) or not 1 <= len(project["name"]) <= 1024:
        invalid("project.name must be a nonempty string of at most 1024 characters")
    identifiers = set()
    for label, identifier in [("project", project["id"])]:
        if not isinstance(identifier, str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", identifier):
            invalid(f"{label}.id must be a stable printable ASCII identifier")
        identifiers.add(identifier)
    total = 0
    for collection, (entry_required, optional) in COLLECTION_FIELDS.items():
        entries = project[collection]
        if not isinstance(entries, list) or len(entries) > MAX_ENTRIES:
            invalid(f"{collection} must be a bounded array")
        total += len(entries)
        for entry in entries:
            fields(entry, entry_required, optional, collection)
            identifier = entry["id"]
            if not isinstance(identifier, str) or not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", identifier):
                invalid(f"{collection}.id must be a stable printable ASCII identifier")
            if identifier in identifiers:
                invalid(f"Duplicate semantic ID {identifier}")
            identifiers.add(identifier)
    if total > MAX_ENTRIES or not project["levels"]:
        invalid("A project needs at least one level and at most 10000 entries")
    for stair in project["stairs"]:
        if type(stair["risers"]) is not int or not 2 <= stair["risers"] <= 1000:
            invalid(f"{stair['id']}.risers must be an integer from 2 to 1000")
    generated_parts = (
        len(project["grids"]) + len(project["spaces"])
        + 2 * len(project["walls"]) + 2 * len(project["slabs"])
        + 2 * len(project["openings"]) + 5 * len(project["types"])
        + sum(stair["risers"] + 1 for stair in project["stairs"]) + 1
    )
    if generated_parts > MAX_GENERATED_PARTS:
        invalid(f"Project estimates {generated_parts} generated parts. This pilot is limited to {MAX_GENERATED_PARTS}.")
    levels = index_by_id(project, "levels")
    walls = index_by_id(project, "walls")
    types = index_by_id(project, "types")
    for level in project["levels"]:
        number(level["elevation"], f"{level['id']}.elevation")
        number(level["height"], f"{level['id']}.height", positive=True)
    for collection in ("spaces", "walls", "slabs"):
        for entry in project[collection]:
            if not isinstance(entry["level"], str) or entry["level"] not in levels:
                invalid(f"{entry['id']} references an unknown level")
    for grid in project["grids"]:
        start, end = point(grid["start"], "grid.start"), point(grid["end"], "grid.end")
        if math.dist(start, end) <= 0:
            invalid(f"{grid['id']} has a zero-length grid axis")
    for space in project["spaces"]:
        if not isinstance(space["name"], str) or not 1 <= len(space["name"]) <= 1024:
            invalid(f"{space['id']}.name must be a nonempty string")
        polygon(space["boundary"], f"{space['id']}.boundary")
    for wall in project["walls"]:
        start, end = point(wall["start"], "wall.start"), point(wall["end"], "wall.end")
        if math.dist(start, end) <= 0:
            invalid(f"{wall['id']} has a zero-length wall baseline")
        number(wall["thickness"], f"{wall['id']}.thickness", positive=True)
        if "height" in wall:
            number(wall["height"], f"{wall['id']}.height", positive=True)
    for slab in project["slabs"]:
        number(slab["thickness"], f"{slab['id']}.thickness", positive=True)
        slab.setdefault("offset", 0)
        slab.setdefault("voids", [])
        number(slab["offset"], f"{slab['id']}.offset")
        outer = polygon(slab["boundary"], f"{slab['id']}.boundary")
        if not isinstance(slab["voids"], list) or len(slab["voids"]) > 256:
            invalid(f"{slab['id']}.voids must be a bounded array")
        holes = []
        for boundary in slab["voids"]:
            hole = polygon(boundary, f"{slab['id']}.voids")
            if not outer.contains(hole) or outer.boundary.intersects(hole) or any(hole.intersects(other) for other in holes):
                invalid(f"{slab['id']} has overlapping voids or a void outside its interior")
            holes.append(hole)
    for definition in project["types"]:
        definition.setdefault("depth", 0.12)
        if definition["kind"] not in ("window", "door") or definition["form"] != "rectangular":
            invalid(f"{definition['id']} must be a rectangular window or door type")
        for key in ("width", "height", "depth"):
            number(definition[key], f"{definition['id']}.{key}", positive=True)
    opening_rectangles = {}
    for opening in project["openings"]:
        if not isinstance(opening["wall"], str) or opening["wall"] not in walls:
            invalid(f"{opening['id']} references an unknown wall")
        if not isinstance(opening["type"], str) or opening["type"] not in types:
            invalid(f"{opening['id']} references an unknown type")
        offset = number(opening["offset"], f"{opening['id']}.offset")
        sill = number(opening["sill"], f"{opening['id']}.sill")
        wall, definition = walls[opening["wall"]], types[opening["type"]]
        length, direction = wall_frame(wall)
        height = wall.get("height", levels[wall["level"]]["height"])
        if offset < 0 or sill < 0 or offset + definition["width"] > length + 1e-9 or sill + definition["height"] > height + 1e-9:
            invalid(f"{opening['id']} does not fit inside its host wall")
        bounds = (offset, sill, offset + definition["width"], sill + definition["height"])
        previous = opening_rectangles.setdefault(wall["id"], [])
        for other in previous:
            if min(bounds[2], other[2]) - max(bounds[0], other[0]) > 1e-9 and min(bounds[3], other[3]) - max(bounds[1], other[1]) > 1e-9:
                invalid(f"{opening['id']} overlaps another host opening")
        previous.append(bounds)
    for wall in project["walls"]:
        if wall_section(project, wall).area <= 0:
            invalid(f"{wall['id']} has no solid remaining after openings")
    from shapely.geometry import Polygon
    for stair in project["stairs"]:
        if not isinstance(stair["from"], str) or not isinstance(stair["to"], str) or stair["from"] not in levels or stair["to"] not in levels:
            invalid(f"{stair['id']} references an unknown stair level")
        start_z, end_z = levels[stair["from"]]["elevation"], levels[stair["to"]]["elevation"]
        if end_z <= start_z:
            invalid(f"{stair['id']} must connect to a higher level")
        origin = point(stair["origin"], f"{stair['id']}.origin")
        direction = point(stair["direction"], f"{stair['id']}.direction")
        magnitude = math.hypot(*direction)
        if magnitude <= 0:
            invalid(f"{stair['id']} has a zero stair direction")
        direction = [coordinate / magnitude for coordinate in direction]
        number(stair["width"], f"{stair['id']}.width", positive=True)
        number(stair["run"], f"{stair['id']}.run", positive=True)
        rise = end_z - start_z
        if not 1e-8 < rise / stair["risers"] <= 1 or stair["run"] / stair["risers"] <= 1e-8:
            invalid(f"{stair['id']} needs positive treads and risers, with riser height at most one metre")
        flight = Polygon(rectangle_points(origin, direction, 0, stair["run"], 0, stair["width"]))
        lower_landing = Polygon(rectangle_points(origin, direction, -stair["width"], 0, 0, stair["width"]))
        upper_landing = Polygon(rectangle_points(origin, direction, stair["run"], stair["run"] + stair["width"], 0, stair["width"]))
        for footprint in (flight, lower_landing, upper_landing):
            for vertex in footprint.exterior.coords:
                point(list(vertex), f"{stair['id']}.footprint")
        lower_supported, upper_supported = False, False
        for slab in project["slabs"]:
            material = Polygon(ring_points(slab["boundary"]), [ring_points(boundary) for boundary in slab["voids"]])
            if abs(slab["offset"]) <= 1e-8:
                if slab["level"] == stair["from"] and material.buffer(1e-8).covers(lower_landing):
                    lower_supported = True
                if slab["level"] == stair["to"] and material.buffer(1e-8).covers(upper_landing):
                    upper_supported = upper_supported or any(Polygon(boundary).buffer(1e-8).covers(flight) for boundary in slab["voids"])
            top = levels[slab["level"]]["elevation"] + slab["offset"]
            bottom = top - slab["thickness"]
            if top <= start_z + 1e-9 or bottom >= end_z - 1e-9:
                continue
            for tread in range(stair["risers"]):
                tread_top = start_z + (tread + 1) * (end_z - start_z) / stair["risers"]
                if min(tread_top, top) - max(start_z, bottom) <= 1e-9:
                    continue
                footprint = Polygon(rectangle_points(origin, direction, tread * stair["run"] / stair["risers"],
                                                     (tread + 1) * stair["run"] / stair["risers"], 0, stair["width"]))
                if footprint.intersection(material).area > 1e-9:
                    invalid(f"{stair['id']} intersects slab {slab['id']}; provide a stair void or clear landing")
        if not lower_supported or not upper_supported:
            invalid(f"{stair['id']} needs supported lower and upper landings, and an upper slab void covering the flight")
    return project


def block_specs(definition):
    """Five disjoint local boxes in bottom, top, left, right, centre order.

    Each box is {"min": [x,y,z], "max": [x,y,z]} in metres. Rails span the
    full width. Jambs sit between rails. The centre is glazing for windows
    and a conceptual panel for doors. Boxes touch without overlapping volume.
    """
    width, height, depth = (definition[key] for key in ("width", "height", "depth"))
    frame = min(0.06, width / 4, height / 4)
    centre_depth = min(0.012 if definition["kind"] == "window" else 0.04, depth)
    return [
        {"min": [0.0, -depth / 2, 0.0], "max": [width, depth / 2, frame]},
        {"min": [0.0, -depth / 2, height - frame], "max": [width, depth / 2, height]},
        {"min": [0.0, -depth / 2, frame], "max": [frame, depth / 2, height - frame]},
        {"min": [width - frame, -depth / 2, frame], "max": [width, depth / 2, height - frame]},
        {"min": [frame, -centre_depth / 2, frame], "max": [width - frame, centre_depth / 2, height - frame]},
    ]


def global_id(project_id, semantic_id):
    import ifcopenshell.guid
    namespace = uuid.uuid5(uuid.NAMESPACE_URL, project_id)
    return ifcopenshell.guid.compress(uuid.uuid5(namespace, semantic_id).hex)


class IfcBuilder:
    def __init__(self, project):
        self.project = project
        self.model = require_runtime().file(schema="IFC4")
        self.context = self.model.create_entity(
            "IfcGeometricRepresentationContext", ContextIdentifier="Model", ContextType="Model",
            CoordinateSpaceDimension=3, Precision=1e-7, WorldCoordinateSystem=self.axis(),
        )
        self.body = self.model.create_entity(
            "IfcGeometricRepresentationSubContext", ContextIdentifier="Body", ContextType="Model",
            ParentContext=self.context, TargetView="MODEL_VIEW",
        )
        self.levels = index_by_id(project, "levels")
        self.storeys = {}
        self.walls = {}
        self.types = {}

    def guid(self, identifier):
        return global_id(self.project["id"], identifier)

    def axis(self, origin=(0, 0, 0), axis=(0, 0, 1), reference=(1, 0, 0)):
        return self.model.create_entity(
            "IfcAxis2Placement3D", Location=self.model.create_entity("IfcCartesianPoint", Coordinates=tuple(float(value) for value in origin)),
            Axis=self.model.create_entity("IfcDirection", DirectionRatios=tuple(float(value) for value in axis)),
            RefDirection=self.model.create_entity("IfcDirection", DirectionRatios=tuple(float(value) for value in reference)),
        )

    def placement(self, parent=None, origin=(0, 0, 0), direction=(1, 0)):
        return self.model.create_entity(
            "IfcLocalPlacement", PlacementRelTo=parent,
            RelativePlacement=self.axis(origin, reference=(*direction, 0)),
        )

    def properties(self, entity, identifier, role, extra=None):
        values = {"SourceId": identifier, "ProjectId": self.project["id"], "Role": role}
        values.update(extra or {})
        properties = []
        for name, value in values.items():
            kind = "IfcInteger" if type(value) is int else "IfcLabel"
            properties.append(self.model.create_entity("IfcPropertySingleValue", Name=name, NominalValue=self.model.create_entity(kind, value)))
        property_set = self.model.create_entity(
            "IfcPropertySet", GlobalId=self.guid(f"$pset/{role}/{identifier}"), Name="Pset_MikiOpenBim", HasProperties=properties,
        )
        if entity.is_a("IfcTypeObject"):
            entity.HasPropertySets = [property_set]
        else:
            self.model.create_entity("IfcRelDefinesByProperties", GlobalId=self.guid(f"$properties/{role}/{identifier}"),
                                     RelatedObjects=[entity], RelatingPropertyDefinition=property_set)

    def entity(self, kind, identifier, role, **attributes):
        semantic_id = f"$fill/{identifier}" if role == "fill" else identifier
        entity = self.model.create_entity(kind, GlobalId=self.guid(semantic_id), Name=identifier, **attributes)
        self.properties(entity, identifier, role)
        return entity

    def curve(self, boundary):
        coordinates = ring_points(boundary)
        points = [self.model.create_entity("IfcCartesianPoint", Coordinates=tuple(float(value) for value in vertex))
                  for vertex in coordinates + [coordinates[0]]]
        return self.model.create_entity("IfcPolyline", Points=points)

    def profile(self, boundary, holes=()):
        attributes = {"ProfileType": "AREA", "OuterCurve": self.curve(boundary)}
        if holes:
            return self.model.create_entity("IfcArbitraryProfileDefWithVoids", **attributes,
                                            InnerCurves=[self.curve(boundary) for boundary in holes])
        return self.model.create_entity("IfcArbitraryClosedProfileDef", **attributes)

    def extrusion(self, boundary, height, bottom=0, holes=()):
        return self.model.create_entity(
            "IfcExtrudedAreaSolid", SweptArea=self.profile(boundary, holes), Position=self.axis((0, 0, bottom)),
            ExtrudedDirection=self.model.create_entity("IfcDirection", DirectionRatios=(0.0, 0.0, 1.0)), Depth=float(height),
        )

    def box(self, specification):
        minimum, maximum = specification["min"], specification["max"]
        boundary = [[minimum[0], minimum[1]], [maximum[0], minimum[1]], [maximum[0], maximum[1]], [minimum[0], maximum[1]]]
        return self.extrusion(boundary, maximum[2] - minimum[2], minimum[2])

    def representation(self, items, kind="SweptSolid"):
        return self.model.create_entity("IfcShapeRepresentation", ContextOfItems=self.body, RepresentationIdentifier="Body",
                                         RepresentationType=kind, Items=items)

    def shape(self, items, kind="SweptSolid"):
        return self.model.create_entity("IfcProductDefinitionShape", Representations=[self.representation(items, kind)])

    def aggregate(self, parent, children, identifier):
        if children:
            self.model.create_entity("IfcRelAggregates", GlobalId=self.guid(f"$aggregate/{identifier}"),
                                     RelatingObject=parent, RelatedObjects=children)

    def contain(self, entity, level):
        self.model.create_entity("IfcRelContainedInSpatialStructure", GlobalId=self.guid(f"$contain/{entity.GlobalId}"),
                                 RelatedElements=[entity], RelatingStructure=self.storeys[level])

    def build(self):
        units = [self.model.create_entity("IfcSIUnit", UnitType=kind, Name=name)
                 for kind, name in (("LENGTHUNIT", "METRE"), ("AREAUNIT", "SQUARE_METRE"), ("VOLUMEUNIT", "CUBIC_METRE"))]
        root = self.entity("IfcProject", self.project["id"], "project", RepresentationContexts=[self.context],
                           UnitsInContext=self.model.create_entity("IfcUnitAssignment", Units=units))
        root.Name = self.project["name"]
        revision = self.model.create_entity("IfcPropertySingleValue", Name="Revision", NominalValue=self.model.create_entity("IfcInteger", self.project["revision"]))
        root.IsDefinedBy[0].RelatingPropertyDefinition.HasProperties += (revision,)
        building = self.entity("IfcBuilding", "$building", "building", ObjectPlacement=self.placement(), CompositionType="ELEMENT")
        building.Name = self.project["name"]
        self.aggregate(root, [building], "project")
        for level in self.project["levels"]:
            self.storeys[level["id"]] = self.entity(
                "IfcBuildingStorey", level["id"], "level", CompositionType="ELEMENT", Elevation=float(level["elevation"]),
                ObjectPlacement=self.placement(building.ObjectPlacement, (0, 0, level["elevation"])),
            )
        self.aggregate(building, list(self.storeys.values()), "building")
        for definition in self.project["types"]:
            mapping = self.model.create_entity("IfcRepresentationMap", MappingOrigin=self.axis(),
                                                MappedRepresentation=self.representation([self.box(box) for box in block_specs(definition)]))
            if definition["kind"] == "window":
                entity = self.entity("IfcWindowType", definition["id"], "type", PredefinedType="WINDOW", PartitioningType="SINGLE_PANEL",
                                     ParameterTakesPrecedence=False, RepresentationMaps=[mapping])
            else:
                entity = self.entity("IfcDoorType", definition["id"], "type", PredefinedType="DOOR", OperationType="SINGLE_SWING_LEFT",
                                     ParameterTakesPrecedence=False, RepresentationMaps=[mapping])
            self.types[definition["id"]] = entity
        for wall in self.project["walls"]:
            length, direction = wall_frame(wall)
            half = wall["thickness"] / 2
            height = wall.get("height", self.levels[wall["level"]]["height"])
            entity = self.entity(
                "IfcWall", wall["id"], "wall", PredefinedType="STANDARD",
                ObjectPlacement=self.placement(self.storeys[wall["level"]].ObjectPlacement, (*wall["start"], 0), direction),
                Representation=self.shape([self.extrusion([[0, -half], [length, -half], [length, half], [0, half]], height)]),
            )
            self.walls[wall["id"]] = entity
            self.contain(entity, wall["level"])
        wall_sources = index_by_id(self.project, "walls")
        type_sources = index_by_id(self.project, "types")
        typed_instances = {}
        for opening in self.project["openings"]:
            wall, definition = wall_sources[opening["wall"]], type_sources[opening["type"]]
            host = self.walls[wall["id"]]
            half = wall["thickness"] / 2
            width, height = definition["width"], definition["height"]
            void = self.entity("IfcOpeningElement", opening["id"], "opening", PredefinedType="OPENING",
                               ObjectPlacement=self.placement(host.ObjectPlacement, (opening["offset"], 0, opening["sill"])),
                               Representation=self.shape([self.extrusion([[0, -half], [width, -half], [width, half], [0, half]], height)]))
            self.model.create_entity("IfcRelVoidsElement", GlobalId=self.guid(f"$void/{opening['id']}"), RelatingBuildingElement=host, RelatedOpeningElement=void)
            definition_entity = self.types[definition["id"]]
            mapping = self.model.create_entity(
                "IfcMappedItem", MappingSource=definition_entity.RepresentationMaps[0],
                MappingTarget=self.model.create_entity("IfcCartesianTransformationOperator3D", LocalOrigin=self.model.create_entity("IfcCartesianPoint", Coordinates=(0.0, 0.0, 0.0)), Scale=1.0),
            )
            kind = "IfcWindow" if definition["kind"] == "window" else "IfcDoor"
            fill = self.entity(kind, opening["id"], "fill", ObjectPlacement=self.placement(void.ObjectPlacement),
                               OverallWidth=float(width), OverallHeight=float(height), Representation=self.shape([mapping], "MappedRepresentation"))
            self.model.create_entity("IfcRelFillsElement", GlobalId=self.guid(f"$fill-relation/{opening['id']}"), RelatingOpeningElement=void, RelatedBuildingElement=fill)
            typed_instances.setdefault(definition["id"], []).append(fill)
            self.contain(fill, wall["level"])
        for identifier, instances in typed_instances.items():
            self.model.create_entity("IfcRelDefinesByType", GlobalId=self.guid(f"$type-relation/{identifier}"),
                                     RelatingType=self.types[identifier], RelatedObjects=instances)
        for space in self.project["spaces"]:
            entity = self.entity("IfcSpace", space["id"], "space", LongName=space["name"], CompositionType="ELEMENT", PredefinedType="SPACE",
                                 ObjectPlacement=self.placement(self.storeys[space["level"]].ObjectPlacement),
                                 Representation=self.shape([self.extrusion(space["boundary"], self.levels[space["level"]]["height"])]))
            self.aggregate(self.storeys[space["level"]], [entity], space["id"])
        for slab in self.project["slabs"]:
            entity = self.entity("IfcSlab", slab["id"], "slab", PredefinedType="FLOOR",
                                 ObjectPlacement=self.placement(self.storeys[slab["level"]].ObjectPlacement, (0, 0, slab["offset"] - slab["thickness"])),
                                 Representation=self.shape([self.extrusion(slab["boundary"], slab["thickness"], holes=slab["voids"])]))
            self.contain(entity, slab["level"])
        for stair in self.project["stairs"]:
            rise = self.levels[stair["to"]]["elevation"] - self.levels[stair["from"]]["elevation"]
            count, run = stair["risers"], stair["run"]
            section = [[0, 0], [run, 0], [run, rise]]
            for step in range(count, 0, -1):
                section.append([(step - 1) * run / count, step * rise / count])
                if step > 1:
                    section.append([(step - 1) * run / count, (step - 1) * rise / count])
            solid = self.model.create_entity("IfcExtrudedAreaSolid", SweptArea=self.profile(section),
                                             Position=self.axis((0, stair["width"], 0), axis=(0, -1, 0)),
                                             ExtrudedDirection=self.model.create_entity("IfcDirection", DirectionRatios=(0.0, 0.0, 1.0)), Depth=float(stair["width"]))
            magnitude = math.hypot(*stair["direction"])
            direction = [coordinate / magnitude for coordinate in stair["direction"]]
            entity = self.entity("IfcStairFlight", stair["id"], "stair", PredefinedType="STRAIGHT", NumberOfRisers=count, NumberOfTreads=count,
                                 RiserHeight=rise / count, TreadLength=run / count,
                                 ObjectPlacement=self.placement(self.storeys[stair["from"]].ObjectPlacement, (*stair["origin"], 0), direction),
                                 Representation=self.shape([solid]))
            self.contain(entity, stair["from"])
        return self.model


def bounds_for(points, bottom, top):
    return {"min": [min(vertex[0] for vertex in points), min(vertex[1] for vertex in points), bottom],
            "max": [max(vertex[0] for vertex in points), max(vertex[1] for vertex in points), top]}


def expected_products(project):
    from shapely.geometry import Polygon
    levels, walls, types = (index_by_id(project, collection) for collection in ("levels", "walls", "types"))
    expected = []
    for wall in project["walls"]:
        level = levels[wall["level"]]
        length, direction = wall_frame(wall)
        height = wall.get("height", level["height"])
        section = wall_section(project, wall)
        pieces = list(section.geoms) if section.geom_type == "MultiPolygon" else [section]
        vertices = [(along, vertical) for piece in pieces for along, vertical in piece.exterior.coords]
        world = [transform_point(wall["start"], direction, along, normal)
                 for along, vertical in vertices for normal in (-wall["thickness"] / 2, wall["thickness"] / 2)]
        bottom, top = min(vertex[1] for vertex in vertices), max(vertex[1] for vertex in vertices)
        expected.append({"id": wall["id"], "role": "wall", "ifcClass": "IfcWall", "level": wall["level"],
                         "volume": section.area * wall["thickness"], "grossVolume": length * height * wall["thickness"],
                         "bounds": bounds_for(world, level["elevation"] + bottom, level["elevation"] + top)})
    for opening in project["openings"]:
        wall, definition = walls[opening["wall"]], types[opening["type"]]
        level = levels[wall["level"]]
        length, direction = wall_frame(wall)
        bottom = level["elevation"] + opening["sill"]
        width, height, depth = (definition[key] for key in ("width", "height", "depth"))
        for role in ("opening", "fill"):
            thickness = wall["thickness"] if role == "opening" else depth
            footprint = rectangle_points(wall["start"], direction, opening["offset"], opening["offset"] + width, -thickness / 2, thickness / 2)
            volume = width * height * thickness
            kind = "IfcOpeningElement"
            if role == "fill":
                frame = min(0.06, width / 4, height / 4)
                centre_depth = min(0.012 if definition["kind"] == "window" else 0.04, depth)
                inner_area = (width - 2 * frame) * (height - 2 * frame)
                volume = (width * height - inner_area) * depth + inner_area * centre_depth
                kind = "IfcWindow" if definition["kind"] == "window" else "IfcDoor"
            expected.append({"id": opening["id"], "role": role, "ifcClass": kind, "level": wall["level"],
                             "hostId": wall["id"], "typeId": definition["id"], "volume": volume,
                             "bounds": bounds_for(footprint, bottom, bottom + height)})
    for space in project["spaces"]:
        level = levels[space["level"]]
        expected.append({"id": space["id"], "role": "space", "ifcClass": "IfcSpace", "level": space["level"],
                         "volume": Polygon(space["boundary"]).area * level["height"],
                         "bounds": bounds_for(space["boundary"], level["elevation"], level["elevation"] + level["height"])})
    for slab in project["slabs"]:
        top = levels[slab["level"]]["elevation"] + slab["offset"]
        area = Polygon(slab["boundary"]).area - sum(Polygon(boundary).area for boundary in slab["voids"])
        expected.append({"id": slab["id"], "role": "slab", "ifcClass": "IfcSlab", "level": slab["level"],
                         "volume": area * slab["thickness"], "bounds": bounds_for(slab["boundary"], top - slab["thickness"], top)})
    for stair in project["stairs"]:
        bottom, top = levels[stair["from"]]["elevation"], levels[stair["to"]]["elevation"]
        magnitude = math.hypot(*stair["direction"])
        direction = [coordinate / magnitude for coordinate in stair["direction"]]
        footprint = rectangle_points(stair["origin"], direction, 0, stair["run"], 0, stair["width"])
        volume = stair["width"] * stair["run"] * (top - bottom) * (stair["risers"] + 1) / (2 * stair["risers"])
        expected.append({"id": stair["id"], "role": "stair", "ifcClass": "IfcStairFlight", "level": stair["from"],
                         "volume": volume, "bounds": bounds_for(footprint, bottom, top)})
    return expected


def close_number(actual, expected):
    return isinstance(actual, (int, float)) and math.isfinite(actual) and math.isclose(actual, expected, rel_tol=1e-6, abs_tol=1e-7)


def same_bounds(actual, expected):
    return all(abs(actual[key][axis] - expected[key][axis]) <= TOLERANCE for key in ("min", "max") for axis in range(3))


def source_properties(entity):
    import ifcopenshell.util.element
    return ifcopenshell.util.element.get_psets(entity, should_inherit=False).get("Pset_MikiOpenBim", {})


def geometric_readback(entity, gross=False):
    import ifcopenshell.geom
    from ifcopenshell import ifcopenshell_wrapper
    native_settings = ifcopenshell.geom.settings()
    native_settings.set("use-world-coords", True)
    native_settings.set("iterator-output", ifcopenshell_wrapper.NATIVE)
    native_settings.set("disable-opening-subtractions", gross)
    native_settings.set("validate", True)
    native = ifcopenshell.geom.create_shape(native_settings, entity, geometry_library="opencascade")
    volume = float(native.volume)
    manifold = native.geometry.size() > 0 and all(native.geometry.item(index).is_manifold() and native.geometry.item(index).num_faces() >= 4
                                                for index in range(native.geometry.size()))
    if gross:
        return volume
    mesh_settings = ifcopenshell.geom.settings()
    mesh_settings.set("use-world-coords", True)
    mesh_settings.set("validate", True)
    mesh = ifcopenshell.geom.create_shape(mesh_settings, entity, geometry_library="opencascade")
    coordinates = mesh.geometry.verts
    if not coordinates or not all(math.isfinite(value) for value in coordinates):
        raise ValueError("The OpenCASCADE shape has no finite vertices")
    bounds = {"min": [min(coordinates[axis::3]) for axis in range(3)],
              "max": [max(coordinates[axis::3]) for axis in range(3)]}
    return volume, bounds, manifold and math.isfinite(volume) and volume > 0


def inspect_project(source, input_path):
    ifcopenshell = require_runtime()
    project = validate_project(source)
    try:
        model = ifcopenshell.open(str(input_path))
    except Exception as error:
        raise WorkerError("invalid_ifc", "Could not reopen the supplied IFC file") from error
    expected = expected_products(project)
    report = {
        "schemaVersion": 1, "passed": False, "verified": False, "projectId": project["id"], "revision": project["revision"], "ifcSchema": model.schema,
        "engine": {"name": "IfcOpenShell", "version": IFCOPENSHELL_VERSION, "kernel": "OpenCASCADE"},
        "sourceCounts": {collection: len(project[collection]) for collection in COLLECTION_FIELDS},
        "counts": {}, "checks": [], "products": [], "quantities": {},
    }

    def check(name, passed, failures=None):
        entry = {"name": name, "passed": bool(passed)}
        if failures:
            entry["failedIds"] = failures
        report["checks"].append(entry)

    check("ifc4", model.schema == "IFC4")
    if model.schema != "IFC4":
        report["failedChecks"] = 1
        return report
    logger = ifcopenshell.validate.json_logger()
    try:
        ifcopenshell.validate.validate(model, logger)
        check("ifcSchema", not logger.statements)
    except Exception:
        check("ifcSchema", False)
    root_ids = [root.GlobalId for root in model.by_type("IfcRoot")]
    check("uniqueGlobalIds", len(root_ids) == len(set(root_ids)))
    expected_counts = {kind: 0 for kind in ("IfcProject", "IfcBuilding", "IfcBuildingStorey", "IfcSpace", "IfcWall", "IfcSlab", "IfcStairFlight",
                                           "IfcOpeningElement", "IfcWindow", "IfcDoor", "IfcWindowType", "IfcDoorType")}
    expected_counts.update(IfcProject=1, IfcBuilding=1, IfcBuildingStorey=len(project["levels"]))
    for item in expected:
        expected_counts[item["ifcClass"]] += 1
    for definition in project["types"]:
        expected_counts["IfcWindowType" if definition["kind"] == "window" else "IfcDoorType"] += 1
    report["counts"] = {kind: len(model.by_type(kind)) for kind in expected_counts}
    check("quantities", report["counts"] == expected_counts)
    expected_product_ids = {global_id(project["id"], "$building")}
    expected_product_ids.update(global_id(project["id"], level["id"]) for level in project["levels"])
    expected_product_ids.update(global_id(project["id"], f"$fill/{item['id']}" if item["role"] == "fill" else item["id"]) for item in expected)
    actual_products = model.by_type("IfcProduct")
    check("productInventory", len(actual_products) == len(expected_product_ids) and {entity.GlobalId for entity in actual_products} == expected_product_ids)
    forbidden = ("IfcBuildingElementProxy", "IfcTessellatedItem", "IfcFacetedBrep")
    check("exactRepresentations", not any(model.by_type(kind) for kind in forbidden))

    def find(identifier, kind):
        try:
            entity = model.by_guid(global_id(project["id"], identifier))
            return entity if entity.is_a(kind) else None
        except (RuntimeError, ValueError):
            return None

    identity_failures = []

    def identity(entity, identifier, role):
        if entity is None:
            identity_failures.append(identifier)
            return False
        properties = source_properties(entity)
        valid = all(properties.get(key) == value for key, value in (("SourceId", identifier), ("ProjectId", project["id"]), ("Role", role)))
        if not valid:
            identity_failures.append(identifier)
        return valid

    root, building = find(project["id"], "IfcProject"), find("$building", "IfcBuilding")
    identity(root, project["id"], "project")
    identity(building, "$building", "building")
    check("revision", root is not None and source_properties(root).get("Revision") == project["revision"])
    units = root.UnitsInContext.Units if root is not None and root.UnitsInContext else ()
    length_units = [unit for unit in units if getattr(unit, "UnitType", None) == "LENGTHUNIT"]
    check("metreUnits", len(length_units) == 1 and length_units[0].is_a("IfcSIUnit") and length_units[0].Name == "METRE" and length_units[0].Prefix is None)
    hierarchy_failures = []

    def aggregated(child, parent):
        return child is not None and parent is not None and len(child.Decomposes) == 1 and child.Decomposes[0].RelatingObject == parent

    if not aggregated(building, root):
        hierarchy_failures.append("$building")
    import ifcopenshell.util.placement
    for level in project["levels"]:
        storey = find(level["id"], "IfcBuildingStorey")
        identity(storey, level["id"], "level")
        valid = aggregated(storey, building)
        try:
            matrix = ifcopenshell.util.placement.get_local_placement(storey.ObjectPlacement)
            target = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, level["elevation"]], [0, 0, 0, 1]]
            valid = valid and close_number(storey.Elevation, level["elevation"]) and all(close_number(matrix[row][column], target[row][column]) for row in range(4) for column in range(4))
        except (AttributeError, TypeError, ValueError):
            valid = False
        if not valid:
            hierarchy_failures.append(level["id"])
    type_failures = []
    for definition in project["types"]:
        entity = find(definition["id"], "IfcWindowType" if definition["kind"] == "window" else "IfcDoorType")
        identity(entity, definition["id"], "type")
        expected_kind = "WINDOW" if definition["kind"] == "window" else "DOOR"
        if entity is None or len(entity.RepresentationMaps or ()) != 1 or entity.PredefinedType != expected_kind:
            type_failures.append(definition["id"])
        elif entity.RepresentationMaps[0].MappedRepresentation.RepresentationType != "SweptSolid" or len(entity.RepresentationMaps[0].MappedRepresentation.Items) != 5 or not all(item.is_a("IfcExtrudedAreaSolid") for item in entity.RepresentationMaps[0].MappedRepresentation.Items):
            type_failures.append(definition["id"])
    relation_failures, geometry_failures, wall_void_failures = [], [], []
    for item in expected:
        identifier = f"$fill/{item['id']}" if item["role"] == "fill" else item["id"]
        entity = find(identifier, item["ifcClass"])
        identity(entity, item["id"], item["role"])
        result = {key: item[key] for key in ("id", "role", "ifcClass", "level", "hostId", "typeId") if key in item}
        result.update(globalId=global_id(project["id"], identifier), passed=False)
        report["products"].append(result)
        if entity is None:
            geometry_failures.append(identifier)
            continue
        storey = find(item["level"], "IfcBuildingStorey")
        if item["role"] == "space":
            valid_storey = aggregated(entity, storey)
        elif item["role"] == "opening":
            host = find(item["hostId"], "IfcWall")
            valid_storey = len(entity.VoidsElements) == 1 and entity.VoidsElements[0].RelatingBuildingElement == host and len(entity.HasFillings) == 1
            valid_storey = valid_storey and entity.HasFillings[0].RelatedBuildingElement == find(f"$fill/{item['id']}", "IfcElement")
            if not valid_storey:
                relation_failures.append(identifier)
        else:
            valid_storey = len(entity.ContainedInStructure) == 1 and entity.ContainedInStructure[0].RelatingStructure == storey
        if not valid_storey:
            hierarchy_failures.append(identifier)
        try:
            representations = entity.Representation.Representations
            representation = representations[0]
            exact = len(representations) == 1 and representation.RepresentationType == "SweptSolid" and all(solid.is_a("IfcExtrudedAreaSolid") for solid in representation.Items)
            if item["role"] == "fill":
                definition = next(value for value in project["types"] if value["id"] == item["typeId"])
                type_entity = find(definition["id"], "IfcWindowType" if definition["kind"] == "window" else "IfcDoorType")
                exact = len(representations) == 1 and representation.RepresentationType == "MappedRepresentation" and len(representation.Items) == 1 and representation.Items[0].is_a("IfcMappedItem")
                valid_type = len(entity.IsTypedBy) == 1 and entity.IsTypedBy[0].RelatingType == type_entity and type_entity is not None
                valid_type = valid_type and exact and representation.Items[0].MappingSource == type_entity.RepresentationMaps[0]
                valid_type = valid_type and close_number(entity.OverallWidth, definition["width"]) and close_number(entity.OverallHeight, definition["height"])
                if not valid_type:
                    type_failures.append(identifier)
                if len(entity.FillsVoids) != 1 or entity.FillsVoids[0].RelatingOpeningElement != find(item["id"], "IfcOpeningElement"):
                    relation_failures.append(identifier)
            volume, bounds, solid_valid = geometric_readback(entity)
            volume_matches, bounds_match = close_number(volume, item["volume"]), same_bounds(bounds, item["bounds"])
            result.update(volume=volume, expectedVolume=item["volume"], bounds=bounds,
                          passed=bool(exact and solid_valid and volume_matches and bounds_match))
            if not result["passed"]:
                geometry_failures.append(identifier)
            if item["role"] == "wall":
                gross = geometric_readback(entity, gross=True)
                void = gross - volume
                result.update(grossVolume=gross, voidVolume=void)
                if not close_number(gross, item["grossVolume"]) or not close_number(void, item["grossVolume"] - item["volume"]):
                    wall_void_failures.append(identifier)
        except Exception:
            result["passed"] = False
            result["error"] = "opencascade_readback_failed"
            geometry_failures.append(identifier)
    check("sourceIdentities", not identity_failures, identity_failures)
    check("storeys", not hierarchy_failures, hierarchy_failures)
    check("sharedTypes", not type_failures, type_failures)
    check("voidFillRelations", not relation_failures, relation_failures)
    check("solidGeometryVolumeBounds", not geometry_failures, geometry_failures)
    check("wallVoidVolumes", not wall_void_failures, wall_void_failures)
    for role in ("wall", "slab", "space", "stair", "opening", "fill"):
        report["quantities"][role + "Volume"] = sum(item.get("volume", 0) for item in report["products"] if item["role"] == role)
    report["failedChecks"] = sum(not entry["passed"] for entry in report["checks"])
    report["passed"] = report["failedChecks"] == 0
    report["verified"] = report["passed"]
    return report


def canonical_paths(source, targets):
    paths = [Path(source).resolve()] if not isinstance(source, dict) else []
    resolved = [Path(target).resolve() for target in targets]
    for path in resolved:
        if not path.parent.is_dir() or path.is_dir():
            raise WorkerError("invalid_path", "Output parents must exist and output targets must be regular files")
    all_paths = paths + resolved
    for index, path in enumerate(all_paths):
        for other in all_paths[:index]:
            if os.path.normcase(str(path)) == os.path.normcase(str(other)) or (path.exists() and other.exists() and os.path.samefile(path, other)):
                raise WorkerError("invalid_path", "Source, model and report must be distinct files, including filesystem aliases")
    return resolved


@contextmanager
def output_locks(targets, overwrite):
    locks = []
    try:
        for target in sorted(targets, key=lambda path: os.path.normcase(str(path))):
            lock = target.with_name(target.name + ".miki-ifc.lock")
            try:
                descriptor = os.open(lock, os.O_CREAT | os.O_RDWR, 0o600)
            except OSError as error:
                raise WorkerError("io_failed", "Could not open an output lock file") from error
            try:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as error:
                os.close(descriptor)
                if error.errno in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                    raise WorkerError("output_busy", "Another process holds an output lock. Retry after it finishes") from error
                raise WorkerError("io_failed", "Could not acquire an output lock") from error
            locks.append(descriptor)
        if not overwrite and any(target.exists() for target in targets):
            raise WorkerError("output_exists", "An output already exists. Explicit --overwrite is required to replace either output")
        yield
    finally:
        for descriptor in reversed(locks):
            os.close(descriptor)


def temporary_path(target):
    descriptor, name = tempfile.mkstemp(prefix=f".{target.name}.", suffix=".tmp", dir=target.parent)
    os.close(descriptor)
    return Path(name)


def publish(staged, overwrite):
    backups, published = {}, []
    try:
        if overwrite:
            for temporary, target in staged:
                if target.exists():
                    backup = temporary_path(target)
                    backups[target] = backup
                    shutil.copy2(target, backup)
        for temporary, target in staged:
            if overwrite:
                os.replace(temporary, target)
            else:
                os.link(temporary, target)
            published.append(target)
    except OSError as error:
        for target in reversed(published):
            if target in backups:
                os.replace(backups.pop(target), target)
            else:
                target.unlink(missing_ok=True)
        raise WorkerError("publication_failed", "Could not publish both verified outputs; previous files were restored") from error
    finally:
        for backup in backups.values():
            backup.unlink(missing_ok=True)


def write_report(report, target):
    target.write_text(json.dumps(report, ensure_ascii=True, allow_nan=False, separators=(",", ":")) + "\n", encoding="utf-8")


def compact_report(report):
    keys = ("schemaVersion", "passed", "verified", "projectId", "revision", "ifcSchema", "counts", "failedChecks")
    result = {key: report[key] for key in keys if key in report}
    failures = [check for check in report.get("checks", []) if not check["passed"]]
    if failures:
        result["checks"] = failures
    return result


def export_project(source, output_path, report_path, *, overwrite=False):
    project = validate_project(source)
    output, report_target = canonical_paths(source, [output_path, report_path])
    staged = []
    try:
        with output_locks([output, report_target], overwrite):
            for target in (output, report_target):
                staged.append((temporary_path(target), target))
            model_temporary, report_temporary = (entry[0] for entry in staged)
            IfcBuilder(project).build().write(str(model_temporary))
            report = inspect_project(project, model_temporary)
            if not report["passed"]:
                raise WorkerError("verification_failed", "The reopened IFC failed geometric or semantic checks; outputs were not published", report)
            write_report(report, report_temporary)
            publish(staged, overwrite)
            return report
    except WorkerError:
        raise
    except Exception as error:
        raise WorkerError("export_failed", "IFC export failed before both verified outputs could be published") from error
    finally:
        for temporary, target in staged:
            temporary.unlink(missing_ok=True)


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    exporter = commands.add_parser("export", help="Export IFC4 and verify reopened OpenCASCADE solids")
    exporter.add_argument("--project", required=True)
    exporter.add_argument("--output", required=True)
    exporter.add_argument("--report", required=True)
    exporter.add_argument("--overwrite", action="store_true")
    inspector = commands.add_parser("inspect", help="Compare an existing IFC against its source JSON")
    inspector.add_argument("--project", required=True)
    inspector.add_argument("--input", required=True)
    inspector.add_argument("--report")
    inspector.add_argument("--overwrite", action="store_true")
    options = parser.parse_args(arguments)
    try:
        if options.command == "export":
            report = export_project(options.project, options.output, options.report, overwrite=options.overwrite)
        elif options.report:
            input_path, report_target = canonical_paths(options.project, [options.input, options.report])
            with output_locks([report_target], options.overwrite):
                report = inspect_project(options.project, input_path)
                temporary = temporary_path(report_target)
                try:
                    write_report(report, temporary)
                    publish([(temporary, report_target)], options.overwrite)
                finally:
                    temporary.unlink(missing_ok=True)
        else:
            report = inspect_project(options.project, options.input)
        response = compact_report(report) if options.report else report
        print(json.dumps(response, ensure_ascii=True, allow_nan=False, separators=(",", ":")))
        return 0 if report["passed"] else 1
    except WorkerError as error:
        response = compact_report(error.report) if error.report else {"schemaVersion": 1, "passed": False, "verified": False}
        response["error"] = {"code": error.code, "message": str(error)}
        print(json.dumps(response, ensure_ascii=True, allow_nan=False, separators=(",", ":")))
        return 1 if error.code == "verification_failed" else 2
    except (OSError, ValueError):
        print(json.dumps({"schemaVersion": 1, "passed": False, "verified": False, "error": {"code": "io_failed", "message": "Could not complete the requested file operation"}}))
        return 2


if __name__ == "__main__":
    sys.exit(main())
