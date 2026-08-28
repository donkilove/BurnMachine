using BurnMachine;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// v0.2.0 协议扩展测试：通道掩码、条码、U 命令、C 回复详情解析。
/// 对照 docs/XW16Pro扩展串口控制协议.md（手册第 10 章）与现有 v0.1.1 行为。
/// </summary>
public class BurnProtocolExtensionTests
{
    private const string BurnId = "00881289";

    // 手册示例：`C{序列号}|{掩码}|{镜像号}|{校验和}|{剩余}|{结果码}
    private static string OkC(string mask = "00000001", string image = "0765",
        string checksum = "0A38AEF0", string remaining = "FFFFFFFFFFFFFFFF", string code = "0")
        => $"`C{BurnId}|{mask}|{image}|{checksum}|{remaining}|{code}\r\n";

    // 手册示例：`U{序列号}|{掩码}|{镜像号}|{校验和}|{剩余}|{结果码}|{UID区50字符}
    private static string OkU(string mask = "00000001", string uidZone = "0C31FF410B3342393631540443000000000000000000000000")
        => $"`U{BurnId}|{mask}|0000|00000000|FFFFFFFFFFFFFFFF|0|{uidZone}\r\n";

    // ---- 通道掩码 ----

    [Fact]
    public void BuildBurnCommand_ChannelA_MatchesV011Format()
        => Assert.Equal("`P00881289|00000001|0765\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.A));

    [Fact]
    public void BuildBurnCommand_ChannelB_UsesMask00000002()
        => Assert.Equal("`P00881289|00000002|0765\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.B));

    [Fact]
    public void BuildBurnCommand_BothChannels_UsesMask00000003()
        => Assert.Equal("`P00881289|00000003|0765\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.Both));

    [Fact]
    public void BuildClearCommand_ChannelB_UsesMask00000002()
        => Assert.Equal("`F00881289|00000002\r\n",
            BurnProtocol.BuildClearCommand(BurnId, ChannelMask.B));

    [Fact]
    public void BuildQueryCommand_ChannelB_UsesMask00000002()
        => Assert.Equal("`C00881289 00000002\r\n".Replace(" ", ""),
            BurnProtocol.BuildQueryCommand(BurnId, ChannelMask.B));

    // ---- 条码 ----

    [Fact]
    public void BuildBurnCommand_WithBarcode_AppendsHexField()
    {
        // 手册示例：{0x30,0x31,0x32,0x33,0x33,0x35,0x36} → 30313233333536
        byte[] barcode = [0x30, 0x31, 0x32, 0x33, 0x33, 0x35, 0x36];
        Assert.Equal("`P00881289|00000001|0765|30313233333536\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.A, barcode));
    }

    [Fact]
    public void BuildBurnCommand_WithBarcode_ChannelB_CombinesBoth()
        => Assert.Equal("`P00881289|00000002|0765|00FF10\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.B, [0x00, 0xFF, 0x10]));

    [Fact]
    public void BuildBurnCommand_NullBarcode_OmitsField()
        => Assert.Equal("`P00881289|00000001|0765\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.A, null));

    [Fact]
    public void BuildBurnCommand_EmptyBarcode_OmitsField()
        => Assert.Equal("`P00881289|00000001|0765\r\n",
            BurnProtocol.BuildBurnCommand(BurnId, "0765", ChannelMask.A, []));

    // ---- 旧签名兼容回归 ----

    [Fact]
    public void BuildBurnCommand_OldSignature_Unchanged()
        => Assert.Equal("`P00881289|00000001|0765\r\n", BurnProtocol.BuildBurnCommand(BurnId, "0765"));

    // ---- U 命令构造 ----

    [Fact]
    public void BuildUidQueryCommand_MatchesSpec_NoPipe()
        => Assert.Equal("`U00881289 00000001\r\n".Replace(" ", ""),
            BurnProtocol.BuildUidQueryCommand(BurnId));

    [Fact]
    public void BuildUidQueryCommand_ChannelB_UsesMask00000002()
        => Assert.Equal("`U00881289 00000002\r\n".Replace(" ", ""),
            BurnProtocol.BuildUidQueryCommand(BurnId, ChannelMask.B));

    // ---- C 回复详情解析 ----

    [Fact]
    public void ParseDetailed_Success_ParsesAllFields()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(), BurnId);
        Assert.Equal(BurnResultKind.Success, r.Kind);
        Assert.Equal(765, r.ImageNo);
        Assert.Equal(0x0A38AEF0u, r.MainChecksum);
        Assert.Null(r.RemainingCount);          // FFFFFFFFFFFFFFFF = 无限次
        Assert.Equal(BurnStatus.Success, r.Status);
    }

    [Fact]
    public void ParseDetailed_WithRemainingCount_ParsesDecimalValue()
    {
        // 实测格式（BurnWorkerTests）：剩余 0000000000016BC4 = 0x16BC4 = 93124
        var r = BurnProtocol.ParseResponseDetailed(OkC(remaining: "0000000000016BC4"), BurnId);
        Assert.Equal(BurnResultKind.Success, r.Kind);
        Assert.Equal(93124L, r.RemainingCount);
    }

    [Fact]
    public void ParseDetailed_Image9999_NullImageNo()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(image: "9999"), BurnId);
        Assert.Equal(BurnResultKind.Success, r.Kind);
        Assert.Null(r.ImageNo);
    }

    [Fact]
    public void ParseDetailed_ChecksumFFFFFFFF_NullChecksum()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(checksum: "FFFFFFFF"), BurnId);
        Assert.Null(r.MainChecksum);
    }

    [Fact]
    public void ParseDetailed_ResultCode1_StatusFailed_KindFailure()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(code: "1"), BurnId);
        Assert.Equal(BurnResultKind.Failure, r.Kind);   // 兼容：非 0 即 Failure
        Assert.Equal(BurnStatus.Failed, r.Status);
    }

    [Fact]
    public void ParseDetailed_ResultCode2_StatusCleared_KindFailure()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(code: "2"), BurnId);
        Assert.Equal(BurnResultKind.Failure, r.Kind);
        Assert.Equal(BurnStatus.Cleared, r.Status);
    }

    [Fact]
    public void ParseDetailed_ResultCode3_StatusNoRecord_KindFailure()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(code: "3"), BurnId);
        Assert.Equal(BurnResultKind.Failure, r.Kind);
        Assert.Equal(BurnStatus.NoRecord, r.Status);
    }

    [Fact]
    public void ParseDetailed_MalformedResultCode_StatusNull()
    {
        var r = BurnProtocol.ParseResponseDetailed(OkC(code: "00"), BurnId);
        Assert.Equal(BurnResultKind.Failure, r.Kind);
        Assert.Null(r.Status);
    }

    [Fact]
    public void ParseDetailed_NoResponse_AllNull()
    {
        var r = BurnProtocol.ParseResponseDetailed(null, BurnId);
        Assert.Equal(BurnResultKind.NoResponse, r.Kind);
        Assert.Null(r.ImageNo);
        Assert.Null(r.MainChecksum);
        Assert.Null(r.RemainingCount);
        Assert.Null(r.Status);
    }

    [Fact]
    public void ParseDetailed_NoBacktickPrefix_FormatError_AllNull()
    {
        var r = BurnProtocol.ParseResponseDetailed("no-backtick|a|b|c|0", BurnId);
        Assert.Equal(BurnResultKind.FormatError, r.Kind);
        Assert.Null(r.Status);
    }

    [Fact]
    public void ParseDetailed_EchoedIdMismatch_FormatError()
    {
        var r = BurnProtocol.ParseResponseDetailed("`C00000000|00000001|0765|0A38AEF0|FFFFFFFFFFFFFFFF|0", BurnId);
        Assert.Equal(BurnResultKind.FormatError, r.Kind);
    }

    // ---- 结果码解析（v0.3.0 轮询用） ----

    [Theory]
    [InlineData("0", BurnStatus.Success)]
    [InlineData("1", BurnStatus.Failed)]
    [InlineData("2", BurnStatus.Cleared)]
    [InlineData("3", BurnStatus.NoRecord)]
    public void ParseQueryStatus_ValidCode_ReturnsStatus(string code, BurnStatus expected)
        => Assert.Equal(expected, BurnProtocol.ParseQueryStatus(OkC(code: code), BurnId));

    [Fact]
    public void ParseQueryStatus_NoResponse_ReturnsNull()
        => Assert.Null(BurnProtocol.ParseQueryStatus(null, BurnId));

    [Fact]
    public void ParseQueryStatus_EchoedIdMismatch_ReturnsNull()
        => Assert.Null(BurnProtocol.ParseQueryStatus("`C00000000|00000001|0765|0A38AEF0|FFFFFFFFFFFFFFFF|0", BurnId));

    [Fact]
    public void ParseQueryStatus_MalformedCode_ReturnsNull()
        => Assert.Null(BurnProtocol.ParseQueryStatus(OkC(code: "00"), BurnId));

    // ---- U 回复解析 ----

    [Fact]
    public void ParseUid_Success_ExtractsUidBytes()
    {
        // 手册示例：0C = 12 字节，UID = 31 FF 41 0B 33 42 39 36 31 54 04 43
        var r = BurnProtocol.ParseUidResponse(OkU(), BurnId);
        Assert.Equal(BurnResultKind.Success, r.Base.Kind);
        Assert.Equal(new byte[] { 0x31, 0xFF, 0x41, 0x0B, 0x33, 0x42, 0x39, 0x36, 0x31, 0x54, 0x04, 0x43 }, r.Uid);
    }

    [Fact]
    public void ParseUid_SuccessWithResultFields_ParsesBase()
    {
        var r = BurnProtocol.ParseUidResponse(OkU(), BurnId);
        Assert.Equal(0, r.Base.ImageNo);
        Assert.Equal(0u, r.Base.MainChecksum);
        Assert.Equal(BurnStatus.Success, r.Base.Status);
    }

    [Fact]
    public void ParseUid_MissingUidZone_KeepsBaseKind_UidNull()
    {
        // 审计 BM-01：6 段有效帧（结果码 0）缺 UID 区——保留 Success 判定，仅 Uid=null
        // （此前改判 FormatError 使成功帧被误判"烧录超时"NG）
        var r = BurnProtocol.ParseUidResponse("`U00881289|00000001|0000|00000000|FFFFFFFFFFFFFFFF|0\r\n", BurnId);
        Assert.Equal(BurnResultKind.Success, r.Base.Kind);
        Assert.Equal(BurnStatus.Success, r.Base.Status);
        Assert.Null(r.Uid);
    }

    [Fact]
    public void ParseUid_MissingUidZone_FailureFrame_KeepsFailure()
    {
        // 6 段失败帧（结果码 1）：保留 Failure 判定，仅 Uid=null
        var r = BurnProtocol.ParseUidResponse("`U00881289|00000001|0000|00000000|FFFFFFFFFFFFFFFF|1\r\n", BurnId);
        Assert.Equal(BurnResultKind.Failure, r.Base.Kind);
        Assert.Equal(BurnStatus.Failed, r.Base.Status);
        Assert.Null(r.Uid);
    }

    [Fact]
    public void ParseUid_TooShortUidZone_UidNull()
    {
        var r = BurnProtocol.ParseUidResponse(OkU(uidZone: "0C"), BurnId);
        Assert.Null(r.Uid);
    }

    [Fact]
    public void ParseUid_InvalidHexInUidZone_UidNull()
    {
        var r = BurnProtocol.ParseUidResponse(OkU(uidZone: "0C" + new string('Z', 48)), BurnId);
        Assert.Null(r.Uid);
    }

    [Fact]
    public void ParseUid_LengthExceedsData_UidNull()
    {
        // 声明 0x30 = 48 字节，但实际可用数据不足
        var r = BurnProtocol.ParseUidResponse(OkU(uidZone: "30" + new string('0', 48)), BurnId);
        Assert.Null(r.Uid);
    }

    [Fact]
    public void ParseUid_NoResponse_BaseNoResponse_UidNull()
    {
        var r = BurnProtocol.ParseUidResponse(null, BurnId);
        Assert.Equal(BurnResultKind.NoResponse, r.Base.Kind);
        Assert.Null(r.Uid);
    }
}
