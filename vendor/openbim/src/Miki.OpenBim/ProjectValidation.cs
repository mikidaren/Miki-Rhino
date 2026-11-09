namespace Miki.OpenBim;

internal static class ProjectValidation
{
    internal const int MaxParts = 10000;
    private const int MaxVertices = 1024;
    private const double MaxCoordinate = 1000000;
    private const double MaxLength = 10000;

    internal static bool IsId(string? value) => value is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    internal static void Validate(BuildingProject project)
    {
        if (project is null) throw new InvalidOperationException("Project is required.");
        Require(project.SchemaVersion == 1, "schemaVersion must be 1.");
        Require(project.Units == "m", "Source units must be m.");
        Require(project.Revision >= 0, "Revision must be nonnegative.");
        Name(project.Name, "Project name");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddId(project.Id, ids);
        Collection(project.Levels, level => level.Id, ids, "levels");
        Collection(project.Grids, grid => grid.Id, ids, "grids");
        Collection(project.Spaces, space => space.Id, ids, "spaces");
        Collection(project.Walls, wall => wall.Id, ids, "walls");
        Collection(project.Slabs, slab => slab.Id, ids, "slabs");
        Collection(project.Types, type => type.Id, ids, "types");
        Collection(project.Openings, opening => opening.Id, ids, "openings");
        Collection(project.Stairs, stair => stair.Id, ids, "stairs");
        Require(project.Levels.Length > 0, "At least one level is required.");
        foreach (var stair in project.Stairs)
            Require(stair.Risers is >= 2 and <= 1000, $"{stair.Id}.risers must be between 2 and 1000.");
        var generatedParts = ProjectEngine.EstimateGeneratedParts(project);
        Require(generatedParts <= ProjectEngine.MaxGeneratedParts,
            $"Project estimates {generatedParts} generated parts. This pilot is limited to {ProjectEngine.MaxGeneratedParts}.");
        var levels = project.Levels.ToDictionary(level => level.Id, StringComparer.Ordinal);
        var walls = project.Walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var types = project.Types.ToDictionary(type => type.Id, StringComparer.Ordinal);
        foreach (var level in project.Levels)
        {
            Coordinate(level.Elevation, $"{level.Id}.elevation");
            Positive(level.Height, $"{level.Id}.height");
            Coordinate(level.Elevation + level.Height, $"{level.Id} top elevation");
        }
        foreach (var grid in project.Grids) Baseline(grid.Start, grid.End, grid.Id);
        foreach (var space in project.Spaces)
        {
            Reference(levels, space.Level, space.Id);
            Name(space.Name, $"{space.Id}.name");
            Polygon(space.Boundary, space.Id);
        }
        foreach (var wall in project.Walls)
        {
            var level = Reference(levels, wall.Level, wall.Id);
            Baseline(wall.Start, wall.End, wall.Id);
            Positive(wall.Thickness, $"{wall.Id}.thickness");
            if (wall.Height.HasValue) Positive(wall.Height.Value, $"{wall.Id}.height");
            Coordinate(level.Elevation + (wall.Height ?? level.Height), $"{wall.Id} top elevation");
        }
        foreach (var slab in project.Slabs)
        {
            var level = Reference(levels, slab.Level, slab.Id);
            Polygon(slab.Boundary, slab.Id);
            Positive(slab.Thickness, $"{slab.Id}.thickness");
            Bounded(slab.Offset, -MaxLength, MaxLength, $"{slab.Id}.offset");
            Coordinate(level.Elevation + slab.Offset, $"{slab.Id} top elevation");
            Coordinate(level.Elevation + slab.Offset - slab.Thickness, $"{slab.Id} bottom elevation");
            Require(slab.Voids is { Length: <= 64 }, $"{slab.Id}.voids must contain at most 64 polygons.");
            for (var index = 0; index < slab.Voids.Length; index++)
            {
                var hole = slab.Voids[index];
                Polygon(hole, $"{slab.Id}.voids[{index}]");
                Require(PlanGeometry.Contains(slab.Boundary, hole, allowBoundary: false), $"{slab.Id} void must lie strictly inside the slab.");
                for (var previous = 0; previous < index; previous++)
                    Require(!PlanGeometry.TouchOrOverlap(hole, slab.Voids[previous]), $"{slab.Id} voids must be disjoint.");
            }
        }
        foreach (var type in project.Types)
        {
            Require(type.Kind is "window" or "door", $"{type.Id}.kind must be window or door.");
            Require(type.Form == "rectangular", $"{type.Id}.form must be rectangular.");
            Positive(type.Width, $"{type.Id}.width");
            Positive(type.Height, $"{type.Id}.height");
            Positive(type.Depth, $"{type.Id}.depth");
        }
        foreach (var opening in project.Openings)
        {
            var wall = Reference(walls, opening.Wall, opening.Id);
            var type = Reference(types, opening.Type, opening.Id);
            Bounded(opening.Offset, 0, MaxLength, $"{opening.Id}.offset");
            Bounded(opening.Sill, 0, MaxLength, $"{opening.Id}.sill");
            Require(opening.Offset + type.Width <= PlanGeometry.Length(wall.Start, wall.End) + PlanGeometry.Epsilon,
                $"{opening.Id} exceeds its wall length.");
            Require(opening.Sill + type.Height <= (wall.Height ?? levels[wall.Level].Height) + PlanGeometry.Epsilon,
                $"{opening.Id} exceeds its wall height.");
        }
        foreach (var group in project.Openings.GroupBy(opening => opening.Wall, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(opening => opening.Offset).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var first = ordered[index];
                var firstType = types[first.Type];
                for (var next = index + 1; next < ordered.Length; next++)
                {
                    var second = ordered[next];
                    if (second.Offset >= first.Offset + firstType.Width - PlanGeometry.Epsilon) break;
                    var secondType = types[second.Type];
                    Require(Math.Min(first.Sill + firstType.Height, second.Sill + secondType.Height)
                        - Math.Max(first.Sill, second.Sill) <= PlanGeometry.Epsilon,
                        $"Openings '{first.Id}' and '{second.Id}' overlap.");
                }
            }
        }
        foreach (var stair in project.Stairs) Stair(stair, levels, project.Slabs);
    }

    private static void Stair(BimStair stair, Dictionary<string, BimLevel> levels, BimSlab[] slabs)
    {
        var from = Reference(levels, stair.From, stair.Id);
        var to = Reference(levels, stair.To, stair.Id);
        var rise = to.Elevation - from.Elevation;
        Positive(rise, $"{stair.Id} rise");
        Point(stair.Origin, $"{stair.Id}.origin");
        Point(stair.Direction, $"{stair.Id}.direction");
        Require(PlanGeometry.Length([0, 0], stair.Direction) > PlanGeometry.Epsilon, $"{stair.Id} direction must be nonzero.");
        Positive(stair.Width, $"{stair.Id}.width");
        Positive(stair.Run, $"{stair.Id}.run");
        Require(rise / stair.Risers > PlanGeometry.Epsilon && rise / stair.Risers <= 1,
            $"{stair.Id} riser height must be positive and at most 1 m.");
        Require(stair.Run / stair.Risers > PlanGeometry.Epsilon, $"{stair.Id} tread must be positive.");
        var flight = PlanGeometry.StairRectangle(stair, 0, stair.Run);
        var bottom = PlanGeometry.StairRectangle(stair, -stair.Width, 0);
        var landing = PlanGeometry.StairRectangle(stair, stair.Run, stair.Run + stair.Width);
        foreach (var point in flight.Concat(bottom).Concat(landing)) Point(point, $"{stair.Id} footprint");
        Require(slabs.Any(slab => slab.Level == stair.From && Math.Abs(slab.Offset) <= PlanGeometry.Epsilon
            && PlanGeometry.Supports(slab, bottom)), $"{stair.Id} needs a supported lower landing at its source elevation.");
        var upperSlabs = slabs.Where(slab => slab.Level == stair.To && Math.Abs(slab.Offset) <= PlanGeometry.Epsilon).ToArray();
        Require(upperSlabs.Any(slab => slab.Voids.Any(hole => PlanGeometry.Contains(hole, flight, allowBoundary: true))
            && PlanGeometry.Supports(slab, landing)), $"{stair.Id} needs an upper slab void covering the flight and a reachable landing.");
        foreach (var slab in slabs)
        {
            var top = levels[slab.Level].Elevation + slab.Offset;
            var bottomElevation = top - slab.Thickness;
            if (top <= from.Elevation + PlanGeometry.Epsilon || bottomElevation >= to.Elevation - PlanGeometry.Epsilon) continue;
            for (var index = 0; index < stair.Risers; index++)
            {
                var stepTop = from.Elevation + rise * (index + 1) / stair.Risers;
                if (bottomElevation >= stepTop - PlanGeometry.Epsilon) continue;
                var tread = PlanGeometry.StairRectangle(stair, stair.Run * index / stair.Risers, stair.Run * (index + 1) / stair.Risers);
                Require(!PlanGeometry.IntersectsSlab(slab, tread), $"{stair.Id} flight intersects slab '{slab.Id}'.");
            }
        }
    }

    private static void Collection<T>(T[]? entries, Func<T, string> getId, HashSet<string> ids, string label) where T : class
    {
        Require(entries is not null, $"{label} must be an array.");
        Require(entries!.Length <= MaxParts && ids.Count - 1 + entries.Length <= MaxParts, $"Project is limited to {MaxParts} parts.");
        foreach (var entry in entries)
        {
            Require(entry is not null, $"{label} entries must be objects.");
            AddId(getId(entry!), ids);
        }
    }

    private static void AddId(string? id, HashSet<string> ids)
    {
        Require(IsId(id), "IDs must contain 1 to 128 ASCII letters, digits, dots, underscores or hyphens and start with a letter or digit.");
        Require(ids.Add(id!), $"Duplicate project id '{id}'.");
    }

    private static T Reference<T>(Dictionary<string, T> entries, string? id, string owner)
    {
        if (id is null || !entries.TryGetValue(id, out var result))
            throw new InvalidOperationException($"{owner} references missing id '{id}'.");
        return result;
    }

    private static void Name(string? value, string label) =>
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 256, $"{label} must contain 1 to 256 characters.");

    private static void Bounded(double value, double minimum, double maximum, string label) =>
        Require(double.IsFinite(value) && value >= minimum && value <= maximum, $"{label} must be finite and between {minimum} and {maximum}.");

    private static void Coordinate(double value, string label) => Bounded(value, -MaxCoordinate, MaxCoordinate, label);

    private static void Positive(double value, string label) =>
        Require(double.IsFinite(value) && value > PlanGeometry.Epsilon && value <= MaxLength,
            $"{label} must be greater than {PlanGeometry.Epsilon} and at most {MaxLength} m.");

    private static void Point(double[]? point, string label)
    {
        Require(point is { Length: 2 }, $"{label} must be [x,y].");
        Coordinate(point![0], label);
        Coordinate(point[1], label);
    }

    private static void Baseline(double[] start, double[] end, string label)
    {
        Point(start, $"{label}.start");
        Point(end, $"{label}.end");
        Positive(PlanGeometry.Length(start, end), $"{label} length");
    }

    private static void Polygon(double[][]? polygon, string label)
    {
        Require(polygon is { Length: >= 3 and <= MaxVertices }, $"{label} must have 3 to {MaxVertices} vertices.");
        foreach (var point in polygon!) Point(point, label);
        Require(PlanGeometry.IsSimple(polygon), $"{label} must be a nondegenerate simple polygon without repeated closing vertices.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
