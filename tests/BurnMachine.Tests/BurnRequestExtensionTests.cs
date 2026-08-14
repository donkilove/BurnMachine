using BurnMachine;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>v0.2.0 BurnRequest 扩展测试：新增 Channels/Barcode 属性（主构造不变，向后兼容）</summary>
public class BurnRequestExtensionTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.1;

    [Fact]
    public void NewRequest_DefaultChannels_IsChannelA()
    {
        var req = new BurnRequest(BurnSerial, BurnId, BurnProgram, BurnTime);
        Assert.Equal(ChannelMask.A, req.Channels);
    }

    [Fact]
    public void NewRequest_DefaultBarcode_IsNull()
    {
        var req = new BurnRequest(BurnSerial, BurnId, BurnProgram, BurnTime);
        Assert.Null(req.Barcode);
    }

    [Fact]
    public void NewRequest_OldFourArgCtor_StillWorks()
    {
        // 向后兼容：v0.1.1 的 4 参数调用方式原样可用
        var req = new BurnRequest(BurnSerial, BurnId, BurnProgram, BurnTime);
        Assert.Equal(BurnSerial, req.BurnSerial);
        Assert.Equal(BurnId, req.BurnId);
        Assert.Equal(BurnProgram, req.BurnProgram);
        Assert.Equal(BurnTime, req.BurnTimeSeconds);
    }

    [Fact]
    public void NewRequest_WithInitProperties_KeepsValues()
    {
        var req = new BurnRequest(BurnSerial, BurnId, BurnProgram, BurnTime)
        {
            Channels = ChannelMask.B,
            Barcode = [0xAB, 0xCD],
        };
        Assert.Equal(ChannelMask.B, req.Channels);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, req.Barcode);
    }

    [Fact]
    public void NewRequest_WithBothChannels_Accepted()
    {
        var req = new BurnRequest(BurnSerial, BurnId, BurnProgram, BurnTime) { Channels = ChannelMask.Both };
        Assert.Equal(ChannelMask.Both, req.Channels);
    }
}
