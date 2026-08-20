using System.IO;
using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// BurnWorker 扩展测试：通道掩码/条码参与整轮时序（v0.6.0 起 QueryUidAsync 已退役，
/// UID 由轮询 U 查询完成轮响应直接带出，见 BurnWorkerPollingTests）。
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
}
