using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.MapRenderer.Painters;
using Content.Server.Maps;
using Content.Shared._RMC14.Rules;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using SixLabors.ImageSharp;

namespace Content.MapRenderer;

internal static class RenderedMapExporter
{
    private const string TacMapOutputDirectoryName = "tacmap";

    private sealed class TacMapTarget
    {
        public required string Id;
        public required string Name;
        public required string ResourcePath;
        public required string FilePath;
        public readonly HashSet<string> MatchKeys = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DiscoveredMap
    {
        public required string ResourcePath;
        public string Name = string.Empty;
        public int NamePriority;
        public readonly HashSet<string> MatchKeys = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string GetOutputRoot(CommandLineArguments arguments)
    {
        return Path.Combine(arguments.OutputPath, TacMapOutputDirectoryName);
    }

    public static string GetStandaloneViewerPath()
    {
        return Path.GetFullPath(Path.Combine("Tools", "TacticalMapViewer", "index.html"));
    }

    public static async Task Run(CommandLineArguments arguments, ExternalTestContext testContext)
    {
        var targets = await DiscoverTargets(arguments, testContext);
        if (targets.Count == 0)
        {
            Console.WriteLine("No maps matched the provided input.");
            return;
        }

        var outputRoot = GetOutputRoot(arguments);
        var mapsDirectory = Path.Combine(outputRoot, "maps");
        var imagesDirectory = Path.Combine(outputRoot, "images");
        Directory.CreateDirectory(mapsDirectory);
        Directory.CreateDirectory(imagesDirectory);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var manifest = new MapExportManifest
        {
            GeneratedUtc = DateTime.UtcNow
        };

        Console.WriteLine($"Exporting rendered maps to {outputRoot}");

        foreach (var target in targets)
        {
            Console.WriteLine($"[map-export] Loading {target.Name} ({target.ResourcePath})");

            var renderTarget = new RenderMapFile { FileName = target.FilePath };
            await using var painter = new MapPainter(renderTarget, testContext);

            try
            {
                await painter.Initialize();
                var mapData = await painter.ExportRenderedMapData(target.Id, target.Name);

                if (mapData.Grids.Count == 0)
                {
                    Console.WriteLine($"[map-export] Skipping {target.Id}: no AreaGrid components found.");
                    continue;
                }

                var gridsById = mapData.Grids.ToDictionary(g => g.GridId, StringComparer.Ordinal);
                await foreach (var renderedGrid in painter.Paint())
                {
                    if (renderedGrid.GridUid is not { } gridUid ||
                        !painter.TryGetGridExportId(gridUid, out var gridId) ||
                        !gridsById.TryGetValue(gridId, out var gridExport))
                    {
                        renderedGrid.Image.Dispose();
                        continue;
                    }

                    var imageFileName = $"{target.Id}-{gridId}.png";
                    var imageFilePath = Path.Combine(imagesDirectory, imageFileName);
                    await renderedGrid.Image.SaveAsPngAsync(imageFilePath);

                    gridExport.Image = new MapRenderImage
                    {
                        File = Path.Combine("images", imageFileName).Replace('\\', '/'),
                        Width = renderedGrid.Image.Width,
                        Height = renderedGrid.Image.Height,
                        PixelsPerTile = TilePainter.TileImageSize
                    };

                    renderedGrid.Image.Dispose();
                }

                var mapFileName = $"{target.Id}.json";
                var mapFilePath = Path.Combine(mapsDirectory, mapFileName);
                var mapJson = JsonSerializer.Serialize(mapData, jsonOptions);
                await File.WriteAllTextAsync(mapFilePath, mapJson);

                manifest.Maps.Add(new MapExportManifestMap
                {
                    Id = target.Id,
                    Name = target.Name,
                    File = Path.Combine("maps", mapFileName).Replace('\\', '/')
                });

                Console.WriteLine($"[map-export] Wrote {mapData.Grids.Count} grids to {mapFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[map-export] Failed to export {target.ResourcePath}:");
                Console.WriteLine(ex);
            }
            finally
            {
                try
                {
                    await painter.CleanReturnAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[map-export] Cleanup error for {target.Id}: {ex}");
                }
            }
        }

        manifest.Maps.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        manifest = RebuildManifestFromExports(outputRoot, jsonOptions, manifest.GeneratedUtc);

        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson);

        Console.WriteLine($"[map-export] Export complete. Maps exported: {manifest.Maps.Count}");
        Console.WriteLine($"[map-export] Open the standalone viewer: {GetStandaloneViewerPath()}");
        Console.WriteLine($"[map-export] Then load export folder: {outputRoot}");
    }

    private static MapExportManifest RebuildManifestFromExports(
        string outputRoot,
        JsonSerializerOptions jsonOptions,
        DateTime generatedUtc)
    {
        var manifest = new MapExportManifest
        {
            GeneratedUtc = generatedUtc
        };

        var mapsDirectory = Path.Combine(outputRoot, "maps");
        if (!Directory.Exists(mapsDirectory))
            return manifest;

        foreach (var file in Directory.GetFiles(mapsDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var map = JsonSerializer.Deserialize<MapExport>(json, jsonOptions);
                if (map == null || string.IsNullOrWhiteSpace(map.Id))
                    continue;

                manifest.Maps.Add(new MapExportManifestMap
                {
                    Id = map.Id,
                    Name = string.IsNullOrWhiteSpace(map.Name) ? map.Id : map.Name,
                    File = Path.Combine("maps", Path.GetFileName(file)).Replace('\\', '/')
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[map-export] Failed to include exported map in manifest: {file}");
                Console.WriteLine(ex);
            }
        }

        manifest.Maps.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return manifest;
    }

    private static async Task<List<TacMapTarget>> DiscoverTargets(CommandLineArguments arguments, ExternalTestContext testContext)
    {
        var discovered = await DiscoverPrototypeMaps(testContext);
        var selected = SelectMaps(arguments, discovered);

        selected.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return selected;
    }

    private static async Task<List<DiscoveredMap>> DiscoverPrototypeMaps(ExternalTestContext testContext)
    {
        var discovered = new Dictionary<string, DiscoveredMap>(StringComparer.OrdinalIgnoreCase);

        await using var pair = await PoolManager.GetServerClient(testContext: testContext);
        var prototypes = pair.Server.ResolveDependency<IPrototypeManager>();
        var componentFactory = pair.Server.ResolveDependency<IComponentFactory>();

        await pair.Server.WaitPost(() =>
        {
            foreach (var gameMap in prototypes.EnumeratePrototypes<GameMapPrototype>())
            {
                var path = gameMap.MapPath.ToString();
                if (!IsRmcMapPath(path))
                    continue;

                AddOrUpdate(discovered, path, gameMap.MapName, 1, gameMap.ID);
            }

            foreach (var entity in prototypes.EnumeratePrototypes<EntityPrototype>())
            {
                if (!entity.TryGetComponent(out RMCPlanetMapPrototypeComponent? planet, componentFactory))
                    continue;

                var path = planet.Map.ToString();
                if (!IsRmcMapPath(path))
                    continue;

                AddOrUpdate(discovered, path, entity.Name, 2, entity.ID);
            }
        });

        return discovered.Values.ToList();
    }

    private static void AddOrUpdate(
        Dictionary<string, DiscoveredMap> discovered,
        string resourcePath,
        string? name,
        int namePriority,
        string additionalKey)
    {
        if (!discovered.TryGetValue(resourcePath, out var entry))
        {
            entry = new DiscoveredMap
            {
                ResourcePath = resourcePath,
                Name = !string.IsNullOrWhiteSpace(name) ? name : Path.GetFileNameWithoutExtension(resourcePath),
                NamePriority = namePriority
            };
            discovered[resourcePath] = entry;
        }
        else if (namePriority > entry.NamePriority && !string.IsNullOrWhiteSpace(name))
        {
            entry.Name = name;
            entry.NamePriority = namePriority;
        }

        var fileName = Path.GetFileNameWithoutExtension(resourcePath);
        if (!string.IsNullOrWhiteSpace(fileName))
            entry.MatchKeys.Add(fileName);

        entry.MatchKeys.Add(resourcePath);
        entry.MatchKeys.Add(additionalKey);
        if (!string.IsNullOrWhiteSpace(name))
            entry.MatchKeys.Add(name);
    }

    private static List<TacMapTarget> SelectMaps(CommandLineArguments arguments, List<DiscoveredMap> discovered)
    {
        var targets = new List<TacMapTarget>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byPath = discovered.ToDictionary(x => x.ResourcePath, StringComparer.OrdinalIgnoreCase);

        if (arguments.Maps.Count == 0)
        {
            foreach (var map in discovered)
            {
                if (!TryCreateTarget(map.ResourcePath, map.Name, map.MatchKeys, usedIds, out var target))
                    continue;

                selectedPaths.Add(map.ResourcePath);
                targets.Add(target);
            }

            return targets;
        }

        foreach (var token in arguments.Maps)
        {
            var matchedAny = false;

            foreach (var candidate in discovered)
            {
                if (!candidate.MatchKeys.Contains(token))
                    continue;

                matchedAny = true;
                if (selectedPaths.Contains(candidate.ResourcePath))
                    continue;

                if (!TryCreateTarget(candidate.ResourcePath, candidate.Name, candidate.MatchKeys, usedIds, out var target))
                    continue;

                selectedPaths.Add(candidate.ResourcePath);
                targets.Add(target);
            }

            if (matchedAny)
                continue;

            if (TryNormalizeResourcePath(token, out var resourcePath))
            {
                if (byPath.TryGetValue(resourcePath, out var known))
                {
                    if (!selectedPaths.Contains(known.ResourcePath) &&
                        TryCreateTarget(known.ResourcePath, known.Name, known.MatchKeys, usedIds, out var target))
                    {
                        selectedPaths.Add(known.ResourcePath);
                        targets.Add(target);
                    }
                    continue;
                }

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { token, resourcePath };
                var fallbackName = Path.GetFileNameWithoutExtension(resourcePath);
                if (!selectedPaths.Contains(resourcePath) &&
                    TryCreateTarget(resourcePath, fallbackName, keys, usedIds, out var directTarget))
                {
                    selectedPaths.Add(resourcePath);
                    targets.Add(directTarget);
                }

                continue;
            }

            Console.WriteLine($"[map-export] Ignoring unknown map token '{token}'.");
        }

        return targets;
    }

    private static bool TryCreateTarget(
        string resourcePath,
        string? displayName,
        IEnumerable<string> keys,
        HashSet<string> usedIds,
        out TacMapTarget target)
    {
        target = default!;
        var diskPath = ResourcePathToDiskPath(resourcePath);
        if (!File.Exists(diskPath))
        {
            Console.WriteLine($"[map-export] Skipping missing map file: {resourcePath} -> {diskPath}");
            return false;
        }

        var baseId = Path.GetFileNameWithoutExtension(resourcePath);
        var id = MakeUniqueId(SanitizeId(baseId), usedIds);
        var name = string.IsNullOrWhiteSpace(displayName) ? baseId : displayName.Trim();

        target = new TacMapTarget
        {
            Id = id,
            Name = name,
            ResourcePath = resourcePath,
            FilePath = diskPath
        };

        foreach (var key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                target.MatchKeys.Add(key);
        }

        target.MatchKeys.Add(resourcePath);
        target.MatchKeys.Add(id);
        target.MatchKeys.Add(baseId);
        return true;
    }

    private static bool TryNormalizeResourcePath(string token, out string resourcePath)
    {
        resourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (token.StartsWith("/Maps/", StringComparison.OrdinalIgnoreCase))
        {
            resourcePath = token.Replace('\\', '/');
            return true;
        }

        if (File.Exists(token))
        {
            var full = Path.GetFullPath(token);
            var resourcesRoot = Path.GetFullPath("Resources");
            if (!full.StartsWith(resourcesRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            var relative = Path.GetRelativePath(resourcesRoot, full).Replace('\\', '/');
            resourcePath = "/" + relative;
            return true;
        }

        return false;
    }

    private static bool IsRmcMapPath(string path)
    {
        return path.StartsWith("/Maps/_RMC14/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResourcePathToDiskPath(string resourcePath)
    {
        var normalized = resourcePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine("Resources", normalized));
    }

    private static string MakeUniqueId(string id, HashSet<string> used)
    {
        if (used.Add(id))
            return id;

        var i = 2;
        while (true)
        {
            var candidate = $"{id}_{i}";
            if (used.Add(candidate))
                return candidate;
            i++;
        }
    }

    private static string SanitizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "map";

        Span<char> tmp = stackalloc char[raw.Length];
        var len = 0;
        foreach (var ch in raw)
        {
            var lower = char.ToLowerInvariant(ch);
            if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
                tmp[len++] = lower;
            else
                tmp[len++] = '_';
        }

        var value = new string(tmp[..len]).Trim('_');
        if (value.Length == 0)
            return "map";
        return value;
    }
}
