namespace PiAgent.Poller;

/// <summary>
/// Host-managed read-PAT reading — same discipline as the Outbox Worker's PatFile (decision 50: one
/// fine-grained read-only PAT for all configured repositories, decisions 50/65): unset/missing/empty
/// fail startup naming the env var or file path; token content is never included.
/// </summary>
public static class PatFile
{
    public static string Read(string? patFile, string envName)
    {
        if (string.IsNullOrWhiteSpace(patFile))
            throw new PollerConfigException(
                $"configuration error: {envName} is not set — the Poller syncs with a fine-grained read-only PAT (decision 50)");
        if (!File.Exists(patFile))
            throw new PollerConfigException(
                $"configuration error: the PAT file '{patFile}' does not exist (host-managed secret, decisions 50/54)");
        try
        {
            var pat = File.ReadAllText(patFile).Trim();
            if (pat.Length == 0)
                throw new PollerConfigException($"configuration error: the PAT file '{patFile}' is empty");
            return pat;
        }
        catch (IOException exception)
        {
            throw new PollerConfigException($"configuration error: cannot read the PAT file '{patFile}' ({exception.Message})");
        }
    }
}
