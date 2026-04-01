using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Reflection;
using Robust.Shared.Replays;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

internal static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Regex HrefFinder = new("<a href=\"([\\d]+)/\">", RegexOptions.Compiled);
    private static readonly Regex ReplayFinder = new("<a href=\"([^\"]+\\.zip)\">", RegexOptions.Compiled);

    public static int Main(string[] args)
    {
        var tempFiles = new List<string>();
        try
        {
            SQLitePCL.Batteries_V2.Init();

            var options = Options.Parse(args);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = options.ContentDbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();

            var replayCandidates = ResolveReplayCandidates(options, tempFiles);

            var attemptedBuilds = new List<string>();
            var matchedReplayCount = 0;
            long totalExtractedBytes = 0;
            var totalExtractedFiles = 0;
            var totalChatMessages = 0;

            foreach (var replayPath in replayCandidates)
            {
                var replayInfo = ReplayInfo.Read(replayPath);
                Console.WriteLine($"Replay build: {replayInfo.ForkId}/{replayInfo.ForkVersion}");
                Console.WriteLine($"Replay engine: {replayInfo.EngineVersion}");

                var cachedVersion = CachedVersion.Find(
                    connection,
                    replayInfo.ForkId,
                    replayInfo.ForkVersion);

                if (cachedVersion == null)
                {
                    attemptedBuilds.Add($"{replayInfo.ForkId}/{replayInfo.ForkVersion}");
                    Console.WriteLine("No matching launcher cache entry for this replay. Trying next replay...");
                    continue;
                }

                matchedReplayCount++;
                Console.WriteLine($"Using cached version id {cachedVersion.Id} with engine {cachedVersion.EngineVersion}");
                Console.WriteLine($"Selected replay: {replayPath}");
                Console.WriteLine($"Writing files under {options.OutputPath}");

                Directory.CreateDirectory(options.OutputPath);

                var replayFolderName = GetReplayFolderName(replayPath, replayInfo);
                var replayOutputPath = Path.Combine(options.OutputPath, replayFolderName);
                var buildOutputPath = Path.Combine(replayOutputPath, "build");
                var chatOutputPath = ResolveChatOutputPath(options.ChatOutputPath, replayOutputPath);

                Console.WriteLine($"Replay export folder: {replayOutputPath}");
                Directory.CreateDirectory(replayOutputPath);

                var (fileCount, extractedBytes) = ExtractCachedBuild(connection, cachedVersion.Id, buildOutputPath);
                totalExtractedFiles += fileCount;
                totalExtractedBytes += extractedBytes;

                File.WriteAllLines(
                    Path.Combine(replayOutputPath, "launcher_cache_extract.txt"),
                    [
                        $"replay_file={Path.GetFileName(replayPath)}",
                        $"fork_id={replayInfo.ForkId}",
                        $"fork_version={replayInfo.ForkVersion}",
                        $"engine_version={cachedVersion.EngineVersion}",
                        $"content_version_id={cachedVersion.Id}",
                        $"extracted_at_utc={DateTime.UtcNow:O}",
                    ]);

                Console.WriteLine($"Writing chat logs to {chatOutputPath}");
                var chatCount = ExtractChatLogs(replayPath, buildOutputPath, chatOutputPath);
                totalChatMessages += chatCount;
                Console.WriteLine($"Wrote {chatCount} chat messages.");
            }

            if (matchedReplayCount == 0)
            {
                var detail = attemptedBuilds.Count == 0
                    ? "No replay candidates were found."
                    : $"Tried replay builds: {string.Join(", ", attemptedBuilds)}";
                throw new InvalidOperationException(
                    $"No cached launcher content matched the selected replay source. {detail}");
            }

            Console.WriteLine(
                $"Done. Exported {matchedReplayCount} replay(s), wrote {totalChatMessages} chat messages, extracted {totalExtractedFiles} files, {totalExtractedBytes} bytes.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
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

    private static (int FileCount, long TotalBytes) ExtractCachedBuild(SqliteConnection connection, long versionId, string outputPath)
    {
        var fileCount = 0;
        long totalBytes = 0;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.Path, c.Size, c.Compression, c.Data
            FROM ContentManifest m
            INNER JOIN Content c ON c.Id = m.ContentId
            WHERE m.VersionId = $versionId
            ORDER BY m.Path
            """;
        command.Parameters.AddWithValue("$versionId", versionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = reader.GetString(0).Replace('/', Path.DirectorySeparatorChar);
            var expectedSize = reader.GetInt64(1);
            var compression = reader.GetInt32(2);
            var data = (byte[]) reader["Data"];

            var fullPath = Path.Combine(outputPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var bytes = Decompress(data, compression, expectedSize);
            File.WriteAllBytes(fullPath, bytes);

            fileCount++;
            totalBytes += bytes.LongLength;

            if (fileCount % 500 == 0)
                Console.WriteLine($"Extracted {fileCount} files...");
        }

        return (fileCount, totalBytes);
    }

    private static int ExtractChatLogs(string replayPath, string extractedBuildPath, string chatOutputPath)
    {
        var assembliesDir = Path.Combine(extractedBuildPath, "Assemblies");
        if (!Directory.Exists(assembliesDir))
            throw new DirectoryNotFoundException($"Assemblies directory not found: {assembliesDir}");

        var contentSharedPath = Path.Combine(assembliesDir, "Content.Shared.dll");
        if (!File.Exists(contentSharedPath))
            throw new FileNotFoundException("Extracted Content.Shared.dll was not found", contentSharedPath);

        var contentDatabasePath = Path.Combine(assembliesDir, "Content.Shared.Database.dll");

        var deps = IoCManager.InitThread();
        deps.Clear();

        RegisterClientIocReflective(deps);
        deps.BuildGraph();

        var cfg = IoCManager.Resolve<IConfigurationManager>();
        Invoke(cfg, "Initialize", false);

        var reflection = IoCManager.Resolve<IReflectionManager>();
        reflection.Initialize();

        var assemblies = new List<Assembly>
        {
            Assembly.Load("Robust.Client"),
            typeof(IConfigurationManager).Assembly,
        };

        assemblies.Add(Assembly.LoadFrom(contentSharedPath));
        if (File.Exists(contentDatabasePath))
            assemblies.Add(Assembly.LoadFrom(contentDatabasePath));

        foreach (var assembly in assemblies)
            Invoke(cfg, "LoadCVarsFromAssembly", assembly);

        reflection.LoadAssemblies(assemblies);

        var serializer = IoCManager.Resolve<IRobustSerializer>();
        serializer.Initialize();

        var metadata = ReplayInfo.ReadMetadataMap(replayPath);
        using (var archive = ZipFile.OpenRead(replayPath))
        {
            var stringsEntry = archive.GetEntry("_replay/strings.dat")
                ?? throw new InvalidOperationException("Replay is missing _replay/strings.dat");
            using (var stream = stringsEntry.Open())
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                var mappedStringType = typeof(IRobustSerializer).Assembly.GetType("Robust.Shared.Serialization.IRobustMappedStringSerializer", throwOnError: true)!;
                var mappedStringSerializer = IoCManager.ResolveType(mappedStringType);
                Invoke(mappedStringSerializer, "SetPackage",
                    Convert.FromHexString(ReplayInfo.Require(metadata, "stringHash")),
                    copy.ToArray());
            }

            var cvarsEntry = archive.GetEntry("_replay/cvars.toml")
                ?? throw new InvalidOperationException("Replay is missing _replay/cvars.toml");
            using (var stream = cvarsEntry.Open())
            {
                cfg.LoadFromTomlStream(stream);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(chatOutputPath))!);
        using var archiveForRead = ZipFile.OpenRead(replayPath);
        using var writer = new StreamWriter(chatOutputPath, false);

        var chatType = assemblies.Last(a => a.GetName().Name == "Content.Shared").GetType("Content.Shared.Chat.ChatMessage", throwOnError: true)!;
        var chatChannelField = chatType.GetField("Channel")!;
        var chatMessageField = chatType.GetField("Message")!;
        var chatWrappedMessageField = chatType.GetField("WrappedMessage")!;
        var chatSenderEntityField = chatType.GetField("SenderEntity")!;
        var chatSenderKeyField = chatType.GetField("SenderKey")!;
        var chatHideChatField = chatType.GetField("HideChat")!;
        var chatHidePopupField = chatType.GetField("HidePopup")!;

        var initialTimeBase = (
            new TimeSpan(long.Parse(ReplayInfo.Require(metadata, "timeBaseTime"), CultureInfo.InvariantCulture)),
            new GameTick(uint.Parse(ReplayInfo.Require(metadata, "timeBaseTick"), CultureInfo.InvariantCulture)));

        var currentTimeBase = initialTimeBase;
        TimeSpan? initialServerTime = null;
        var chatCount = 0;

        var dataEntries = archiveForRead.Entries
            .Where(e => e.FullName.StartsWith("_replay/data_", StringComparison.Ordinal) && e.FullName.EndsWith(".dat", StringComparison.Ordinal))
            .OrderBy(e => ParseReplayDataIndex(e.FullName))
            .ToArray();

        var intBuf = new byte[4];
        for (var entryIndex = 0; entryIndex < dataEntries.Length; entryIndex++)
        {
            var entry = dataEntries[entryIndex];
            if (entryIndex % 100 == 0 || entryIndex == dataEntries.Length - 1)
                Console.WriteLine($"Reading replay chunk {entryIndex + 1}/{dataEntries.Length}");

            using var fileStream = entry.Open();
            using var decompressStream = new ZStdDecompressStream(fileStream, false);

            fileStream.ReadExactly(intBuf);
            var uncompressedSize = BitConverter.ToInt32(intBuf);

            using var decompressedStream = new MemoryStream(uncompressedSize);
            decompressStream.CopyTo(decompressedStream);
            decompressedStream.Position = 0;

            while (decompressedStream.Position < decompressedStream.Length)
            {
                serializer.DeserializeDirect(decompressedStream, out Robust.Shared.GameStates.GameState state);
                serializer.DeserializeDirect(decompressedStream, out ReplayMessage msg);

                UpdateTimeBaseFromReplayMessage(msg, ref currentTimeBase);

                var serverTime = GetServerTime(state.ToSequence, currentTimeBase, cfg);
                initialServerTime ??= serverTime;
                var replayTime = serverTime - initialServerTime.Value;

                foreach (var message in msg.Messages)
                {
                    if (message == null || message.GetType() != chatType)
                        continue;

                    var row = new ChatLogRow(
                        Path.GetFileName(replayPath),
                        int.TryParse(ReplayInfo.Require(metadata, "roundId"), CultureInfo.InvariantCulture, out var roundId) ? roundId : null,
                        ReplayInfo.TryParseReplayDate(metadata)?.Add(replayTime),
                        replayTime,
                        chatChannelField.GetValue(message)?.ToString() ?? "Unknown",
                        chatMessageField.GetValue(message)?.ToString() ?? string.Empty,
                        chatWrappedMessageField.GetValue(message)?.ToString() ?? string.Empty,
                        Convert.ToString(chatSenderEntityField.GetValue(message), CultureInfo.InvariantCulture) ?? string.Empty,
                        chatSenderKeyField.GetValue(message) as int?,
                        chatHideChatField.GetValue(message) as bool? ?? false,
                        chatHidePopupField.GetValue(message) as bool? ?? false);

                    writer.WriteLine(JsonSerializer.Serialize(row));
                    chatCount++;
                }
            }
        }

        return chatCount;
    }

    private static string GetReplayFolderName(string replayPath, ReplayInfo replayInfo)
    {
        var replayName = Path.GetFileNameWithoutExtension(replayPath);
        if (!string.IsNullOrWhiteSpace(replayName) &&
            !replayName.StartsWith("launcher-cache-replay-", StringComparison.OrdinalIgnoreCase))
        {
            return SanitizePathSegment(replayName);
        }

        var metadata = ReplayInfo.ReadMetadataMap(replayPath);
        if (metadata.TryGetValue("roundId", out var roundId) && !string.IsNullOrWhiteSpace(roundId))
            return $"round_{SanitizePathSegment(roundId)}";

        return $"{SanitizePathSegment(replayInfo.ForkId)}_{SanitizePathSegment(replayInfo.ForkVersion)}";
    }

    private static string ResolveChatOutputPath(string? requestedChatOutputPath, string replayOutputPath)
    {
        if (string.IsNullOrWhiteSpace(requestedChatOutputPath))
            return Path.Combine(replayOutputPath, "chat.jsonl");

        var hasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(requestedChatOutputPath));
        if (hasExtension)
            return Path.Combine(replayOutputPath, Path.GetFileName(requestedChatOutputPath));

        return Path.Combine(requestedChatOutputPath, Path.GetFileName(replayOutputPath), "chat.jsonl");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static int ParseReplayDataIndex(string fullName)
    {
        var fileName = Path.GetFileNameWithoutExtension(fullName);
        var underscore = fileName.LastIndexOf('_');
        if (underscore < 0)
            return int.MaxValue;

        return int.TryParse(fileName[(underscore + 1)..], CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : int.MaxValue;
    }

    private static void UpdateTimeBaseFromReplayMessage(ReplayMessage message, ref (TimeSpan Time, GameTick Tick) currentTimeBase)
    {
        foreach (var entry in message.Messages)
        {
            if (entry is ReplayMessage.CvarChangeMsg cvar)
                currentTimeBase = cvar.TimeBase;
        }
    }

    private static TimeSpan GetServerTime(GameTick tick, (TimeSpan Time, GameTick Tick) currentTimeBase, IConfigurationManager cfg)
    {
        var rate = cfg.GetCVar<int>(CVars.NetTickrate.Name);
        var period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / rate);
        return currentTimeBase.Time + (tick.Value - currentTimeBase.Tick.Value) * period;
    }

    private static void RegisterClientIocReflective(IDependencyCollection deps)
    {
        var clientAssembly = Assembly.Load("Robust.Client");
        var clientIocType = clientAssembly.GetType("Robust.Client.ClientIoC", throwOnError: true)!;
        var registerMethod = clientIocType.GetMethod("RegisterIoC", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(clientIocType.FullName, "RegisterIoC");
        var displayModeType = clientAssembly.GetType("Robust.Client.GameController+DisplayMode", throwOnError: true)!;
        var headless = Enum.Parse(displayModeType, "Headless");
        registerMethod.Invoke(null, [headless, deps]);
    }

    private static object? Invoke(object target, string method, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == method && m.GetParameters().Length == args.Length)
            .ToArray();

        foreach (var candidate in methods)
        {
            var parameters = candidate.GetParameters();
            var compatible = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (args[i] == null)
                    continue;

                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]) &&
                    !(parameters[i].ParameterType.IsValueType && parameters[i].ParameterType == args[i]!.GetType()))
                {
                    compatible = false;
                    break;
                }
            }

            if (compatible)
                return candidate.Invoke(target, args);
        }

        throw new MissingMethodException(target.GetType().FullName, method);
    }

    private static IReadOnlyList<string> ResolveReplayCandidates(Options options, List<string> tempFiles)
    {
        if (options.ReplayPath != null)
            return [options.ReplayPath];

        if (options.ReplayUrl != null)
            return [DownloadReplay(options.ReplayUrl, tempFiles)];

        var urls = GetRemoteReplayUrls(options.ReplayRootUrl!, options.LastRounds ?? 1);
        if (urls.Count == 0)
            throw new InvalidOperationException($"No replay URLs found under {options.ReplayRootUrl}");

        var results = new List<string>(urls.Count);
        foreach (var url in urls)
            results.Add(DownloadReplay(url, tempFiles));

        return results;
    }

    private static string DownloadReplay(string replayUrl, List<string> tempFiles)
    {
        Console.WriteLine($"Downloading replay {replayUrl}");
        var tempPath = Path.Combine(Path.GetTempPath(), $"launcher-cache-replay-{Guid.NewGuid():N}.zip");
        using var response = Http.GetAsync(replayUrl).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var input = response.Content.ReadAsStream();
        using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        tempFiles.Add(tempPath);
        return tempPath;
    }

    private static List<string> GetRemoteReplayUrls(string rootUrl, int limit)
    {
        var replayUrls = new List<string>();
        var normalizedRoot = rootUrl.TrimEnd('/');
        var root = Http.GetStringAsync($"{normalizedRoot}/").GetAwaiter().GetResult();

        foreach (var year in HrefFinder.Matches(root).Select(match => match.Groups[1].Value).Reverse())
        {
            var yearHtml = Http.GetStringAsync($"{normalizedRoot}/{year}/").GetAwaiter().GetResult();
            foreach (var month in HrefFinder.Matches(yearHtml).Select(match => match.Groups[1].Value).Reverse())
            {
                var monthHtml = Http.GetStringAsync($"{normalizedRoot}/{year}/{month}/").GetAwaiter().GetResult();
                foreach (var day in HrefFinder.Matches(monthHtml).Select(match => match.Groups[1].Value).Reverse())
                {
                    var dayHtml = Http.GetStringAsync($"{normalizedRoot}/{year}/{month}/{day}/").GetAwaiter().GetResult();
                    foreach (var replay in ReplayFinder.Matches(dayHtml).Select(match => match.Groups[1].Value))
                    {
                        replayUrls.Add($"{normalizedRoot}/{year}/{month}/{day}/{replay}");
                        if (replayUrls.Count >= limit)
                            return replayUrls;
                    }
                }
            }
        }

        return replayUrls;
    }

    private static byte[] Decompress(byte[] data, int compression, long expectedSize)
    {
        return compression switch
        {
            0 => data,
            2 => DecompressZstd(data, expectedSize),
            _ => throw new NotSupportedException($"Unsupported launcher cache compression type: {compression}"),
        };
    }

    private static byte[] DecompressZstd(byte[] data, long expectedSize)
    {
        using var input = new MemoryStream(data, writable: false);
        using var zstd = new ZStdDecompressStream(input, ownStream: false);
        using var output = expectedSize > 0
            ? new MemoryStream(capacity: checked((int) Math.Min(expectedSize, int.MaxValue)))
            : new MemoryStream();
        zstd.CopyTo(output);

        if (expectedSize > 0 && output.Length != expectedSize)
            Console.Error.WriteLine($"Warning: expected {expectedSize} bytes, got {output.Length} bytes after decompression.");

        return output.ToArray();
    }
}

sealed class Options
{
    public const string DefaultReplayRootUrl = "https://replays.rouny-ss14.com/replays/alamo";

    public string? ReplayPath { get; init; }
    public string? ReplayUrl { get; init; }
    public string? ReplayRootUrl { get; init; }
    public int? LastRounds { get; init; }
    public required string OutputPath { get; init; }
    public string? ChatOutputPath { get; init; }
    public required string ContentDbPath { get; init; }

    public static Options Parse(string[] args)
    {
        string? replayPath = null;
        string? replayUrl = null;
        string? replayRootUrl = null;
        string? outputPath = null;
        string? chatOutputPath = null;
        int? lastRounds = null;
        var contentDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Space Station 14",
            "launcher",
            "content.db");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--replay":
                    replayPath = Next(args, ref i, "--replay");
                    break;
                case "--replay-url":
                    replayUrl = Next(args, ref i, "--replay-url");
                    break;
                case "--replay-root-url":
                    replayRootUrl = Next(args, ref i, "--replay-root-url");
                    break;
                case "--last-rounds":
                    if (!int.TryParse(Next(args, ref i, "--last-rounds"), CultureInfo.InvariantCulture, out var parsedLastRounds) || parsedLastRounds <= 0)
                        throw new ArgumentException("--last-rounds must be a positive integer");
                    lastRounds = parsedLastRounds;
                    break;
                case "--output":
                    outputPath = Next(args, ref i, "--output");
                    break;
                case "--chat-output":
                    chatOutputPath = Next(args, ref i, "--chat-output");
                    break;
                case "--content-db":
                    contentDbPath = Next(args, ref i, "--content-db");
                    break;
                case "--help":
                case "-h":
                    PrintUsageAndExit();
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("--output is required");

        var explicitSources = 0;
        if (!string.IsNullOrWhiteSpace(replayPath))
            explicitSources++;
        if (!string.IsNullOrWhiteSpace(replayUrl))
            explicitSources++;

        if (explicitSources > 1)
            throw new ArgumentException("Specify only one of --replay or --replay-url");

        if (explicitSources == 0)
            replayRootUrl ??= DefaultReplayRootUrl;

        if (explicitSources > 0 && !string.IsNullOrWhiteSpace(replayRootUrl))
            throw new ArgumentException("--replay-root-url cannot be used with --replay or --replay-url");

        return new Options
        {
            ReplayPath = replayPath == null ? null : Path.GetFullPath(replayPath),
            ReplayUrl = replayUrl,
            ReplayRootUrl = replayRootUrl,
            LastRounds = lastRounds,
            OutputPath = Path.GetFullPath(outputPath),
            ChatOutputPath = string.IsNullOrWhiteSpace(chatOutputPath) ? null : Path.GetFullPath(chatOutputPath),
            ContentDbPath = Path.GetFullPath(contentDbPath),
        };
    }

    private static string Next(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {name}");

        return args[++i];
    }

    private static void PrintUsageAndExit()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Tools/launcher_cache_extract -- --replay <replay.zip> --output <directory> [--content-db <content.db>]");
        Console.WriteLine("  dotnet run --project Tools/launcher_cache_extract -- --replay-url <replay.zip url> --output <directory> [--content-db <content.db>]");
        Console.WriteLine("  dotnet run --project Tools/launcher_cache_extract -- --last-rounds <n> --output <directory> [--chat-output <chat.jsonl>] [--replay-root-url <url>] [--content-db <content.db>]");
        Environment.Exit(0);
    }
}

sealed class ReplayInfo
{
    public required string ForkId { get; init; }
    public required string ForkVersion { get; init; }
    public required string EngineVersion { get; init; }

    public static ReplayInfo Read(string replayPath)
    {
        var map = ReadMetadataMap(replayPath);

        return new ReplayInfo
        {
            ForkId = Require(map, "buildForkId"),
            ForkVersion = Require(map, "buildForkVersion"),
            EngineVersion = Require(map, "engineVersion"),
        };
    }

    public static Dictionary<string, string> ReadMetadataMap(string replayPath)
    {
        using var archive = ZipFile.OpenRead(replayPath);
        var entry = archive.GetEntry("_replay/replay.yml")
            ?? throw new InvalidOperationException($"Replay metadata not found in {replayPath}");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return ParseYamlMap(reader.ReadToEnd());
    }

    private static Dictionary<string, string> ParseYamlMap(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "..." || trimmed.StartsWith('#'))
                continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim().Trim('"');
            map[key] = value;
        }

        return map;
    }

    public static string Require(Dictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Replay metadata is missing {key}");

        return value;
    }

    public static DateTime? TryParseReplayDate(Dictionary<string, string> map)
    {
        if (!map.TryGetValue("time", out var value))
            return null;

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;

        return parsed;
    }
}

sealed record ChatLogRow(
    string ReplayFile,
    int? RoundId,
    DateTime? RecordedAtUtc,
    TimeSpan ReplayTime,
    string ChatChannel,
    string Message,
    string WrappedMessage,
    string SenderEntity,
    int? SenderKey,
    bool HideChat,
    bool HidePopup);

sealed class CachedVersion
{
    public required long Id { get; init; }
    public required string EngineVersion { get; init; }

    public static CachedVersion? Find(SqliteConnection connection, string forkId, string forkVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cv.Id, ced.ModuleVersion
            FROM ContentVersion cv
            LEFT JOIN ContentEngineDependency ced ON ced.VersionId = cv.Id AND ced.ModuleName = 'Robust'
            WHERE cv.ForkId = $forkId AND cv.ForkVersion = $forkVersion
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$forkId", forkId);
        command.Parameters.AddWithValue("$forkVersion", forkVersion);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new CachedVersion
        {
            Id = reader.GetInt64(0),
            EngineVersion = reader.IsDBNull(1) ? "unknown" : reader.GetString(1),
        };
    }
}
