using System.IO;
using System.Text;

namespace BurnMachine.Channel;

/// <summary>
/// 可编程模拟串口通道：模拟真实设备时序——响应在收到查询指令后才进入可读缓冲；
/// 记录写入、可注入打开失败，便于离线开发与自动化测试（对照 ScpiInstrument 的 MockInstrumentChannel）。
/// </summary>
public sealed class MockSerialChannel : ISerialChannel
{
    private readonly StringBuilder _buffer = new();   // 驱动缓冲（已到达字节）
    private readonly Queue<string> _pending = new();  // 设备待发送响应（查询指令到达后才释放）
    private readonly List<string> _writes = new();
    private bool _queryReceived;

    /// <summary>已写入的全部文本（按写入顺序）</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>设置后 Open 将抛出 IOException（模拟占用/拒绝访问）</summary>
    public string? OpenError { get; set; }

    /// <summary>设置后前 N 次 Open 抛 IOException，之后成功（模拟暂时占用后恢复）</summary>
    public int OpenFailuresRemaining { get; set; }

    /// <summary>ResetInputBuffer 被调用的次数（审核修复：验证残留缓冲清除时机）</summary>
    public int ResetInputBufferCalls { get; private set; }

    public bool IsOpen { get; private set; }

    /// <summary>入队一条设备响应（查询指令写入后、下一次读取时才进入可读缓冲，模拟真实设备行为）</summary>
    public void EnqueueResponse(string response) => _pending.Enqueue(response);

    /// <summary>直接向驱动缓冲注入字节（模拟切帧后设备才发出的残留帧，供残留清除场景测试）</summary>
    public void InjectDriverBytes(string bytes) => _buffer.Append(bytes);

    public void Open(string portName, int baudRate)
    {
        if (OpenFailuresRemaining > 0)
        {
            OpenFailuresRemaining--;
            throw new IOException(OpenError ?? "拒绝访问");
        }

        if (OpenError is not null)
        {
            throw new IOException(OpenError);
        }

        IsOpen = true;
    }

    public void Write(string text)
    {
        _writes.Add(text);
        if (text.StartsWith("`C") || text.StartsWith("`U"))   // 查询指令（含 UID 扩展查询）：设备将响应
        {
            _queryReceived = true;
        }
    }

    public string ReadAvailable()
    {
        if (_queryReceived)
        {
            _queryReceived = false;
            while (_pending.Count > 0)
            {
                _buffer.Append(_pending.Dequeue());
            }
        }

        if (_buffer.Length == 0)
        {
            return "";
        }

        var all = _buffer.ToString();
        _buffer.Clear();
        return all;
    }

    /// <summary>清空驱动缓冲（模拟 DiscardInBuffer），并记录调用次数</summary>
    public void ResetInputBuffer()
    {
        ResetInputBufferCalls++;
        _buffer.Clear();
    }

    public void Close() => IsOpen = false;

    public void Dispose() => Close();   // 与 SerialPortChannel 语义对齐：释放即关闭
}
