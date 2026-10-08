# M10 统一模型协议接入验证记录

日期：2026-10-08

依据 `PLAN.md` 第 21 节，模型调用边界迁移到 `Microsoft.Extensions.AI.IChatClient`，底层使用官方 OpenAI SDK；Chat Completions 与 Responses 两种协议由配置显式选择。Anthropic 按第 21 节结论继续排除在外置依赖之外。

## 已交付

- `Services/ChatCompletions/ModelClientFactory` 成为模型客户端唯一创建入口：CLI `run`、`tinyharness mcp` host 与 `doctor --connect` 共用；`chat-completions`（缺省）构造 `OpenAI.Chat.ChatClient.AsIChatClient()`，`responses` 构造 `ResponsesClient.AsIChatClient(model)`。OPENAI001 实验性警告仅在两处构造点以最小 pragma 范围关闭，依据为第 21 节与 `artifacts/protocol-compat` 探针的 NativeAOT 验证。
- `Services/ChatCompletions/MicrosoftAiChatClient` 实现 `IChatCompletionClient`：内部请求映射为 M.E.AI 消息与 `ChatOptions`（工具为声明式 schema，不启用自动函数执行）；流方向映射文本、完整工具调用与 reasoning 事件，流末经 `ToChatResponse()` 提取 usage 与 finish reason。工具参数 JSON 全部经 `JsonNode` 组装，不依赖反射序列化。
- Responses 路径无服务端会话状态：每次请求经 `RawRepresentationFactory` 设 `store:false`，不使用 `previous_response_id`；reasoning 的 item id 与 encrypted content 以 `TextReasoningContent` 的 `AdditionalProperties["reasoningItemId"]` 与 `ProtectedData` 契约键在历史中回传（契约出处：`artifacts/protocol-compat/source/OpenAIResponsesChatClient.cs`）。
- 内部 DTO 扩展：`ChatMessage.Reasoning`（文本/ProtectedData/ItemId 三元组）贯通 Agent Loop 历史、压缩折叠区间、session 快照与审计脱敏；`ChatStreamEvent` 新增 reasoning/usage 事件与 FinishReason 字段；`StreamAccumulator` 按 itemId 分界累积 reasoning 条目。
- 配置链新增 `chatApi`（`chat-completions` | `responses`，大小写不敏感）：项目配置与用户配置 profile、`ConfigResolver`、`config show`/`provider list`/`doctor` 展示与 MCP host 解析全部贯通；旧配置缺省为 `chat-completions`，不按模型名猜测协议。
- 模型 usage 与结束原因端到端传递：每步真实 usage（如有）与 finish reason 以 `model_usage` 审计行落盘；`ConversationContext` 以最近一次"请求前估算 vs 实测输入 token"的钳位比值（0.5–4.0）校准后续视图估算——校准后的数字仍是估算，缺少真实 usage 时保持原估算行为。
- 旧 `OpenAiChatCompletionClient`（直接包装 SDK `ChatClient` 的实现）已删除，其契约测试由 `MicrosoftAiChatClientTests`（Chat Completions，9 项）与 `MicrosoftAiChatClientResponsesTests`（Responses，4 项）替代。

## 离线验证

- `dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj`：**408/408 通过，0 失败，0 跳过**（迁移前基线 368：删除旧客户端测试 7 项，新增适配器/累加器/持久化/配置/校准测试 47 项）。
- Chat Completions 契约测试基于本地模拟 SSE 服务（`MockSseServer`）驱动真实 SDK 传输，覆盖：多分片文本流、wire 分片参数拼装、一轮多工具调用、请求形状（路径/`stream:true`/tools schema/两轮 messages 与 tool_call_id）、usage、finish reason、HTTP 错误含状态码、中途取消、AgentLoop 两轮真实传输闭环。
- Responses 契约测试覆盖：reasoning delta + `output_item.done`（encrypted_content）+ 文本分片 + 两条 function call 分片参数 + usage 的流式拼装；第二轮请求 `store:false`、无 `previous_response_id`、恰一个 reasoning item（id 与 encrypted_content 原样回传）、两个 `function_call_output`、顶层 model 正确；AgentLoop 两轮闭环中 reasoning 在历史中存活并被重放；HTTP 错误路径。
- 配置兼容测试覆盖：显式 `responses` 生效、旧配置缺省 `chat-completions`、非法值报错（含键名与原值）、大小写与 camelCase 同义、用户配置 save/load 往返、MCP host 透传。
- 校准测试覆盖：比值生效与向上取整、0.5/4.0 钳位、非正数与 null 忽略、校准后压缩不变量仍成立。
- `dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained --force -o artifacts/m10-nativeaot`：exit 0，完整日志 **0 条 trimming/AOT/IL 警告**。
- 发布产物从仓库外部目录验证：`--help` exit 0；离线 `doctor` exit 0（正确显示 `chat api chat-completions`）；`smoke --config tinyharness.json` exit 0（17 steps、16 tool executions、Completed）。

## 验证边界

- 以上为离线验证：SDK 适配与本地执行路径的证据，不构成任何真实 endpoint 或模型的兼容结论。真实供应商调用按第 21 节另行授权；`README` tested 清单不因此变更。
- Responses 原生产物路径的协议级验证沿用 `artifacts/protocol-compat` 探针结论；本仓库 NativeAOT smoke 使用离线脚本客户端，覆盖的是入口、配置与执行链路。
- Responses 与 Chat Completions 的 FinishReason 字符串形态存在 SDK 内部常量与 wire 原值的差异，未强行归一；后续如做字符串比较需注意。
