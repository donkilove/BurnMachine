# 烧录轮询等待模式（BurnWaitMode.Polling）

> 状态：**已退役的历史规格（审计 BM 系列标注）**——v0.6.0 起 `BurnWaitMode` 已移除，
> 轮询为唯一等待方式（默认间隔 30ms、超时 3500ms，见 `BurnWorker` 常量与
> `docs/specs/timing-tighten.md`）；本文档保留作历史参考，其中 Fixed 模式、
> `BurnWaitMode` 枚举、interval 默认 100ms 等表述均已过时。
> 另有下列数值/顺序亦已过时：读窗口 **200ms**（现 250ms）、间隔范围 **50~10000**（现 30~10000）、
> 轮询写序 **"写 C → ResetInputBuffer"**（现为先清后写，审计 BM-05）。

## 目标

`BurnWorker.ExecuteAsync` 支持不固定时间等待的烧录方式：发送 `P` 烧录指令后，按固定间隔轮询 `C` 查询，结果码 `0`（成功）/`1`（失败）出现即判定烧录结束，无需预先估算烧录耗时；烧录失败能第一时间发现，烧录提前完成也无需干等。

## 背景与实测依据

XW16Pro 无"烧录完成主动上报"机制，只有 `C` 查询的 request-response 应答。实现前用真实硬件实测了"烧录进行中"的查询行为（200ms 间隔轮询，4s 窗口）：

```
[F之后]   C查询 => code='2'（已清空）
#1~#7    code='2'（烧录中，机器正常应答查询，不干扰烧录）
#8       code='0'（烧录完成，剩余次数 95765→95764）
```

结论：烧录期间 `C` 查询照常响应且返回结果码 `2`；烧录完成变 `0`/`1`；轮询不干扰烧录过程。

## 范围与非目标

- 只改 `src/BurnMachine`（`BurnWorker`、新增 `BurnWaitMode`、`BurnProtocol.ParseQueryStatus`）与测试。
- 不新增独立方法；`ExecuteAsync` 原签名调用方零破坏（新增可选参数）。
- 不动 `BurnProtocol.ParseResponse` 语义（结果码非 0 一律 `Failure` 的旧行为保留，`ParseQueryStatus` 单独提供原始结果码）。
- 不动 `SerialPortChannel` / `MockSerialChannel` / `BurnRequest` / `QueryUidAsync`。

## 验收标准

- [x] 轮询模式：`F` → `P` → 按 interval 轮询 `C`，结果码 `0`/`1` 立即终止。
- [x] 结果码 `2`/`3` 与无响应/无效帧：视为仍在烧录，继续轮询（不判失败）。
- [x] 超过 `pollingTimeoutMs` 仍未出结果：返回 `BurnOutcome(false, Failure, "烧录超时：…")`。
- [x] 默认（`BurnWaitMode.Fixed`）行为与 v0.2.0 完全一致，既有测试全绿。
- [x] 参数校验：interval 50~10000ms、timeout 100~600000ms，越界抛 `ArgumentOutOfRangeException`（在打开串口/重试循环之前抛出）。
- [x] 取消：轮询等待期间 `ct` 取消抛 `OperationCanceledException`。
- [x] 全量回归 109 测试通过；真实硬件轮询烧录成功（剩余次数正常消耗）。
- [x] 默认值 100ms/3500ms 与读窗口/读取粒度（200ms/20ms）经真实硬件验证。

## 数据流与接口变化

```
接口：ExecuteAsync(request, ct,
        waitMode = BurnWaitMode.Fixed,
        pollingIntervalMs = 100,      // 50~10000，默认 100
        pollingTimeoutMs = 3500)      // 100~600000，默认 3500
```

- 新增 `BurnWaitMode` 枚举（`Fixed`=0 默认 / `Polling`=1）。
- 新增 `BurnProtocol.ParseQueryStatus(response, burnId)` → `BurnStatus?`（`0/1/2/3`；帧无效返回 `null`），复用既有 `ParseCore` 帧校验。
- 轮询循环：写 `C` → `ResetInputBuffer` → 读 200ms 窗口（`PollingReadWindowMs`，20ms 读取粒度 `PollingReadPollMs`，复用 `ReadResponseAsync` 的窗口/粒度参数化；固定模式仍为 1s 窗口/100ms 粒度不受影响）→ 解析结果码 → 终止或 `Task.Delay(interval)`。
- 轮询模式下 `request.BurnTimeSeconds` 被忽略（由 `pollingTimeoutMs` 取代）。
- 轮询超时按正常结果返回（非异常），不触发整轮重试（与固定模式"查询结果失败不重试"一致）。

## 边界情况与失败处理

| 情况 | 行为 |
|---|---|
| 结果码 0 | 成功，立即返回 |
| 结果码 1 | 失败，立即返回 |
| 结果码 2/3 | 继续轮询 |
| 无响应 / 帧无效（回显不匹配等） | 继续轮询 |
| 总超时 | 失败 + "烧录超时：{ms}ms 内未查询到完成结果" |
| 轮询期间取消 | 抛 `OperationCanceledException`（与固定模式一致） |
| 串口打开失败 | 沿用整轮重试（最多 2 次） |

## 测试方案

`tests/BurnMachine.Tests/BurnWorkerPollingTests.cs`（10 个用例，含 `ScriptedPollingChannel` 逐次应答模拟）：

- 烧录中×2 → 成功：3 次查询、指令序 F/P/C/C/C
- 结果码 1 → 失败且不再轮询
- 结果码 3 → 继续轮询
- 首轮无响应 → 继续轮询
- 超时（300ms）→ 失败 + "超时"，至少轮询 2 次
- 参数越界（4 组）→ 抛异常
- 轮询中取消 → 抛异常

## 假设与风险

- 假设设备在烧录期间始终应答 `C` 查询（实测成立；若某型号烧录中静默，表现为"无响应继续轮询"，仅影响超时判定时机，不误报失败）。
- 频繁查询对设备无副作用（实测 12 次查询烧录结果正常）。
- 剩余次数有限镜像每次烧录消耗 1 次（95765→95761，实测确认真实写入）。
- **读窗口/粒度实测依据**（9600 波特，COM3 / 00911008）：首字节 83~99ms、完整帧 116~134ms（设备处理 ~80ms + 帧传输 ~54ms）。读窗口 200ms 留 ~50% 余量；读取粒度 20ms 消除"白等一拍"（100ms 粒度下帧 134ms 到齐却要等 200ms 拍），轮询周期从波动 214~333ms 收窄为稳定 218~247ms。周期下限 ~230ms 由设备响应延迟 + 波特率决定（读帧 ≥134ms + 间隔 100ms），粒度再细（<20ms）无额外收益。
- 默认超时 3500ms 覆盖实测烧录时长（~2.3~2.5s）并留 ~1s 余量；换更大镜像需调大。
