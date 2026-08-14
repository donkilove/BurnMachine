namespace BurnMachine;

/// <summary>
/// 结构化烧录结果（v0.2.0 新增）：在既有判定（Kind/Detail）之上补充 C 回复详情字段。
/// 字段语义见 docs/XW16Pro扩展串口控制协议.md §6。
/// </summary>
/// <param name="Kind">判定类型（沿用既有语义：非 0 结果码一律 Failure）</param>
/// <param name="Detail">状态文本（沿用既有中文提示）</param>
/// <param name="ImageNo">上次烧录镜像号；9999 或未烧录过 → null</param>
/// <param name="MainChecksum">上次烧录镜像主校验和；FFFFFFFF → null</param>
/// <param name="RemainingCount">镜像剩余可烧录次数；FFFFFFFFFFFFFFFF（无限次）→ null</param>
/// <param name="Status">烧录结果码（0/1/2/3）；无响应/格式错误/畸形 → null</param>
public sealed record BurnResult(
    BurnResultKind Kind,
    string Detail,
    int? ImageNo,
    uint? MainChecksum,
    long? RemainingCount,
    BurnStatus? Status);

/// <summary>
/// UID 扩展查询结果（v0.2.0 新增），对应 U 命令回复。
/// 需要固件版本 &gt; 20240103000000 才支持。
/// </summary>
/// <param name="Base">前 6 段解析结果（与 C 回复同构）</param>
/// <param name="Uid">上次烧录芯片的 UID 数据；解析失败/无数据 → null</param>
public sealed record UidQueryResult(BurnResult Base, byte[]? Uid);
