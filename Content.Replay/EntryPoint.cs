using Content.Client.Replay;
using Content.Replay.Menu;
using Content.Replay.ChatExport;
using JetBrains.Annotations;
using Robust.Client;
using Robust.Client.Console;
using Robust.Client.Replays.Loading;
using Robust.Client.Serialization;
using Robust.Client.State;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared;

namespace Content.Replay;

[UsedImplicitly]
public sealed class EntryPoint : GameClient
{
    [Dependency] private readonly IBaseClient _client = default!;
    [Dependency] private readonly IStateManager _stateMan = default!;
    [Dependency] private readonly ContentReplayPlaybackManager _contentReplayPlaybackMan = default!;
    [Dependency] private readonly IClientConGroupController _conGrp = default!;
    [Dependency] private readonly IReplayLoadManager _loadMan = default!;
    [Dependency] private readonly IGameController _gameController = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IClientRobustSerializer _serializer = default!;

    public override void Init()
    {
        base.Init();
        IoCManager.BuildGraph();
        IoCManager.InjectDependencies(this);
    }

    public override void PostInit()
    {
        base.PostInit();
        _client.StartSinglePlayer();
        _conGrp.Implementation = new ReplayConGroup();

        if (ReplayChatExportOptions.Current is { } exportOptions)
        {
            _cfg.SetCVar(CVars.ReplayIgnoreErrors, true);
            _ = new ReplayChatExporter(_loadMan, _gameController, _serializer, _cfg).RunAsync(exportOptions);
            return;
        }

        _contentReplayPlaybackMan.DefaultState = typeof(ReplayMainScreen);
        _stateMan.RequestStateChange<ReplayMainScreen>();
    }
}
