namespace BurnMachine;

/// <summary>单点烧录结果</summary>
public sealed record BurnOutcome(bool Success, BurnResultKind Kind, string Detail)
{
    /// <summary>
    /// 烧录芯片 UID（v0.5.0 新增）：轮询模式 pollingQuery=U 时随完成轮响应带出；
    /// 其余路径（C 轮询/固定模式）为 null。完成轮 UID 长度前缀 00 或区段畸形 → null。
    /// </summary>
    public IReadOnlyList<byte>? Uid { get; init; }
}
