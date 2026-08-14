# BurnMachine

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

基于 .NET 8 的 XW16Pro 烧录机串口通信库：指令构造、响应解析与单点烧录执行时序（9600 8N1）。

本库从 [BurnMachineHost](https://github.com/donkilove/BurnMachineHost) 机台烧录上位机中提取并发布为可复用组件，让多个上位机应用可以共享一套经过充分验证的烧录机通信栈，而无需重复维护源代码。

## 功能特性

- **指令构造** —— 清空（`` `F ``）/ 烧录（`` `P ``，支持通道掩码与可选条码）/ 查询（`` `C ``）/ UID 扩展查询（`` `U ``）指令按协议规格生成
- **响应解析** —— 五段帧校验、回显序列号校验（防串扰/粘包错位）、结果码精确匹配（0=成功）、帧长上限 256
- **结构化结果** —— `ParseResponseDetailed` 解析镜像号/主校验和/剩余次数/结果码（0/1/2/3）；`ParseUidResponse` 解析 UID 数据区（需固件 > 20240103000000）
- **通道选择** —— `ChannelMask`（A / B / 双通道），清空/烧录/查询全程按所选通道发送
- **条码写入** —— 烧录时可选携带条码字节（ASCII hex），大小端由镜像配置决定
- **单点烧录执行器 `BurnWorker`** —— 清空→烧录→查询完整时序，整轮最多尝试 2 次（失败自动重试 1 次，间隔 1 秒），协作式取消；`QueryUidAsync` 独立 UID 查询
- **粘包/半包防护** —— 累积缓冲 + 换行帧切分，粘包只取第一帧，半包累积到帧边界
- **可注入串口通道** —— `ISerialChannel` 抽象 + `SerialPortChannel`（System.IO.Ports）真实实现 + `MockSerialChannel` 可编程模拟（离线开发/测试）

## 安装

```bash
dotnet add package BurnMachine --version 0.2.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证，请配置具有 `read:packages` 权限的令牌。

## 快速开始

```csharp
using BurnMachine;
using BurnMachine.Channel;

var worker = new BurnWorker(() => new SerialPortChannel(), Console.WriteLine);
var outcome = await worker.ExecuteAsync(
    new BurnRequest("COM3", "00881289", "0765", burnTimeSeconds: 3)
    {
        Channels = ChannelMask.B,          // 可选：B 通道（默认 A）
        Barcode = [0x30, 0x31, 0x32],      // 可选：烧录时写入条码
    },
    CancellationToken.None);

Console.WriteLine(outcome.Success ? "烧录成功" : $"烧录失败: {outcome.Detail}");
```

查询烧录结果详情（镜像号/校验和/剩余次数）：

```csharp
var result = BurnProtocol.ParseResponseDetailed(response, "00881289");
Console.WriteLine($"镜像号: {result.ImageNo}, 剩余次数: {result.RemainingCount}, 状态: {result.Status}");
```

UID 扩展查询（需固件 > 20240103000000）：

```csharp
var uidResult = await worker.QueryUidAsync("COM3", "00881289", ct: CancellationToken.None);
Console.WriteLine($"UID: {Convert.ToHexString(uidResult.Uid ?? [])}");
```

无硬件环境可用 `MockSerialChannel` 离线开发：

```csharp
var mock = new MockSerialChannel();
mock.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
var worker = new BurnWorker(() => mock);
```

## 协议摘要

| 指令 | 格式 | 说明 |
|---|---|---|
| 清空 | `` `F{burnId}\|{掩码8hex}\r\n `` | 清空烧录机指定通道 |
| 烧录 | `` `P{burnId}\|{掩码8hex}\|{program}[<nowiki>|</nowiki>{条码hex}]\r\n `` | 开始烧录（条码可选） |
| 查询 | `` `C{burnId}{掩码8hex}\r\n `` | 查询结果（注意无 `\|` 分隔） |
| UID 查询 | `` `U{burnId}{掩码8hex}\r\n `` | UID 扩展查询（固件 > 20240103000000） |

完整协议规格见 [XW16Pro 扩展串口控制协议](../../Burn/docs/XW16Pro扩展串口控制协议.md)。

## 构建与测试

```bash
dotnet build BurnMachine.sln
dotnet test BurnMachine.sln
```

## 项目结构

```
BurnMachine.sln
src/BurnMachine/            类库（net8.0，NuGet 包 BurnMachine）
├── BurnProtocol.cs         指令构造与响应解析（BurnResultKind / BurnResult / UidQueryResult）
├── BurnRequest.cs          单点烧录请求（通道/条码可选）
├── BurnOutcome.cs          单点烧录结果
├── BurnWorker.cs           清空→烧录→查询执行时序 + UID 查询
├── ChannelMask.cs          通道掩码与结果码枚举（v0.2.0）
├── BurnResult.cs           结构化烧录结果与 UID 查询结果（v0.2.0）
└── Channel/                串口通道
    ├── ISerialChannel.cs   通道抽象（可注入自定义实现）
    ├── SerialPortChannel.cs System.IO.Ports 实现
    └── MockSerialChannel.cs 可编程模拟通道
tests/BurnMachine.Tests/     协议 + 执行器测试（97 个，含 v0.2.0 扩展用例）
```

## 许可协议

[MIT](LICENSE)
