namespace PiAgent.Platform.Secrets;

/// <summary>
/// Host-managed PAT reading, single copy of the discipline shared by the Poller, Outbox Worker and
/// Config Loader (fine-grained PATs, host-managed secret files, one read-only sync PAT):
/// unset/missing/empty fail startup naming the env var or file path;
/// token content is never included in errors. The value is read once at startup — a fixed
/// secret requires a redeploy, which the compose restart gate makes visible.
/// </summary>
public static class PatFile
{
    /// <param name="purpose">Human-readable purpose suffix for the "not set" message — it names what
    /// the PAT is for (e.g. "the Poller syncs with a fine-grained read-only PAT").</param>
    public static string Read(string? patFile, string envName, string purpose)
    {
        if (string.IsNullOrWhiteSpace(patFile))
            throw new Platform.Config.ConfigException(
                $"configuration error: {envName} is not set — {purpose}");
        if (!File.Exists(patFile))
            throw new Platform.Config.ConfigException(
                $"configuration error: the PAT file '{patFile}' does not exist (host-managed secret)");
        try
        {
            var pat = File.ReadAllText(patFile).Trim();
            if (pat.Length == 0)
                throw new Platform.Config.ConfigException($"configuration error: the PAT file '{patFile}' is empty");
            return pat;
        }
        catch (IOException exception)
        {
            throw new Platform.Config.ConfigException(
                $"configuration error: cannot read the PAT file '{patFile}' ({exception.Message})");
        }
    }
}
