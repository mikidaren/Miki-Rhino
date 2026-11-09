namespace Miki.OpenBim;

internal static class PlanGeometry
{
    internal const double Epsilon = 1e-8;

    internal static double Length(double[] start, double[] end)
    {
        var deltaX = end[0] - start[0];
        var deltaY = end[1] - start[1];
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static double Cross(double[] start, double[] end, double[] point) =>
        (end[0] - start[0]) * (point[1] - start[1]) - (end[1] - start[1]) * (point[0] - start[0]);

    private static int Side(double[] start, double[] end, double[] point)
    {
        var value = Cross(start, end, point);
        var tolerance = Epsilon * Math.Max(1, Length(start, end));
        return Math.Abs(value) <= tolerance ? 0 : Math.Sign(value);
    }

    private static bool OnSegment(double[] start, double[] end, double[] point) =>
        Side(start, end, point) == 0
        && point[0] >= Math.Min(start[0], end[0]) - Epsilon
        && point[0] <= Math.Max(start[0], end[0]) + Epsilon
        && point[1] >= Math.Min(start[1], end[1]) - Epsilon
        && point[1] <= Math.Max(start[1], end[1]) + Epsilon;

    internal static bool SegmentsIntersect(double[] firstStart, double[] firstEnd, double[] secondStart, double[] secondEnd, bool proper = false)
    {
        var firstSide = Side(firstStart, firstEnd, secondStart);
        var secondSide = Side(firstStart, firstEnd, secondEnd);
        var thirdSide = Side(secondStart, secondEnd, firstStart);
        var fourthSide = Side(secondStart, secondEnd, firstEnd);
        if (firstSide * secondSide < 0 && thirdSide * fourthSide < 0) return true;
        if (proper) return false;
        return (firstSide == 0 && OnSegment(firstStart, firstEnd, secondStart))
            || (secondSide == 0 && OnSegment(firstStart, firstEnd, secondEnd))
            || (thirdSide == 0 && OnSegment(secondStart, secondEnd, firstStart))
            || (fourthSide == 0 && OnSegment(secondStart, secondEnd, firstEnd));
    }

    internal static bool IsSimple(double[][] polygon)
    {
        var area = 0.0;
        for (var index = 0; index < polygon.Length; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Length];
            if (Length(start, end) <= Epsilon) return false;
            area += Cross(polygon[0], start, end);
            var previous = polygon[(index + polygon.Length - 1) % polygon.Length];
            if (Side(previous, start, end) == 0
                && (previous[0] - start[0]) * (end[0] - start[0]) + (previous[1] - start[1]) * (end[1] - start[1]) > Epsilon * Epsilon)
                return false;
            for (var other = index + 1; other < polygon.Length; other++)
            {
                if (other == index + 1 || (index == 0 && other == polygon.Length - 1)) continue;
                if (SegmentsIntersect(start, end, polygon[other], polygon[(other + 1) % polygon.Length])) return false;
            }
        }
        return Math.Abs(area) > Epsilon;
    }

    internal static int Locate(double[][] polygon, double[] point)
    {
        var inside = false;
        for (var index = 0; index < polygon.Length; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Length];
            if (OnSegment(start, end, point)) return 0;
            if ((start[1] > point[1]) != (end[1] > point[1])
                && point[0] < (end[0] - start[0]) * (point[1] - start[1]) / (end[1] - start[1]) + start[0])
                inside = !inside;
        }
        return inside ? 1 : -1;
    }

    internal static bool Contains(double[][] outer, double[][] inner, bool allowBoundary)
    {
        for (var index = 0; index < inner.Length; index++)
        {
            var start = inner[index];
            var end = inner[(index + 1) % inner.Length];
            var location = Locate(outer, start);
            if (location < 0 || (!allowBoundary && location == 0)) return false;
            if (!SegmentCovered(outer, start, end, allowBoundary)) return false;
        }
        return true;
    }

    private static bool SegmentCovered(double[][] polygon, double[] start, double[] end, bool allowBoundary)
    {
        var parameters = new List<double> { 0, 1 };
        var deltaX = end[0] - start[0];
        var deltaY = end[1] - start[1];
        var squaredLength = deltaX * deltaX + deltaY * deltaY;
        for (var index = 0; index < polygon.Length; index++)
        {
            var edgeStart = polygon[index];
            var edgeEnd = polygon[(index + 1) % polygon.Length];
            if (!SegmentsIntersect(start, end, edgeStart, edgeEnd)) continue;
            if (!allowBoundary) return false;
            var edgeX = edgeEnd[0] - edgeStart[0];
            var edgeY = edgeEnd[1] - edgeStart[1];
            var denominator = deltaX * edgeY - deltaY * edgeX;
            if (Math.Abs(denominator) > Epsilon * Epsilon)
            {
                var parameter = ((edgeStart[0] - start[0]) * edgeY - (edgeStart[1] - start[1]) * edgeX) / denominator;
                parameters.Add(Math.Clamp(parameter, 0, 1));
            }
            foreach (var point in new[] { edgeStart, edgeEnd })
            {
                if (OnSegment(start, end, point))
                    parameters.Add(Math.Clamp(((point[0] - start[0]) * deltaX + (point[1] - start[1]) * deltaY) / squaredLength, 0, 1));
            }
        }
        parameters.Sort();
        for (var index = 1; index < parameters.Count; index++)
        {
            var parameter = (parameters[index - 1] + parameters[index]) / 2;
            var location = Locate(polygon, [start[0] + parameter * deltaX, start[1] + parameter * deltaY]);
            if (location < 0 || (!allowBoundary && location == 0)) return false;
        }
        return true;
    }

    internal static bool TouchOrOverlap(double[][] first, double[][] second)
    {
        if (Locate(first, second[0]) >= 0 || Locate(second, first[0]) >= 0) return true;
        return EdgesCross(first, second, proper: false);
    }

    private static bool EdgesCross(double[][] first, double[][] second, bool proper)
    {
        for (var firstIndex = 0; firstIndex < first.Length; firstIndex++)
            for (var secondIndex = 0; secondIndex < second.Length; secondIndex++)
                if (SegmentsIntersect(first[firstIndex], first[(firstIndex + 1) % first.Length],
                    second[secondIndex], second[(secondIndex + 1) % second.Length], proper)) return true;
        return false;
    }

    internal static double[][] StairRectangle(BimStair stair, double startDistance, double endDistance)
    {
        var magnitude = Math.Sqrt(stair.Direction[0] * stair.Direction[0] + stair.Direction[1] * stair.Direction[1]);
        var unitX = stair.Direction[0] / magnitude;
        var unitY = stair.Direction[1] / magnitude;
        var start = new[] { stair.Origin[0] + unitX * startDistance, stair.Origin[1] + unitY * startDistance };
        var end = new[] { stair.Origin[0] + unitX * endDistance, stair.Origin[1] + unitY * endDistance };
        return [start, end, [end[0] - unitY * stair.Width, end[1] + unitX * stair.Width],
            [start[0] - unitY * stair.Width, start[1] + unitX * stair.Width]];
    }

    internal static bool Supports(BimSlab slab, double[][] rectangle)
    {
        if (!Contains(slab.Boundary, rectangle, allowBoundary: true)) return false;
        return slab.Voids.All(hole => ClippedArea(hole, rectangle) <= Epsilon);
    }

    internal static bool IntersectsSlab(BimSlab slab, double[][] rectangle)
    {
        var area = ClippedArea(slab.Boundary, rectangle);
        foreach (var hole in slab.Voids) area -= ClippedArea(hole, rectangle);
        return area > Epsilon;
    }

    private static double ClippedArea(double[][] polygon, double[][] rectangle)
    {
        var vertices = polygon.ToList();
        for (var edge = 0; edge < rectangle.Length && vertices.Count > 0; edge++)
        {
            var start = rectangle[edge];
            var end = rectangle[(edge + 1) % rectangle.Length];
            var clipped = new List<double[]>();
            var previous = vertices[^1];
            var previousDistance = Cross(start, end, previous);
            foreach (var current in vertices)
            {
                var currentDistance = Cross(start, end, current);
                if ((previousDistance >= 0) != (currentDistance >= 0))
                {
                    var parameter = previousDistance / (previousDistance - currentDistance);
                    clipped.Add([previous[0] + parameter * (current[0] - previous[0]),
                        previous[1] + parameter * (current[1] - previous[1])]);
                }
                if (currentDistance >= 0) clipped.Add(current);
                previous = current;
                previousDistance = currentDistance;
            }
            vertices = clipped;
        }
        if (vertices.Count < 3) return 0;
        var area = 0.0;
        for (var index = 1; index < vertices.Count - 1; index++)
            area += Cross(vertices[0], vertices[index], vertices[index + 1]);
        return Math.Abs(area) / 2;
    }
}
