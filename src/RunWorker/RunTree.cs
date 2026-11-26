using System.Runtime.InteropServices;
using System.Text.Json;
using PiAgent.Events;

namespace PiAgent.RunWorker;

/// <summary>
/// The per-Run tree (docs/run-worker.md invariant 5 — provisioning is internal): input.json v2
/// plus the writable directories the agent container mounts (/workspace /session /logs /spool),
/// chowned to the container's uid — uid mapping between the app-host container and the host has
/// no managed API, so chown goes through libc; best-effort on non-root runtimes (macOS dev/test
/// hosts fail with EPERM and log; the deployed app-host container runs as root and succeeds).
/// </summary>
public static class RunTree
{
    public const string WorkspaceDirName = "workspace";

    public static string Ensure(string root, long runId, long containerUserId, RunInput input)
    {
        var runDir = Path.Combine(root, $"run-{runId}");
        var workspace = Path.Combine(runDir, WorkspaceDirName);
        Directory.CreateDirectory(workspace);
        var logs = Directory.CreateDirectory(Path.Combine(runDir, "logs"));
        // a reprovision means the previous attempt never settled (else this Run would not be
        // scheduled again) — its undelivered spool entries belong to a dead attempt. Entries
        // written before the session-id fix carry no sessionId and would slip past the
        // callback guard, so the wipe happens at the source, not in the guard (docs/run-session-id.md
        // "Instance-scoped callbacks", fix B). The .terminal-delivered marker lives here too.
        var spoolPath = Path.Combine(runDir, "spool");
        if (Directory.Exists(spoolPath))
            Directory.Delete(spoolPath, recursive: true);
        var spool = Directory.CreateDirectory(spoolPath);
        var session = Directory.CreateDirectory(Path.Combine(runDir, "session"));

        File.WriteAllText(
            Path.Combine(runDir, "input.json"),
            JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        MakeWritableByContainerUser(containerUserId, workspace, logs.FullName, spool.FullName, session.FullName);
        return runDir;
    }

    private static void MakeWritableByContainerUser(long uid, params string[] paths)
    {
        if (OperatingSystem.IsWindows())
            return; // dev-only convenience; the compose stack always runs on unix
        foreach (var path in paths)
        {
            if (chown(path, (uint)uid, (uint)uid) != 0)
                Console.Error.WriteLine(
                    $"run-tree: chown({path}, {uid}) failed (errno {Marshal.GetLastWin32Error()}) — " +
                    "non-root dev/test runtime; the deployed app-host container (root) succeeds");
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chown")]
    private static extern int chown(string path, uint owner, uint group);
}
