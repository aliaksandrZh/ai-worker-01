using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.BuildWorker.Builds;

/// <summary>
/// The durable local build-job state (02 §12b, decision 81): one JSON document per `buildId` under
/// `<StateRoot>/jobs/`, build logs under `<StateRoot>/logs/` and the pinned materialized build context
/// under `<StateRoot>/context/<buildId>/`. Writes are single-file atomic (temp + rename) so a crash
/// loses at most the last transition, and the startup rescan reconciles from Docker state. Results are
/// therefore retrievable after worker restarts, and redispatched builds reuse the same buildId
/// (the runner rebuilds idempotently from the pinned snapshot — same input, same tag).
/// The Build Worker has no database: this directory is the whole operational state (§4 data ownership).
/// </summary>
public sealed class BuildDurableStore
{
    private readonly string _stateRoot;

    public BuildDurableStore(Microsoft.Extensions.Options.IOptions<BuildOptions> options)
    {
        _stateRoot = options.Value.StateRoot;
        Directory.CreateDirectory(JobsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ContextRoot);
    }

    private string JobsDirectory => Path.Combine(_stateRoot, "jobs");
    private string LogsDirectory => Path.Combine(_stateRoot, "logs");
    private string ContextRoot => Path.Combine(_stateRoot, "context");

    public string LogFile(Guid buildId) => Path.Combine(LogsDirectory, $"{buildId}.log");

    /// <summary>Raised from Insert when the buildId file already exists (idempotency race guard).</summary>
    public sealed class BuildAlreadyExistsException(Guid buildId)
        : InvalidOperationException($"build job {buildId} already exists");

    /// <summary>
    /// The persisted build-job document (camelCase on disk). `imageTag` is fixed at insert time from the
    /// pinned profile version so a restart can finalize a `building` job whose daemon build finished
    /// (tag exists) without recomputing anything.
    /// </summary>
    public sealed record BuildJobDoc
    {
        [JsonPropertyName("buildId")] public Guid BuildId { get; set; }
        [JsonPropertyName("profile")] public string Profile { get; set; } = null!;
        [JsonPropertyName("profileVersion")] public int ProfileVersion { get; set; }
        [JsonPropertyName("sourceCommitSha")] public string? SourceCommitSha { get; set; }
        [JsonPropertyName("configSnapshot")] public string ConfigSnapshot { get; set; } = null!;
        [JsonPropertyName("buildContextDirectory")] public string BuildContextDirectory { get; set; } = null!;
        [JsonPropertyName("state")] public string State { get; set; } = BuildStates.Queued;
        [JsonPropertyName("imageTag")] public string? ImageTag { get; set; }
        [JsonPropertyName("imageId")] public string? ImageId { get; set; }
        [JsonPropertyName("imageDigest")] public string? ImageDigest { get; set; }
        [JsonPropertyName("buildLogReference")] public string? BuildLogReference { get; set; }
        [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonPropertyName("executionAttempts")] public int ExecutionAttempts { get; set; }
        [JsonPropertyName("executionLastError")] public string? ExecutionLastError { get; set; }
        [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
        [JsonPropertyName("startedAtUtc")] public DateTime? StartedAtUtc { get; set; }
        [JsonPropertyName("completedAtUtc")] public DateTime? CompletedAtUtc { get; set; }
    }

    public bool Exists(Guid buildId) => File.Exists(JobPath(buildId));

    /// <summary>Loads a build job; a corrupt document is renamed `.corrupt` (audit trail) and reported as
    /// absent so a redispatch can recreate it — local state is recoverable from the durable dispatch.</summary>
    public BuildJobDoc? TryLoad(Guid buildId)
    {
        var path = JobPath(buildId);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<BuildJobDoc>(File.ReadAllText(path), BuildJson.Default);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    public List<BuildJobDoc> LoadAll()
    {
        var docs = new List<BuildJobDoc>();
        foreach (var path in Directory.EnumerateFiles(JobsDirectory, "*.json"))
        {
            try
            {
                docs.Add(JsonSerializer.Deserialize<BuildJobDoc>(File.ReadAllText(path), BuildJson.Default)
                    ?? throw new JsonException("null document"));
            }
            catch (JsonException)
            {
                File.Move(path, path + ".corrupt", overwrite: true);
            }
        }
        return docs;
    }

    /// <summary>Insert race-safely: CreateNew throws when another writer landed the same buildId first.
    /// The caller must load and return the winner's document.</summary>
    public void Insert(BuildJobDoc doc)
    {
        try
        {
            using var created = new FileStream(JobPath(doc.BuildId), FileMode.CreateNew);
            JsonSerializer.Serialize(created, doc, BuildJson.Default);
        }
        catch (IOException) when (File.Exists(JobPath(doc.BuildId)))
        {
            throw new BuildAlreadyExistsException(doc.BuildId);
        }
    }

    public void Save(BuildJobDoc doc)
    {
        var path = JobPath(doc.BuildId);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, doc, BuildJson.Default);
            file.Flush();
        }
        // atomic replacement: readers see either the previous or the new document, never a torn write
        File.Move(temporary, path, overwrite: true);
    }

    public StreamWriter AppendLog(Guid buildId) => new(LogFile(buildId), append: true) { AutoFlush = true };

    private string JobPath(Guid buildId) => Path.Combine(JobsDirectory, $"{buildId}.json");

    public string ContextDirectory(Guid buildId) => Path.Combine(ContextRoot, $"build-{buildId}");
}
