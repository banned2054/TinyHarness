# Changelog / 更新日志

本文件记录 TinyHarness.NET 各版本的重要变化。每个版本包含内容对应的简体中文与英文说明。

All notable changes to TinyHarness.NET are documented here, with matching Simplified Chinese and English notes for each version.

## 📘 Versions / 版本

- [🚀 Release v0.0.1 — Initial Prototype / 首个原型](#release-v001)

<a id="release-v001"></a>

## 🚀 Release v0.0.1 — Initial Prototype / 首个原型

[简体中文](#简体中文-v001) | [English](#english-v001)

Release Date / 发布日期：Unreleased / 待发布

### 简体中文 v0.0.1

这是 TinyHarness.NET 的首个原型版本，汇集从基础执行循环到多轮会话的初始能力。基础任务已能用于实际尝试，仍在根据使用反馈修复细节，适合体验与反馈。

#### ✨ 新增

- 基于 .NET 10 的本地 CLI，支持单次任务 `run`、流式多轮对话 `chat` 和 Windows x64 NativeAOT 构建。
- 文件与进程工具：`list_files`、`search_text`、`read_file`、`apply_patch`、`shell`，支持顺序工具调用、命令超时、输出裁剪与取消清理。
- `Prepare → Authorize → Execute` 工具审批流程，以及工作区信任、只读访问、单次批准和本次运行内批准。
- 统一追加式会话日志，提供离线 `session list`／`session show` 和 `chat --session <id>` 续聊；中断后存在未知副作用时，写入和进程执行保持阻断，等待用户检查确认。
- Provider profile、模型与上下文窗口配置、环境变量或 Windows Credential Manager 密钥引用，以及离线配置检查；显式支持 Chat Completions 与 Responses 协议。
- 根目录与局部 `AGENTS.md` 加载、上下文用量统计和自动压缩，保留完整本地会话历史。
- 可选思考内容展示，以及安全边界上的补充输入、暂停、继续、取消和工作项查看；普通步数、累计 token 与总时长默认不限，独立模型请求和工具执行次数预算保留。
- 本机只读 MCP stdio 服务，向外部客户端提供 `ask_glm` worker。
- 可选 Windows 工具进程沙箱，直接编排 [banned2054/codex](https://github.com/banned2054/codex) fork 的组件，并提供 `sandbox status`／`provision`／`verify` 管理入口。

#### 📦 构建与分发

- 配置 GitHub Actions 构建 TinyHarness NativeAOT 与固定 commit 的 Codex fork 沙箱组件，整理 Windows x64 ZIP。
- 包内包含主程序、沙箱 setup／runner／managed-deny-probe、独立双语使用说明、沙箱配置示例、构建来源与文件哈希，以及 fork 的 LICENSE／NOTICE。
- 手动 workflow 运行上传构建 artifact；匹配项目版本的 `v*` tag 构建通过后创建 Draft + Prerelease，不自动公开。
- 提供中英文 README、使用说明和[打包文档](releasing.md)，原阶段计划统一并入[历史计划](../PAST_PLAN.md)。

#### 🧪 测试

- 默认测试使用模拟模型和本地夹具，不依赖真实模型服务或 API key；提供原生产物的离线工具链路 smoke。
- 打包流程包含版本与来源校验、ZIP SHA-256 校验和解压后的原生 smoke；这些检查不替代真实供应商或 Windows 隔离验收。

#### ⚠️ 已知限制

- 终端界面尚未完善，权限请求、AI 回复与用户输入缺乏清晰区分，输入编辑与运行中控制仍很简陋。
- 模型回答、摘要和工作项状态可能出错；本版不作稳定性或全面安全性保证，需核对实际文件变化和工具结果。
- 会话内容不加密且可能包含秘密；配置、命令、内置提示词和存储格式变化可能影响旧会话续聊。
- 目前以 Windows x64 为验证平台。沙箱需要单独配置组件路径并由用户明确初始化，只隔离其工具进程；其他平台及与 Codex 发布版共存仍未验证。

### English v0.0.1

This is the first TinyHarness.NET prototype, bringing together the initial execution loop and multi-turn session capabilities. Basic tasks can be tried in real work, with practical issues still being fixed through use and feedback. It is intended for experimentation and feedback.

#### ✨ Added

- A local .NET 10 CLI with one-shot `run`, streaming multi-turn `chat`, and Windows x64 NativeAOT builds.
- File and process tools: `list_files`, `search_text`, `read_file`, `apply_patch`, and `shell`, with sequential tool calls, command timeouts, bounded output, and cancellation cleanup.
- A `Prepare → Authorize → Execute` approval flow, workspace trust, read-only access, one-time approval, and approval scoped to the current run.
- A unified append-only session log with offline `session list` / `session show` and resume through `chat --session <id>`; uncertain side effects after interruption keep writes and process execution blocked until the user inspects and acknowledges the state.
- Provider profiles, model and context-window configuration, environment-variable or Windows Credential Manager key references, and offline configuration checks; explicit Chat Completions and Responses support.
- Root and scoped `AGENTS.md` loading, context usage accounting, and automatic compaction while retaining full local session history.
- Optional readable reasoning display, plus safe-boundary amendments, pause, resume, cancellation, and work-item inspection; ordinary steps, total tokens, and wall time are unlimited by default, while separate model-request and tool-execution budgets remain active.
- A local read-only MCP stdio server exposing the `ask_glm` worker to external clients.
- An optional Windows tool-process sandbox directly orchestrating components from the [banned2054/codex](https://github.com/banned2054/codex) fork, with `sandbox status` / `provision` / `verify` management commands.

#### 📦 Build and Distribution

- A GitHub Actions workflow configured to build TinyHarness NativeAOT and the sandbox components from a pinned Codex-fork commit, then assemble a Windows x64 ZIP.
- A package containing the main executable, sandbox setup/runner/managed-deny-probe, standalone bilingual instructions, a sandbox configuration example, build provenance and file hashes, and the fork's LICENSE/NOTICE.
- Manual workflow runs upload build artifacts; successful builds for `v*` tags matching the project version create a Draft + Prerelease without automatically publishing it.
- English and Chinese READMEs, usage instructions, and [packaging documentation](releasing.md); the previous milestone plans are consolidated in the [historical plan archive](../PAST_PLAN.md).

#### 🧪 Tests

- Default tests use simulated models and local fixtures without a real model service or API key; the native executable provides an offline tool-flow smoke check.
- The packaging flow includes version and source validation, ZIP SHA-256 verification, and a native smoke check after extraction; these checks do not replace real-provider or Windows-isolation acceptance.

#### ⚠️ Known Limitations

- The terminal interface is unfinished: permission requests, AI replies, and user input are not clearly separated, and input editing and runtime controls remain rudimentary.
- Model answers, summaries, and work-item statuses can be wrong; this version provides no stability or comprehensive security guarantee. Check actual file changes and tool results.
- Sessions are not encrypted and may contain secrets; changes to configuration, commands, the built-in prompt, or storage formats may affect resume compatibility.
- Windows x64 is the current validation platform. The sandbox requires separately configured component paths and explicit user provisioning, and isolates only its tool processes. Other platforms and coexistence with a Codex release installation remain unverified.
