# BurnMachine

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

基于 .NET 8 的 XW16Pro 烧录机串口通信库：指令构造、响应解析与单点烧录执行时序（9600 8N1）。

本库从 [BurnMachineHost](https://github.com/donkilove/BurnMachineHost) 机台烧录上位机中提取并发布为可复用组件，让多个上位机应用可以共享一套经过充分验证的烧录机通信栈，而无需重复维护源代码。

## 功能特性

- **指令构造** —— 清空（`` `F ``）/ 烧录（`` `P ``）/ 查询（`` `C ``）指令按协议规格生成
- **响应解析** —— 五段帧校验、回显序列号校验（防串扰/粘包错位）、结果码精确匹配（0=成功）、帧长上限 256
- **单点烧录执行器 `BurnWorker`** —— 清空→烧录→查询完整时序，打开失败自动重试 2 次，协作式取消
- **粘包/半包防护** —— 累积缓冲 + 换行帧切分，粘包只取第一帧，半包累积到帧边界
- **可注入串口通道** —— `ISerialChannel` 抽象 + `SerialPortChannel`（System.IO.Ports）真实实现 + `MockSerialChannel` 可编程模拟（离线开发/测试）

## 安装

```bash
dotnet add package BurnMachine --version 0.1.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证，请配置具有 `read:packages` 权限的令牌。

## 快速开始

```csharp
using BurnMachine;
using BurnMachine.Channel;

var worker = new BurnWorker(() => new SerialPortChannel(), Console.WriteLine);
var outcome = await worker.ExecuteAsync(
    new BurnRequest("COM3", "00881289", "0765", burnTimeSeconds: 3),
    CancellationToken.None);

Console.WriteLine(outcome.Success ? "烧录成功" : $"烧录失败: {outcome.Detail}");
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
| 清空 | `` `F{burnId}\|00000001\r\n `` | 清空烧录机 |
| 烧录 | `` `P{burnId}\|00000001\|{program}\r\n `` | 开始烧录 |
| 查询 | `` `C{burnId}00000001\r\n `` | 查询结果（注意无 `\|` 分隔） |

完整协议规格见 BurnMachineHost 仓库 `docs/串口协议规格.md` §2。

## 构建与测试

```bash
dotnet build BurnMachine.sln
dotnet test BurnMachine.sln
```

## 项目结构

```
BurnMachine.sln
src/BurnMachine/            类库（net8.0，NuGet 包 BurnMachine）
├── BurnProtocol.cs         指令构造与响应解析（BurnResultKind）
├── BurnRequest.cs          单点烧录请求
├── BurnOutcome.cs          单点烧录结果
├── BurnWorker.cs           清空→烧录→查询执行时序
└── Channel/                串口通道
    ├── ISerialChannel.cs   通道抽象（可注入自定义实现）
    ├── SerialPortChannel.cs System.IO.Ports 实现
    └── MockSerialChannel.cs 可编程模拟通道
tests/BurnMachine.Tests/     协议 + 执行器测试（23 个）
```

## 许可协议

[MIT](LICENSE)
