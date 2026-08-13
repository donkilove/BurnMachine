namespace BurnMachine;

/// <summary>单点烧录结果</summary>
public sealed record BurnOutcome(bool Success, BurnResultKind Kind, string Detail);
