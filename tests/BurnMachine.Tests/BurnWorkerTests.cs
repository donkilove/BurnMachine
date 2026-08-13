using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// BurnWorker 集成验证：与协议层、串口通道抽象衔接
/// （对照 BurnMachineHost docs/测试计划.md §5 手工冒烟的可自动化部分）。
/// </summary>
public class BurnWorkerTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.01;

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
        Assert.Contains(statuses, s => s.Contains("收到响应"));
        Assert.False(port.IsOpen);   // 已关闭
    }

    [Fact]
    public async Task BurnWorker_NoResponse_IsFailure()
    {
        var port = new MockSerialChannel();   // 无响应
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.NoResponse, outcome.Kind);
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
}
