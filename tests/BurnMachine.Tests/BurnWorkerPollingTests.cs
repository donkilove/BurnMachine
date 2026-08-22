using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// 轮询等待模式（v0.6.0 起为唯一烧录等待方式）测试。
/// 终止语义由真实硬件实测确认（COM3 / 00911008 / 镜像 0000）：
/// 烧录进行中 C 查询返回结果码 2（已清空），烧录完成变 0；轮询不干扰烧录。
/// 轮询循环：结果码 0/1 判定完成，2/3 与无响应/无效帧继续，超时判失败。
/// </summary>
public class BurnWorkerPollingTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.1;   // 轮询模式忽略 BurnTimeSeconds（超时取代固定等待）

    private const string BurnInProgress =
        "`C00881289|00000001|0002|002A9717|0000000000016BC4|2\r\n";   // 结果码 2：已清空/烧录中
    private const string NoRecord =
        "`C00881289|00000001|9999|FFFFFFFF|FFFFFFFFFFFFFFFF|3\r\n";   // 结果码 3：无记录
    private const string Success =
        "`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n";
    private const string Failed =
        "`C00881289|00000001|0002|002A9717|0000000000016BC4|1\r\n";

    private static BurnRequest NewRequest()
        => new(BurnSerial, BurnId, BurnProgram, BurnTime);

    [Fact]
    public void Polling_DefaultParameters_Are30msIntervalAnd3500msTimeout()
    {
        // 默认值经真实硬件验证：30ms 间隔 16~17 轮查询零丢帧（v0.6.3），3500ms 覆盖实测烧录时长（~2.3s）留有余量
        Assert.Equal(30, BurnWorker.DefaultPollingIntervalMs);
        Assert.Equal(3500, BurnWorker.DefaultPollingTimeoutMs);
    }

    [Fact]
    public async Task Polling_DefaultParameters_ExecuteWithDefaults()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, Success]);
        var worker = new BurnWorker(() => port);

        // 不传间隔/超时：使用默认值 30ms / 3500ms
        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));
    }

    // ==================== v0.5.0：轮询模式 U 命令支持（PollingQueryKind 开关） ====================
    // 实测依据（COM9/00911008/镜像 0000）：烧录中 U 查询返回结果码 2 照常应答不干扰烧录，
    // 完成后返回 0 且 UID 区携带 16 字节真实数据；部分轮 UID 长度前缀为 00（解析为 null）。

    private const string UidInProgress =   // 结果码 2，UID 长度 0x00 → Uid=null
        "`U00881289|00000001|0002|002A9717|0000000000016BC4|2|00343435500B0039364F0048001F0000000000000000000000\r\n";
    private const string UidSuccess =      // 结果码 0，UID 长度 0x10 → 16 字节 UID
        "`U00881289|00000001|0002|002A9717|0000000000016BC4|0|10343435500B0039364F0048001F0000000000000000000000\r\n";
    private const string UidFailed =       // 结果码 1，UID 长度 0x10
        "`U00881289|00000001|0002|002A9717|0000000000016BC4|1|10343435500B0039364F0048001F0000000000000000000000\r\n";

    private static readonly byte[] ExpectedUid =
        [0x34, 0x34, 0x35, 0x50, 0x0B, 0x00, 0x39, 0x36, 0x4F, 0x00, 0x48, 0x00, 0x1F, 0x00, 0x00, 0x00];

    [Fact]
    public async Task Polling_UidQuery_PollsWithUAndReturnsUid()
    {
        var port = new ScriptedPollingChannel([UidInProgress, UidInProgress, UidSuccess]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000,
            pollingQuery: PollingQueryKind.U);

        Assert.True(outcome.Success);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
        // 指令顺序：清空 → 烧录 → 3 次 U 轮询（烧录中 2 次 + 完成 1 次），不再出现 C 查询
        Assert.Equal("`F00881289|00000001\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000001|0765\r\n", port.Writes[1]);
        Assert.Equal(3, port.Writes.Count(w => w.StartsWith("`U")));
        Assert.DoesNotContain(port.Writes, w => w.StartsWith("`C"));
        // 完成轮响应中的 UID 随结果带出
        Assert.NotNull(outcome.Uid);
        Assert.Equal(ExpectedUid, outcome.Uid);
    }

    [Fact]
    public async Task Polling_UidQuery_FailedRoundCarriesUid()
    {
        var port = new ScriptedPollingChannel([UidInProgress, UidFailed]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000,
            pollingQuery: PollingQueryKind.U);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Failure, outcome.Kind);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`U")));   // 结果码 1 立即停止
        Assert.Equal(ExpectedUid, outcome.Uid);   // 失败轮同样携带 UID
    }

    [Fact]
    public async Task Polling_UidQuery_ZeroLengthUidZone_ReturnsNullUid()
    {
        // 完成轮 UID 长度前缀 00（设备怪癖实测出现）→ Uid=null，不抛异常
        var zeroLenSuccess =
            "`U00881289|00000001|0002|002A9717|0000000000016BC4|0|00343435500B0039364F0048001F0000000000000000000000\r\n";
        var port = new ScriptedPollingChannel([zeroLenSuccess]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000,
            pollingQuery: PollingQueryKind.U);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Uid);
    }

    [Fact]
    public async Task Polling_UidQuery_NoResponseRound_ContinuesPolling()
    {
        var port = new ScriptedPollingChannel(["", UidSuccess]);   // 首轮无应答 → 继续 U 轮询
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000,
            pollingQuery: PollingQueryKind.U);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`U")));
        Assert.Equal(ExpectedUid, outcome.Uid);
    }

    [Fact]
    public async Task Polling_UidQuery_Timeout_ReturnsFailure()
    {
        var port = new ScriptedPollingChannel([UidInProgress]);   // 永远"烧录中"
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 200, pollingTimeoutMs: 300,
            pollingQuery: PollingQueryKind.U);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Failure, outcome.Kind);
        Assert.Contains("超时", outcome.Detail);
    }

    [Fact]
    public async Task Polling_DefaultQueryKind_IsC_AndUidIsNull()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, Success]);
        var worker = new BurnWorker(() => port);

        // 不传 pollingQuery：默认 C 查询，outcome.Uid 为 null
        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(0, (int)PollingQueryKind.C);   // C 为默认枚举值
        Assert.Null(outcome.Uid);
        Assert.DoesNotContain(port.Writes, w => w.StartsWith("`U"));
    }

    [Fact]
    public async Task Polling_BurnInProgressThenSuccess_PollsUntilCodeZero()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, BurnInProgress, Success]);
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000);

        Assert.True(outcome.Success);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
        Assert.Equal("烧录成功", outcome.Detail);
        // 指令顺序：清空 → 烧录 → 3 次轮询查询（烧录中 2 次 + 完成 1 次）
        Assert.Equal("`F00881289|00000001\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000001|0765\r\n", port.Writes[1]);
        Assert.Equal(3, port.Writes.Count(w => w.StartsWith("`C")));
        Assert.Contains(statuses, s => s.Contains("轮询"));
        Assert.False(port.IsOpen);   // 执行结束已关闭
    }

    [Fact]
    public async Task Polling_ResultCodeOne_ReturnsFailure()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, Failed]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Failure, outcome.Kind);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));   // 结果码 1 立即停止，不再轮询
    }

    [Fact]
    public async Task Polling_NoRecordCodeThree_ContinuesPolling()
    {
        var port = new ScriptedPollingChannel([NoRecord, Success]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));   // 结果码 3 不算失败，继续轮询
    }

    [Fact]
    public async Task Polling_NoResponseRound_ContinuesPolling()
    {
        var port = new ScriptedPollingChannel(["", Success]);   // 第一轮设备无应答，继续轮询
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));
    }

    [Fact]
    public async Task Polling_MalformedFrame_ContinuesPolling()
    {
        // 回显序列号不匹配（畸形帧）→ ParseQueryStatus 返回 null → 视为仍在烧录继续轮询 → 下一轮成功
        var port = new ScriptedPollingChannel(
            ["`C99999999|00000001|0002|002A9717|0000000000016BC4|0\r\n", Success]);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 50, pollingTimeoutMs: 4000);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));
    }

    [Fact]
    public async Task Polling_Timeout_ReturnsFailureWithTimeoutDetail()
    {
        var port = new ScriptedPollingChannel([BurnInProgress]);   // 永远"烧录中"
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            pollingIntervalMs: 200, pollingTimeoutMs: 300);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Failure, outcome.Kind);
        Assert.Contains("超时", outcome.Detail);
        Assert.True(port.Writes.Count(w => w.StartsWith("`C")) >= 2);   // 至少轮询 2 次后才超时
    }

    [Theory]
    [InlineData(10, 4000)]      // 间隔低于下限 30ms
    [InlineData(20000, 4000)]   // 间隔高于上限 10000ms
    [InlineData(200, 50)]       // 超时低于下限 100ms
    [InlineData(200, 700000)]   // 超时高于上限 600000ms
    public async Task Polling_InvalidParameters_Throw(int intervalMs, int timeoutMs)
    {
        var port = new ScriptedPollingChannel([]);
        var worker = new BurnWorker(() => port);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            worker.ExecuteAsync(NewRequest(), CancellationToken.None,
                intervalMs, timeoutMs));
    }

    [Fact]
    public async Task Polling_CancellationDuringWait_Throws()
    {
        var port = new ScriptedPollingChannel([BurnInProgress]);
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(60);   // 取消落在轮询等待期间

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.ExecuteAsync(NewRequest(), cts.Token,
                pollingIntervalMs: 200, pollingTimeoutMs: 4000));
    }

    /// <summary>
    /// 按查询逐次应答的模拟通道：每次收到一条 C 查询指令，释放下一条预置响应
    /// （模拟真实设备"烧录中返回 2、完成返回 0/1"的逐次应答，而非一次释放全部）。
    /// </summary>
    private sealed class ScriptedPollingChannel : ISerialChannel
    {
        private readonly string[] _responses;
        private readonly List<string> _writes = new();
        private int _queryCount;
        private int _cWrites;

        public ScriptedPollingChannel(string[] responses) => _responses = responses;

        public IReadOnlyList<string> Writes => _writes;
        public bool IsOpen { get; private set; }

        public void Open(string portName, int baudRate) => IsOpen = true;

        public void Write(string text)
        {
            _writes.Add(text);
            if (text.Length > 1 && text[0] == '`' && text[1] is 'C' or 'U')   // C/U 查询都驱动逐次应答
            {
                _cWrites++;
            }
        }

        public string ReadAvailable()
        {
            // 每收到一条新查询指令，释放下一条预置响应（只释放一次，随后返回空）
            if (_cWrites > _queryCount && _queryCount < _responses.Length)
            {
                return _responses[_queryCount++];
            }

            return "";
        }

        public void ResetInputBuffer() { }
        public void Close() => IsOpen = false;
        public void Dispose() { }
    }
}
