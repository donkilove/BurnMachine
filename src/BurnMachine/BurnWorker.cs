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

    public async Task<BurnOutcome> ExecuteAsync(BurnRequest request, CancellationToken ct)
    {
        for (var retry = 0; retry < MaxRetries; retry++)
        {
            ISerialChannel? ser = null;
            try
            {
                _status?.Invoke($"尝试打开烧录机串口 {request.BurnSerial}，第 {retry + 1}/{MaxRetries} 次");
                ser = _channelFactory();
                ser.Open(request.BurnSerial, _baudRate);
                ser.ResetInputBuffer();   // 审核修复：清打开时驱动缓冲残留（设备上电噪声/上次会话数据）

                ser.Write(BurnProtocol.BuildClearCommand(request.BurnId));
                await Task.Delay(100, ct);

                ser.Write(BurnProtocol.BuildBurnCommand(request.BurnId, request.BurnProgram));

                _status?.Invoke($"等待烧录时间: {request.BurnTimeSeconds}秒");
                await Task.Delay(TimeSpan.FromSeconds(request.BurnTimeSeconds), ct);

                ser.Write(BurnProtocol.BuildQueryCommand(request.BurnId));
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
    /// 读响应：累积缓冲，总超时 1s，收到换行（帧边界）提前结束。
    /// 审核修复：按协议规格 §2.3 切帧——粘包（缓冲含多帧）时只取第一帧（到第一个 \n 为止），
    /// 余量丢弃（查询为 request-response 模式，下次查询前会 ResetInputBuffer），避免整块解析误判。
    /// </summary>
    private static async Task<string> ReadResponseAsync(ISerialChannel ser, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < TimeSpan.FromMilliseconds(ResponseWindowMs))
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

            await Task.Delay(ReadPollMs, ct);
        }

        return sb.ToString().Trim();
    }
}
