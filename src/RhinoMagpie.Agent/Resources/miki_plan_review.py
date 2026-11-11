"""Read-only concept plan review for Miki OpenBIM schema 1."""

import argparse
from collections import defaultdict, deque
import json
import math
from pathlib import Path
import re
import sys

if __name__ == "__main__":
    # The embeddable Windows runtime and -I omit the script directory.
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from miki_ifc import (WorkerError, block_specs, duplicate_keys, index_by_id, rectangle_points,
                      transform_point, validate_project, wall_frame, wall_section)


LINE_TOL_M = 0.001
AREA_TOL_M2 = 0.000001
# Concept traversability screen, not an accessibility, egress or code check.
MIN_CLEAR_WIDTH_M = 0.60
MIN_CLEAR_HEIGHT_M = 1.80
# Fixed geometry conventions of the schema-1 layout baseline.
LAYOUT_LEVELS = ("LB.L1", "LB.L2")
LAYOUT_INSET_M = 0.20
LAYOUT_MIN_BYPASS_M = 0.90


def _layout_profile(layout, fail):
    """Validate the raw brief without rewriting it or requiring approval metadata."""
    def fields(obj, at, required, optional=()):
        if not isinstance(obj, dict) or not set(required) <= obj.keys() <= set(required) | set(optional):
            fail(f"Invalid {at} fields.")

    def measure(value, at, low, high, scale=10):
        if type(value) not in (int, float) or not low <= value <= high:
            fail(f"{at} must be a number in [{low}, {high}].")
        if abs(value * scale - round(value * scale)) > 1e-9:
            fail(f"{at} must use increments of {1 / scale:g}.")

    def identity(obj, at, used):
        identifier, name = obj["id"], obj["name"]
        if (not isinstance(identifier, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", identifier)
                or identifier.startswith("LB.") or identifier in used):
            fail(f"{at}.id must be a unique layout ID outside LB.*.")
        if (not isinstance(name, str) or not name.strip()
                or len(name.encode("utf-16-le", errors="surrogatepass")) > 512
                or any(0xD800 <= ord(character) <= 0xDFFF for character in name)):
            fail(f"{at}.name must be nonblank and at most 256 UTF-16 code units.")
        used.add(identifier)

    fields(layout, "layout", ("schemaVersion", "id", "name", "envelope", "corridor", "stair", "levels"),
           ("approved",))
    if type(layout["schemaVersion"]) is not int or layout["schemaVersion"] != 1:
        fail("layout.schemaVersion must be 1.")
    if "approved" in layout and layout["approved"] is not True:
        fail("layout.approved must be true when present.")
    used = set()
    identity(layout, "layout", used)
    envelope, corridor, stair = (layout[key] for key in ("envelope", "corridor", "stair"))
    fields(envelope, "layout.envelope", ("widthM", "depthM"))
    fields(corridor, "layout.corridor", ("pattern", "widthM"))
    fields(stair, "layout.stair", ("startXM", "widthM", "runM", "risers"))
    if corridor["pattern"] != "all_rooms_direct":
        fail("layout.corridor.pattern must be all_rooms_direct.")
    for group, obj, keys in (("envelope", envelope, ("widthM", "depthM")),
                             ("corridor", corridor, ("widthM",)), ("stair", stair, ("widthM", "runM"))):
        for key in keys:
            measure(obj[key], f"layout.{group}.{key}", 0.1, 30)
    measure(stair["startXM"], "layout.stair.startXM", 0, 30)
    if type(stair["risers"]) is not int or not 2 <= stair["risers"] <= 50:
        fail("layout.stair.risers must be an integer from 2 through 50.")

    if not isinstance(layout["levels"], list) or len(layout["levels"]) != 2:
        fail("layout.levels must contain two entries in bottom-to-top order.")
    targets = {}
    for index, level in enumerate(layout["levels"]):
        at = f"layout.levels[{index}]"
        fields(level, at, ("heightM", "spaces", "requiredAdjacencies"))
        measure(level["heightM"], f"{at}.heightM", 2.2, 6)
        if not isinstance(level["spaces"], list) or not 1 <= len(level["spaces"]) <= 5:
            fail(f"{at}.spaces must contain 1–5 rooms.")
        room_ids = set()
        for space in level["spaces"]:
            fields(space, f"{at}.spaces[]", ("id", "name", "areaM2"))
            identity(space, f"{at}.spaces[]", used)
            room_ids.add(space["id"])
            bounds = space["areaM2"]
            fields(bounds, f"{at}.spaces[].areaM2", ("min", "target", "max"))
            for key, value in bounds.items():
                measure(value, f"{at}.spaces[].areaM2.{key}", 0.01, 900, scale=100)
            if not bounds["min"] <= bounds["target"] <= bounds["max"]:
                fail(f"{at}.spaces[].areaM2 must satisfy min <= target <= max.")
            targets[space["id"]] = {key: bounds[key] for key in ("min", "max")}
        pairs = level["requiredAdjacencies"]
        if not isinstance(pairs, list) or len(pairs) > 10:
            fail(f"{at}.requiredAdjacencies must be a list of at most ten room pairs.")
        edges = set()
        for pair in pairs:
            if (not isinstance(pair, list) or len(pair) != 2 or pair[0] == pair[1]
                    or any(not isinstance(room, str) or room not in room_ids for room in pair)):
                fail(f"{at}.requiredAdjacencies must pair distinct rooms on this level.")
            edge = frozenset(pair)
            if edge in edges:
                fail(f"{at}.requiredAdjacencies must not repeat pairs.")
            edges.add(edge)
    return targets


def _policy(profile, spaces):
    def fail(message):
        raise WorkerError("invalid_plan_profile", message)

    if profile is None:
        profile = {}
    if not isinstance(profile, dict) or profile.keys() - {"transitSpaces", "areaTargetsM2", "layout"}:
        fail("Profile accepts transitSpaces, areaTargetsM2 and layout only.")
    layout_targets = _layout_profile(profile["layout"], fail) if "layout" in profile else {}
    corridors = {f"{level}.CORRIDOR" for level in LAYOUT_LEVELS} if layout_targets else set()
    known = spaces.keys() | layout_targets.keys() | corridors
    transit = profile.get("transitSpaces", [])
    if not isinstance(transit, list) or any(not isinstance(i, str) or i not in known for i in transit):
        fail("Each transit space must be a known space ID.")
    targets = profile.get("areaTargetsM2", {})
    if not isinstance(targets, dict):
        fail("areaTargetsM2 must be an object keyed by existing space ID.")
    for space_id, bounds in targets.items():
        if (space_id not in known or not isinstance(bounds, dict) or not bounds
                or bounds.keys() - {"min", "max"}
                or any(type(v) not in (int, float) or not 0 <= v <= 1e13 for v in bounds.values())
                or bounds.get("min", 0) > bounds.get("max", math.inf)):
            fail(f"Invalid area target for '{space_id}'.")
    return (corridors, {**targets, **layout_targets}) if corridors else (set(transit), targets)


def _point_on_frame(wall, direction, offset, normal):
    return transform_point(wall["start"], direction, offset, normal)


def _geometries(project):
    from shapely import GeometryCollection, Polygon, union_all

    floor_parts = defaultdict(list)
    for slab in project["slabs"]:
        if abs(slab["offset"]) <= LINE_TOL_M:
            floor_parts[slab["level"]].append(Polygon(slab["boundary"], holes=slab["voids"]))
    floors = {level: union_all(parts) for level, parts in floor_parts.items()}

    walls = index_by_id(project, "walls")
    types = index_by_id(project, "types")
    door_portals = {}
    cutouts = defaultdict(list)
    for opening in project["openings"]:
        definition = types[opening["type"]]
        if definition["kind"] != "door" or opening["sill"] > LINE_TOL_M:
            continue
        wall = walls[opening["wall"]]
        _, direction = wall_frame(wall)
        start, end = opening["offset"], opening["offset"] + definition["width"]
        half = wall["thickness"] / 2
        portal = Polygon(rectangle_points(wall["start"], direction, start, end, -half, half))
        cutouts[wall["id"]].append(portal)
        door_portals[opening["id"]] = (wall, direction, start, end, half, portal)

    wall_parts = defaultdict(list)
    for wall in project["walls"]:
        length, direction = wall_frame(wall)
        half = wall["thickness"] / 2
        footprint = Polygon(rectangle_points(wall["start"], direction, 0, length, -half, half))
        wall_parts[wall["level"]].append(footprint.difference(union_all(cutouts[wall["id"]])))
    solids = defaultdict(GeometryCollection, {level: union_all(parts) for level, parts in wall_parts.items()})
    return floors, solids, door_portals


def _partition_spans(wall, direction, length, normal, shape):
    from shapely import LineString, Point, get_parts

    axis = LineString([_point_on_frame(wall, direction, x, normal) for x in (0, length)])
    return sorted(sorted((axis.project(Point(segment.coords[0])), axis.project(Point(segment.coords[-1]))))
                  for segment in get_parts(axis.intersection(shape))
                  if segment.geom_type == "LineString" and segment.length > LINE_TOL_M)


def _spans_overlap(*groups):
    """Find a common interval across sorted longitudinal spans in one sweep."""
    streams = [iter(group) for group in groups]
    current = [next(stream, None) for stream in streams]
    while all(span is not None for span in current):
        first_end = min(range(len(current)), key=lambda i: current[i][1])
        if current[first_end][1] - max(span[0] for span in current) > LINE_TOL_M:
            return True
        current[first_end] = next(streams[first_end], None)
    return False


def _shared_partition(walls, a, b, polygons, padded, solids):
    for wall in walls:
        length, direction = wall_frame(wall)
        half = wall["thickness"] / 2
        for left, right in ((a, b), (b, a)):
            lface = _partition_spans(wall, direction, length, -half, padded[left])
            rface = _partition_spans(wall, direction, length, half, padded[right])
            if not lface or not rface:
                continue
            # Faces may be within tolerance, but actual room interiors must
            # also overlap along the same wall span; corners are insufficient.
            lin = _partition_spans(wall, direction, length, -half - 2 * LINE_TOL_M, polygons[left])
            rin = _partition_spans(wall, direction, length, half + 2 * LINE_TOL_M, polygons[right])
            solid = _partition_spans(wall, direction, length, 0, solids)
            if _spans_overlap(lface, rface, lin, rin, solid):
                return True
    return False


def _review_layout(project, profile, spaces, polygons, padded, solids, door_links, stair_links, issue):
    from shapely import Polygon, box, union_all

    layout = profile["layout"]
    if project["id"] != layout["id"]:
        issue("LAYOUT_PROJECT_ID", project["id"], expectedId=layout["id"])
    levels = index_by_id(project, "levels")
    if set(levels) != set(LAYOUT_LEVELS):
        issue("LAYOUT_LEVEL_SET", *LAYOUT_LEVELS, actualIds=sorted(levels))

    width, depth = (layout["envelope"][key] for key in ("widthM", "depthM"))
    envelope = box(0, 0, width, depth)
    hall_width = layout["corridor"]["widthM"]
    stair_brief = layout["stair"]
    start, stair_width, run = (stair_brief[key] for key in ("startXM", "widthM", "runM"))
    inset = LAYOUT_INSET_M
    hall = box(inset, inset, width - inset, inset + hall_width)
    flight = box(start, inset, start + run, inset + stair_width)
    corridor = hall.difference(flight)
    corridor_feasible = (start - stair_width >= inset - LINE_TOL_M
                         and start + run + stair_width <= width - inset + LINE_TOL_M
                         and hall_width - stair_width >= LAYOUT_MIN_BYPASS_M - LINE_TOL_M
                         and inset + hall_width < depth - inset)
    corridor_ids = tuple(f"{level}.CORRIDOR" for level in LAYOUT_LEVELS)
    if "transitSpaces" in profile and set(profile["transitSpaces"]) != set(corridor_ids):
        issue("LAYOUT_TRANSIT_SPACES", *corridor_ids, actualIds=sorted(set(profile["transitSpaces"])))

    def same_shape(actual, expected):
        return actual.buffer(LINE_TOL_M).covers(expected) and expected.buffer(LINE_TOL_M).covers(actual)

    door_pairs = {frozenset(link["spaces"]) for link in door_links}
    area_targets = profile.get("areaTargetsM2", {})
    slabs, walls = defaultdict(list), defaultdict(list)
    for slab in project["slabs"]:
        if abs(slab["offset"]) <= LINE_TOL_M:
            slabs[slab["level"]].append(Polygon(slab["boundary"]))
    for wall in project["walls"]:
        walls[wall["level"]].append(wall)

    expected_spaces = set(corridor_ids)
    elevation = 0
    for level_id, brief, corridor_id in zip(LAYOUT_LEVELS, layout["levels"], corridor_ids):
        height = brief["heightM"]
        if level_id in levels:
            for field, expected in (("height", height), ("elevation", elevation)):
                actual = levels[level_id][field]
                if abs(actual - expected) > LINE_TOL_M:
                    issue(f"LAYOUT_LEVEL_{field.upper()}", level_id, actualM=actual, expectedM=expected)
        elevation += height

        floor_outline = union_all(slabs[level_id])  # The stair void is not an exterior notch.
        wall_footprints = []
        for wall in walls[level_id]:
            length, direction = wall_frame(wall)
            half = wall["thickness"] / 2
            wall_footprints.append(Polygon(rectangle_points(wall["start"], direction, 0, length, -half, half)))
        shell = union_all(wall_footprints)
        if (not same_shape(floor_outline, envelope)
                or not shell.buffer(LINE_TOL_M).covers(envelope.boundary)
                or not envelope.buffer(LINE_TOL_M).covers(shell)):
            issue("LAYOUT_ENVELOPE", level_id, expectedWidthM=width, expectedDepthM=depth,
                  slabBoundsM=list(floor_outline.bounds) if not floor_outline.is_empty else None,
                  wallBoundsM=list(shell.bounds) if not shell.is_empty else None)

        actual_hall = polygons.get(corridor_id)
        if (not corridor_feasible or actual_hall is None or spaces[corridor_id]["level"] != level_id
                or not same_shape(actual_hall, corridor)):
            issue("LAYOUT_CORRIDOR_GEOMETRY", corridor_id, level=level_id, expectedWidthM=hall_width,
                  actualBoundsM=list(actual_hall.bounds) if actual_hall is not None else None)

        for room in brief["spaces"]:
            room_id = room["id"]
            expected_spaces.add(room_id)
            supplied = area_targets.get(room_id, {})
            if any(abs(value - room["areaM2"][key]) > AREA_TOL_M2 for key, value in supplied.items()):
                issue("LAYOUT_AREA_TARGET_MISMATCH", room_id, suppliedM2=supplied,
                      expectedM2={key: room["areaM2"][key] for key in ("min", "max")})
            actual = spaces.get(room_id)
            if actual is None:
                issue("LAYOUT_ROOM_MISSING", room_id, level_id)
                continue
            if actual["level"] != level_id or actual["name"] != room["name"]:
                issue("LAYOUT_ROOM_IDENTITY", room_id, level_id, actualLevel=actual["level"],
                      actualName=actual["name"], expectedName=room["name"])
            if frozenset((room_id, corridor_id)) not in door_pairs:
                issue("LAYOUT_CORRIDOR_DOOR", room_id, corridor_id)

        for a, b in brief["requiredAdjacencies"]:
            if (not all(room in spaces and spaces[room]["level"] == level_id for room in (a, b))
                    or not _shared_partition(walls[level_id], a, b, polygons, padded, solids[level_id])):
                issue("LAYOUT_REQUIRED_ADJACENCY", a, b, level=level_id)

    for room_id in sorted(spaces.keys() - expected_spaces):
        issue("LAYOUT_ROOM_UNEXPECTED", room_id, spaces[room_id]["level"])

    if len(project["stairs"]) != 1:
        issue("LAYOUT_STAIR_COUNT", *LAYOUT_LEVELS, actualCount=len(project["stairs"]), expectedCount=1)
        return
    stair = project["stairs"][0]
    if (stair["from"], stair["to"]) != LAYOUT_LEVELS:
        issue("LAYOUT_STAIR_LEVELS", stair["id"], expectedLevels=list(LAYOUT_LEVELS),
              actualLevels=[stair["from"], stair["to"]])
    measures = {"startXM": stair["origin"][0], "widthM": stair["width"], "runM": stair["run"],
                "risers": stair["risers"]}
    differences = {key: {"actual": value, "expected": stair_brief[key]} for key, value in measures.items()
                   if abs(value - stair_brief[key]) > (0 if key == "risers" else LINE_TOL_M)}
    if differences:
        issue("LAYOUT_STAIR_DIMENSIONS", stair["id"], differences=differences)
    direction = [d / math.hypot(*stair["direction"]) for d in stair["direction"]]
    if (abs(stair["origin"][1] - inset) > LINE_TOL_M
            or math.dist(direction, (1, 0)) * stair["run"] > LINE_TOL_M):
        issue("LAYOUT_STAIR_ALIGNMENT", stair["id"], actualOrigin=stair["origin"], actualDirection=direction,
              expectedOrigin=[start, inset], expectedDirection=[1, 0])
    if not any(link["id"] == stair["id"] and link["spaces"] == list(corridor_ids) for link in stair_links):
        issue("LAYOUT_STAIR_LANDINGS", stair["id"], *corridor_ids)


def _flight_blockers(project, stair, direction, levels):
    from shapely import Polygon, box

    lower, upper = (levels[stair[key]]["elevation"] for key in ("from", "to"))
    run, width, count = stair["run"], stair["width"], stair["risers"]
    flight = Polygon(rectangle_points(stair["origin"], direction, 0, run, 0, width))
    for wall in project["walls"]:
        level = levels[wall["level"]]
        base = level["elevation"]
        top = base + wall.get("height", level["height"])
        if top <= lower or base >= upper + LINE_TOL_M:
            continue
        length, along = wall_frame(wall)
        half = wall["thickness"] / 2
        footprint = Polygon(rectangle_points(wall["start"], along, 0, length, -half, half))
        if flight.intersection(footprint).area <= AREA_TOL_M2:
            continue
        section = wall_section(project, wall)
        origin_x, origin_y = wall["start"]
        for step in range(count):
            tread = Polygon(rectangle_points(stair["origin"], direction, run * step / count,
                            run * (step + 1) / count, 0, width))
            overlap = tread.intersection(footprint)
            if overlap.area <= AREA_TOL_M2:
                continue
            projections = [(x - origin_x) * along[0] + (y - origin_y) * along[1]
                           for x, y in overlap.exterior.coords]
            step_top = lower + (upper - lower) * (step + 1) / count
            # Test actual solid below the tread and just across its top.
            # This is not a vertical headroom or human-swept-volume test.
            if section.intersection(box(min(projections), lower - base, max(projections),
                                        step_top + LINE_TOL_M - base)).area > AREA_TOL_M2:
                yield wall["id"]
                break


def _stair_routes(graph, landings, transit):
    """Root a route tree in landings; non-transit rooms are leaves."""
    expanding = deque(sorted(transit & landings.keys()))
    next_step = {}
    while expanding:
        room = expanding.popleft()
        for other, door in sorted(graph[room]):
            if other in next_step or other in landings:
                continue
            next_step[other] = (room, door)
            if other in transit:
                expanding.append(other)
    return next_step


def review_plan(source, profile=None):
    project = validate_project(source)
    from shapely import LineString, Polygon
    from shapely.strtree import STRtree

    spaces = index_by_id(project, "spaces")
    transit, targets = _policy(profile, spaces)
    floors, solids, portals = _geometries(project)
    polygons = {i: Polygon(s["boundary"]) for i, s in spaces.items()}
    padded = {i: p.buffer(LINE_TOL_M) for i, p in polygons.items()}
    by_level = defaultdict(list)
    for room_id, space in spaces.items():
        by_level[space["level"]].append(room_id)
    issues, rooms, excluded = [], {}, set()

    def issue(code, *ids, **details):
        issues.append({"code": code, "ids": list(ids), **details})

    if not spaces:
        issue("NO_SPACES", project["id"])
    for level in sorted(by_level):
        ids = sorted(by_level[level])
        floor = floors.get(level)
        if floor is None:
            issue("NO_FLOOR_FOOTPRINT", level)
        tree = STRtree([polygons[i] for i in ids])
        for index, room_id in enumerate(ids):
            shape = polygons[room_id]
            area = shape.area
            outside = shape.difference(floor).area if floor is not None else None
            wall_area = shape.intersection(solids[level]).area
            bounds = targets.get(room_id)
            rooms[room_id] = {
                "level": level, "role": "transit" if room_id in transit else "room",
                "polygonAreaM2": round(area, 6), "areaTargetM2": bounds,
                "outsideFloorM2": None if outside is None else round(outside, 6),
                "wallOverlapM2": round(wall_area, 6), "toStair": None,
            }
            if outside is None or outside > AREA_TOL_M2:
                excluded.add(room_id)
                if outside is not None:
                    issue("ROOM_OUTSIDE_FLOOR", room_id, areaM2=round(outside, 6))
            if wall_area > AREA_TOL_M2:
                excluded.add(room_id)
                issue("ROOM_OVERLAPS_WALL", room_id, areaM2=round(wall_area, 6))
            if bounds and area < bounds.get("min", 0) - AREA_TOL_M2:
                issue("AREA_BELOW_MIN", room_id, actualM2=round(area, 6), limitM2=bounds["min"])
            if bounds and area > bounds.get("max", math.inf) + AREA_TOL_M2:
                issue("AREA_ABOVE_MAX", room_id, actualM2=round(area, 6), limitM2=bounds["max"])
            for candidate in sorted(tree.query(shape)):
                if candidate <= index:
                    continue
                other = ids[candidate]
                overlap = shape.intersection(polygons[other]).area
                if overlap > AREA_TOL_M2:
                    issue("ROOM_OVERLAP", room_id, other, areaM2=round(overlap, 6))
                    excluded.update([room_id, other])

    graph = defaultdict(list)
    door_links, unlinked = [], []
    openings, types = index_by_id(project, "openings"), index_by_id(project, "types")
    for door_id, (wall, direction, start, end, half, portal) in sorted(portals.items()):
        # The centre box is the region inside the two jambs and two rails.
        # Its size approximates a fully open door; no swing or leaf is modelled.
        inner = block_specs(types[openings[door_id]["type"]])[-1]
        clear_width, clear_height = (inner["max"][axis] - inner["min"][axis] for axis in (0, 2))
        if clear_width < MIN_CLEAR_WIDTH_M - 1e-9 or clear_height < MIN_CLEAR_HEIGHT_M - 1e-9:
            unlinked.append({"id": door_id, "reason": "below_concept_clear_opening"})
            issue("DOOR_CLEAR_OPENING_TOO_SMALL", door_id, clearWidthM=round(clear_width, 6),
                  clearHeightM=round(clear_height, 6), minClearWidthM=MIN_CLEAR_WIDTH_M,
                  minClearHeightM=MIN_CLEAR_HEIGHT_M)
            continue
        level = wall["level"]
        floor = floors.get(level)
        if (floor is None or portal.difference(floor).area > AREA_TOL_M2
                or portal.intersection(solids[level]).area > AREA_TOL_M2):
            unlinked.append({"id": door_id, "reason": "threshold_unsupported_or_obstructed"})
            continue
        sides = []
        for sign in (-1, 1):
            # Test both the wall face and a line on the room side so a gap or
            # a corner touch cannot manufacture a connection.
            face, inside = (LineString([_point_on_frame(wall, direction, x, sign * (half + inset))
                                        for x in (start, end)]) for inset in (0, 2 * LINE_TOL_M))
            sides.append([i for i in by_level[level] if i not in excluded
                          and padded[i].covers(face) and polygons[i].covers(inside)])
        if any(len(side) != 1 for side in sides) or sides[0] == sides[1]:
            unlinked.append({"id": door_id, "reason": "two_distinct_supported_spaces_not_found"})
            continue
        a, b = sides[0][0], sides[1][0]
        graph[a].append((b, door_id))
        graph[b].append((a, door_id))
        door_links.append({"id": door_id, "hostWall": wall["id"], "level": level, "spaces": [a, b]})

    stair_links, landings = [], {}
    levels = index_by_id(project, "levels")
    for stair in sorted(project["stairs"], key=lambda s: s["id"]):
        width, run = stair["width"], stair["run"]
        direction = [d / math.hypot(*stair["direction"]) for d in stair["direction"]]
        blockers = list(_flight_blockers(project, stair, direction, levels))
        if blockers:
            issue("STAIR_FLIGHT_BLOCKED_BY_WALL", stair["id"], *blockers[:7],
                  wallsOmitted=max(0, len(blockers) - 7))
            continue
        connected = []
        for level, start, end in ((stair["from"], -width, 0), (stair["to"], run, run + width)):
            landing = Polygon(rectangle_points(stair["origin"], direction, start, end, 0, width))
            candidates = [i for i in by_level[level] if i not in excluded and padded[i].covers(landing)]
            if len(candidates) != 1 or landing.intersection(solids[level]).area > AREA_TOL_M2:
                issue("STAIR_LANDING_NOT_LINKED", stair["id"], level)
            else:
                connected.append(candidates[0])
        if len(connected) == 2:
            stair_links.append({"id": stair["id"], "spaces": connected})
            for room_id in connected:
                landings.setdefault(room_id, stair["id"])

    if profile is not None and "layout" in profile:
        _review_layout(project, profile, spaces, polygons, padded, solids, door_links, stair_links, issue)

    next_step = _stair_routes(graph, landings, transit)
    for room_id, room in rooms.items():
        if room_id not in landings and room_id not in next_step:
            issue("NO_STAIR_ROUTE", room_id)
            continue
        via_spaces, via_doors = [room_id], []
        while via_spaces[-1] in next_step:
            other, door = next_step[via_spaces[-1]]
            via_spaces.append(other)
            via_doors.append(door)
        room["toStair"] = {"spaces": via_spaces, "doors": via_doors, "stair": landings[via_spaces[-1]]}

    return {
        "check": "miki.plan-review/0.1", "projectId": project["id"], "revision": project["revision"],
        "passed": not issues, "lineToleranceM": LINE_TOL_M, "areaToleranceM2": AREA_TOL_M2,
        "rooms": rooms, "issues": issues, "doorLinks": door_links, "stairLinks": stair_links,
        "unlinkedDoors": unlinked,
    }


def compact_report(report, max_issues=8):
    """Small model-facing result; full evidence remains available locally."""
    rooms, issues = report["rooms"], report["issues"]
    return {
        **{key: report[key] for key in ("check", "projectId", "revision", "passed")},
        "areaTargetsChecked": sum(r["areaTargetM2"] is not None for r in rooms.values()),
        "stairRoutes": {"found": sum(r["toStair"] is not None for r in rooms.values()), "total": len(rooms)},
        "issues": issues[:max_issues], "issuesOmitted": max(0, len(issues) - max_issues),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--profile", type=Path, help="JSON profile with transitSpaces, areaTargetsM2 and/or layout")
    parser.add_argument("--compact", action="store_true", help="Output bounded issue details and summary counts")
    args = parser.parse_args(argv)
    try:
        profile = None
        if args.profile:
            if args.profile.stat().st_size > 1024 * 1024:
                raise WorkerError("invalid_plan_profile", "Profile exceeds 1 MiB.")
            profile = json.loads(args.profile.read_text(encoding="utf-8-sig"), object_pairs_hook=duplicate_keys)
            if profile is None:
                raise WorkerError("invalid_plan_profile", "Profile must be a JSON object.")
        result = review_plan(args.project, profile)
    except (WorkerError, OSError, ValueError, RecursionError) as error:
        print(json.dumps({"passed": False, "error": getattr(error, "code", "invalid_input"), "message": str(error)}))
        return 2
    output = (json.dumps(compact_report(result), separators=(",", ":"), allow_nan=False)
              if args.compact else json.dumps(result, indent=2, allow_nan=False))
    print(output)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
