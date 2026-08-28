using System.Globalization;

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

    /// <summary>条码字节长度上限（审计 BM-07：防御性上限——协议文档未限定条码长度，产线条码远小于此，防畸形/恶意超长帧）</summary>
    public const int MaxBarcodeBytes = 64;

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

    // ==================== v0.2.0 新增（向后兼容：既有方法签名不变） ====================

    /// <summary>清空指令（指定通道）：`F{burnId}|{通道掩码8hex}\r\n</summary>
    public static string BuildClearCommand(string burnId, ChannelMask channels)
    {
        ValidateBurnId(burnId);
        return $"`F{burnId}|{FormatChannelMask(channels)}\r\n";
    }

    /// <summary>查询指令（指定通道）：`C{burnId}{通道掩码8hex}\r\n（注意：无 | 分隔）</summary>
    public static string BuildQueryCommand(string burnId, ChannelMask channels)
    {
        ValidateBurnId(burnId);
        return $"`C{burnId}{FormatChannelMask(channels)}\r\n";
    }

    /// <summary>烧录指令（指定通道，可选条码）：`P{burnId}|{掩码}|{镜像号}[|{条码hex}]\r\n</summary>
    /// <param name="burnId">烧录机 ID（8 位十进制数字）</param>
    /// <param name="burnProgram">烧录程序位号（4 位十进制数字）</param>
    /// <param name="channels">烧录通道掩码</param>
    /// <param name="barcode">条码字节（每字节转 2 位大写 hex；大小端由镜像配置决定，库不处理）；null/空省略字段（v0.4.0 类型改为 IReadOnlyList&lt;byte&gt;）</param>
    public static string BuildBurnCommand(string burnId, string burnProgram, ChannelMask channels, IReadOnlyList<byte>? barcode = null)
    {
        ValidateBurnId(burnId);
        ValidateBurnProgram(burnProgram);
        // 审计 BM-07：条码长度上限（防御性，防畸形/恶意超长帧）
        if (barcode is { Count: > MaxBarcodeBytes })
        {
            throw new ArgumentException($"条码长度超过上限 {MaxBarcodeBytes} 字节（实际 {barcode.Count}）", nameof(barcode));
        }

        var cmd = $"`P{burnId}|{FormatChannelMask(channels)}|{burnProgram}";
        if (barcode is { Count: > 0 })
        {
            cmd += $"|{ConvertBarcodeToHex(barcode)}";
        }

        return cmd + "\r\n";
    }

    /// <summary>UID 扩展查询指令：`U{burnId}{通道掩码8hex}\r\n（注意：无 | 分隔，与 C 命令一致）</summary>
    public static string BuildUidQueryCommand(string burnId, ChannelMask channels = ChannelMask.A)
    {
        ValidateBurnId(burnId);
        return $"`U{burnId}{FormatChannelMask(channels)}\r\n";
    }

    /// <summary>
    /// 解析烧录机响应并输出结构化详情（C 命令回复）。
    /// 判定语义与 <see cref="ParseResponse(string?, string, out string)"/> 完全一致（Kind/Detail），
    /// 额外解析镜像号/主校验和/剩余次数/结果码（见 <see cref="BurnResult"/>）。
    /// </summary>
    public static BurnResult ParseResponseDetailed(string? response, string burnId)
        => ParseDetailedCore(response, burnId, 'C');

    /// <summary>
    /// 解析 UID 扩展查询响应（U 命令回复）：前 6 段与 C 回复同构，第 7 段为 UID 区（固定 50 字符）。
    /// 需要固件版本 &gt; 20240103000000 才支持。第 7 段缺失（旧固件/6 段帧）→ 保留 Base 判定
    /// （Kind/Status 原样，结果码 0 仍判成功），仅 Uid=null（审计 BM-01：不再降级 FormatError，
    /// 防成功帧被误判"烧录超时"NG）；第 7 段内容畸形/数据不足 → 仅 Uid=null（不抛异常）。
    /// </summary>
    public static UidQueryResult ParseUidResponse(string? response, string burnId)
    {
        var baseResult = ParseDetailedCore(response, burnId, 'U');
        if (baseResult.Kind is not (BurnResultKind.Success or BurnResultKind.Failure))
        {
            return new UidQueryResult(baseResult, null);
        }

        var parts = response!.Trim().Split('|');
        if (parts.Length < 7)
        {
            // 审计 BM-01：6 段有效帧（结果码 0/1）缺 UID 区——保留 Base 判定（成功/失败），
            // 仅 Uid=null；此前改判 FormatError 使成功帧 Status=null → 轮询空转至超时误判 NG
            return new UidQueryResult(baseResult, null);
        }

        return new UidQueryResult(baseResult, TryParseUidZone(parts[6]));
    }

    // ---- v0.2.0 内部实现 ----

    /// <summary>
    /// 通道掩码 → 8 位大写十六进制（A=00000001、B=00000002、Both=00000003）。
    /// 审计 BM-04：零值/0xFFFFFFFF 属笔误（分别清空无通道/全部通道），构造指令时拒绝。
    /// </summary>
    private static string FormatChannelMask(ChannelMask channels)
    {
        var value = (uint)channels;
        if (value == 0 || value == 0xFFFFFFFF)
        {
            throw new ArgumentException("通道掩码不能为 0 或 0xFFFFFFFF（笔误可清空全部通道）", nameof(channels));
        }

        return value.ToString("X8");
    }

    /// <summary>条码字节 → 大写 hex 字符串（每字节 2 字符）</summary>
    private static string ConvertBarcodeToHex(IReadOnlyList<byte> barcode)
    {
        var sb = new System.Text.StringBuilder(barcode.Count * 2);
        foreach (var b in barcode)
        {
            sb.Append(b.ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 详情解析核心（C/U 共用）：判定语义与既有 ParseResponse 一致，成功/失败时补充详情字段。
    /// 详情字段宽松解析：缺段/畸形一律 null，不改变 Kind 判定。
    /// </summary>
    private static BurnResult ParseDetailedCore(string? response, string burnId, char commandChar)
    {
        var kind = ParseCore(response, burnId, commandChar, out var detail);
        if (kind is not (BurnResultKind.Success or BurnResultKind.Failure))
        {
            return new BurnResult(kind, detail, null, null, null, null);
        }

        var parts = response!.Trim().Split('|');
        return new BurnResult(
            kind,
            detail,
            TryParseImageNo(parts.Length > 2 ? parts[2] : null),
            TryParseChecksum(parts.Length > 3 ? parts[3] : null),
            TryParseRemaining(parts.Length >= 6 ? parts[4] : null),   // 5 段旧格式时 parts[4] 为结果码，不当作剩余次数
            TryParseStatus(parts.Length >= 6 ? parts[5] : parts[^1]));
    }

    /// <summary>镜像号：4 位十进制；9999（未烧录过）或解析失败 → null</summary>
    private static int? TryParseImageNo(string? s)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v != 9999 ? v : null;

    /// <summary>主校验和：8 位十六进制；FFFFFFFF 或解析失败 → null</summary>
    private static uint? TryParseChecksum(string? s)
        => uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v != 0xFFFFFFFF ? v : null;

    /// <summary>剩余次数：16 位十六进制；FFFFFFFFFFFFFFFF（无限次，.NET 解析为 -1）或解析失败 → null</summary>
    private static long? TryParseRemaining(string? s)
        => long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    /// <summary>结果码：0/1/2/3；其它（含畸形）→ null</summary>
    private static BurnStatus? TryParseStatus(string s)
        => s switch
        {
            "0" => BurnStatus.Success,
            "1" => BurnStatus.Failed,
            "2" => BurnStatus.Cleared,
            "3" => BurnStatus.NoRecord,
            _ => null,
        };

    /// <summary>
    /// UID 区解析：前 2 位为 UID 长度（十六进制字节数），随后为该长度的 UID 数据，其余无意义。
    /// 长度 0 / 数据不足 / 非法 hex → null（不抛异常）。
    /// </summary>
    private static byte[]? TryParseUidZone(string zone)
    {
        if (zone.Length < 2
            || !int.TryParse(zone[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length)
            || length == 0)   // v0.5.0：长度 0 = 无 UID 数据（实测设备烧录中部分轮前缀为 00），返回 null 而非空数组
        {
            return null;
        }

        var hexLength = length * 2;
        if (zone.Length < 2 + hexLength)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(zone.Substring(2, hexLength));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 C 查询响应的结果码（0=成功 / 1=失败 / 2=已清空 / 3=无记录）。
    /// 供轮询等待模式判定烧录进度：0/1 = 烧录结束，2/3 = 仍在烧录。
    /// 帧无效（无响应 / 格式错误 / 回显序列号不匹配 / 结果码畸形）→ null。
    /// </summary>
    public static BurnStatus? ParseQueryStatus(string? response, string burnId)
    {
        var kind = ParseCore(response, burnId, 'C', out _);
        if (kind is not (BurnResultKind.Success or BurnResultKind.Failure))
        {
            return null;
        }

        var parts = response!.Trim().Split('|');
        var last = parts.Length >= 6 ? parts[5] : parts[^1];
        return TryParseStatus(last);
    }

    /// <summary>
    /// 解析烧录机响应（输入已按帧切分；此处先 trim 再判定）。
    /// </summary>
    /// <param name="response">完整响应帧（可为 null/空）</param>
    /// <param name="burnId">本次查询使用的烧录机 ID（审核修复：校验响应回显序列号，防粘包错位/串扰）</param>
    /// <param name="detail">状态栏文本（与 BurnMachineHost 原版中文提示一致）</param>
    public static BurnResultKind ParseResponse(string? response, string burnId, out string detail)
        => ParseCore(response, burnId, 'C', out detail);

    /// <summary>
    /// 解析核心（C/U 共用）：判定语义与既有 ParseResponse 完全一致。
    /// v0.2.0 重构：命令字参数化，供 UID 查询（U 命令）复用回显校验，避免两处逻辑漂移。
    /// </summary>
    private static BurnResultKind ParseCore(string? response, string burnId, char commandChar, out string detail)
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

        // 回显序列号校验：首段应为 `{命令字} + 8 位序列号，且与本次查询的 burn_id 一致
        // （设备对查询回显请求中的序列号，校验可确认响应属于本次查询，防串扰/粘包错位）
        var first = parts[0];
        var echoedId = first.Length >= 10 && first[0] == '`' && first[1] == commandChar ? first[2..] : null;
        if (echoedId is null || echoedId.Length != 8 || echoedId != burnId)
        {
            detail = "响应格式错误：回显序列号不匹配";
            return BurnResultKind.FormatError;
        }

        var last = parts.Length >= 6 ? parts[5] : parts[^1];   // 结果码固定在第 6 段；5 段旧格式时为末段（U 回复第 7 段为 UID 区，不算结果码）
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
