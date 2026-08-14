namespace BurnMachine;

/// <summary>
/// 烧录通道掩码（v0.2.0 新增）。
/// 对应协议通道掩码字段（8 位十六进制，从最低位起依次为 A、B、C、D…通道使能标记）。
/// </summary>
[Flags]
public enum ChannelMask : uint
{
    /// <summary>A 通道（掩码 00000001）</summary>
    A = 1,

    /// <summary>B 通道（掩码 00000002；仅开启双通道模式时设备才响应）</summary>
    B = 2,

    /// <summary>A、B 双通道（掩码 00000003）</summary>
    Both = A | B,
}

/// <summary>
/// 烧录结果码（v0.2.0 新增），对应 C/U 回复末段结果字段。
/// 0=成功，1=失败，2=上次结果被清空过，3=开机后此通道还没烧录过芯片（也没被清空过）。
/// </summary>
public enum BurnStatus
{
    /// <summary>烧录成功</summary>
    Success = 0,

    /// <summary>烧录失败</summary>
    Failed = 1,

    /// <summary>上次烧录结果被清空过</summary>
    Cleared = 2,

    /// <summary>开机后此通道还没烧录过芯片，也没被清空过状态</summary>
    NoRecord = 3,
}
