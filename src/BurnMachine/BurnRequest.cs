namespace BurnMachine;

/// <summary>单点烧录请求（执行器参数）</summary>
public sealed record BurnRequest(string BurnSerial, string BurnId, string BurnProgram, double BurnTimeSeconds);
