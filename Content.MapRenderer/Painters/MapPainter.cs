using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Content.Client.Markers;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Content.MapRenderer;
using Content.Server.GameTicking;
using Content.Shared._RMC14.Areas;
using Robust.Client.GameObjects;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.MapRenderer.Painters
{
    public sealed class MapPainter : IAsyncDisposable
    {
        private static readonly FieldInfo? AreaExcludeFromTacMapRenderField =
            typeof(AreaComponent).GetField(nameof(AreaComponent.ExcludeFromTacMapRender), BindingFlags.Instance | BindingFlags.Public);

        private static readonly FieldInfo? AreaMinimapColorField =
            typeof(AreaComponent).GetField(nameof(AreaComponent.MinimapColor), BindingFlags.Instance | BindingFlags.Public);

        private readonly RenderMap _map;
        private readonly ITestContextLike _testContextLike;

        private TestPair? _pair;
        private Entity<MapGridComponent>[] _grids = [];
        private readonly Dictionary<EntityUid, string> _gridIds = new();

        public MapPainter(RenderMap map, ITestContextLike testContextLike)
        {
            _map = map;
            _testContextLike = testContextLike;
        }

        public async Task Initialize()
        {
            var stopwatch = RStopwatch.StartNew();

            var poolSettings = new PoolSettings
            {
                DummyTicker = false,
                Connected = true,
                Destructive = true,
                Fresh = true,
                // Seriously whoever made MapPainter use GameMapPrototype I wish you step on a lego one time.
                Map = _map is RenderMapPrototype prototype ? prototype.Prototype : PoolManager.TestMap,
            };
            _pair = await PoolManager.GetServerClient(poolSettings, _testContextLike);

            Console.WriteLine($"Loaded client and server in {(int)stopwatch.Elapsed.TotalMilliseconds} ms");

            if (_map is RenderMapFile mapFile)
            {
                using var stream = File.OpenRead(mapFile.FileName);

                await _pair.Server.WaitPost(() =>
                {
                    var loadOptions = new MapLoadOptions
                    {
                        // Accept loading both maps and grids without caring about what the input file truly is.
                        DeserializationOptions =
                        {
                            LogOrphanedGrids = false,
                        },
                    };

                    if (!_pair.Server.System<MapLoaderSystem>().TryLoadGeneric(stream, mapFile.FileName, out var loadResult, loadOptions))
                        throw new IOException($"File {mapFile.FileName} could not be read");

                    _grids = loadResult.Grids.ToArray();
                });
            }
        }

        public async Task SetupView(bool showMarkers)
        {
            if (_pair == null)
                throw new InvalidOperationException("Instance not initialized!");

            await _pair.Client.WaitPost(() =>
            {
                if (_pair.Client.EntMan.TryGetComponent(_pair.Client.PlayerMan.LocalEntity, out SpriteComponent? sprite))
                {
                    _pair.Client.System<SpriteSystem>()
                        .SetVisible((_pair.Client.PlayerMan.LocalEntity.Value, sprite), false);
                }
            });

            if (showMarkers)
            {
                await _pair.Client.WaitPost(() =>
                {
                    _pair.Client.System<MarkerSystem>().MarkersVisible = true;
                });
            }
        }

        public async Task<MapViewerData> GenerateMapViewerData(ParallaxOutput? parallaxOutput)
        {
            if (_pair == null)
                throw new InvalidOperationException("Instance not initialized!");

            var mapShort = _map.ShortName;

            string fullName;
            if (_map is RenderMapPrototype prototype)
            {
                fullName = _pair.Server.ProtoMan.Index(prototype.Prototype).MapName;
            }
            else
            {
                fullName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(mapShort);
            }

            var mapViewerData = new MapViewerData
            {
                Id = mapShort,
                Name = fullName,
            };

            if (parallaxOutput != null)
            {
                await _pair.Client.WaitPost(() =>
                {
                    var res = _pair.Client.InstanceDependencyCollection.Resolve<IResourceManager>();
                    mapViewerData.ParallaxLayers.Add(LayerGroup.DefaultParallax(res, parallaxOutput));
                });
            }

            return mapViewerData;
        }

        public async Task<MapExport> ExportRenderedMapData(string mapId, string mapName)
        {
            if (_pair == null)
                throw new InvalidOperationException("Instance not initialized!");

            var export = new MapExport
            {
                Id = mapId,
                Name = mapName,
            };

            await _pair.RunTicksSync(10);
            await Task.WhenAll(_pair.Client.WaitIdleAsync(), _pair.Server.WaitIdleAsync());

            var serverEntity = _pair.Server.ResolveDependency<IServerEntityManager>();
            var entityManager = _pair.Server.ResolveDependency<IEntityManager>();
            var prototypes = _pair.Server.ResolveDependency<IPrototypeManager>();
            var compFactory = _pair.Server.ResolveDependency<IComponentFactory>();
            var mapSystem = entityManager.System<SharedMapSystem>();

            await _pair.Server.WaitPost(() =>
            {
                var gridIndex = 0;
                _gridIds.Clear();

                foreach (var (uid, grid) in _grids)
                {
                    if (!serverEntity.TryGetComponent(uid, out AreaGridComponent? areaGrid))
                        continue;

                    gridIndex++;
                    var gridId = $"grid_{gridIndex}";
                    var renderBounds = ResolveRenderBounds(uid, grid, mapSystem);
                    var gridExport = BuildGridExport(gridId, areaGrid, renderBounds, prototypes, compFactory);
                    gridExport.Entities = BuildEntityCells(uid, grid, renderBounds, mapSystem, serverEntity);
                    export.Grids.Add(gridExport);
                    _gridIds[uid] = gridId;
                }
            });

            return export;
        }

        public bool TryGetGridExportId(EntityUid uid, out string gridId)
        {
            return _gridIds.TryGetValue(uid, out gridId!);
        }

        public async IAsyncEnumerable<RenderedGridImage<Rgba32>> Paint()
        {
            if (_pair == null)
                throw new InvalidOperationException("Instance not initialized!");

            var client = _pair.Client;
            var server = _pair.Server;

            var sEntityManager = server.ResolveDependency<IServerEntityManager>();
            var sPlayerManager = server.ResolveDependency<IPlayerManager>();

            var entityManager = server.ResolveDependency<IEntityManager>();
            var mapSys = entityManager.System<SharedMapSystem>();

            await _pair.RunTicksSync(10);
            await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

            var sMapManager = server.ResolveDependency<IMapManager>();

            var tilePainter = new TilePainter(client, server);
            var entityPainter = new GridPainter(client, server);
            var xformQuery = sEntityManager.GetEntityQuery<TransformComponent>();
            var xformSystem = sEntityManager.System<SharedTransformSystem>();

            await server.WaitPost(() =>
            {
                var playerEntity = sPlayerManager.Sessions.Single().AttachedEntity;

                if (playerEntity.HasValue)
                {
                    sEntityManager.DeleteEntity(playerEntity.Value);
                }

                if (_map is RenderMapPrototype)
                {
                    var mapId = sEntityManager.System<GameTicker>().DefaultMap;
                    _grids = sMapManager.GetAllGrids(mapId).ToArray();
                }

                foreach (var (uid, _) in _grids)
                {
                    var gridXform = xformQuery.GetComponent(uid);
                    xformSystem.SetWorldRotation(gridXform, Angle.Zero);
                }
            });

            await _pair.RunTicksSync(10);
            await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

            foreach (var (uid, grid) in _grids)
            {
                var tiles = mapSys.GetAllTiles(uid, grid).ToList();
                if (tiles.Count == 0)
                {
                    Console.WriteLine($"Warning: Grid {uid} was empty. Skipping image rendering.");
                    continue;
                }
                var tileXSize = grid.TileSize * TilePainter.TileImageSize;
                var tileYSize = grid.TileSize * TilePainter.TileImageSize;

                var minX = tiles.Min(t => t.X);
                var minY = tiles.Min(t => t.Y);
                var maxX = tiles.Max(t => t.X);
                var maxY = tiles.Max(t => t.Y);
                var w = (maxX - minX + 1) * tileXSize;
                var h = (maxY - minY + 1) * tileYSize;
                var customOffset = new Vector2();

                //MapGrids don't have LocalAABB, so we offset them to align the bottom left corner with 0,0 coordinates
                if (grid.LocalAABB.IsEmpty())
                    customOffset = new Vector2(-minX, -minY);

                var gridCanvas = new Image<Rgba32>(w, h);

                await server.WaitPost(() =>
                {
                    tilePainter.Run(gridCanvas, uid, grid, customOffset);
                    entityPainter.Run(gridCanvas, uid, grid, customOffset);

                    gridCanvas.Mutate(e => e.Flip(FlipMode.Vertical));
                });

                var renderedImage = new RenderedGridImage<Rgba32>(gridCanvas)
                {
                    GridUid = uid,
                    Offset = xformSystem.GetWorldPosition(uid),
                };

                yield return renderedImage;
            }
        }

        public async Task CleanReturnAsync()
        {
            if (_pair == null)
                throw new InvalidOperationException("Instance not initialized!");

            await _pair.CleanReturnAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_pair != null)
                await _pair.DisposeAsync();
        }

        private static MapExportGrid BuildGridExport(
            string gridId,
            AreaGridComponent areaGrid,
            MapBounds renderBounds,
            IPrototypeManager prototypes,
            IComponentFactory componentFactory)
        {
            var bounds = ResolveBounds(areaGrid);
            var areaIds = new List<string>();
            var areaIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var renderInfo = new Dictionary<string, (bool Exclude, uint Color)>(StringComparer.Ordinal);

            var areas = new List<MapAreaCell>(areaGrid.Areas.Count);
            foreach (var (pos, areaProto) in areaGrid.Areas)
            {
                if (!IsWithinBounds(pos, bounds))
                    continue;

                var id = areaProto.Id;
                if (!areaIndex.TryGetValue(id, out var idx))
                {
                    idx = areaIds.Count;
                    areaIds.Add(id);
                    areaIndex[id] = idx;
                }

                areas.Add(new MapAreaCell(pos.X, pos.Y, idx));
            }

            var colors = new List<MapColorCell>(areaGrid.Colors.Count);
            var colorPositions = new HashSet<Vector2i>();
            foreach (var (pos, color) in areaGrid.Colors)
            {
                if (!IsWithinBounds(pos, bounds))
                    continue;

                colors.Add(new MapColorCell(pos.X, pos.Y, PackColor(color)));
                colorPositions.Add(pos);
            }

            foreach (var (pos, areaProto) in areaGrid.Areas)
            {
                if (!IsWithinBounds(pos, bounds) || colorPositions.Contains(pos))
                    continue;

                var (exclude, color) = GetAreaRenderInfo(areaProto.Id, prototypes, componentFactory, renderInfo);
                if (exclude)
                    continue;

                colors.Add(new MapColorCell(pos.X, pos.Y, color));
            }

            var labels = new List<MapLabelCell>(areaGrid.Labels.Count);
            foreach (var (pos, text) in areaGrid.Labels)
            {
                if (!IsWithinBounds(pos, bounds))
                    continue;

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                labels.Add(new MapLabelCell(pos.X, pos.Y, text));
            }

            var areaInfo = new List<MapAreaInfo>(areaIds.Count);
            foreach (var id in areaIds)
            {
                var info = new MapAreaInfo
                {
                    Id = id,
                    Name = id,
                };

                if (prototypes.TryIndex<EntityPrototype>(id, out var proto))
                {
                    info.Name = proto.Name;

                    if (proto.TryGetComponent(out AreaComponent? areaComp, componentFactory))
                    {
                        info.Cas = areaComp.CAS;
                        info.MortarFire = areaComp.MortarFire;
                        info.MortarPlacement = areaComp.MortarPlacement;
                        info.Lasing = areaComp.Lasing;
                        info.Medevac = areaComp.Medevac;
                        info.Paradropping = areaComp.Paradropping;
                        info.OrbitalBombard = areaComp.OB;
                        info.SupplyDrop = areaComp.SupplyDrop;
                        info.Fulton = areaComp.Fulton;
                        info.LandingZone = areaComp.LandingZone;
                        info.LinkedLz = areaComp.LinkedLz;
                    }
                }

                areaInfo.Add(info);
            }

            return new MapExportGrid
            {
                GridId = gridId,
                HasMapBounds = areaGrid.HasTacMapBounds,
                Bounds = bounds,
                RenderBounds = renderBounds,
                Colors = colors,
                Areas = areas,
                AreaIds = areaIds,
                AreaInfo = areaInfo,
                Labels = labels,
            };
        }

        private static MapBounds ResolveBounds(AreaGridComponent grid)
        {
            var hasAny = false;
            var fullMin = Vector2i.Zero;
            var fullMax = Vector2i.Zero;

            void Accumulate(Vector2i pos)
            {
                if (!hasAny)
                {
                    hasAny = true;
                    fullMin = pos;
                    fullMax = pos;
                }
                else
                {
                    fullMin = Vector2i.ComponentMin(fullMin, pos);
                    fullMax = Vector2i.ComponentMax(fullMax, pos);
                }
            }

            foreach (var (pos, _) in grid.Colors)
            {
                Accumulate(pos);
            }

            foreach (var (pos, _) in grid.Areas)
            {
                Accumulate(pos);
            }

            foreach (var (pos, _) in grid.Labels)
            {
                Accumulate(pos);
            }

            if (!hasAny)
                return new MapBounds(0, 0, 0, 0);

            if (grid.HasTacMapBounds)
            {
                var boundsMin = Vector2i.ComponentMax(fullMin, grid.TacMapBoundsMin);
                var boundsMax = Vector2i.ComponentMin(fullMax, grid.TacMapBoundsMax);

                if (boundsMax.X >= boundsMin.X && boundsMax.Y >= boundsMin.Y)
                {
                    return new MapBounds(
                        boundsMin.X,
                        boundsMin.Y,
                        boundsMax.X,
                        boundsMax.Y);
                }
            }

            return new MapBounds(fullMin.X, fullMin.Y, fullMax.X, fullMax.Y);
        }

        private static MapBounds ResolveRenderBounds(
            EntityUid gridUid,
            MapGridComponent grid,
            SharedMapSystem mapSystem)
        {
            var tiles = mapSystem.GetAllTiles(gridUid, grid).ToList();
            if (tiles.Count == 0)
                return new MapBounds(0, 0, 0, 0);

            var minX = tiles.Min(t => t.X);
            var minY = tiles.Min(t => t.Y);
            var maxX = tiles.Max(t => t.X);
            var maxY = tiles.Max(t => t.Y);
            return new MapBounds(minX, minY, maxX, maxY);
        }

        private static bool IsWithinBounds(Vector2i pos, MapBounds bounds)
        {
            return pos.X >= bounds.MinX &&
                   pos.X <= bounds.MaxX &&
                   pos.Y >= bounds.MinY &&
                   pos.Y <= bounds.MaxY;
        }

        private static List<MapEntityCell> BuildEntityCells(
            EntityUid gridUid,
            MapGridComponent grid,
            MapBounds renderBounds,
            SharedMapSystem mapSystem,
            IEntityManager entities)
        {
            var byTile = new Dictionary<Vector2i, List<MapEntityInfo>>();
            var query = entities.AllEntityQueryEnumerator<TransformComponent, MetaDataComponent>();

            while (query.MoveNext(out var uid, out var xform, out var meta))
            {
                if (uid == gridUid || xform.GridUid != gridUid)
                    continue;

                if (meta.EntityPrototype == null)
                    continue;

                var indices = mapSystem.CoordinatesToTile(gridUid, grid, xform.Coordinates);
                if (!IsWithinBounds(indices, renderBounds))
                    continue;

                if (!byTile.TryGetValue(indices, out var tileEntities))
                {
                    tileEntities = new List<MapEntityInfo>();
                    byTile[indices] = tileEntities;
                }

                tileEntities.Add(new MapEntityInfo
                {
                    Name = meta.EntityName,
                    PrototypeId = meta.EntityPrototype.ID
                });
            }

            var cells = new List<MapEntityCell>(byTile.Count);
            foreach (var (indices, tileEntities) in byTile)
            {
                tileEntities.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                cells.Add(new MapEntityCell(indices.X, indices.Y, tileEntities));
            }

            cells.Sort(static (a, b) =>
            {
                var compareX = a.X.CompareTo(b.X);
                return compareX != 0 ? compareX : a.Y.CompareTo(b.Y);
            });

            return cells;
        }

        private static (bool Exclude, uint Color) GetAreaRenderInfo(
            string areaId,
            IPrototypeManager prototypes,
            IComponentFactory componentFactory,
            Dictionary<string, (bool Exclude, uint Color)> cache)
        {
            if (cache.TryGetValue(areaId, out var info))
                return info;

            var exclude = false;
            var color = Robust.Shared.Maths.Color.FromHex("#6c6767d8");

            if (prototypes.TryIndex<EntityPrototype>(areaId, out var proto) &&
                proto.TryGetComponent(out AreaComponent? areaComp, componentFactory))
            {
                if (AreaExcludeFromTacMapRenderField?.GetValue(areaComp) is bool excludeValue)
                    exclude = excludeValue;

                if (AreaMinimapColorField?.GetValue(areaComp) is Robust.Shared.Maths.Color areaColor &&
                    areaColor != default)
                {
                    color = areaColor.WithAlpha(0.5f);
                }
            }

            info = (exclude, PackColor(color));
            cache[areaId] = info;
            return info;
        }

        private static uint PackColor(Robust.Shared.Maths.Color color)
        {
            return ((uint) color.RByte << 24) |
                   ((uint) color.GByte << 16) |
                   ((uint) color.BByte << 8) |
                   color.AByte;
        }
    }
}
