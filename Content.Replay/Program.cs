using Robust.Client;
using Content.Replay.ChatExport;

namespace Content.Replay;

internal static class Program
{
    internal const string ReplayExportEnvVar = "RMC14_REPLAY_CHAT_EXPORT";

    public static void Main(string[] args)
    {
        var launchArgs = ReplayChatExportOptions.ConfigureAndStrip(args);
        var exportMode = ReplayChatExportOptions.Current != null;

        if (exportMode)
            Environment.SetEnvironmentVariable(ReplayExportEnvVar, "1");

        ContentStart.StartLibrary(launchArgs, new GameControllerOptions()
        {
            Sandboxing = !exportMode,
            ContentModulePrefix = "Content.",
            ContentBuildDirectory = "Content.Replay",
            DefaultWindowTitle = "SS14 Replay",
            UserDataDirectoryName = "Space Station 14",
            ConfigFileName = "replay.toml"
        });
    }
}
