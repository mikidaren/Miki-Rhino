namespace Miki.OpenBim;

public static partial class ProjectEngine
{
    public static BuildingProject CreateDemo()
    {
        var project = new BuildingProject(1, "TWO-STOREY", "Two-storey architectural concept", 0, "m",
            [new("L1", 0, 3.2), new("L2", 3.2, 3.2)],
            [new("GRID-A", [0, 0], [0, 8]), new("GRID-B", [6, 0], [6, 8]), new("GRID-C", [12, 0], [12, 8]),
             new("GRID-1", [0, 0], [12, 0]), new("GRID-2", [0, 4], [12, 4]), new("GRID-3", [0, 8], [12, 8])],
            [new("ROOM-L1-S", "L1", "Living zone", [[.24, .24], [5.8, .24], [5.8, 2.2], [.24, 2.2]]),
             new("ROOM-L1-N", "L1", "Studio zone", [[.24, 4.12], [11.76, 4.12], [11.76, 7.76], [.24, 7.76]]),
             new("ROOM-L2-S", "L2", "Bedroom zone", [[.24, .24], [5.8, .24], [5.8, 2.2], [.24, 2.2]]),
             new("ROOM-L2-N", "L2", "Study zone", [[.24, 4.12], [11.76, 4.12], [11.76, 7.76], [.24, 7.76]])],
            [new("WALL-L1-S", "L1", [0, 0], [12, 0], .24), new("WALL-L1-E", "L1", [12, 0], [12, 8], .24),
             new("WALL-L1-N", "L1", [12, 8], [0, 8], .24), new("WALL-L1-W", "L1", [0, 8], [0, 0], .24),
             new("WALL-L2-S", "L2", [0, 0], [12, 0], .24), new("WALL-L2-E", "L2", [12, 0], [12, 8], .24),
             new("WALL-L2-N", "L2", [12, 8], [0, 8], .24), new("WALL-L2-W", "L2", [0, 8], [0, 0], .24),
             new("WALL-L1-P", "L1", [0, 4], [12, 4], .24), new("WALL-L2-P", "L2", [0, 4], [12, 4], .24)],
            [new("SLAB-L1", "L1", [[0, 0], [12, 0], [12, 8], [0, 8]], .2),
             new BimSlab("SLAB-L2", "L2", [[0, 0], [12, 0], [12, 8], [0, 8]], .2)
             { Voids = [[[3.1, 2.4], [8, 2.4], [8, 3.7], [3.1, 3.7]]] }],
            [new("WIN-A", "window", "rectangular", 1.2, 1.4, .12),
             new("WIN-B", "window", "rectangular", .8, 1, .12),
             new("DOOR-A", "door", "rectangular", .9, 2.1, .12)],
            [new("WIN-L1-1", "WALL-L1-S", "WIN-A", 1.5, .9), new("WIN-L1-2", "WALL-L1-S", "WIN-A", 4.5, .9),
             new("WIN-L2-1", "WALL-L2-S", "WIN-A", 1.5, .9), new("WIN-L2-2", "WALL-L2-S", "WIN-A", 4.5, .9),
             new("WIN-L1-B", "WALL-L1-N", "WIN-B", 2, 1), new("WIN-L2-B", "WALL-L2-N", "WIN-B", 2, 1),
             new("DOOR-L1", "WALL-L1-S", "DOOR-A", 9.5, 0),
             new("DOOR-L1-P", "WALL-L1-P", "DOOR-A", 8.5, 0), new("DOOR-L2-P", "WALL-L2-P", "DOOR-A", 8.5, 0)],
            [new("STAIR-L1-L2", "L1", "L2", [3.2, 2.5], [1, 0], 1.1, 4.8, 18)]);
        Validate(project);
        return project;
    }
}
