# 轮询模式 U 命令支持（PollingQueryKind 开关 + BurnOutcome.Uid）

> 状态：规格（关键前提已真机验证，2026-08-17，COM9 / 00911008 / A 通道 / 镜像 0000）

## 目标

轮询模式支持用 U 命令替代 C 命令轮询（开关切换）：烧录完成时响应帧已带 UID 数据，`BurnOutcome` 新增可选 `Uid` 字段带出，无需完成后再补一次查询。默认仍为 C 命令，行为与 v0.4.1 完全一致。

## 实测依据（真机，COM9 / 00911008 / 镜像 0000）

**烧录中 U 轮询验证**（F→P 启动后循环发 U，250ms 读窗口）：

```
[U#01~#09] Status=Cleared(2)  照常应答，烧录正常进行（不干扰烧录，与 C 行为一致）
[U#10]     Status=Success(0)  UID=343435500B0039364F0048001F000000（16 字节，真实数据）
```

- 烧录中 U 查询不干扰烧录 ✓（与 burn-polling.md 中 C 的实测结论一致）
- 完成后 U 响应携带真实 UID ✓
- **设备怪癖**：烧录中部分轮 UID 区长度前缀为 `00`（TryParseUidZone 解析为 `Uid=null`）；完成轮实测前缀 `10`。完成轮理论上也可能赶上 `00` 前缀 → 该轮 `Uid=null`（不重查、不抛异常；`QueryUidAsync` 已于 v0.6.0 移除，无重查路径，调用方需接受 `Uid=null` 或人工干预）。
- U 响应帧更长（~96 字符，完整帧最迟 202ms < 250ms 读窗口，余量已由 timing-tighten 规格预留）。

## 范围与非目标

- `src/BurnMachine`：
  - 新增枚举 `PollingQueryKind`（`C = 0` 默认 / `U = 1`），与 `BurnWaitMode` 同文件放置。
  - `ExecuteAsync` 新增可选参数 `PollingQueryKind pollingQuery = PollingQueryKind.C`（既有调用零破坏）。
  - `WaitForBurnCompletionAsync` 按 `pollingQuery` 发 C 或 U：U 轮用 `BurnProtocol.ParseUidResponse` 取 `Base.Status` 判定进度、终止轮带出 `Uid`。
  - `BurnOutcome` 新增 `IReadOnlyList<byte>? Uid { get; init; }`（构造器不变，向后兼容）。
- 测试与文档：`BurnWorkerPollingTests` 扩展 U 轮询用例；README 轮询章节补充说明。
- **非目标**：不改 `ParseQueryStatus`（C 路径原样）、不动轮询 C 的默认行为、不改 `BurnProtocol` 解析语义。（注：`BurnWaitMode`/固定模式/`QueryUidAsync` 已随 v0.6.0 退役，历史快照见本文档下方接口图。）

## 验收标准

- [x] 默认（`pollingQuery = C`）：行为与 v0.4.1 完全一致，既有轮询用例全绿。
- [x] `pollingQuery = U`：指令序 `F → P → U/U/...`；`Status` 0/1 终止，2/3 与无响应/无效帧继续轮询，严格超时语义不变（读窗口 ≤ 剩余时间）。
- [x] U 轮询成功/失败终止时：`outcome.Uid` 携带响应帧 UID（`Uid=null` 场景不抛异常）。
- [x] C 轮询与固定模式下 `outcome.Uid == null`。
- [x] 参数越界/取消/重试行为与 C 轮询一致（无新增校验点）。
- [x] 全量单测通过（125 个，含 6 个新增 U 轮询用例）；真机 U 轮询烧录成功（`outcome.Uid` 非空：`343435500B0039364F0048001F000000`）。

## 数据流与接口变化

```
接口：ExecuteAsync(request, ct,
        waitMode = BurnWaitMode.Fixed,
        pollingIntervalMs = 100,
        pollingTimeoutMs = 3500,
        pollingQuery = PollingQueryKind.C)     // v0.5.0 新增

WaitForBurnCompletionAsync：
  C 轮：BuildQueryCommand      + ParseQueryStatus  （现状不变）
  U 轮：BuildUidQueryCommand   + ParseUidResponse  → status = r.Base.Status；uid = r.Uid
  终止轮：return new BurnOutcome(...) { Uid = uid };

BurnOutcome：record BurnOutcome(bool Success, BurnResultKind Kind, string Detail)
             { public IReadOnlyList<byte>? Uid { get; init; } }
```

轮询循环其余逻辑（250ms 读窗口、20ms 粒度、间隔、严格超时、取消）C/U 共用，不分支。

## 边界情况与失败处理

| 情况 | 行为 |
|---|---|
| U 无响应 / 帧无效 | 继续轮询（与 C 轮一致）；总超时判失败（Kind=Timeout，审计 BM-02） |
| 旧固件（≤20240103000000）不支持 U | 全程无响应 → 超时失败（Kind=Timeout）；文档注明 `pollingQuery=U` 需新固件 |
| 完成轮 UID 长度前缀 00 / UID 区畸形 | `outcome.Uid = null`，不抛异常、不重查 |
| 完成轮缺 UID 区（6 段有效帧） | **保留烧录判定（成功/失败原样），仅 `Uid=null`**（审计 BM-01：不再降级 FormatError 误判超时 NG） |
| 结果码 2/3 | 继续轮询（Status 判定，与 C 一致） |
| 取消 / 串口异常 | 与 C 轮询一致（取消抛异常、异常走整轮重试） |

## 测试方案

`tests/BurnMachine.Tests/BurnWorkerPollingTests.cs` 扩展（`ScriptedPollingChannel` 注入 U 响应序列）：

- U 轮询：2→2→0（带 UID 区）→ 成功，指令序 `F/P/U/U/U`，`outcome.Uid` 断言非空且字节正确。
- U 轮询失败：2→1（带 UID 区）→ 失败且 `outcome.Uid` 仍有值。
- U 轮询完成轮 UID 长度 00 → `outcome.Uid == null`。
- U 轮询首轮无响应 → 继续轮询至成功。
- U 轮询超时 → 失败（"烧录超时"）。
- 默认参数 C 路径：既有用例全绿（无需新增）。

真机验证（实现后）：`out/probe` 工具扩展 SDK U 轮询烧录 1 次，断言 `outcome.Uid` 非空。

## 假设与风险

- 假设烧录中 U 查询不干扰烧录（已实测 ✓，9 轮烧录中查询烧录正常完成）。
- U 轮每轮耗时更长（完整帧 ~202ms + 间隔 100ms ≈ 300ms/轮 vs C 的 ~230ms/轮）：默认超时 3500ms 实测覆盖（烧录 ~2.8s，完成轮 ~2.8s 时命中），但余量比 C 轮小 ~250ms；U 轮询场景建议按需调大 `pollingTimeoutMs`（README 注明）。
- 风险低：纯新增枚举/可选参数/init 属性，无破坏性变更。
