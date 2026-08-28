using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// BurnWorker 集成验证：与协议层、串口通道抽象衔接
/// （v0.6.0 起烧录等待唯一方式为轮询，对照 BurnMachineHost docs/测试计划.md §5 手工冒烟的可自动化部分）。
/// </summary>
public class BurnWorkerTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.1;   // 校验下限（与规格 0.1-600s 一致，审核修复）

    private static BurnRequest NewRequest()
        => new(BurnSerial, BurnId, BurnProgram, BurnTime);

    [Fact]
    public async Task BurnWorker_Success_SequenceAndResult()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");   // 实测真实格式
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
        // 指令顺序：清空 → 烧录 → 查询
        Assert.Equal("`F00881289|00000001\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000001|0765\r\n", port.Writes[1]);
        Assert.Equal("`C00881289 00000001\r\n".Replace(" ", ""), port.Writes[2]);
        Assert.Contains(statuses, s => s.Contains("烧录成功"));   // 轮询模式终止提示
        // v0.6.1：指令级日志——清空/烧录指令发送时经状态回调输出（供宿主执行日志可见）
        Assert.Contains(statuses, s => s.Contains("发送清空指令") && s.Contains("`F00881289|00000001"));
        Assert.Contains(statuses, s => s.Contains("发送烧录指令") && s.Contains("`P00881289|00000001|0765"));
        Assert.False(port.IsOpen);   // 已关闭
    }

    [Fact]
    public async Task BurnWorker_NoResponse_IsTimeoutFailure()
    {
        var port = new MockSerialChannel();   // 无响应：轮询循环至超时
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None, pollingTimeoutMs: 300);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Timeout, outcome.Kind);   // 审计 BM-02：超时独立 Kind（原 Failure）
        Assert.Contains("超时", outcome.Detail);
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_RetriesThenError()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Error, outcome.Kind);
    }

    [Fact]
    public async Task BurnWorker_Cancellation_Throws()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.ExecuteAsync(NewRequest(), cts.Token));
    }

    // ---- 审核修复：协议健壮性（粘包/半包，对照串口协议规格 §2.3 切帧） ----

    [Fact]
    public async Task BurnWorker_StickyFrames_TakesFirstFrameOnly()
    {
        var port = new MockSerialChannel();
        // 查询响应帧后粘着下一帧头（设备延迟回显/多帧场景）——必须只切第一帧，余量丢弃
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n`C00881289|");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);   // 修复前：整块解析，末段为第二帧头残片 → 解析错误
    }

    [Fact]
    public async Task BurnWorker_HalfFrame_AccumulatesUntilNewline()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0");   // 半帧（无换行）
        port.EnqueueResponse("\r\n");                                                     // 补帧尾
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);   // 累积到换行（帧边界）才结算
    }

    // ---- 审核修复：执行器健壮性（关闭异常隔离 / 残留缓冲清除 / 重试与取消回归） ----

    [Fact]
    public async Task BurnWorker_CloseThrows_ReturnsOutcomeWithoutRetry()
    {
        var channel = new CloseThrowingChannel();
        var worker = new BurnWorker(() => channel);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);           // 关闭异常不影响已算出的结果
        Assert.Equal(1, channel.OpenAttempts);  // 不因关闭异常触发整轮重试
        Assert.Equal(1, channel.CloseCalls);
    }

    [Fact]
    public async Task BurnWorker_ReusedChannel_StaleBytesAreDiscardedBeforeQuery()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);

        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var first = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);
        Assert.True(first.Success);

        // 模拟上次切帧后设备才发出的残留帧头（已到达驱动缓冲，真实 Open 不会清它，靠 Discard 清除）
        port.InjectDriverBytes("`C0088");
        var resetsAfterFirst = port.ResetInputBufferCalls;

        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var second = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        // 修复前：残留帧头先被读到 → 回显序列号不匹配 → FormatError
        Assert.True(second.Success);
        // 每次执行至少清除 2 次：Open 后 1 次 + 查询写入后 1 次
        Assert.True(port.ResetInputBufferCalls >= resetsAfterFirst + 2);
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_RetriesThenSucceeds()
    {
        var port = new MockSerialChannel { OpenFailuresRemaining = 1 };   // 第 1 次打开失败，第 2 次成功
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, statuses.Count(s => s.Contains("尝试打开")));   // 整轮最多 2 次：失败 1 次 + 成功 1 次
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_AttemptsExactlyTwice()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Error, outcome.Kind);
        Assert.Equal(2, statuses.Count(s => s.Contains("尝试打开")));   // 整轮最多 2 次
    }

    [Fact]
    public async Task BurnWorker_Cancellation_DuringPolling_Throws()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);   // 取消落在轮询等待期间

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.ExecuteAsync(new BurnRequest("COM3", "00881289", "0765", 60), cts.Token));
    }

    /// <summary>Close 抛异常的通道：验证收尾异常不逃逸、不触发重试</summary>
    private sealed class CloseThrowingChannel : ISerialChannel
    {
        public int OpenAttempts { get; private set; }
        public int CloseCalls { get; private set; }
        public bool IsOpen { get; private set; }
        public void Open(string portName, int baudRate) { OpenAttempts++; IsOpen = true; }
        public void Write(string text) { }
        public string ReadAvailable() => "`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n";
        public void ResetInputBuffer() { }
        public void Close() { CloseCalls++; IsOpen = false; throw new InvalidOperationException("关闭失败: 句柄无效"); }
        public void Dispose() { }
    }
}

