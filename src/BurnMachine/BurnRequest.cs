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
