using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project.Persistence;

/// <summary>What became of a per-user file when it was read.</summary>
public enum UserFileState
{
    /// <summary>Read, or missing (the defaults; the file is created by the first write).</summary>
    Usable,

    /// <summary>Not a file of this format: set aside as <c>*.damaged</c>; the defaults are used and the next write creates
    /// a new file.</summary>
    Damaged,

    /// <summary>Written by a newer version: the defaults are used and the file is never overwritten.</summary>
    Newer,

    /// <summary>Could not be read (I/O): the defaults are used and the file is never overwritten.</summary>
    Unreadable
}

/// <summary>
/// One small JSON document of one Windows user (D057 §2): <c>{ "format", "formatVersion", "value" }</c>. Read whole at
/// start; written whole, atomically (a temporary file moved over it), so a crash never leaves half a file. Two running
/// instances write in turn — the last write wins (the document is view state and preferences, never project data). A file
/// that isn't this format is set aside as <c>*.damaged</c>; one of a newer version or one that can't be read is never
/// overwritten.
/// </summary>
public sealed class UserJsonFile<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _formatId;
    private readonly int _formatVersion;
    private readonly ILogger _logger;

    public UserJsonFile(string filePath, string formatId, int formatVersion, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        _formatId = formatId;
        _formatVersion = formatVersion;
        _logger = logger;
    }

    public string FilePath { get; }

    public UserFileState State { get; private set; } = UserFileState.Usable;

    /// <summary>The stored value, or a new <typeparamref name="T"/> when there is none or it can't be used.</summary>
    public T Read()
    {
        string text;
        try
        {
            if (!File.Exists(FilePath)) return new T();
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            State = UserFileState.Unreadable;
            _logger.LogWarning(ex, "The user file {Path} could not be read; the defaults are used.", FilePath);
            return new T();
        }

        try
        {
            var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not an object");
            if (root["format"]?.GetValue<string>() != _formatId) throw new JsonException("another format");
            var version = root["formatVersion"]?.GetValue<int>() ?? throw new JsonException("no format version");
            if (version > _formatVersion)
            {
                State = UserFileState.Newer;
                _logger.LogWarning("The user file {Path} is of a newer version ({Version}); the defaults are used and it is kept.",
                    FilePath, version);
                return new T();
            }
            if (version < 1) throw new JsonException("an unknown format version");
            return root["value"]?.Deserialize<T>(Options) ?? throw new JsonException("no value");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            State = UserFileState.Damaged;
            _logger.LogWarning(ex, "The user file {Path} is damaged; it is set aside and the defaults are used.", FilePath);
            SetAside();
            return new T();
        }
    }

    /// <summary>Writes <paramref name="value"/>; false (logged) when it was not written.</summary>
    public bool Write(T value)
    {
        if (State is UserFileState.Newer or UserFileState.Unreadable) return false;
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var document = new JsonObject
            {
                ["format"] = _formatId,
                ["formatVersion"] = _formatVersion,
                ["value"] = JsonSerializer.SerializeToNode(value, Options)
            };
            File.WriteAllText(temp, document.ToJsonString(Options));
            File.Move(temp, FilePath, overwrite: true);
            State = UserFileState.Usable;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The user file {Path} could not be written.", FilePath);
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private void SetAside()
    {
        try
        {
            File.Move(FilePath, FilePath + ".damaged", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The damaged user file {Path} could not be set aside.", FilePath);
        }
    }
}
