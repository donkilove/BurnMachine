using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// 轮询等待模式（BurnWaitMode.Polling）测试。
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
    public void Polling_DefaultParameters_Are100msIntervalAnd3500msTimeout()
    {
        // 默认值经真实硬件验证：100ms 间隔设备稳定应答，3500ms 覆盖实测烧录时长（~2.3s）留有余量
        Assert.Equal(100, BurnWorker.DefaultPollingIntervalMs);
        Assert.Equal(3500, BurnWorker.DefaultPollingTimeoutMs);
    }

    [Fact]
    public async Task Polling_DefaultParameters_ExecuteWithDefaults()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, Success]);
        var worker = new BurnWorker(() => port);

        // 不传间隔/超时：使用默认值 100ms / 3500ms
        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None, BurnWaitMode.Polling);

        Assert.True(outcome.Success);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));
    }

    [Fact]
    public async Task Polling_BurnInProgressThenSuccess_PollsUntilCodeZero()
    {
        var port = new ScriptedPollingChannel([BurnInProgress, BurnInProgress, Success]);
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None,
            BurnWaitMode.Polling, pollingIntervalMs: 50, pollingTimeoutMs: 4000);

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
            BurnWaitMode.Polling, pollingIntervalMs: 50, pollingTimeoutMs: 4000);

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
            BurnWaitMode.Polling, pollingIntervalMs: 50, pollingTimeoutMs: 4000);

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
            BurnWaitMode.Polling, pollingIntervalMs: 50, pollingTimeoutMs: 4000);

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
            BurnWaitMode.Polling, pollingIntervalMs: 50, pollingTimeoutMs: 4000);

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
            BurnWaitMode.Polling, pollingIntervalMs: 200, pollingTimeoutMs: 300);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Failure, outcome.Kind);
        Assert.Contains("超时", outcome.Detail);
        Assert.True(port.Writes.Count(w => w.StartsWith("`C")) >= 2);   // 至少轮询 2 次后才超时
    }

    [Theory]
    [InlineData(10, 4000)]      // 间隔低于下限 50ms
    [InlineData(20000, 4000)]   // 间隔高于上限 10000ms
    [InlineData(200, 50)]       // 超时低于下限 100ms
    [InlineData(200, 700000)]   // 超时高于上限 600000ms
    public async Task Polling_InvalidParameters_Throw(int intervalMs, int timeoutMs)
    {
        var port = new ScriptedPollingChannel([]);
        var worker = new BurnWorker(() => port);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            worker.ExecuteAsync(NewRequest(), CancellationToken.None,
                BurnWaitMode.Polling, intervalMs, timeoutMs));
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
                BurnWaitMode.Polling, pollingIntervalMs: 200, pollingTimeoutMs: 4000));
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
            if (text.StartsWith("`C"))
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
