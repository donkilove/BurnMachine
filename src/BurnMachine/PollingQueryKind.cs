namespace BurnMachine;

/// <summary>
/// 轮询查询命令（v0.5.0 新增）：控制轮询循环发 C 还是 U 查询。
/// U 与 C 响应同构（结果码 0/1/2/3 语义一致），U 额外携带上次烧录芯片的 UID 区，
/// 完成轮响应直接带出 UID，无需完成后再补查一次。需要固件 &gt; 20240103000000 才支持 U；
/// 旧固件 U 全程无响应，轮询将超时判失败（与 C 无响应语义一致）。
/// </summary>
public enum PollingQueryKind
{
    /// <summary>C 查询：结果码判定烧录进度（默认）</summary>
    C = 0,

    /// <summary>U 查询：结果码判定 + 完成轮响应携带 UID（BurnOutcome.Uid）</summary>
    U = 1,
}
