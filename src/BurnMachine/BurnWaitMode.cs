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
