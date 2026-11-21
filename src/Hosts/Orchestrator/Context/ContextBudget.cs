namespace PiAgent.Orchestrator.Context;

/// <summary>
/// Hard size limits of the Context Assembler (02 §14a/§17, 04 §7, decision 63). All values are
/// UTF-8 BYTES. V1 has no tokenizer (decision 56: model selection is V2), so token limits are
/// enforced through <see cref="ContextTokenEstimator"/> — see OBS-6 in docs/observations.md.
/// </summary>
public static class ContextBudgets
{
    /// <summary>Per-artifact cap (02 §17 / decision 32; also enforced by the Runtime API at artifact creation).</summary>
    public const long MaxArtifactBytes = 256 * 1024;

    /// <summary>Total artifact content cap per Task input (02 §17 / decision 32).</summary>
    public const long MaxArtifactTotalBytes = 512 * 1024;

    /// <summary>Cap over the whole serialized input — the frozen RunContextSnapshot content (decision 63).</summary>
    public const long MaxSerializedInputBytes = 512 * 1024;

    /// <summary>The effective main budget over the serialized input: min(serialized-input limit, token limit
    /// converted to bytes). Enforcing one monotone byte figure is equivalent because the estimator maps
    /// bytes → tokens monotonically (tokens ≤ max ⟺ bytes ≤ 4·max).</summary>
    public static long MainBudgetBytes(long maxContextTokens) =>
        Math.Min(MaxSerializedInputBytes, ContextTokenEstimator.TokenBudgetBytes(maxContextTokens));
}

/// <summary>
/// THE V1 token estimator (decision 63's `max_context_tokens` enforcement). V1 has no tokenizer: model
/// selection is deferred to V2 (decision 56), so tokens are approximated deterministically as
/// `ceil(utf8_bytes / 4)` over the serialized input. The estimate is applied to the serialized JSON —
/// the Bridge renders that JSON as ONE structured Markdown prompt (04 §4, decision 63) of roughly the
/// same size — and is conservative (JSON overhead, escaped non-ASCII and ASCII-only content are all
/// over-counted rather than under-counted). See OBS-6 in docs/observations.md for the revisit trigger.
/// </summary>
public static class ContextTokenEstimator
{
    /// <summary>The V1 heuristic: bytes per token (ASCII-heavy JSON averages ~4 bytes/token).</summary>
    public const int BytesPerToken = 4;

    /// <summary>`ceil(utf8Bytes / BytesPerToken)` — never fractional.</summary>
    public static long EstimateTokens(long utf8Bytes)
    {
        if (utf8Bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(utf8Bytes), "byte count cannot be negative");
        return (utf8Bytes + BytesPerToken - 1) / BytesPerToken;
    }

    /// <summary>Estimate over UTF-8-encoded text (the estimator's canonical input).</summary>
    public static long EstimateTokens(ReadOnlySpan<char> text) => EstimateTokens(System.Text.Encoding.UTF8.GetByteCount(text));

    /// <summary>`tokens ≤ max` ⟺ `bytes ≤ TokenBudgetBytes(max)` — the byte form the budgets enforce.</summary>
    public static long TokenBudgetBytes(long maxContextTokens)
    {
        if (maxContextTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxContextTokens), "max_context_tokens must be positive (decision 56)");
        return maxContextTokens * BytesPerToken;
    }
}
