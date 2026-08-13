namespace BurnMachine;

/// <summary>烧录机响应结果类型（与 BurnMachineHost 原版解析分支一一对应）</summary>
public enum BurnResultKind
{
    /// <summary>末段首字符 == '0'</summary>
    Success,
    /// <summary>末段首字符 != '0'</summary>
    Failure,
    /// <summary>未收到响应数据</summary>
    NoResponse,
    /// <summary>不以 ` 开头，或 | 分段不足 5</summary>
    FormatError,
    /// <summary>解析过程异常（如末段为空导致索引越界）</summary>
    Error,
}

/// <summary>
/// 烧录机协议：指令构造与响应解析。
/// 规格见 BurnMachineHost 仓库 docs/串口协议规格.md §2，行为与 BurnMachineHost 原版 BurnThread.run 一致。
/// </summary>
public static class BurnProtocol
{
    /// <summary>响应帧长度上限（字符数，审核修复：拒畸形/恶意超长帧，实测查询响应约 60 字符）</summary>
    public const int MaxResponseLength = 256;

    /// <summary>清空指令：`F{burnId}|00000001\r\n</summary>
    public static string BuildClearCommand(string burnId)
    {
        ValidateBurnId(burnId);
        return $"`F{burnId}|00000001\r\n";
    }

    /// <summary>烧录指令：`P{burnId}|00000001|{burnProgram}\r\n</summary>
    public static string BuildBurnCommand(string burnId, string burnProgram)
    {
        ValidateBurnId(burnId);
        ValidateBurnProgram(burnProgram);
        return $"`P{burnId}|00000001|{burnProgram}\r\n";
    }

    /// <summary>查询指令：`C{burnId}00000001\r\n（注意：无 | 分隔）</summary>
    public static string BuildQueryCommand(string burnId)
    {
        ValidateBurnId(burnId);
        return $"`C{burnId}00000001\r\n";
    }

    /// <summary>
    /// 解析烧录机响应（输入已按帧切分；此处先 trim 再判定）。
    /// </summary>
    /// <param name="response">完整响应帧（可为 null/空）</param>
    /// <param name="burnId">本次查询使用的烧录机 ID（审核修复：校验响应回显序列号，防粘包错位/串扰）</param>
    /// <param name="detail">状态栏文本（与 BurnMachineHost 原版中文提示一致）</param>
    public static BurnResultKind ParseResponse(string? response, string burnId, out string detail)
    {
        detail = "";
        response = response?.Trim();
        if (string.IsNullOrEmpty(response))
        {
            detail = "未收到响应数据";
            return BurnResultKind.NoResponse;
        }

        if (response.Length > MaxResponseLength)
        {
            detail = $"响应格式错误：帧长度超限（>{MaxResponseLength}字符）";
            return BurnResultKind.FormatError;
        }

        if (response[0] != '`')
        {
            detail = "响应格式错误：不是以`开头";
            return BurnResultKind.FormatError;
        }

        var parts = response.Split('|');
        if (parts.Length < 5)
        {
            detail = $"响应格式错误：部分数量不足 {parts.Length}/5";
            return BurnResultKind.FormatError;
        }

        // 回显序列号校验：首段应为 `C + 8 位序列号，且与本次查询的 burn_id 一致
        // （设备对查询回显请求中的序列号，校验可确认响应属于本次查询，防串扰/粘包错位）
        var first = parts[0];
        var echoedId = first.Length >= 10 && first[0] == '`' && first[1] == 'C' ? first[2..] : null;
        if (echoedId is null || echoedId.Length != 8 || echoedId != burnId)
        {
            detail = "响应格式错误：回显序列号不匹配";
            return BurnResultKind.FormatError;
        }

        var last = parts[^1];
        if (last.Length == 0)
        {
            // 原版 parts[-1][0] 对空串抛 IndexError，被 except 捕获 → Error
            detail = "响应解析错误: Index was out of range";
            return BurnResultKind.Error;
        }

        // 结果码按协议精确匹配（审核修复）：0=成功；1/2/3/畸形多字符一律失败
        if (last == "0")
        {
            detail = "烧录成功";
            return BurnResultKind.Success;
        }

        detail = "烧录失败";
        return BurnResultKind.Failure;
    }

    // ---- 输入校验（审核修复：严格设备字段规则，指令构造与 BurnRequest 共用） ----

    /// <summary>烧录等待时间下界（秒）</summary>
    public const double MinBurnTimeSeconds = 0.1;

    /// <summary>烧录等待时间上界（秒）</summary>
    public const double MaxBurnTimeSeconds = 600;

    /// <summary>校验烧录机 ID：8 位十进制数字（设备手册序列号字段规则）</summary>
    internal static void ValidateBurnId(string burnId)
    {
        if (burnId is null || burnId.Length != 8 || !burnId.All(c => c is >= '0' and <= '9'))
        {
            throw new ArgumentException("烧录机ID必须为8位十进制数字", nameof(burnId));
        }
    }

    /// <summary>校验烧录程序位号：4 位十进制数字（设备镜像号字段规则）</summary>
    internal static void ValidateBurnProgram(string burnProgram)
    {
        if (burnProgram is null || burnProgram.Length != 4 || !burnProgram.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("烧录程序位号必须为4位十进制数字", nameof(burnProgram));
        }
    }

    /// <summary>校验烧录等待时间：finite 且 0.1~600 秒（与上位机 BurnConfig 规则一致）</summary>
    internal static void ValidateBurnTime(double burnTimeSeconds)
    {
        if (!double.IsFinite(burnTimeSeconds)
            || burnTimeSeconds < MinBurnTimeSeconds
            || burnTimeSeconds > MaxBurnTimeSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(burnTimeSeconds), "烧录时间必须在0.1-600秒之间");
        }
    }
}
