using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Sources;

public interface ILogSource
{
    string Id { get; }
    string SourceType { get; }
    IAsyncEnumerable<RawLogEntry> ReadAsync(CancellationToken cancellationToken);
}

public interface ILogSourceRegistry
{
    IReadOnlyList<ILogSource> GetSources();
}

public sealed class OptionsLogSourceRegistry : ILogSourceRegistry
{
    private readonly IReadOnlyList<ILogSource> _sources;

    public OptionsLogSourceRegistry(
        IOptions<LogSourcesOptions> options,
        IOptions<IngestionOptions> ingestionOptions,
        IHostEnvironment hostEnvironment,
        ILoggerFactory loggerFactory)
    {
        var checkpointStore = new JsonLogSourceCheckpointStore(
            hostEnvironment.ContentRootPath,
            ingestionOptions.Value,
            loggerFactory.CreateLogger<JsonLogSourceCheckpointStore>());

        _sources = options.Value.Sources
            .Select(source => CreateSource(source, ingestionOptions.Value, checkpointStore, hostEnvironment.ContentRootPath, loggerFactory))
            .ToArray();
    }

    public IReadOnlyList<ILogSource> GetSources() => _sources;

    private static ILogSource CreateSource(
        LogSourceDescriptorOptions descriptor,
        IngestionOptions ingestionOptions,
        ILogSourceCheckpointStore checkpointStore,
        string contentRootPath,
        ILoggerFactory loggerFactory)
    {
        return descriptor.Type.Trim().ToLowerInvariant() switch
        {
            "file" => new FileLogSource(
                descriptor,
                ingestionOptions,
                checkpointStore,
                contentRootPath,
                loggerFactory.CreateLogger<FileLogSource>()),
            _ => throw new InvalidOperationException($"Unsupported log source type: {descriptor.Type}"),
        };
    }
}

internal interface ILogSourceCheckpointStore
{
    long GetOffset(string sourceKey);
    void SaveOffset(string sourceKey, long offset);
}

internal sealed class JsonLogSourceCheckpointStore : ILogSourceCheckpointStore
{
    private readonly string _checkpointPath;
    private readonly ILogger<JsonLogSourceCheckpointStore> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _offsets;

    public JsonLogSourceCheckpointStore(string contentRootPath, IngestionOptions options, ILogger<JsonLogSourceCheckpointStore> logger)
    {
        _checkpointPath = Path.IsPathRooted(options.CheckpointFilePath)
            ? options.CheckpointFilePath
            : Path.GetFullPath(Path.Combine(contentRootPath, options.CheckpointFilePath));
        _logger = logger;
        _offsets = LoadOffsets();
    }

    public long GetOffset(string sourceKey)
    {
        lock (_sync)
        {
            return _offsets.TryGetValue(sourceKey, out var offset) ? offset : 0;
        }
    }

    public void SaveOffset(string sourceKey, long offset)
    {
        lock (_sync)
        {
            _offsets[sourceKey] = Math.Max(0, offset);
            PersistOffsets();
        }
    }

    private Dictionary<string, long> LoadOffsets()
    {
        if (!File.Exists(_checkpointPath))
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(_checkpointPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            return parsed is null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load ingestion checkpoints from {Path}. Starting fresh.", _checkpointPath);
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private void PersistOffsets()
    {
        try
        {
            var directory = Path.GetDirectoryName(_checkpointPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = $"{_checkpointPath}.tmp";
            var json = JsonSerializer.Serialize(_offsets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _checkpointPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist ingestion checkpoints to {Path}.", _checkpointPath);
        }
    }
}

public sealed class FileLogSource : ILogSource
{
    private static readonly Regex TimestampStartRegex = new(
        "^\\d{4}-\\d{2}-\\d{2}(?:[ T]\\d{2}\\s*:\\s*\\d{2}\\s*:\\s*\\d{2}(?:\\.\\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SyslogStartRegex = new(
        "^[A-Za-z]{3}\\s+\\d{1,2}\\s+\\d{2}:\\d{2}:\\d{2}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JsonStartRegex = new(
        "^\\s*\\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KeyValueTimestampStartRegex = new(
        "^\\s*timestamp=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly LogSourceDescriptorOptions _descriptor;
    private readonly IngestionOptions _ingestionOptions;
    private readonly ILogSourceCheckpointStore _checkpointStore;
    private readonly string _resolvedPath;
    private readonly string _checkpointKey;
    private readonly ILogger<FileLogSource> _logger;

    public FileLogSource(
        LogSourceDescriptorOptions descriptor,
        IngestionOptions ingestionOptions,
        ILogSourceCheckpointStore checkpointStore,
        string contentRootPath,
        ILogger<FileLogSource> logger)
    {
        _descriptor = descriptor;
        _ingestionOptions = ingestionOptions;
        _checkpointStore = checkpointStore;
        _resolvedPath = Path.IsPathRooted(descriptor.Path) ? descriptor.Path : Path.GetFullPath(Path.Combine(contentRootPath, descriptor.Path));
        _checkpointKey = $"{descriptor.Id}|{_resolvedPath}";
        _logger = logger;
    }

    public string Id => _descriptor.Id;

    public string SourceType => _descriptor.SourceType;

    public async IAsyncEnumerable<RawLogEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(_resolvedPath))
        {
            _logger.LogWarning("Log source file does not exist: {Path}", _resolvedPath);
            yield break;
        }

        var startOffset = _ingestionOptions.EnableSourceCheckpoints ? _checkpointStore.GetOffset(_checkpointKey) : 0;
        await using var stream = new FileStream(_resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (startOffset < 0 || startOffset > stream.Length)
        {
            startOffset = 0;
        }

        var eventBuilder = new StringBuilder();
        var eventStartOffset = startOffset;
        var currentOffset = startOffset;

        await foreach (var lineSlice in ReadLinesAsync(stream, startOffset, cancellationToken))
        {
            currentOffset = lineSlice.NextOffset;
            if (string.IsNullOrWhiteSpace(lineSlice.Line))
            {
                continue;
            }

            if (eventBuilder.Length == 0)
            {
                eventStartOffset = lineSlice.StartOffset;
                eventBuilder.Append(lineSlice.Line);
                continue;
            }

            if (IsEventStart(lineSlice.Line))
            {
                yield return CreateEntry(eventBuilder.ToString(), eventStartOffset, lineSlice.StartOffset);
                eventBuilder.Clear();
                eventStartOffset = lineSlice.StartOffset;
                eventBuilder.Append(lineSlice.Line);
                continue;
            }

            eventBuilder.AppendLine();
            eventBuilder.Append(lineSlice.Line);
        }

        if (eventBuilder.Length > 0)
        {
            yield return CreateEntry(eventBuilder.ToString(), eventStartOffset, currentOffset);
        }

        if (_ingestionOptions.EnableSourceCheckpoints)
        {
            _checkpointStore.SaveOffset(_checkpointKey, currentOffset);
        }
    }

    private RawLogEntry CreateEntry(string text, long startOffset, long endOffset)
    {
        return new RawLogEntry(
            Id,
            SourceType,
            text,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["path"] = _resolvedPath,
                ["start_offset"] = startOffset.ToString(CultureInfo.InvariantCulture),
                ["end_offset"] = endOffset.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static bool IsEventStart(string line)
    {
        return TimestampStartRegex.IsMatch(line)
               || SyslogStartRegex.IsMatch(line)
               || JsonStartRegex.IsMatch(line)
               || KeyValueTimestampStartRegex.IsMatch(line);
    }

    private static async IAsyncEnumerable<LineSlice> ReadLinesAsync(
        FileStream stream,
        long startOffset,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int bufferSize = 8192;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var lineBuffer = new ArrayBufferWriter<byte>();
        var lineStartOffset = startOffset;

        stream.Seek(startOffset, SeekOrigin.Begin);
        var currentOffset = startOffset;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var chunkStartOffset = currentOffset;
                var segmentStart = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    if (i > segmentStart)
                    {
                        lineBuffer.Write(buffer.AsSpan(segmentStart, i - segmentStart));
                    }

                    var nextOffset = chunkStartOffset + i + 1;
                    yield return new LineSlice(DecodeLine(lineBuffer.WrittenSpan), lineStartOffset, nextOffset);
                    lineBuffer.Clear();
                    lineStartOffset = nextOffset;
                    segmentStart = i + 1;
                }

                if (segmentStart < read)
                {
                    lineBuffer.Write(buffer.AsSpan(segmentStart, read - segmentStart));
                }

                currentOffset = chunkStartOffset + read;
            }

            if (lineBuffer.WrittenCount > 0)
            {
                yield return new LineSlice(DecodeLine(lineBuffer.WrittenSpan), lineStartOffset, currentOffset);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string DecodeLine(ReadOnlySpan<byte> bytes)
    {
        var lineBytes = bytes;
        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
        {
            lineBytes = lineBytes[..^1];
        }

        var line = Encoding.UTF8.GetString(lineBytes);
        if (line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        return line;
    }

    private readonly record struct LineSlice(string Line, long StartOffset, long NextOffset);
}
