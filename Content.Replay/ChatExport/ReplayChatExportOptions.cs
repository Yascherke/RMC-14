using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared.Chat;
using Robust.Client;
using Robust.Client.Replays.Loading;
using Robust.Client.Serialization;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameStates;
using Robust.Shared.Replays;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Replay.ChatExport;

internal sealed class ReplayChatExportOptions
{
    public const string DefaultReplayRootUrl = "https://replays.rouny-ss14.com/replays/alamo";

    public required string OutputPath { get; init; }
    public required string Format { get; init; }
    public string? ReplayPath { get; init; }
    public string? ReplayDirectory { get; init; }
    public string? ReplayRootUrl { get; init; }
    public int? LastRounds { get; init; }

    public static ReplayChatExportOptions? Current { get; private set; }

    public static string[] ConfigureAndStrip(string[] args)
    {
        if (!TryParse(args, out var options, out var passthrough, out var error))
        {
            Console.Error.WriteLine(error);
            Environment.Exit(1);
        }

        Current = options;

        if (Current != null)
        {
        }

        return passthrough.ToArray();
    }

    private static bool TryParse(
        IReadOnlyList<string> args,
        out ReplayChatExportOptions? options,
        out List<string> passthrough,
        out string? error)
    {
        options = null;
        passthrough = new List<string>();
        error = null;

        var export = false;
        string? replayPath = null;
        string? replayDirectory = null;
        string? replayRootUrl = null;
        string? output = null;
        string format = "jsonl";
        int? lastRounds = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--export-chat":
                    export = true;
                    break;
                case "--replay":
                    if (!TryNext(args, ref i, out replayPath))
                    {
                        error = "Missing value for --replay.";
                        return false;
                    }
                    break;
                case "--replay-dir":
                    if (!TryNext(args, ref i, out replayDirectory))
                    {
                        error = "Missing value for --replay-dir.";
                        return false;
                    }
                    break;
                case "--replay-root-url":
                    if (!TryNext(args, ref i, out replayRootUrl))
                    {
                        error = "Missing value for --replay-root-url.";
                        return false;
                    }
                    break;
                case "--output":
                    if (!TryNext(args, ref i, out output))
                    {
                        error = "Missing value for --output.";
                        return false;
                    }
                    break;
                case "--format":
                    if (!TryNext(args, ref i, out format))
                    {
                        error = "Missing value for --format.";
                        return false;
                    }
                    break;
                case "--last-rounds":
                    if (!TryNext(args, ref i, out var lastRoundsText) || !int.TryParse(lastRoundsText, out var parsedLastRounds) || parsedLastRounds <= 0)
                    {
                        error = "--last-rounds must be a positive integer.";
                        return false;
                    }
                    lastRounds = parsedLastRounds;
                    break;
                default:
                    passthrough.Add(args[i]);
                    break;
            }
        }

        if (!export)
            return true;

        if (string.IsNullOrWhiteSpace(output))
        {
            error = "--output is required with --export-chat.";
            return false;
        }

        var sourceCount = 0;
        if (!string.IsNullOrWhiteSpace(replayPath))
            sourceCount++;
        if (!string.IsNullOrWhiteSpace(replayDirectory))
            sourceCount++;
        if (!string.IsNullOrWhiteSpace(replayRootUrl))
            sourceCount++;

        if (sourceCount > 1)
        {
            error = "Specify only one of --replay, --replay-dir, or --replay-root-url.";
            return false;
        }

        replayRootUrl ??= sourceCount == 0 ? DefaultReplayRootUrl : null;

        if (format is not ("jsonl" or "csv"))
        {
            error = "--format must be jsonl or csv.";
            return false;
        }

        options = new ReplayChatExportOptions
        {
            ReplayPath = replayPath,
            ReplayDirectory = replayDirectory,
            ReplayRootUrl = replayRootUrl,
            OutputPath = output,
            Format = format,
            LastRounds = lastRounds,
        };

        return true;
    }

    private static bool TryNext(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}

internal sealed class ReplayChatExporter
{
    private static readonly Regex HrefFinder = new("<a href=\"([\\d]+)/\">", RegexOptions.Compiled);
    private static readonly Regex ReplayFinder = new("<a href=\"([^\"]+\\.zip)\">", RegexOptions.Compiled);

    private readonly IReplayLoadManager _loadManager;
    private readonly IGameController _controller;
    private readonly IClientRobustSerializer _serializer;
    private readonly IConfigurationManager _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ReplayChatExporter(
        IReplayLoadManager loadManager,
        IGameController controller,
        IClientRobustSerializer serializer,
        IConfigurationManager cfg)
    {
        _loadManager = loadManager;
        _controller = controller;
        _serializer = serializer;
        _cfg = cfg;
    }

    public async Task<int> RunAsync(ReplayChatExportOptions options)
    {
        var tempFiles = new List<string>();
        try
        {
            var replays = (await GetReplayPathsAsync(options, tempFiles)).ToArray();
            Console.WriteLine($"Found {replays.Length} replay file(s) to process.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

            await using var stream = new FileStream(options.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            if (options.Format == "csv")
                await writer.WriteLineAsync("replay_file,round_id,recorded_at_utc,replay_time,chat_channel,message,wrapped_message,hide_chat,hide_popup,sender_entity,sender_key");

            var processed = 0;
            var skipped = 0;
            var recordsWritten = 0;

            foreach (var replayPath in replays)
            {
                processed++;
                Console.WriteLine($"[{processed}/{replays.Length}] Processing {Path.GetFileName(replayPath)}");

                try
                {
                    using var archive = ZipFile.OpenRead(replayPath);
                    using var fileReader = new ReplayFileReaderZip(archive, ReplayConstants.ReplayZipFolder);

                    var metadata = _loadManager.LoadYamlMetadata(fileReader);
                    var meta = ReadMetadata(replayPath, metadata);
                    EnsureCompatibleReplay(metadata);
                    LoadSerializerPackage(fileReader, metadata);

                    foreach (var record in ReadChatRecords(replayPath, fileReader, meta))
                    {
                        if (options.Format == "csv")
                            await writer.WriteLineAsync(ToCsv(record));
                        else
                            await writer.WriteLineAsync(JsonSerializer.Serialize(record));

                        recordsWritten++;
                    }
                }
                catch (Exception e)
                {
                    skipped++;
                    Console.Error.WriteLine($"Skipping {Path.GetFileName(replayPath)}: {e}");
                }
            }

            await writer.FlushAsync();
            Console.WriteLine($"Wrote {recordsWritten} chat message(s) to {options.OutputPath}. Processed: {processed}, skipped: {skipped}.");
            Environment.Exit(0);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.Exit(1);
            return 1;
        }
        finally
        {
            foreach (var tempFile in tempFiles)
            {
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<IEnumerable<string>> GetReplayPathsAsync(ReplayChatExportOptions options, List<string> tempFiles)
    {
        if (options.ReplayPath != null)
        {
            return [Path.GetFullPath(options.ReplayPath)];
        }

        if (options.ReplayDirectory != null)
        {
            IEnumerable<FileInfo> files = Directory.EnumerateFiles(options.ReplayDirectory, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => ParseReplayTimestamp(file) ?? file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name);

            if (options.LastRounds is { } lastRounds)
                files = files.Take(lastRounds);

            return files.OrderBy(file => ParseReplayTimestamp(file) ?? file.LastWriteTimeUtc)
                .ThenBy(file => file.Name)
                .Select(file => file.FullName)
                .ToArray();
        }

        var replayUrls = await GetRemoteReplayUrlsAsync(options.ReplayRootUrl!, options.LastRounds);
        var downloaded = new List<string>();

        foreach (var replayUrl in replayUrls)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"rmc14-chat-{Guid.NewGuid():N}.zip");
            await using var input = await _http.GetStreamAsync(replayUrl);
            await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
            downloaded.Add(tempPath);
            tempFiles.Add(tempPath);
        }

        return downloaded;
    }

    private async Task<List<string>> GetRemoteReplayUrlsAsync(string rootUrl, int? lastRounds)
    {
        var replayUrls = new List<string>();
        var root = await _http.GetStringAsync($"{rootUrl.TrimEnd('/')}/");

        foreach (var year in HrefFinder.Matches(root).Select(match => match.Groups[1].Value).Reverse())
        {
            var yearHtml = await _http.GetStringAsync($"{rootUrl}/{year}/");
            foreach (var month in HrefFinder.Matches(yearHtml).Select(match => match.Groups[1].Value).Reverse())
            {
                var monthHtml = await _http.GetStringAsync($"{rootUrl}/{year}/{month}/");
                foreach (var day in HrefFinder.Matches(monthHtml).Select(match => match.Groups[1].Value).Reverse())
                {
                    var dayHtml = await _http.GetStringAsync($"{rootUrl}/{year}/{month}/{day}/");
                    foreach (var replay in ReplayFinder.Matches(dayHtml).Select(match => match.Groups[1].Value))
                    {
                        replayUrls.Add($"{rootUrl}/{year}/{month}/{day}/{replay}");
                        if (lastRounds is { } limit && replayUrls.Count >= limit)
                            return replayUrls;
                    }
                }
            }
        }

        return replayUrls;
    }

    private static DateTime? ParseReplayTimestamp(FileInfo file)
    {
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var parts = name.Split('-');
        if (parts.Length < 2)
            return null;

        var datePart = parts[0].Replace('_', '-');
        var timePart = parts[1].Replace('_', ':');

        if (!DateTime.TryParse($"{datePart}T{timePart}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;

        return parsed;
    }

    private static (int? RoundId, DateTime? RecordedAtUtc) ReadMetadata(string replayPath, MappingDataNode? metadata)
    {
        int? roundId = null;
        DateTime? recordedAtUtc = null;

        if (metadata != null)
        {
            if (metadata.TryGet<ValueDataNode>("roundId", out var roundNode) && int.TryParse(roundNode.Value, out var parsedRound))
                roundId = parsedRound;

            if (metadata.TryGet<ValueDataNode>(ReplayConstants.MetaKeyTime, out var timeNode) &&
                DateTime.TryParse(timeNode.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime))
            {
                recordedAtUtc = parsedTime;
            }
        }

        if (recordedAtUtc == null)
            recordedAtUtc = ParseReplayTimestamp(new FileInfo(replayPath));

        return (roundId, recordedAtUtc);
    }

    private void EnsureCompatibleReplay(MappingDataNode? metadata)
    {
        if (metadata == null)
            throw new Exception("Failed to load yaml metadata");

        var replayTypeHash = ((ValueDataNode) metadata[ReplayConstants.MetaKeyTypeHash]).Value;
        if (string.Equals(replayTypeHash, _serializer.GetSerializableTypesHashString(), StringComparison.OrdinalIgnoreCase))
            return;

        metadata.TryGet<ValueDataNode>(ReplayConstants.MetaKeyForkVersion, out var forkVersionNode);
        metadata.TryGet<ValueDataNode>(ReplayConstants.MetaKeyEngineVersion, out var engineVersionNode);

        var forkVersion = forkVersionNode?.Value ?? "unknown";
        var engineVersion = engineVersionNode?.Value ?? "unknown";

        throw new Exception(
            "Replay is incompatible with this build. " +
            $"Replay buildForkVersion={forkVersion}, engineVersion={engineVersion}, " +
            $"replay typeHash={replayTypeHash}, local typeHash={_serializer.GetSerializableTypesHashString()}.");
    }

    private void LoadSerializerPackage(IReplayFileReader fileReader, MappingDataNode? metadata)
    {
        if (metadata == null)
            throw new Exception("Failed to load yaml metadata");

        if (!metadata.TryGet<ValueDataNode>(ReplayConstants.MetaKeyStringHash, out var stringNode))
            throw new Exception("Replay metadata is missing stringHash");

        using var stringFile = fileReader.Open(ReplayConstants.FileStrings);
        using var stringData = new MemoryStream();
        stringFile.CopyTo(stringData);
        _serializer.SetStringSerializerPackage(Convert.FromHexString(stringNode.Value), stringData.ToArray());

        using var cvarFile = fileReader.Open(ReplayConstants.FileCvars);
        _cfg.LoadFromTomlStream(cvarFile);
    }

    private IEnumerable<ReplayChatRecord> ReadChatRecords(
        string replayPath,
        IReplayFileReader fileReader,
        (int? RoundId, DateTime? RecordedAtUtc) meta)
    {
        var metadata = _loadManager.LoadYamlMetadata(fileReader) ?? throw new Exception("Failed to load yaml metadata");

        var startTick = ((ValueDataNode) metadata[ReplayConstants.MetaKeyStartTick]).Value;
        var timeBaseTick = ((ValueDataNode) metadata[ReplayConstants.MetaKeyBaseTick]).Value;
        var timeBaseTimespan = ((ValueDataNode) metadata[ReplayConstants.MetaKeyBaseTime]).Value;

        var totalData = fileReader.AllFiles.Count(x => x.Filename.StartsWith(ReplayConstants.DataFilePrefix));
        var currentTimeBase = (
            new TimeSpan(long.Parse(timeBaseTimespan, CultureInfo.InvariantCulture)),
            new GameTick(uint.Parse(timeBaseTick, CultureInfo.InvariantCulture)));

        var initialTick = new GameTick(uint.Parse(startTick, CultureInfo.InvariantCulture));
        var replayStartTime = TimeSpan.Zero;
        var initialized = false;

        var intBuf = new byte[4];
        var i = 0;
        var name = new ResPath($"{ReplayConstants.DataFilePrefix}{i++}.{ReplayConstants.Ext}");
        while (fileReader.Exists(name))
        {
            Console.WriteLine($"  Reading {name} ({i}/{totalData})");

            using var fileStream = fileReader.Open(name);
            using var decompressStream = new ZStdDecompressStream(fileStream, false);

            fileStream.ReadExactly(intBuf);
            var uncompressedSize = BitConverter.ToInt32(intBuf);

            using var decompressedStream = new MemoryStream(uncompressedSize);
            decompressStream.CopyTo(decompressedStream);
            decompressedStream.Position = 0;

            while (decompressedStream.Position < decompressedStream.Length)
            {
                _serializer.DeserializeDirect(decompressedStream, out GameState state);
                _serializer.DeserializeDirect(decompressedStream, out ReplayMessage msg);

                UpdateTimeBase(msg, ref currentTimeBase);

                var serverTime = GetServerTime(state.ToSequence, currentTimeBase);
                if (!initialized)
                {
                    initialTick = state.ToSequence;
                    replayStartTime = serverTime;
                    initialized = true;
                }

                var replayTime = serverTime - replayStartTime;

                foreach (var message in msg.Messages)
                {
                    if (message is not ChatMessage chat)
                        continue;

                    yield return new ReplayChatRecord(
                        Path.GetFileName(replayPath),
                        meta.RoundId,
                        meta.RecordedAtUtc?.Add(replayTime),
                        replayTime,
                        chat.Channel.ToString(),
                        chat.Message,
                        chat.WrappedMessage,
                        chat.HideChat,
                        chat.HidePopup,
                        chat.SenderEntity.Id.ToString(CultureInfo.InvariantCulture),
                        chat.SenderKey);
                }
            }

            name = new ResPath($"{ReplayConstants.DataFilePrefix}{i++}.{ReplayConstants.Ext}");
        }
    }

    private void UpdateTimeBase(ReplayMessage message, ref (TimeSpan Time, GameTick Tick) currentTimeBase)
    {
        foreach (var obj in message.Messages)
        {
            if (obj is not ReplayMessage.CvarChangeMsg cvar)
                continue;

            foreach (var (name, value) in cvar.ReplicatedCvars)
            {
                _cfg.SetCVar(name, value, force: true);
            }

            currentTimeBase = cvar.TimeBase;
        }
    }

    private TimeSpan GetServerTime(GameTick tick, (TimeSpan Time, GameTick Tick) currentTimeBase)
    {
        var rate = _cfg.GetCVar<int>(CVars.NetTickrate.Name);
        var period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / rate);
        return currentTimeBase.Time + (tick.Value - currentTimeBase.Tick.Value) * period;
    }

    private static Task NoopCallback(float _, float __, LoadingState ___, bool ____) => Task.CompletedTask;

    private static string ToCsv(ReplayChatRecord record)
    {
        return string.Join(",",
            EscapeCsv(record.ReplayFile),
            EscapeCsv(record.RoundId?.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(record.RecordedAtUtc?.ToString("O")),
            EscapeCsv(record.ReplayTime.ToString()),
            EscapeCsv(record.ChatChannel),
            EscapeCsv(record.Message),
            EscapeCsv(record.WrappedMessage),
            EscapeCsv(record.HideChat.ToString()),
            EscapeCsv(record.HidePopup.ToString()),
            EscapeCsv(record.SenderEntity),
            EscapeCsv(record.SenderKey?.ToString(CultureInfo.InvariantCulture)));
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

internal sealed record ReplayChatRecord(
    string ReplayFile,
    int? RoundId,
    DateTime? RecordedAtUtc,
    TimeSpan ReplayTime,
    string ChatChannel,
    string Message,
    string WrappedMessage,
    bool HideChat,
    bool HidePopup,
    string SenderEntity,
    int? SenderKey);
