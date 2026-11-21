using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace PiAgent.RuntimeApi;

public sealed class StorageOptions
{
    /// <summary>In-container view of the managed root (decision 52: `/var/lib/pi-agent/workflows`).</summary>
    public string ManagedRoot { get; set; } = "/var/lib/pi-agent/workflows";
    /// <summary>Host absolute path of the same tree — composed into the container mount strings Periphery resolves.</summary>
    public string HostRoot { get; set; } = "/var/lib/pi-agent/workflows";
    /// <summary>In-container view of the host-managed profile secrets (decision 54); existence is verified on this path.</summary>
    public string SecretsManagedDir { get; set; } = "/etc/pi-agent/secrets";
    /// <summary>Host absolute path handed to the read-only `/run/secrets/<name>` mounts.</summary>
    public string SecretsHostDir { get; set; } = "/etc/pi-agent/secrets";
    /// <summary>Agent containers run as this host uid; per-Run writable dirs are chowned to it.</summary>
    public int ContainerUserId { get; set; } = 1000;
    /// <summary>§16 `max_log_size` — deployment-level copy kept in sync with system.yaml (documented shortcut).</summary>
    public int MaxLogMegabytes { get; set; } = 50;
}

/// <summary>Per-Run storage tree (decision 52 + the callback-spool addition): `{root}/{workflow_id}/runs/{run_id}/
/// {pi-session, logs, callback-spool}`, plus the Task workspace `{root}/{workflow_id}/tasks/{task_id}/workspace`.</summary>
public sealed record RunTreePaths(string RunDir, string SessionDir, string LogsDir, string SpoolDir, string TokenFile);

/// <summary>
/// Creates and hands out physical per-Run/workspace paths. Paths are returned in both views: the in-cont
/// iner `ManagedRoot` (what `AgentSession.session_file_path` and `Run.log_storage_reference` record — the
/// §52/§57 reference shape) and the `HostRoot` absolute host path that must go into container mount strings.
/// </summary>
public sealed class RuntimeStorageService(IOptions<StorageOptions> options)
{
    public StorageOptions Options { get; } = options.Value;

    public RunTreePaths RunTreePaths(Guid workflowId, Guid runId)
    {
        var runDir = $"{Options.ManagedRoot}/{workflowId}/runs/{runId}";
        return new RunTreePaths(
            runDir,
            $"{runDir}/pi-session",
            $"{runDir}/logs",
            $"{runDir}/callback-spool",
            $"{runDir}/callback-token");
    }

    public string RunTreeHostPath(Guid workflowId, Guid runId) =>
        $"{Options.HostRoot}/{workflowId}/runs/{runId}";

    public string WorkspacePath(Guid workflowId, Guid taskId) => $"{Options.ManagedRoot}/{workflowId}/tasks/{taskId}/workspace";

    public string WorkspaceHostPath(Guid workflowId, Guid taskId) => $"{Options.HostRoot}/{workflowId}/tasks/{taskId}/workspace";

    /// <summary>Creates the per-Run tree: `pi-session/`, `logs/`, `callback-spool/` (writable by the container uid)
    /// plus the root-owned callback-token file the mount serves read-only.</summary>
    public async Task<RunTreePaths> EnsureRunTreeAsync(Guid workflowId, Guid runId, string callbackToken, CancellationToken cancellationToken)
    {
        var paths = RunTreePaths(workflowId, runId);
        Directory.CreateDirectory(paths.SessionDir);
        Directory.CreateDirectory(paths.LogsDir);
        Directory.CreateDirectory(paths.SpoolDir);
        await File.WriteAllTextAsync(paths.TokenFile, callbackToken, cancellationToken);
        MakeReadableByContainerUser(paths.SessionDir, paths.LogsDir, paths.SpoolDir);
        MakeSecretFile(paths.TokenFile);
        return paths;
    }

    /// <summary>Creates (or reuses) the Task workspace directory, chowned to the container uid.</summary>
    public string EnsureWorkspaceDirectory(Guid workflowId, Guid taskId)
    {
        var path = WorkspacePath(workflowId, taskId);
        Directory.CreateDirectory(path);
        MakeReadableByContainerUser(path);
        return path;
    }

    public static long DirectorySizeBytes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    /// <summary>Resolves a profile secret reference (decision 54, fail closed): the returned host path is the
    /// mount source; the checks (single flat name, must exist, must be non-empty, files only — a directory
    /// named like a secret is not a mountable secret) probe the in-container managed view, which is the
    /// only one guaranteed to exist. Error strings are name-free prefixes; the callers add the name.</summary>
    public (string? HostPath, string? Error) SecretHostPath(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return (null, "must be a single flat file name");
        var managed = Path.Combine(Options.SecretsManagedDir, name);
        if (!File.Exists(managed))
            return (null, $"has no host-managed file under {Options.SecretsManagedDir}");
        if (new FileInfo(managed).Length == 0)
            return (null, "exists but is empty; supply the host-managed secret value");
        return ($"{Options.SecretsHostDir}/{name}", null);
    }

    /// <summary>chown via libc — uid mapping between the runtime-api container and the host has no managed API.
    /// Best-effort on non-root runtimes (macOS dev/test hosts fail with EPERM and log; real deployments
    /// run the api container as root and the chown succeeds).</summary>
    private void MakeReadableByContainerUser(params string[] paths)
    {
        if (OperatingSystem.IsWindows())
            return; // dev-only convenience; the compose stack always runs on unix
        foreach (var path in paths)
        {
            if (chown(path, (uint)Options.ContainerUserId, (uint)Options.ContainerUserId) != 0)
                Console.Error.WriteLine($"runtime-storage: chown({path}, {Options.ContainerUserId}) failed (errno {Marshal.GetLastWin32Error()}) — non-root dev/test runtime; the deployed api container (root) succeeds");
        }
    }

    private void MakeSecretFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return; // dev-only convenience; the compose stack always runs on unix
        // root-owned 0444: the mount is read-only anyway, and the container's non-root consumer (uid 1000)
        // must be able to read it — docker-secrets convention.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (chown(path, 0, 0) != 0)
            Console.Error.WriteLine($"runtime-storage: chown({path}, 0) failed (errno {Marshal.GetLastWin32Error()}) — non-root dev/test runtime; the deployed api container (root) succeeds");
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chown")]
    private static extern int chown(string path, uint owner, uint group);
}
