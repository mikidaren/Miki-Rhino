using Rhino;
using Rhino.Geometry;

namespace Miki.OpenBim.Rhino;

internal sealed record NativePart(string Id, string SourceId, string Kind, string Level,
    GeometryBase? Geometry, double ExpectedVolume = 0, string? Type = null,
    string? Host = null, Transform Placement = default);

internal static class NativeGeometry
{
    internal static List<NativePart> Build(BuildingProject project, double scale, double tolerance)
    {
        var parts = new List<NativePart>();
        var levels = project.Levels.ToDictionary(level => level.Id);
        var types = project.Types.ToDictionary(type => type.Id);
        foreach (var grid in project.Grids)
            parts.Add(new NativePart(grid.Id, grid.Id, "grid", "site",
                new LineCurve(Point(grid.Start, 0, scale), Point(grid.End, 0, scale))));
        foreach (var space in project.Spaces)
            parts.Add(new NativePart(space.Id, space.Id, "space-plan", space.Level,
                Polygon(space.Boundary, levels[space.Level].Elevation + .01, scale)));
        foreach (var wall in project.Walls)
        {
            var level = levels[wall.Level];
            double length = Length(wall.Start, wall.End);
            double height = wall.Height ?? level.Height;
            var plane = Placement(wall.Start, wall.End[0] - wall.Start[0], wall.End[1] - wall.Start[1], level.Elevation, scale);
            var body = Box(plane, 0, length, -wall.Thickness / 2, wall.Thickness / 2, 0, height, scale);
            var holes = project.Openings.Where(opening => opening.Wall == wall.Id).ToArray();
            double removed = 0;
            if (holes.Length > 0)
            {
                double margin = Math.Max(.01, tolerance / scale * 10);
                var cutters = holes.Select(opening =>
                {
                    var type = types[opening.Type];
                    removed += type.Width * type.Height * wall.Thickness;
                    return Box(plane, opening.Offset, opening.Offset + type.Width,
                        -wall.Thickness / 2 - margin, wall.Thickness / 2 + margin,
                        opening.Sill == 0 ? -margin : opening.Sill,
                        opening.Sill + type.Height >= height ? height + margin : opening.Sill + type.Height, scale);
                }).ToArray();
                body = Cut(body, cutters, tolerance, wall.Id);
            }
            double expected = length * wall.Thickness * height - removed;
            RequireVolume(body, expected * Math.Pow(scale, 3), tolerance, wall.Id);
            parts.Add(new NativePart(wall.Id, wall.Id, "wall", wall.Level, body, expected));
            var footprint = new[]
            {
                plane.PointAt(0, -wall.Thickness * scale / 2, .005 * scale),
                plane.PointAt(length * scale, -wall.Thickness * scale / 2, .005 * scale),
                plane.PointAt(length * scale, wall.Thickness * scale / 2, .005 * scale),
                plane.PointAt(0, wall.Thickness * scale / 2, .005 * scale),
                plane.PointAt(0, -wall.Thickness * scale / 2, .005 * scale),
            };
            parts.Add(new NativePart(wall.Id + "/plan", wall.Id, "wall-plan", wall.Level, new PolylineCurve(footprint)));
        }
        foreach (var slab in project.Slabs)
        {
            double top = levels[slab.Level].Elevation + slab.Offset;
            var outer = Polygon(slab.Boundary, top, scale);
            var extrusion = Extrusion.Create(outer, -slab.Thickness * scale, true)
                ?? throw new InvalidOperationException("无法拉伸楼板：" + slab.Id);
            var body = extrusion.ToBrep();
            double area = Area(slab.Boundary);
            if (slab.Voids is { Length: > 0 })
            {
                double margin = Math.Max(.01, tolerance / scale * 10);
                var cutters = slab.Voids.Select(boundary =>
                {
                    area -= Area(boundary);
                    var profile = Polygon(boundary, top + margin, scale);
                    return (Extrusion.Create(profile, -(slab.Thickness + 2 * margin) * scale, true)
                        ?? throw new InvalidOperationException("无法构建楼板洞口：" + slab.Id)).ToBrep();
                }).ToArray();
                body = Cut(body, cutters, tolerance, slab.Id);
            }
            double expected = area * slab.Thickness;
            RequireVolume(body, expected * Math.Pow(scale, 3), tolerance, slab.Id);
            parts.Add(new NativePart(slab.Id, slab.Id, "slab", slab.Level, body, expected));
            parts.Add(new NativePart(slab.Id + "/plan", slab.Id, "slab-plan", slab.Level, outer));
        }
        foreach (var opening in project.Openings)
        {
            var wall = project.Walls.Single(item => item.Id == opening.Wall);
            var level = levels[wall.Level];
            var type = types[opening.Type];
            double length = Length(wall.Start, wall.End);
            double directionX = (wall.End[0] - wall.Start[0]) / length;
            double directionY = (wall.End[1] - wall.Start[1]) / length;
            double[] origin = [wall.Start[0] + directionX * opening.Offset, wall.Start[1] + directionY * opening.Offset];
            var plane = Placement(origin, directionX, directionY, level.Elevation + opening.Sill, scale);
            var transform = Transform.PlaneToPlane(Plane.WorldXY, plane);
            parts.Add(new NativePart(opening.Id, opening.Id, type.Kind, wall.Level, null,
                BlockVolume(type), type.Id, wall.Id, transform));
            var markPlane = Placement(origin, directionX, directionY, level.Elevation + .02, scale);
            parts.Add(new NativePart(opening.Id + "/plan", opening.Id, "opening-plan", wall.Level,
                new LineCurve(markPlane.PointAt(0, 0), markPlane.PointAt(type.Width * scale, 0)), Host: wall.Id));
        }
        foreach (var stair in project.Stairs)
        {
            double lower = levels[stair.From].Elevation;
            double rise = levels[stair.To].Elevation - lower;
            var plane = Placement(stair.Origin, stair.Direction[0], stair.Direction[1], lower, scale);
            for (int index = 0; index < stair.Risers; index++)
            {
                double depth = stair.Run / stair.Risers;
                double top = (index + 1) * rise / stair.Risers;
                parts.Add(new NativePart(stair.Id + "/step-" + (index + 1), stair.Id, "stair", stair.From,
                    Box(plane, index * depth, (index + 1) * depth, 0, stair.Width, 0, top, scale), depth * stair.Width * top));
            }
            var footprint = new[] { plane.Origin, plane.PointAt(stair.Run * scale, 0),
                plane.PointAt(stair.Run * scale, stair.Width * scale), plane.PointAt(0, stair.Width * scale), plane.Origin };
            parts.Add(new NativePart(stair.Id + "/plan", stair.Id, "stair-plan", stair.From, new PolylineCurve(footprint)));
        }
        if (parts.Any(part => part.Geometry is { IsValid: false }))
            throw new InvalidOperationException("参数生成了无效的原生几何，尚未修改文档。");
        return parts;
    }

    internal static GeometryBase[] Block(BimType type, double scale)
    {
        double frame = Math.Min(.06, Math.Min(type.Width / 4, type.Height / 4));
        double panel = Math.Min(type.Kind == "door" ? .04 : .012, type.Depth);
        return
        [
            Box(Plane.WorldXY, 0, type.Width, -type.Depth / 2, type.Depth / 2, 0, frame, scale),
            Box(Plane.WorldXY, 0, type.Width, -type.Depth / 2, type.Depth / 2, type.Height - frame, type.Height, scale),
            Box(Plane.WorldXY, 0, frame, -type.Depth / 2, type.Depth / 2, frame, type.Height - frame, scale),
            Box(Plane.WorldXY, type.Width - frame, type.Width, -type.Depth / 2, type.Depth / 2, frame, type.Height - frame, scale),
            Box(Plane.WorldXY, frame, type.Width - frame, -panel / 2, panel / 2, frame, type.Height - frame, scale),
        ];
    }

    internal static double BlockVolume(BimType type)
    {
        double frame = Math.Min(.06, Math.Min(type.Width / 4, type.Height / 4));
        double panel = Math.Min(type.Kind == "door" ? .04 : .012, type.Depth);
        return (2 * type.Width * frame + 2 * (type.Height - 2 * frame) * frame) * type.Depth +
            (type.Width - 2 * frame) * (type.Height - 2 * frame) * panel;
    }

    internal static double Volume(GeometryBase geometry)
    {
        if (geometry is Brep brep)
        {
            using var properties = VolumeMassProperties.Compute(brep);
            return properties?.Volume ?? double.NaN;
        }
        return 0;
    }

    private static void RequireVolume(Brep brep, double expected, double tolerance, string id)
    {
        double actual = Volume(brep);
        if (!brep.IsValid || !brep.IsSolid || !double.IsFinite(actual) ||
            Math.Abs(actual - expected) > Math.Max(Math.Abs(expected) * 1e-6, Math.Pow(tolerance, 3) * 10))
            throw new InvalidOperationException("实体或真实洞口体积检查未通过：" + id);
    }

    private static Brep Cut(Brep body, Brep[] cutters, double tolerance, string id)
    {
        var result = Brep.CreateBooleanDifference([body], cutters, tolerance);
        if (result is not { Length: 1 } || !result[0].IsSolid)
            throw new InvalidOperationException("无法生成连续且封闭的带洞实体：" + id);
        return result[0];
    }

    private static Brep Box(Plane plane, double minX, double maxX, double minY, double maxY,
        double minZ, double maxZ, double scale) => new Box(plane, new Interval(minX * scale, maxX * scale),
            new Interval(minY * scale, maxY * scale), new Interval(minZ * scale, maxZ * scale)).ToBrep();

    private static Plane Placement(double[] origin, double directionX, double directionY, double elevation, double scale)
    {
        var axis = new Vector3d(directionX, directionY, 0);
        if (!axis.Unitize()) throw new InvalidOperationException("方向不能为零。");
        return new Plane(Point(origin, elevation, scale), axis, new Vector3d(-axis.Y, axis.X, 0));
    }

    private static Point3d Point(double[] point, double elevation, double scale) =>
        new(point[0] * scale, point[1] * scale, elevation * scale);

    private static PolylineCurve Polygon(double[][] boundary, double elevation, double scale)
    {
        var points = boundary.Select(point => Point(point, elevation, scale)).ToList();
        if (points[0].DistanceTo(points[^1]) > 1e-9) points.Add(points[0]);
        return new PolylineCurve(points);
    }

    private static double Length(double[] start, double[] end) =>
        Math.Sqrt(Math.Pow(end[0] - start[0], 2) + Math.Pow(end[1] - start[1], 2));

    private static double Area(double[][] boundary) => Math.Abs(boundary.Select((point, index) =>
        point[0] * boundary[(index + 1) % boundary.Length][1] - point[1] * boundary[(index + 1) % boundary.Length][0]).Sum()) / 2;
}
