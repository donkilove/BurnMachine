namespace BurnMachine;

/// <summary>
/// 单点烧录请求（执行器参数；构造时按严格设备字段规则校验）。
/// burnId 必须为 8 位十进制数字、burnProgram 必须为 4 位十进制数字、烧录时间必须为 0.1~600 秒。
/// </summary>
public sealed record BurnRequest
{
    /// <summary>烧录机串口号（如 COM3）</summary>
    public string BurnSerial { get; }

    /// <summary>烧录机 ID（8 位十进制数字）</summary>
    public string BurnId { get; }

    /// <summary>烧录程序位号（4 位十进制数字）</summary>
    public string BurnProgram { get; }

    /// <summary>烧录等待时间（秒，0.1~600）</summary>
    public double BurnTimeSeconds { get; }

    /// <summary>烧录通道（v0.2.0 新增；默认 A 通道，行为与 v0.1.1 一致）</summary>
    public ChannelMask Channels { get; init; } = ChannelMask.A;

    /// <summary>
    /// 烧录时写入的条码字节（v0.2.0 新增；v0.4.0 类型改为 IReadOnlyList&lt;byte&gt; 以支持不可变数据；
    /// 默认 null 不写条码；大小端由镜像配置决定）
    /// </summary>
    public IReadOnlyList<byte>? Barcode { get; init; }

    public BurnRequest(string burnSerial, string burnId, string burnProgram, double burnTimeSeconds)
    {
        if (string.IsNullOrWhiteSpace(burnSerial))
        {
            throw new ArgumentException("串口名不能为空", nameof(burnSerial));
        }

        BurnProtocol.ValidateBurnId(burnId);
        BurnProtocol.ValidateBurnProgram(burnProgram);
        BurnProtocol.ValidateBurnTime(burnTimeSeconds);

        BurnSerial = burnSerial;
        BurnId = burnId;
        BurnProgram = burnProgram;
        BurnTimeSeconds = burnTimeSeconds;
    }
}
