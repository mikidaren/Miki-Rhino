"""Deterministic, single-loaded-corridor layout baseline. Standard library only."""

import argparse
from decimal import Decimal, DecimalException
from itertools import permutations, product
import json
from pathlib import Path
import re


# Geometry uses integer decimetres; clear areas use integer dm².
WALL = 2
DOOR = 9
MIN_ROOM_WIDTH = 15
MIN_ROOM_DEPTH = 20
MIN_BYPASS = 9
MAX_BRIEF_BYTES = 64 * 1024
FAMILY = "two_storey_single_loaded_corridor/1"


def strict_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key!r}.")
        result[key] = value
    return result


def decimal_number(token):
    try:
        value = Decimal(token)
    except DecimalException as error:
        raise ValueError("JSON number exponent is out of range.") from error
    if not value.is_finite():
        raise ValueError("JSON numeric values must be finite.")
    return value


def keys(obj, names, at):
    if not isinstance(obj, dict) or obj.keys() != set(names.split()):
        raise ValueError(f"{at} must be an object with these fields: {names}.")


def measure(value, scale, minimum, maximum, at):
    if type(value) not in (int, Decimal) or not minimum <= value <= maximum:
        raise ValueError(f"{at} must be a number in [{minimum}, {maximum}].")
    result = int(value * scale)
    if value != Decimal(result) / scale:
        raise ValueError(f"{at} must use increments of {1 / scale:g}.")
    return result


def identifier(value, at):
    if (not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value)
            or value.startswith("LB.")):
        raise ValueError(f"{at} must be an ASCII Warehouse ID outside the reserved LB. namespace.")
    return value


def title(value, at):
    if not isinstance(value, str) or not value.strip() or len(value.encode("utf-16-le")) > 512:
        raise ValueError(f"{at} must contain 1–256 UTF-16 characters and must not be blank.")
    return value


def load_brief(path):
    with path.open("rb") as source:
        raw = source.read(MAX_BRIEF_BYTES + 1)
    if len(raw) > MAX_BRIEF_BYTES:
        raise ValueError("Brief exceeds 64 KiB.")
    brief = json.loads(raw.decode("utf-8-sig"), parse_float=decimal_number,
                       parse_constant=decimal_number, object_pairs_hook=strict_object)
    keys(brief, "schemaVersion approved id name envelope corridor stair levels", "brief")
    if type(brief["schemaVersion"]) is not int or brief["schemaVersion"] != 1:
        raise ValueError("schemaVersion must be 1.")
    if brief["approved"] is not True:
        raise ValueError("approved must be true for the structured brief.")
    project_id = identifier(brief["id"], "id")
    name = title(brief["name"], "name")
    used = {project_id}

    envelope, corridor, stair = (brief[k] for k in ("envelope", "corridor", "stair"))
    keys(envelope, "widthM depthM", "envelope")
    keys(corridor, "pattern widthM", "corridor")
    keys(stair, "startXM widthM runM risers", "stair")
    if corridor["pattern"] != "all_rooms_direct":
        raise ValueError("corridor.pattern must be all_rooms_direct.")
    width, depth = (measure(envelope[k], 10, Decimal("0.1"), 30, f"envelope.{k}")
                    for k in ("widthM", "depthM"))
    hall_width = measure(corridor["widthM"], 10, Decimal("0.1"), 30, "corridor.widthM")
    start = measure(stair["startXM"], 10, 0, 30, "stair.startXM")
    flight_width = measure(stair["widthM"], 10, Decimal("0.1"), 30, "stair.widthM")
    run = measure(stair["runM"], 10, Decimal("0.1"), 30, "stair.runM")
    risers = stair["risers"]
    if type(risers) is not int or not 2 <= risers <= 50:
        raise ValueError("stair.risers must be an integer from 2 through 50.")

    if not isinstance(brief["levels"], list) or len(brief["levels"]) != 2:
        raise ValueError("levels must contain exactly two entries in bottom-to-top order.")
    levels = []
    for index, level in enumerate(brief["levels"]):
        at = f"levels[{index}]"
        keys(level, "heightM spaces requiredAdjacencies", at)
        height = measure(level["heightM"], 10, Decimal("2.2"), 6, f"{at}.heightM")
        if not isinstance(level["spaces"], list) or not 1 <= len(level["spaces"]) <= 5:
            raise ValueError(f"{at}.spaces must contain 1–5 named rooms.")
        rooms = {}
        for item, space in enumerate(level["spaces"]):
            label = f"{at}.spaces[{item}]"
            keys(space, "id name areaM2", label)
            room_id = identifier(space["id"], f"{label}.id")
            if room_id in used:
                raise ValueError(f"ID {room_id!r} is not globally unique.")
            used.add(room_id)
            keys(space["areaM2"], "min target max", f"{label}.areaM2")
            areas = tuple(measure(space["areaM2"][k], 100, Decimal("0.01"), 900, f"{label}.areaM2.{k}")
                          for k in ("min", "target", "max"))
            if not areas[0] <= areas[1] <= areas[2]:
                raise ValueError(f"{label}.areaM2 must satisfy min <= target <= max.")
            rooms[room_id] = (title(space["name"], f"{label}.name"), *areas)

        pairs = level["requiredAdjacencies"]
        if not isinstance(pairs, list) or len(pairs) > 10:
            raise ValueError(f"{at}.requiredAdjacencies must be a list of at most 10 room pairs.")
        edges = set()
        for pair in pairs:
            if (not isinstance(pair, list) or len(pair) != 2
                    or any(not isinstance(i, str) or i not in rooms for i in pair) or pair[0] == pair[1]):
                raise ValueError(f"{at}.requiredAdjacencies must pair two distinct IDs on the same level.")
            edge = tuple(sorted(pair))
            if edge in edges:
                raise ValueError(f"{at}.requiredAdjacencies repeats {list(edge)!r}.")
            edges.add(edge)
        levels.append((f"LB.L{index + 1}", height, rooms, edges))
    return project_id, name, width, depth, hall_width, (start, flight_width, run, risers), levels


def m(dm):
    return dm / 10


def m2(square_dm):
    return square_dm / 100


def solve_level(level, available, depth):
    level_id, _, rooms, edges = level
    ids = sorted(rooms)
    orders = [order for order in permutations(ids)
              if edges <= {tuple(sorted(pair)) for pair in zip(order, order[1:])}]
    issues = []
    if not orders:
        issues.append({"code": "REQUIRED_ADJACENCIES_NOT_A_ROW", "level": level_id,
                       "requiredAdjacencies": sorted(edges)})
    # Available length is the sum of clear room widths, excluding partition walls.
    budget = available - (len(ids) - 1) * WALL
    limits = []
    for room_id in ids:
        _, lo_area, target, hi_area = rooms[room_id]
        lo = max(MIN_ROOM_WIDTH, (lo_area + depth - 1) // depth)
        hi = hi_area // depth
        if lo > hi:
            issues.append({"code": "ROOM_AREA_HAS_NO_GRID_WIDTH", "level": level_id, "roomId": room_id,
                           "roomDepthM": m(depth), "minimumWidthM": m(MIN_ROOM_WIDTH),
                           "areaIntervalM2": [m2(lo_area), m2(hi_area)],
                           "firstWidthAtOrAboveMinimumM": m(lo), "lastWidthAtOrBelowMaximumM": m(hi)})
        limits.append((lo, hi, target))

    min_total = sum(lo for lo, _, _ in limits)
    max_total = sum(hi for _, hi, _ in limits)
    if all(lo <= hi for lo, hi, _ in limits):
        if budget < min_total:
            issues.append({"code": "ROOM_ROW_TOO_SHORT", "level": level_id, "availableClearWidthM": m(budget),
                           "minimumRequiredClearWidthM": m(min_total), "roomDepthM": m(depth)})
        elif budget > max_total:
            issues.append({"code": "ROOM_ROW_CANNOT_FILL", "level": level_id, "availableClearWidthM": m(budget),
                           "maximumAllowedClearWidthM": m(max_total), "roomDepthM": m(depth)})
    if issues:
        return None, issues

    # Best (area error, ID-ordered width tuple) at each used width. The same
    # best dimensions apply to all valid orders because their clear depth is fixed.
    states = {0: (0, ())}
    for lo, hi, target in limits:
        following = {}
        for used, (error, chosen) in states.items():
            for width in range(lo, min(hi, budget - used) + 1):
                total = used + width
                option = (error + abs(width * depth - target), (*chosen, width))
                if total not in following or option < following[total]:
                    following[total] = option
        states = following
    score, widths = states[budget]  # Integer intervals guarantee a split once the sum bounds pass.
    return (orders, dict(zip(ids, widths)), score), []


def point(x, y):
    return [m(x), m(y)]


def polygon(*pairs):
    return [point(x, y) for x, y in pairs]


def rect(x0, y0, x1, y1):
    return polygon((x0, y0), (x1, y0), (x1, y1), (x0, y1))


def export(brief, results, orders):
    project_id, name, width, depth, hall_width, stair, levels = brief
    start, stair_width, run, risers = stair
    end = start + run
    hall_y = WALL + hall_width
    room_y = hall_y + WALL
    half = WALL // 2
    project = {"schemaVersion": 1, "id": project_id, "name": name, "revision": 0, "units": "m",
               "levels": [], "grids": [], "spaces": [], "walls": [], "slabs": [],
               "types": [{"id": "LB.DOOR", "kind": "door", "form": "rectangular",
                          "width": m(DOOR), "height": 2.1, "depth": m(WALL)}],
               "openings": [], "stairs": [{"id": "LB.STAIR", "from": levels[0][0], "to": levels[1][0],
                                           "origin": point(start, WALL), "direction": [1, 0],
                                           "width": m(stair_width), "run": m(run), "risers": risers}]}
    profile = {"transitSpaces": [], "areaTargetsM2": {}}

    elevation = 0
    for index, ((level, height, rooms, _), (_, widths, _), order) in enumerate(zip(levels, results, orders)):
        def wall(label, x0, y0, x1, y1):
            wall_id = f"{level}.W.{label}"
            project["walls"].append({"id": wall_id, "level": level, "start": point(x0, y0),
                                     "end": point(x1, y1), "thickness": m(WALL)})
            return wall_id

        project["levels"].append({"id": level, "elevation": m(elevation), "height": m(height)})
        elevation += height
        project["slabs"].append({"id": f"{level}.SLAB", "level": level, "boundary": rect(0, 0, width, depth),
                                 "thickness": 0.2, "offset": 0,
                                 "voids": [rect(start, WALL, end, WALL + stair_width)] if index else []})
        wall("S", 0, half, width, half)
        wall("N", 0, depth - half, width, depth - half)
        wall("W", half, WALL, half, depth - WALL)
        wall("E", width - half, WALL, width - half, depth - WALL)
        spine = wall("SPINE", WALL, hall_y + half, width - WALL, hall_y + half)
        corridor_id = f"{level}.CORRIDOR"
        profile["transitSpaces"].append(corridor_id)
        # Reserve the flight on both levels; only the upper slab has an actual opening.
        corridor = polygon((WALL, WALL), (start, WALL), (start, WALL + stair_width),
                           (end, WALL + stair_width), (end, WALL), (width - WALL, WALL),
                           (width - WALL, hall_y), (WALL, hall_y))
        project["spaces"].append({"id": corridor_id, "level": level, "name": "Shared corridor",
                                  "boundary": corridor})

        left = WALL
        for n, room_id in enumerate(order, 1):
            right = left + widths[room_id]
            room_name, lo, _, hi = rooms[room_id]
            project["spaces"].append({"id": room_id, "level": level, "name": room_name,
                                      "boundary": rect(left, room_y, right, depth - WALL)})
            profile["areaTargetsM2"][room_id] = {"min": m2(lo), "max": m2(hi)}
            project["openings"].append({"id": f"{level}.DOOR.{n}", "wall": spine, "type": "LB.DOOR",
                                        "offset": (left + right - DOOR - 2 * WALL) / 20, "sill": 0})
            if n != len(order):
                wall(f"PART.{n}", right + half, room_y, right + half, depth - WALL)
            left = right + WALL
    return project, profile


def generate(brief):
    project_id, _, width, depth, hall_width, (start, stair_width, run, risers), levels = brief
    room_depth = depth - 3 * WALL - hall_width
    clear_span = width - 2 * WALL
    report = {"family": FAMILY, "projectId": project_id, "status": "infeasible", "candidateCount": 0,
              "issues": [], "candidates": []}
    issues = report["issues"]
    if room_depth < MIN_ROOM_DEPTH:
        issues.append({"code": "ROOM_BAND_TOO_SHALLOW", "availableRoomDepthM": m(room_depth),
                       "minimumRoomDepthM": m(MIN_ROOM_DEPTH)})
    if hall_width - stair_width < MIN_BYPASS:
        issues.append({"code": "STAIR_BYPASS_TOO_NARROW", "availableBypassM": m(hall_width - stair_width),
                       "minimumBypassM": m(MIN_BYPASS)})
    landing_span = (start - stair_width, start + run + stair_width)
    if landing_span[0] < WALL or landing_span[1] > width - WALL:
        issues.append({"code": "STAIR_LANDINGS_OUTSIDE_CORRIDOR", "landingSpanXM": list(map(m, landing_span)),
                       "corridorSpanXM": [m(WALL), m(width - WALL)]})
    if levels[0][1] > 10 * risers:
        issues.append({"code": "STAIR_RISE_EXCEEDS_SCHEMA_LIMIT", "riseM": m(levels[0][1]), "risers": risers})
    if issues:
        return report, None, None

    results = []
    for level in levels:
        result, problems = solve_level(level, clear_span, room_depth)
        results.append(result)
        issues.extend(problems)
    if issues:
        return report, None, None

    (orders1, _, _), (orders2, _, _) = results
    report.update(status="feasible", candidateCount=len(orders1) * len(orders2))
    for rank, orders in enumerate(product(orders1[:3], orders2[:3]), 1):
        candidate_levels = []
        for (level_id, _, rooms, _), (_, widths, score), order in zip(levels, results, orders):
            candidate_levels.append({"level": level_id, "roomOrder": order, "roomDepthM": m(room_depth),
                                     "clearWidthsM": {i: m(widths[i]) for i in order},
                                     "netAreasM2": {i: m2(widths[i] * room_depth) for i in order},
                                     "absoluteAreaErrorM2": m2(score)})
        report["candidates"].append({"rank": rank, "levels": candidate_levels})
        if rank == 3:
            break
    project, profile = export(brief, results, (orders1[0], orders2[0]))
    return report, project, profile


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--brief", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path, help="Fresh result directory; parent must exist.")
    args = parser.parse_args(argv)

    def status(**fields):
        print(json.dumps(fields, allow_nan=False))

    try:
        brief = load_brief(args.brief)
    except (OSError, ValueError, RecursionError) as error:
        status(status="invalid_input", message=str(error))
        return 1

    report, project, profile = generate(brief)
    try:
        args.out_dir.mkdir()
        files = {"report.json": report}
        if project is not None:
            files = {"project.json": project, "profile.json": profile, **files}
        for filename, data in files.items():
            (args.out_dir / filename).write_bytes((json.dumps(data, indent=2, allow_nan=False) + "\n").encode("utf-8"))
    except FileExistsError:
        status(status="invalid_input", message="--out-dir must not already exist.")
        return 1
    except OSError as error:
        status(status="io_error", message=str(error))
        return 1

    status(status=report["status"], projectId=report["projectId"], candidateCount=report["candidateCount"],
           issueCodes=[issue["code"] for issue in report["issues"]])
    return 0 if project is not None else 2


if __name__ == "__main__":
    raise SystemExit(main())
