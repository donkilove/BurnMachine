using System.IO;

namespace BurnMachine.Channel;

/// <summary>
/// 可编程模拟串口通道：按队列回放响应、记录写入、可注入打开失败，
/// 便于离线开发与自动化测试（对照 ScpiInstrument 的 MockInstrumentChannel）。
/// </summary>
public sealed class MockSerialChannel : ISerialChannel
{
    private readonly Queue<string> _responses = new();
    private readonly List<string> _writes = new();

    /// <summary>已写入的全部文本（按写入顺序）</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>设置后 Open 将抛出 IOException（模拟占用/拒绝访问）</summary>
    public string? OpenError { get; set; }

    public bool IsOpen { get; private set; }

    /// <summary>入队一条响应；ReadAvailable 按 FIFO 回放，队列空时返回空串</summary>
    public void EnqueueResponse(string response) => _responses.Enqueue(response);

    public void Open(string portName, int baudRate)
    {
        if (OpenError is not null)
        {
            throw new IOException(OpenError);
        }

        IsOpen = true;
    }

    public void Write(string text) => _writes.Add(text);

    public string ReadAvailable() => _responses.Count > 0 ? _responses.Dequeue() : "";

    public void ResetInputBuffer()
    {
    }

    public void Close() => IsOpen = false;

    public void Dispose()
    {
    }
}
