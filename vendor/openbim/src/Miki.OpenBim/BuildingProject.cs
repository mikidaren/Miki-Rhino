using System.Text.Json.Serialization;

namespace Miki.OpenBim;

public sealed record BuildingProject(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] int Revision,
    [property: JsonRequired] string Units,
    [property: JsonRequired] BimLevel[] Levels,
    [property: JsonRequired] BimGrid[] Grids,
    [property: JsonRequired] BimSpace[] Spaces,
    [property: JsonRequired] BimWall[] Walls,
    [property: JsonRequired] BimSlab[] Slabs,
    [property: JsonRequired] BimType[] Types,
    [property: JsonRequired] BimOpening[] Openings,
    [property: JsonRequired] BimStair[] Stairs);

public sealed record BimLevel(
    [property: JsonRequired] string Id,
    [property: JsonRequired] double Elevation,
    [property: JsonRequired] double Height);

public sealed record BimGrid(
    [property: JsonRequired] string Id,
    [property: JsonRequired] double[] Start,
    [property: JsonRequired] double[] End);

public sealed record BimSpace(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Level,
    [property: JsonRequired] string Name,
    [property: JsonRequired] double[][] Boundary);

public sealed record BimWall(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Level,
    [property: JsonRequired] double[] Start,
    [property: JsonRequired] double[] End,
    [property: JsonRequired] double Thickness,
    double? Height = null);

public sealed record BimSlab(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Level,
    [property: JsonRequired] double[][] Boundary,
    [property: JsonRequired] double Thickness,
    double Offset = 0)
{
    public double[][][] Voids { get; init; } = [];
}

public sealed record BimType(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Form,
    [property: JsonRequired] double Width,
    [property: JsonRequired] double Height,
    double Depth = .12);

public sealed record BimOpening(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Wall,
    [property: JsonRequired] string Type,
    [property: JsonRequired] double Offset,
    [property: JsonRequired] double Sill);

public sealed record BimStair(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string From,
    [property: JsonRequired] string To,
    [property: JsonRequired] double[] Origin,
    [property: JsonRequired] double[] Direction,
    [property: JsonRequired] double Width,
    [property: JsonRequired] double Run,
    [property: JsonRequired] int Risers);
