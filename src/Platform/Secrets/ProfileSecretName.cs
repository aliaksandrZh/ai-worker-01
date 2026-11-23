using System.Text.RegularExpressions;

namespace PiAgent.Platform.Secrets;

/// <summary>
/// Profile secret reference grammar: names identify host-managed files mounted read-only
/// at `/run/secrets/<name>`. Only names travel through configuration — values never do. The grammar is
/// enforced twice: by the Config Loader at startup over the YAML source, and by the Runtime API at
/// container-plan build over the frozen `environment_configuration` rows (fail closed).
/// </summary>
public static partial class ProfileSecretName
{
    /// <summary>The per-Run callback credential's container path — reserved by the Runtime API;
    /// a profile secret of the same name would collide with its mount.</summary>
    public const string ReservedCallbackToken = "callback-token";

    /// <summary>Single path segment: no separators, no leading dot (kills "." and "..").</summary>
    private static readonly Regex Pattern = NamePattern();

    /// <summary>Null = valid; otherwise the validation error (names only — never secret values).</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "secret reference must be a non-empty name";
        if (!Pattern.IsMatch(name))
            return $"secret reference '{name}' must match [A-Za-z0-9][A-Za-z0-9._-]* (a single file name)";
        if (name == ReservedCallbackToken)
            return $"secret reference '{name}' is reserved for the per-Run callback credential";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex NamePattern();
}
