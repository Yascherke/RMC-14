using System.Linq;
using System.Numerics;
using Content.Client.Examine;
using Content.Shared._RMC14.NightVision;
using Content.Shared._RMC14.Stealth;
using Content.Shared._RMC14.Xenonids;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.Utility;
using Robust.Shared.Enums;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Client._RMC14.NightVision;

public sealed class NightVisionOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entity = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private readonly ContainerSystem _container;
    private readonly ExamineSystem _examine;
    private readonly TransformSystem _transform;
    private readonly EntityQuery<XenoComponent> _xenoQuery;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    private readonly ShaderInstance _shader;
    private readonly List<NightVisionRenderEntry> _entries = new();

    public NightVisionOverlay()
    {
        IoCManager.InjectDependencies(this);

        _container = _entity.System<ContainerSystem>();
        _examine = _entity.System<ExamineSystem>();
        _transform = _entity.System<TransformSystem>();
        _xenoQuery = _entity.GetEntityQuery<XenoComponent>();

        _shader = _prototype.Index<ShaderPrototype>("RMCNightVision").Instance().Duplicate();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entity.TryGetComponent(_players.LocalEntity, out NightVisionComponent? nightVision) ||
            nightVision.State == NightVisionState.Off)
        {
            return;
        }

        var handle = args.WorldHandle;
        var eye = args.Viewport.Eye;
        var eyeRot = eye?.Rotation ?? default;

        _entries.Clear();

        var entities = _entity.EntityQueryEnumerator<RMCNightVisionVisibleComponent, SpriteComponent, TransformComponent>();
        while (entities.MoveNext(out var uid, out var visible, out var sprite, out var xform))
        {
            ShaderInstance? entityShader = null;
            var invisibleComponent = GetInvisibilityComponent(uid);
            if (invisibleComponent != null)
            {
                entityShader = _prototype.Index<ShaderPrototype>("RMCInvisible").Instance().Duplicate();
            }

            _entries.Add(new NightVisionRenderEntry((uid, sprite, xform),
                eye?.Position.MapId,
                nightVision.SeeThroughContainers,
                visible.Priority,
                visible.Transparency,
                entityShader,
                invisibleComponent));
        }

        var allSpriteEntities = _entity.EntityQueryEnumerator<SpriteComponent, TransformComponent>();
        while (allSpriteEntities.MoveNext(out var uid, out var sprite, out var xform))
        {
            if (_entity.HasComponent<RMCNightVisionVisibleComponent>(uid))
                continue;

            var invisibleComponent = GetInvisibilityComponent(uid);
            if (invisibleComponent == null)
                continue;

            var seeThrough = nightVision.SeeThroughContainers && !_xenoQuery.HasComp(uid);
            if (!seeThrough && _container.IsEntityOrParentInContainer(uid, xform: xform))
                continue;

            var entityShader = _prototype.Index<ShaderPrototype>("RMCInvisible").Instance().Duplicate();

            _entries.Add(new NightVisionRenderEntry((uid, sprite, xform),
                eye?.Position.MapId,
                nightVision.SeeThroughContainers,
                0,
                null,
                entityShader,
                invisibleComponent));
        }

        _entries.Sort(SortPriority);

        foreach (var entry in _entries)
        {
            Render(entry.Ent,
                entry.Map,
                handle,
                eyeRot,
                entry.NightVisionSeeThroughContainers,
                entry.Transparency,
                entry.Shader,
                entry.InvisibleComponent);
        }

        if (_players.LocalEntity is { } player)
        {
            var inViewQuery = _entity.EntityQueryEnumerator<RMCNightVisionVisibleInViewComponent, SpriteComponent, TransformComponent>();
            while (inViewQuery.MoveNext(out var uid, out _, out var sprite, out var xform))
            {
                if (!_examine.InRangeUnOccluded(uid, player))
                    continue;

                ShaderInstance? inViewShader = null;
                var inViewInvisibleComponent = GetInvisibilityComponent(uid);
                if (inViewInvisibleComponent != null)
                {
                    inViewShader = _prototype.Index<ShaderPrototype>("RMCInvisible").Instance().Duplicate();
                }

                Render((uid, sprite, xform),
                    eye?.Position.MapId,
                    handle,
                    eyeRot,
                    false,
                    null,
                    inViewShader,
                    inViewInvisibleComponent);
            }
        }

        if (nightVision.SeeThroughContainers)
        {
            var allInvisibleEntities = _entity.EntityQueryEnumerator<SpriteComponent, TransformComponent>();
            while (allInvisibleEntities.MoveNext(out var uid, out var sprite, out var xform))
            {
                if (_entity.HasComponent<RMCNightVisionVisibleComponent>(uid))
                    continue;

                if (_entity.HasComponent<RMCNightVisionVisibleInViewComponent>(uid))
                    continue;

                var invisibleComponent = GetInvisibilityComponent(uid);
                if (invisibleComponent == null)
                    continue;

                if (!_container.IsEntityOrParentInContainer(uid, xform: xform))
                    continue;

                var wallShader = _prototype.Index<ShaderPrototype>("RMCInvisible").Instance().Duplicate();

                Render((uid, sprite, xform),
                    eye?.Position.MapId,
                    handle,
                    eyeRot,
                    true,
                    null,
                    wallShader,
                    invisibleComponent);
            }
        }

        handle.SetTransform(Matrix3x2.Identity);

        if (!nightVision.Green)
            return;

        if (ScreenTexture == null || args.Viewport.Eye == null)
            return;

        _shader.SetParameter("renderScale", args.Viewport.RenderScale * args.Viewport.Eye.Scale);
        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);

        var worldHandle = args.WorldHandle;
        worldHandle.UseShader(_shader);
        worldHandle.DrawRect(args.WorldBounds, Color.White);
        worldHandle.UseShader(null);
    }

    private static int SortPriority(NightVisionRenderEntry x, NightVisionRenderEntry y)
    {
        return x.Priority.CompareTo(y.Priority);
    }

    private void Render(Entity<SpriteComponent, TransformComponent> ent,
            MapId? map,
            DrawingHandleWorld handle,
            Angle eyeRot,
            bool seeThroughContainers,
            float? transparency,
            ShaderInstance? entityShader,
            EntityActiveInvisibleComponent? invisibleComponent)
    {
        var (uid, sprite, xform) = ent;
        if (xform.MapID != map)
            return;

        var seeThrough = seeThroughContainers && !_xenoQuery.HasComp(uid);
        if (!seeThrough && _container.IsEntityOrParentInContainer(uid, xform: xform))
            return;

        var (position, rotation) = _transform.GetWorldPositionRotation(xform);

        var shouldApplyInvisibility = invisibleComponent != null;
        var finalTransparency = transparency ?? 1.0f;

        if (shouldApplyInvisibility)
        {
            finalTransparency *= invisibleComponent.Opacity;
            finalTransparency = Math.Min(finalTransparency, 0.15f);
        }

        var angle = rotation + eyeRot;
        angle = angle.Reduced().FlipPositive();

        var cardinal = Angle.Zero;
        if (sprite is { NoRotation: false, SnapCardinals: true })
            cardinal = angle.RoundToCardinalAngle();

        var entityMatrix = Matrix3Helpers.CreateTransform(position, sprite.NoRotation ? -eyeRot : rotation - cardinal);
        var spriteMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        if (shouldApplyInvisibility && entityShader != null)
        {
            handle.UseShader(entityShader);
        }

        foreach (var iLayer in sprite.AllLayers)
        {
            if (!iLayer.Visible)
                continue;

            var layer = (SpriteComponent.Layer)iLayer;

            if (layer.Texture == null && !iLayer.RsiState.IsValid)
                continue;

            var rsi = iLayer.ActualRsi;
            RSI.State? state = null;
            RsiDirection dir = RsiDirection.South;

            if (iLayer.RsiState.IsValid && rsi != null && rsi.TryGetState(iLayer.RsiState, out state))
            {
                dir = state.RsiDirections switch
                {
                    RsiDirectionType.Dir1 => RsiDirection.South,
                    RsiDirectionType.Dir4 => ((int)Math.Round(angle.Theta / MathHelper.PiOver2) % 4) switch
                    {
                        0 => RsiDirection.South,
                        1 => RsiDirection.East,
                        2 => RsiDirection.North,
                        _ => RsiDirection.West,
                    },
                    _ => angle.GetDir().Convert(state.RsiDirections)
                };
            }

            var offsetDir = layer.DirOffset switch
            {
                SpriteComponent.DirectionOffset.None => dir,
                SpriteComponent.DirectionOffset.Clockwise => (RsiDirection)(((int)dir + 1) % 4),
                SpriteComponent.DirectionOffset.CounterClockwise => (RsiDirection)(((int)dir + 3) % 4),
                SpriteComponent.DirectionOffset.Flip => (RsiDirection)(((int)dir + 2) % 4),
                _ => dir
            };

            layer.GetLayerDrawMatrix(offsetDir, out var layerMatrix);

            var texture = state?.GetFrame(offsetDir, iLayer.AnimationFrame) ?? iLayer.Texture;
            if (texture == null)
                continue;

            var transformMatrix = Matrix3x2.Multiply(layerMatrix, spriteMatrix);
            handle.SetTransform(in transformMatrix);

            Color layerColor;
            if (shouldApplyInvisibility && entityShader != null)
            {
                layerColor = sprite.Color * iLayer.Color;
            }
            else if (shouldApplyInvisibility)
            {
                layerColor = sprite.Color * iLayer.Color * new Color(0.8f, 0.9f, 1.0f, finalTransparency);
            }
else
{
    layerColor = sprite.Color * iLayer.Color * Color.White.WithAlpha(finalTransparency);
}

            var textureSize = texture.Size / 32.0f;
            var quad = Box2.FromDimensions(textureSize / -2, textureSize);

            handle.DrawTextureRectRegion(texture, quad, layerColor);
        }

        if (shouldApplyInvisibility && entityShader != null)
        {
            handle.UseShader(null);
        }
    }

    private EntityActiveInvisibleComponent? GetInvisibilityComponent(EntityUid uid)
    {
        if (_entity.TryGetComponent<EntityActiveInvisibleComponent>(uid, out var invisibleComponent))
            return invisibleComponent;

        if (_entity.TryGetComponent<TransformComponent>(uid, out var transform))
        {
            var currentParent = transform.ParentUid;

            while (currentParent != EntityUid.Invalid)
            {
                if (_entity.TryGetComponent<EntityActiveInvisibleComponent>(currentParent, out var parentInvisible))
                    return parentInvisible;

                if (_entity.TryGetComponent<TransformComponent>(currentParent, out var parentTransform))
                    currentParent = parentTransform.ParentUid;
                else
                    break;
            }
        }

        if (_container.TryGetContainingContainer(uid, out var container))
        {
            if (_entity.TryGetComponent<EntityActiveInvisibleComponent>(container.Owner, out var containerInvisible))
                return containerInvisible;

            return GetInvisibilityComponent(container.Owner);
        }

        return null;
    }
}

public record struct NightVisionRenderEntry(
    (EntityUid, SpriteComponent, TransformComponent) Ent,
    MapId? Map,
    bool NightVisionSeeThroughContainers,
    int Priority,
    float? Transparency,
    ShaderInstance? Shader,
    EntityActiveInvisibleComponent? InvisibleComponent
);
