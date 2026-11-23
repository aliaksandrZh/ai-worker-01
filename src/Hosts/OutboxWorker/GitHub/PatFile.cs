using PiAgent.OutboxWorker.GitHub;
namespace PiAgent.OutboxWorker.GitHub;

/// <summary>
/// Host-managed write-PAT reading (plan ruling 2): same discipline and message shape as the Config
/// Loader's ReadPat. Unset/missing/empty fail startup naming the env var or file path — token
/// content is never included (§14, decision 76). The value is read once at startup; a fixed secret
/// requires a redeploy, which the compose restart gate makes visible (ruling 2).
/// </summary>
public static class PatFile
{
    public static string Read(string? patFile, string envName)
    {
        if (string.IsNullOrWhiteSpace(patFile))
            throw new OutboxWorkerConfigException(
                $"configuration error: {envName} is not set — the Outbox Dispatcher publishes orchestration-owned comments with a repository-scoped write PAT (decision 50)");
        if (!File.Exists(patFile))
            throw new OutboxWorkerConfigException(
                $"configuration error: the PAT file '{patFile}' does not exist (host-managed secret, decisions 50/54)");
        try
        {
            var pat = File.ReadAllText(patFile).Trim();
            if (pat.Length == 0)
                throw new OutboxWorkerConfigException($"configuration error: the PAT file '{patFile}' is empty");
            return pat;
        }
        catch (IOException exception)
        {
            throw new OutboxWorkerConfigException($"configuration error: cannot read the PAT file '{patFile}' ({exception.Message})");
        }
    }
}
