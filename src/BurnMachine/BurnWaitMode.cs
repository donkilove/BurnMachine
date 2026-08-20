namespace BurnMachine;

/// <summary>
/// 烧录等待方式（v0.3.0 新增）。
/// 固定等待：发 P 后固定等待 BurnTimeSeconds 再查询一次（v0.1.x 默认行为，向后兼容）；
/// 轮询：发 P 后按固定间隔轮询 C 查询，结果码 0/1 判定完成，2/3 或无响应继续，超时判失败。
/// 终止语义由真实硬件实测确认（烧录中 C 查询返回结果码 2，烧录完成变 0/1）。
/// </summary>
public enum BurnWaitMode
{
    /// <summary>固定等待：发 P 后等待 BurnTimeSeconds 秒，再查询一次结果（默认）</summary>
    Fixed = 0,

    /// <summary>轮询：发 P 后按 interval 间隔轮询 C 查询，结果码 0/1 即完成，超时判失败</summary>
    Polling = 1,
}

/// <summary>
/// 轮询查询命令（v0.5.0 新增）：控制轮询循环发 C 还是 U 查询。
/// U 与 C 响应同构（结果码 0/1/2/3 语义一致），U 额外携带上次烧录芯片的 UID 区，
/// 完成轮响应直接带出 UID，无需完成后再补查一次。需要固件 &gt; 20240103000000 才支持 U；
/// 旧固件 U 全程无响应，轮询将超时判失败（与 C 无响应语义一致）。
/// </summary>
public enum PollingQueryKind
{
    /// <summary>C 查询：结果码判定烧录进度（默认，行为与 v0.4.1 一致）</summary>
    C = 0,

    /// <summary>U 查询：结果码判定 + 完成轮响应携带 UID（BurnOutcome.Uid）</summary>
    U = 1,
}
