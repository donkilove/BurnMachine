using BurnMachine;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>烧录机协议测试（对照 BurnMachineHost docs/串口协议规格.md §2）</summary>
public class BurnProtocolTests
{
    private const string BurnId = "00881289";

    // 实测响应格式（串口协议规格 §2.7/2.8）：`C{序列号}|{通道掩码}|{镜像号}|{主校验和}|{剩余次数}|{结果码}
    private static string Ok(string code = "0") => $"`C{BurnId}|00000001|0002|002A9717|0000000000016BC4|{code}";

    // ---- 指令构造 P1-P3 ----
    [Fact]
    public void BuildClearCommand_ShouldMatchSpec()
        => Assert.Equal("`F00881289|00000001\r\n", BurnProtocol.BuildClearCommand(BurnId));

    [Fact]
    public void BuildBurnCommand_ShouldMatchSpec()
        => Assert.Equal("`P00881289|00000001|0765\r\n", BurnProtocol.BuildBurnCommand(BurnId, "0765"));

    [Fact]
    public void BuildQueryCommand_ShouldHaveNoPipe()
        => Assert.Equal("`C00881289 00000001\r\n".Replace(" ", ""), BurnProtocol.BuildQueryCommand(BurnId));

    // ---- 响应解析 P4-P11 ----

    [Fact]
    public void Parse_ResultCodeZero_IsSuccess()
    {
        var kind = BurnProtocol.ParseResponse(Ok(), BurnId, out _);
        Assert.Equal(BurnResultKind.Success, kind);
    }

    [Fact]
    public void Parse_ResultCodeOne_IsFailure()
    {
        var kind = BurnProtocol.ParseResponse(Ok("1"), BurnId, out _);
        Assert.Equal(BurnResultKind.Failure, kind);
    }

    [Fact]
    public void Parse_ResultCodeLetter_IsFailure()
    {
        var kind = BurnProtocol.ParseResponse(Ok("E"), BurnId, out _);
        Assert.Equal(BurnResultKind.Failure, kind);
    }

    [Fact]
    public void Parse_MultiCharResultCode_IsFailure()
    {
        // 审核修复：结果码按协议精确匹配（"00" 不是合法结果码，不能因首字符为 0 误判成功）
        var kind = BurnProtocol.ParseResponse(Ok("00"), BurnId, out _);
        Assert.Equal(BurnResultKind.Failure, kind);
    }

    [Fact]
    public void Parse_EmptyResponse_IsNoResponse()
    {
        Assert.Equal(BurnResultKind.NoResponse, BurnProtocol.ParseResponse("", BurnId, out _));
        Assert.Equal(BurnResultKind.NoResponse, BurnProtocol.ParseResponse("  \r\n ", BurnId, out _));
    }

    [Fact]
    public void Parse_NullResponse_IsNoResponse()
        => Assert.Equal(BurnResultKind.NoResponse, BurnProtocol.ParseResponse(null, BurnId, out _));

    [Fact]
    public void Parse_NoBacktickPrefix_IsFormatError()
    {
        var kind = BurnProtocol.ParseResponse("no-backtick|a|b|c|0", BurnId, out var detail);
        Assert.Equal(BurnResultKind.FormatError, kind);
        Assert.Equal("响应格式错误：不是以`开头", detail);
    }

    [Fact]
    public void Parse_LessThanFiveSegments_IsFormatError()
    {
        var kind = BurnProtocol.ParseResponse("`C00881289|B|C", BurnId, out var detail);
        Assert.Equal(BurnResultKind.FormatError, kind);
        Assert.Equal("响应格式错误：部分数量不足 3/5", detail);
    }

    [Fact]
    public void Parse_EmptyLastSegment_IsError()
    {
        // 原版 parts[-1][0] 在末段为空时抛 IndexError → Error 分支
        var kind = BurnProtocol.ParseResponse("`C00881289|00000001|0002|002A9717|", BurnId, out _);
        Assert.Equal(BurnResultKind.Error, kind);
    }

    [Fact]
    public void Parse_ResponseWithSurroundingWhitespace_IsTrimmedFirst()
    {
        var kind = BurnProtocol.ParseResponse($"  {Ok()}\r\n", BurnId, out _);
        Assert.Equal(BurnResultKind.Success, kind);
    }

    // ---- 审核修复：判定完整性加固 ----

    [Fact]
    public void Parse_EchoedIdMismatch_IsFormatError()
    {
        // 响应回显的序列号与本次查询不一致（串扰/粘包错位）→ 拒绝，不能误判为本次结果
        var kind = BurnProtocol.ParseResponse("`C99999999|00000001|0002|002A9717|0000000000016BC4|0", BurnId, out var detail);
        Assert.Equal(BurnResultKind.FormatError, kind);
        Assert.Equal("响应格式错误：回显序列号不匹配", detail);
    }

    [Fact]
    public void Parse_FirstSegmentNotQueryForm_IsFormatError()
    {
        // 首段不是 `C + 8 位序列号（如回显/乱码）→ 拒绝
        var kind = BurnProtocol.ParseResponse("`A|00000001|0002|002A9717|0000000000016BC4|0", BurnId, out _);
        Assert.Equal(BurnResultKind.FormatError, kind);
    }

    [Fact]
    public void Parse_OversizeFrame_IsFormatError()
    {
        var longPayload = new string('A', BurnProtocol.MaxResponseLength + 10);
        var kind = BurnProtocol.ParseResponse($"`C{BurnId}|{longPayload}|0002|002A9717|0", BurnId, out var detail);
        Assert.Equal(BurnResultKind.FormatError, kind);
        Assert.Contains("帧长度超限", detail);
    }

    [Fact]
    public void Parse_DetailOnSuccessAndFailure()
    {
        BurnProtocol.ParseResponse(Ok(), BurnId, out var okDetail);
        Assert.Equal("烧录成功", okDetail);
        BurnProtocol.ParseResponse(Ok("1"), BurnId, out var failDetail);
        Assert.Equal("烧录失败", failDetail);
    }

    // ---- 审核修复：指令构造输入校验（严格设备字段规则） ----

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("008812899")]
    [InlineData("00A81289")]
    [InlineData("0088 289")]
    public void BuildClearCommand_InvalidBurnId_Throws(string burnId)
        => Assert.Throws<ArgumentException>(() => BurnProtocol.BuildClearCommand(burnId));

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("AB12")]
    [InlineData("07651")]
    public void BuildBurnCommand_InvalidBurnProgram_Throws(string program)
        => Assert.Throws<ArgumentException>(() => BurnProtocol.BuildBurnCommand(BurnId, program));

    [Fact]
    public void BuildQueryCommand_InvalidBurnId_Throws()
        => Assert.Throws<ArgumentException>(() => BurnProtocol.BuildQueryCommand("00A81289"));

    [Fact]
    public void BuildCommands_ValidInputs_Unchanged()
    {
        Assert.Equal("`F00881289|00000001\r\n", BurnProtocol.BuildClearCommand(BurnId));
        Assert.Equal("`P00881289|00000001|0765\r\n", BurnProtocol.BuildBurnCommand(BurnId, "0765"));
        Assert.Equal("`C0088128900000001\r\n", BurnProtocol.BuildQueryCommand(BurnId));
    }
}

