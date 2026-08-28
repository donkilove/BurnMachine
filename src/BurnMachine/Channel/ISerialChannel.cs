namespace BurnMachine.Channel;

/// <summary>
/// 串口通道抽象：供 SDK 执行器使用，使用方可注入自定义实现，测试用 MockSerialChannel 替代。
/// 行为语义对应 System.IO.Ports.SerialPort（UTF-8 文本读写，8N1）。
/// </summary>
public interface ISerialChannel : IDisposable
{
    bool IsOpen { get; }

    /// <summary>打开串口（设置 8N1 等参数见实现；失败抛异常）</summary>
    void Open(string portName, int baudRate);

    /// <summary>按 UTF-8 写入文本（不含换行）</summary>
    void Write(string text);

    /// <summary>读取当前可读的全部字节并解码（UTF-8，非法字节以 U+FFFD 替换——审计 BM-06 注释与实现一致）</summary>
    string ReadAvailable();

    /// <summary>清空接收缓冲区</summary>
    void ResetInputBuffer();

    void Close();
}
