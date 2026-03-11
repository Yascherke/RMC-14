using System;
using System.Collections.Generic;

namespace Content.MapRenderer;

public sealed class MapExportManifest
{
    public DateTime GeneratedUtc { get; set; }
    public List<MapExportManifestMap> Maps { get; set; } = new();
}

public sealed class MapExportManifestMap
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
}

public sealed class MapExport
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<MapExportGrid> Grids { get; set; } = new();
}

public sealed class MapExportGrid
{
    public string GridId { get; set; } = string.Empty;
    public bool HasMapBounds { get; set; }
    public MapBounds Bounds { get; set; } = new(0, 0, 0, 0);
    public MapBounds RenderBounds { get; set; } = new(0, 0, 0, 0);
    public MapRenderImage? Image { get; set; }
    public List<MapColorCell> Colors { get; set; } = new();
    public List<MapAreaCell> Areas { get; set; } = new();
    public List<MapLabelCell> Labels { get; set; } = new();
    public List<MapEntityCell> Entities { get; set; } = new();
    public List<string> AreaIds { get; set; } = new();
    public List<MapAreaInfo> AreaInfo { get; set; } = new();
}

public readonly record struct MapBounds(int MinX, int MinY, int MaxX, int MaxY);
public readonly record struct MapColorCell(int X, int Y, uint C);
public readonly record struct MapAreaCell(int X, int Y, int A);
public readonly record struct MapLabelCell(int X, int Y, string T);
public readonly record struct MapEntityCell(int X, int Y, List<MapEntityInfo> Entities);

public sealed class MapRenderImage
{
    public string File { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int PixelsPerTile { get; set; }
}

public sealed class MapEntityInfo
{
    public string Name { get; set; } = string.Empty;
    public string? PrototypeId { get; set; }
}

public sealed class MapAreaInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Cas { get; set; }
    public bool MortarFire { get; set; }
    public bool MortarPlacement { get; set; }
    public bool Lasing { get; set; }
    public bool Medevac { get; set; }
    public bool Paradropping { get; set; }
    public bool OrbitalBombard { get; set; }
    public bool SupplyDrop { get; set; }
    public bool Fulton { get; set; }
    public bool LandingZone { get; set; }
    public string? LinkedLz { get; set; }
}
