namespace codexpet;

public sealed record RateLimitSnapshot(
    RateLimitWindowSnapshot? Primary,
    RateLimitWindowSnapshot? Secondary);

public sealed record RateLimitWindowSnapshot(
    int UsedPercent,
    long? WindowDurationMins,
    DateTimeOffset? ResetsAt);
