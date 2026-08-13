using BurnMachine;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>BurnRequest 输入校验（审核修复：严格设备字段规则——burnId 8 位十进制、program 4 位十进制、时间 0.1-600s）</summary>
public class BurnRequestTests
{
    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("008812899")]
    [InlineData("00A81289")]
    public void InvalidBurnId_Throws(string burnId)
        => Assert.Throws<ArgumentException>(() => new BurnRequest("COM3", burnId, "0765", 0.1));

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("AB12")]
    [InlineData("07651")]
    public void InvalidBurnProgram_Throws(string program)
        => Assert.Throws<ArgumentException>(() => new BurnRequest("COM3", "00881289", program, 0.1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(600.001)]
    public void InvalidBurnTime_Throws(double seconds)
        // ArgumentOutOfRangeException : ArgumentException，用 ThrowsAny 覆盖精确类型
        => Assert.ThrowsAny<ArgumentException>(() => new BurnRequest("COM3", "00881289", "0765", seconds));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidBurnSerial_Throws(string serial)
        => Assert.Throws<ArgumentException>(() => new BurnRequest(serial, "00881289", "0765", 0.1));

    [Fact]
    public void ValidRequest_IsAccepted()
    {
        var req = new BurnRequest("COM3", "00881289", "0765", 0.1);
        Assert.Equal("COM3", req.BurnSerial);
        Assert.Equal("00881289", req.BurnId);
        Assert.Equal("0765", req.BurnProgram);
        Assert.Equal(0.1, req.BurnTimeSeconds);
    }
}
