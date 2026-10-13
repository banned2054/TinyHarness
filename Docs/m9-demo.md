# M9 本机只读 MCP worker 验证记录

日期：2026-09-23

## 已交付

- `tinyharness mcp` 启动本机 stdio MCP 服务，只注册 `ask_glm`，逐行读写 UTF-8 JSON-RPC；诊断写标准错误。
- 每个 `tools/call` 调用使用独立 `WorkerRunner` run。MCP 只接受任务、已知事实、关注路径和期望输出；未知字段、重复字段及错误类型都会被拒绝。模型、endpoint、工作区、权限和预算均由宿主固定。
- worker 只注册 `list_files`、`search_text`、`read_file`；只读 Permission Engine 对 `filesystem.write`、`process.execute` 和未知 capability 直接硬拒绝。工作区、链接和敏感文件策略沿用 Worker 读取策略。
- Worker MCP host 只读取 TinyHarness 用户配置的默认 provider profile 和已选择模型。目标工作区 `tinyharness.json` 与其中的 `commandRules` 不参与 MCP 解析。工作区默认为服务进程当前目录，可在用户配置 `settings.worker.workspaceRoot` 固定。
- 用户配置 `settings.worker` 可设 run 时长、模型请求数、工具时长与次数、任务包字符数、工具输出字符数、单次与累计上下文 token、模型响应字符数。Core 再按稳定硬上限校验，MCP 请求无法覆盖这些设置。worker 结果文本仍受 16,000 字符 Core 上限约束。
- MCP `notifications/cancelled`、输入断开和宿主取消沿调用 token 传播到模型与文件工具；请求串行排队，不共享历史或权限状态。
- worker 返回带状态、结论、工作区相对证据与行号、建议、不确定项和执行统计的有界 JSON 文本结果。模型异常不会把异常消息带入 MCP 响应。

## 离线验证

- `dotnet test TinyHarness.Tests --no-restore --nologo`：**367/367 通过，0 失败，0 跳过**。
- MCP 定向覆盖包含完整 fake 闭环：Codex 风格 initialize 与 `tools/call` → fake Chat Completions → 列举、搜索、读取临时工作区 → 返回结构化结论和行号证据；验证文件未改变，完整工具输出历史未返回。
- 其他覆盖包括：错误参数、请求字段越权、未知工具、超长输入、worker 异常脱敏、取消通知、用户配置与项目配置隔离、焦点/敏感路径、请求状态隔离，以及每种执行预算的边界和未完成状态。
- `dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/m9-nativeaot`：成功生成原生程序，无 trimming/AOT warning。第一次不带 `--no-restore` 的发布被 sandbox 阻止读取用户级 `NuGet.Config`；当前包缓存和资产文件已含 `win-x64`，改为不还原后完成发布。
- 发布产物 `--help`、离线 `smoke --config tinyharness.json` 通过；smoke 完成 17 steps、16 tool executions。
- 使用临时 fake 用户配置和 `.invalid` endpoint，原生程序处理 MCP `initialize` 与 `tools/list`：返回两条协议消息、只列出 `ask_glm`，diagnostic 仅在 stderr，未发出模型请求。

## 真实服务验收

- 日期：2026-09-23。
- 模型：`glm-5.3`，来自默认用户 profile `glm`；endpoint 类型为 HTTPS。未记录凭据或完整 endpoint URL。
- 通过本机 `tinyharness` stdio MCP 服务发送真实 `tools/call`，任务只读取 `TinyHarness.Core/Services/Worker/WorkerRunner.cs` 第 696–704 行，要求说明单次/累计上下文 token 检查和超限行为。
- 首次要求模型读取整份文件时，worker 按工具输出预算返回 `Incomplete`；将范围缩至 9 行后返回 `Completed`，并给出准确路径和行号证据。
- 成功调用统计：3 次模型请求、2 次只读工具调用，耗时约 44 秒。worker 未提供可用的真实 token 用量或费用统计。
- 该结果验证了本次配置的 HTTPS endpoint 与 `glm-5.3` 可完成 TinyHarness MCP worker 调用；不代表其他 endpoint 或模型已验证。

## Codex 客户端验收

- 日期：2026-09-23。重启 Codex 后，当前会话加载并成功调用 `mcp__tiny_harness__ask_glm`；模型为默认 `glm` profile 的 `glm-5.3`，endpoint 类型为 HTTPS。
- 首次参数解析错误 `-32602 Invalid tools/call parameters` 后，MCP 参数解析器增加了对象形式 `_meta` 的兼容处理，同时继续严格校验工具名和任务参数。定向 MCP 测试 **21/21 通过**；修复版 `win-x64` NativeAOT 已发布到标准路径 `artifacts/m9-nativeaot`，原生 `--help` 和离线 `_meta`/预算拒绝冒烟通过。
- 用户明确批准后，Codex 发起的第一次真实只读调查因工具输出预算返回 `Incomplete`：3 次模型请求、1 次只读工具调用、568 个工具输出字符，耗时约 15.8 秒。
- 将任务缩至 `WorkerRunner.cs` 第 696–704 行后，Codex MCP 调用返回 `Completed`：2 次模型请求、1 次只读工具调用、628 个工具输出字符，耗时约 16.4 秒。结果准确说明先估算消息与工具定义 token，再检查单次和累计上限；超限时标记预算耗尽并抛出 `WorkerBudgetExceededException`，证据为该文件第 696–704 行。
- 两次真实 Codex 调用合计 5 次模型请求、2 次只读工具调用，耗时约 32.2 秒。worker 未返回可靠的真实 token 用量或费用数据。`glm-5.3` / 配置的 HTTPS endpoint 组合已通过直接 stdio 与 Codex 客户端两种路径验收；不代表其他模型或 endpoint 已验证。
