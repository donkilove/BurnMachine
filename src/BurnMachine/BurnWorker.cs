using System.Diagnostics;
using System.Text;
using BurnMachine.Channel;

namespace BurnMachine;

/// <summary>
/// 单点烧录执行器：按 XW16Pro 协议执行 清空→烧录→查询 时序（整轮最多尝试 2 次）。
/// 响应读取用累积缓冲 + 换行帧切分（修复 BurnMachineHost 原版粘包/半包问题）。
/// </summary>
public sealed class BurnWorker
{
    private const int MaxRetries = 2;
    private const int RetryDelayMs = 1000;
    private const int ResponseWindowMs = 1000;
    private const int ReadPollMs = 100;

    /// <summary>轮询模式默认查询间隔（ms）</summary>
    public const int DefaultPollingIntervalMs = 100;

    /// <summary>轮询模式默认总超时（ms）</summary>
    public const int DefaultPollingTimeoutMs = 3500;

    /// <summary>
    /// 轮询单轮读窗口（实测完整帧最迟 ~134ms 到达：设备处理 ~80ms + 9600 波特传输 ~54ms；留 ~50% 余量）
    /// </summary>
    private const int PollingReadWindowMs = 200;

    /// <summary>轮询单轮读取粒度（实测 100ms 粒度会白等一拍至 200ms 才能读到完整帧；20ms 拍 ~140ms 即读到）</summary>
    private const int PollingReadPollMs = 20;

    private const int MinPollingIntervalMs = 50;
    private const int MaxPollingIntervalMs = 10000;
    private const int MinPollingTimeoutMs = 100;
    private const int MaxPollingTimeoutMs = 600000;   // 与 BurnTimeSeconds 上限 600s 对齐

    private readonly Func<ISerialChannel> _channelFactory;
    private readonly Action<string>? _status;
    private readonly int _baudRate;

    /// <param name="channelFactory">每次执行新建串口通道的工厂（执行结束即关闭释放）</param>
    /// <param name="status">可选状态回调（如宿主状态栏）；SDK 独立使用可不传</param>
    /// <param name="baudRate">烧录机串口波特率（XW16Pro 协议为 9600 8N1）</param>
    public BurnWorker(Func<ISerialChannel> channelFactory, Action<string>? status = null, int baudRate = 9600)
    {
        _channelFactory = channelFactory;
        _status = status;
        _baudRate = baudRate;
    }

    /// <param name="request">单点烧录请求（轮询模式下 BurnTimeSeconds 被忽略，由 pollingTimeoutMs 取代）</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="waitMode">烧录等待方式（默认 Fixed：固定等待 BurnTimeSeconds 后查询一次）</param>
    /// <param name="pollingIntervalMs">轮询模式下两次 C 查询之间的间隔（ms，50~10000，默认 100）</param>
    /// <param name="pollingTimeoutMs">轮询模式总超时（ms，100~600000，默认 3500）；超时未出结果判失败</param>
    public async Task<BurnOutcome> ExecuteAsync(
        BurnRequest request,
        CancellationToken ct,
        BurnWaitMode waitMode = BurnWaitMode.Fixed,
        int pollingIntervalMs = DefaultPollingIntervalMs,
        int pollingTimeoutMs = DefaultPollingTimeoutMs)
    {
        if (waitMode == BurnWaitMode.Polling)
        {
            ValidatePollingParameters(pollingIntervalMs, pollingTimeoutMs);
        }

        for (var retry = 0; retry < MaxRetries; retry++)
        {
            ISerialChannel? ser = null;
            try
            {
                _status?.Invoke($"尝试打开烧录机串口 {request.BurnSerial}，第 {retry + 1}/{MaxRetries} 次");
                ser = _channelFactory();
                ser.Open(request.BurnSerial, _baudRate);
                ser.ResetInputBuffer();   // 审核修复：清打开时驱动缓冲残留（设备上电噪声/上次会话数据）

                ser.Write(BurnProtocol.BuildClearCommand(request.BurnId, request.Channels));
                await Task.Delay(100, ct);

                ser.Write(BurnProtocol.BuildBurnCommand(request.BurnId, request.BurnProgram, request.Channels, request.Barcode));

                if (waitMode == BurnWaitMode.Polling)
                {
                    // 轮询模式：不再固定等待，改为按间隔轮询 C 查询直到结果码 0/1 或超时
                    return await WaitForBurnCompletionAsync(ser, request, ct, pollingIntervalMs, pollingTimeoutMs);
                }

                _status?.Invoke($"等待烧录时间: {request.BurnTimeSeconds}秒");
                await Task.Delay(TimeSpan.FromSeconds(request.BurnTimeSeconds), ct);

                ser.Write(BurnProtocol.BuildQueryCommand(request.BurnId, request.Channels));
                ser.ResetInputBuffer();   // 审核修复：清查询前累积的残留帧头，防复用通道/残留字节污染本次解析
                await Task.Delay(500, ct);

                var response = await ReadResponseAsync(ser, ct);
                if (!string.IsNullOrEmpty(response))
                {
                    _status?.Invoke($"收到响应: {response}");
                }

                var kind = BurnProtocol.ParseResponse(response, request.BurnId, out var detail);
                return new BurnOutcome(kind == BurnResultKind.Success, kind, detail);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _status?.Invoke($"烧录错误: {e.Message}");
                if (retry < MaxRetries - 1)
                {
                    _status?.Invoke($"等待 {RetryDelayMs / 1000.0:0} 秒后重试...");
                    await Task.Delay(RetryDelayMs, ct);
                }
                else
                {
                    return new BurnOutcome(false, BurnResultKind.Error, $"烧录错误: {e.Message}");
                }
            }
            finally
            {
                if (ser is not null)
                {
                    try
                    {
                        if (ser.IsOpen)
                        {
                            ser.Close();
                            _status?.Invoke($"已关闭烧录机串口 {request.BurnSerial}");
                        }
                    }
                    catch (Exception e)
                    {
                        // 审核修复：关闭/释放异常属收尾噪音，不得逃逸破坏返回值，也不触发重试
                        _status?.Invoke($"关闭烧录机串口异常: {e.Message}");
                    }

                    try
                    {
                        ser.Dispose();
                    }
                    catch (Exception e)
                    {
                        _status?.Invoke($"释放烧录机串口异常: {e.Message}");
                    }
                }
            }
        }

        // 语义上不可达：循环内必然 return（成功/失败结果）或 throw（取消）；保留语句仅为通过编译
        throw new UnreachableException("ExecuteAsync 循环内必然返回或抛出");
    }

    /// <summary>
    /// UID 扩展查询（v0.2.0 新增）：发送 U 命令并解析 UID 查询回复。
    /// 需要设备固件版本 &gt; 20240103000000 才支持（旧固件设备无回复 → 结果 Kind=NoResponse）。
    /// 串口打开失败等环境异常直接上抛（不做重试）。
    /// </summary>
    /// <param name="burnSerial">烧录机串口号（如 COM3）</param>
    /// <param name="burnId">烧录机 ID（8 位十进制数字）</param>
    /// <param name="channels">查询通道（默认 A）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<UidQueryResult> QueryUidAsync(
        string burnSerial, string burnId, ChannelMask channels = ChannelMask.A, CancellationToken ct = default)
    {
        var ser = _channelFactory();
        try
        {
            _status?.Invoke($"打开烧录机串口 {burnSerial} 执行 UID 查询");
            ser.Open(burnSerial, _baudRate);
            ser.ResetInputBuffer();

            ser.Write(BurnProtocol.BuildUidQueryCommand(burnId, channels));
            await Task.Delay(500, ct);

            var response = await ReadResponseAsync(ser, ct);
            if (!string.IsNullOrEmpty(response))
            {
                _status?.Invoke($"收到响应: {response}");
            }

            return BurnProtocol.ParseUidResponse(response, burnId);
        }
        finally
        {
            if (ser is not null)
            {
                try
                {
                    ser.Dispose();
                }
                catch (Exception e)
                {
                    // 收尾噪音：不得逃逸破坏返回值
                    _status?.Invoke($"释放烧录机串口异常: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 轮询等待烧录完成（v0.3.0 新增）：发 P 后按固定间隔轮询 C 查询，
    /// 结果码 0（成功）/1（失败）判定烧录结束，2/3 与无响应/无效帧视为仍在烧录继续轮询，
    /// 超过 timeoutMs 判失败（严格超时：读窗口与等待间隔均受剩余时间限制，总耗时不超过 timeoutMs）。
    /// 终止语义由真实硬件实测确认（烧录中查询返回 2，完成后变 0）。
    /// </summary>
    private async Task<BurnOutcome> WaitForBurnCompletionAsync(
        ISerialChannel ser, BurnRequest request, CancellationToken ct, int intervalMs, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var query = BurnProtocol.BuildQueryCommand(request.BurnId, request.Channels);
        var round = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            round++;

            // 严格超时：剩余时间不足则立即判定超时，不发起新一轮查询
            var remainingMs = timeoutMs - sw.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                var msg = $"烧录超时：{timeoutMs}ms 内未查询到完成结果";
                _status?.Invoke(msg);
                return new BurnOutcome(false, BurnResultKind.Failure, msg);
            }

            ser.Write(query);
            ser.ResetInputBuffer();   // 清查询前累积的残留帧头，防残留字节污染本次解析

            // 单轮读窗口不超过剩余时间（保证总耗时严格 ≤ timeoutMs）
            var windowMs = (int)Math.Min(PollingReadWindowMs, remainingMs);
            var response = await ReadResponseAsync(ser, ct, windowMs, PollingReadPollMs);
            if (!string.IsNullOrEmpty(response))
            {
                _status?.Invoke($"轮询查询（第{round}次）: {response}");
            }

            var status = BurnProtocol.ParseQueryStatus(response, request.BurnId);
            switch (status)
            {
                case BurnStatus.Success:
                    _status?.Invoke("烧录成功");
                    return new BurnOutcome(true, BurnResultKind.Success, "烧录成功");

                case BurnStatus.Failed:
                    _status?.Invoke("烧录失败");
                    return new BurnOutcome(false, BurnResultKind.Failure, "烧录失败");
            }

            // 结果码 2/3（仍在烧录）或帧无效/无响应：继续轮询，等待间隔不超过剩余时间
            var remainingAfterRead = timeoutMs - sw.ElapsedMilliseconds;
            if (remainingAfterRead <= 0)
            {
                var msg = $"烧录超时：{timeoutMs}ms 内未查询到完成结果";
                _status?.Invoke(msg);
                return new BurnOutcome(false, BurnResultKind.Failure, msg);
            }

            _status?.Invoke($"烧录进行中（第{round}次查询，已等待{sw.ElapsedMilliseconds}ms）");
            await Task.Delay(Math.Min(intervalMs, (int)remainingAfterRead), ct);
        }
    }

    /// <summary>校验轮询参数（仅 Polling 模式生效；范围与 BurnTimeSeconds 规则对齐）</summary>
    private static void ValidatePollingParameters(int pollingIntervalMs, int pollingTimeoutMs)
    {
        if (pollingIntervalMs < MinPollingIntervalMs || pollingIntervalMs > MaxPollingIntervalMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingIntervalMs),
                $"轮询间隔必须在{MinPollingIntervalMs}-{MaxPollingIntervalMs}ms之间");
        }

        if (pollingTimeoutMs < MinPollingTimeoutMs || pollingTimeoutMs > MaxPollingTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingTimeoutMs),
                $"轮询超时必须在{MinPollingTimeoutMs}-{MaxPollingTimeoutMs}ms之间");
        }
    }

    /// <summary>
    /// 读响应：累积缓冲，总超时 windowMs（默认 1s），收到换行（帧边界）提前结束；
    /// 读取粒度 pollMs（默认 100ms；轮询模式传 20ms 以免白等一拍）。
    /// 审核修复：按协议规格 §2.3 切帧——粘包（缓冲含多帧）时只取第一帧（到第一个 \n 为止），
    /// 余量丢弃（查询为 request-response 模式，下次查询前会 ResetInputBuffer），避免整块解析误判。
    /// </summary>
    private static async Task<string> ReadResponseAsync(
        ISerialChannel ser, CancellationToken ct, int windowMs = ResponseWindowMs, int pollMs = ReadPollMs)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < windowMs)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ser.ReadAvailable();
            if (chunk.Length > 0)
            {
                var newline = chunk.IndexOf('\n');
                if (newline >= 0)
                {
                    sb.Append(chunk[..(newline + 1)]);   // 只取到第一个换行（含），丢弃粘包余量
                    break;
                }

                sb.Append(chunk);
            }

            await Task.Delay(pollMs, ct);
        }

        return sb.ToString().Trim();
    }
}
