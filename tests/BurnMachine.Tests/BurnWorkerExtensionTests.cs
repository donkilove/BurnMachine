using System.IO;
using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// v0.2.0 BurnWorker 扩展测试：通道掩码/条码参与整轮时序、UID 查询。
/// MockSerialChannel 在收到 `U 查询指令时同样释放响应（模拟真实设备）。
/// </summary>
public class BurnWorkerExtensionTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.1;

    private static BurnRequest NewRequest(ChannelMask channels = ChannelMask.A, byte[]? barcode = null)
        => new(BurnSerial, BurnId, BurnProgram, BurnTime) { Channels = channels, Barcode = barcode };

    [Fact]
    public async Task Execute_DefaultChannel_CommandSequenceMatchesV011()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("`F00881289|00000001\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000001|0765\r\n", port.Writes[1]);
        Assert.Equal("`C00881289 00000001\r\n".Replace(" ", ""), port.Writes[2]);
    }

    [Fact]
    public async Task Execute_ChannelB_AllCommandsUseMask00000002()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000002|0002|002A9717|0000000000016BC4|0\r\n");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(ChannelMask.B), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("`F00881289|00000002\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000002|0765\r\n", port.Writes[1]);
        Assert.Equal("`C00881289 00000002\r\n".Replace(" ", ""), port.Writes[2]);
    }

    [Fact]
    public async Task Execute_WithBarcode_BurnCommandAppendsBarcode()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(barcode: [0x30, 0x31, 0x32]), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("`P00881289|00000001|0765|303132\r\n", port.Writes[1]);
    }

    [Fact]
    public async Task QueryUid_Success_ReturnsUid()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`U00881289|00000001|0000|00000000|FFFFFFFFFFFFFFFF|0|0C31FF410B3342393631540443000000000000000000000000\r\n");
        var worker = new BurnWorker(() => port);

        var result = await worker.QueryUidAsync(BurnSerial, BurnId, ct: CancellationToken.None);

        Assert.Equal(BurnResultKind.Success, result.Base.Kind);
        Assert.Equal(new byte[] { 0x31, 0xFF, 0x41, 0x0B, 0x33, 0x42, 0x39, 0x36, 0x31, 0x54, 0x04, 0x43 }, result.Uid);
        Assert.Equal("`U00881289 00000001\r\n".Replace(" ", ""), port.Writes[0]);
        Assert.False(port.IsOpen);   // 执行结束已关闭
    }

    [Fact]
    public async Task QueryUid_NoResponse_BaseNoResponse()
    {
        var port = new MockSerialChannel();   // 无响应（模拟固件不支持 U 命令/设备静默）
        var worker = new BurnWorker(() => port);

        var result = await worker.QueryUidAsync(BurnSerial, BurnId, ct: CancellationToken.None);

        Assert.Equal(BurnResultKind.NoResponse, result.Base.Kind);
        Assert.Null(result.Uid);
    }

    [Theory]
    [InlineData("ZZ31FF41")]      // 长度声明非 hex
    [InlineData("0C31FF41")]      // 声明 12 字节但数据不足
    public async Task QueryUid_MalformedUidZone_UidNullButBaseSuccess(string uidZone)
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse($"`U00881289|00000001|0000|00000000|FFFFFFFFFFFFFFFF|0|{uidZone}\r\n");
        var worker = new BurnWorker(() => port);

        var result = await worker.QueryUidAsync(BurnSerial, BurnId, ct: CancellationToken.None);

        Assert.Equal(BurnResultKind.Success, result.Base.Kind);   // 结果码 0：基解析成功
        Assert.Null(result.Uid);                                  // UID 区畸形 → null（不抛异常）
    }

    [Fact]
    public async Task QueryUid_OpenFailure_Throws()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var worker = new BurnWorker(() => port);

        await Assert.ThrowsAsync<IOException>(() => worker.QueryUidAsync(BurnSerial, BurnId, ct: CancellationToken.None));
    }

    [Fact]
    public async Task QueryUid_Cancellation_Throws()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.QueryUidAsync(BurnSerial, BurnId, ct: cts.Token));
    }
}
