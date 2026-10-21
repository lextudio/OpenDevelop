using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDevelop.Profiler.Sessions;

public enum SessionKind
{
    Cpu,
    Heap,
    Allocation,
    Dump,
}

public enum SessionStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// The small OpenDevelop-only index kept beside a capture. It is not a profiling format: the raw
/// <c>.nettrace</c> next to it is the record, and it opens without this file.
/// </summary>
public sealed class SessionInfo
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionKind Kind { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionStatus Status { get; set; }

    public DateTime StartedUtc { get; init; }
    public string DisplayName { get; init; } = "";
    public int? ProcessId { get; init; }

    /// <summary>File name of the raw capture, relative to the session directory.</summary>
    public string Artifact { get; init; } = "";

    /// <summary>Why a session failed. Short and free of environment data.</summary>
    public string? Error { get; set; }
}

/// <summary>A session that has been created on disk but may not have an artifact yet.</summary>
public sealed record SessionHandle(SessionInfo Info, string Directory)
{
    public string ArtifactPath => Path.Combine(Directory, Info.Artifact);
}

/// <summary>Keeps one directory per capture under a root, e.g. <c>.opendevelop/profiling</c>.</summary>
public sealed class ProfilingSessionStore(string rootDirectory)
{
    const string IndexFileName = "session.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RootDirectory { get; } = rootDirectory;

    public SessionHandle Create(SessionKind kind, string displayName, int? processId)
    {
        var started = DateTime.UtcNow;
        var id = $"{started:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..6]}";
        var info = new SessionInfo
        {
            Id = id,
            Kind = kind,
            Status = SessionStatus.Running,
            StartedUtc = started,
            DisplayName = displayName,
            ProcessId = processId,
            Artifact = kind switch
            {
                SessionKind.Cpu => "cpu.nettrace",
                SessionKind.Allocation => "allocation.nettrace",
                SessionKind.Dump => "process.dmp",
                _ => "heap.nettrace",
            },
        };
        var directory = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(directory);
        var handle = new SessionHandle(info, directory);
        Save(handle);
        return handle;
    }

    public void Finish(SessionHandle handle, SessionStatus status, string? error = null)
    {
        handle.Info.Status = status;
        handle.Info.Error = error;
        Save(handle);
    }

    /// <summary>All readable sessions, newest first. Damaged or foreign folders are skipped, never fatal.</summary>
    public IReadOnlyList<SessionHandle> List()
    {
        if (!Directory.Exists(RootDirectory))
            return [];

        var sessions = new List<SessionHandle>();
        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var info = TryLoad(Path.Combine(directory, IndexFileName));
            if (info is not null)
                sessions.Add(new SessionHandle(info, directory));
        }
        return sessions.OrderByDescending(s => s.Info.StartedUtc).ToList();
    }

    /// <summary>
    /// Deletes the oldest sessions beyond the newest <paramref name="keep"/>, so captures do not pile up for ever. A session that
    /// is still running is never deleted, and neither is anything that is not a session folder. Returns how many were removed.
    /// </summary>
    public int Prune(int keep)
    {
        var removed = 0;
        foreach (var session in List().Skip(Math.Max(keep, 0)).Where(s => s.Info.Status != SessionStatus.Running))
        {
            try
            {
                Delete(session);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // In use (a capture open in a viewer, say): leave it for the next time.
            }
        }
        return removed;
    }

    /// <summary>Deletes a session folder. Refuses anything that is not a direct child of the root.</summary>
    public void Delete(SessionHandle handle)
    {
        var full = Path.GetFullPath(handle.Directory);
        var root = Path.GetFullPath(RootDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), comparison))
            throw new InvalidOperationException($"'{handle.Directory}' is not a session of this store.");
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    // Written beside the index and moved over it, so a crash part way leaves the previous index rather than a truncated one.
    void Save(SessionHandle handle)
    {
        var index = Path.Combine(handle.Directory, IndexFileName);
        var temporary = index + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(handle.Info, JsonOptions));
        File.Move(temporary, index, overwrite: true);
    }

    static SessionInfo? TryLoad(string indexPath)
    {
        try
        {
            return File.Exists(indexPath)
                ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(indexPath), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
