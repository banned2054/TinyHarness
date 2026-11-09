# Changelog / 更新日志

本文件记录 TinyHarness.NET 各版本的重要变化。每个版本包含内容对应的简体中文与英文说明。

All notable changes to TinyHarness.NET are documented here, with matching Simplified Chinese and English notes for each version.

## 📘 Versions / 版本

- [🚧 Unreleased / 未发布](#unreleased)
- [🚀 Release v0.0.1 — Initial Prototype / 首个原型](#release-v001)

<a id="unreleased"></a>

## 🚧 Unreleased / 未发布

### 简体中文

#### ✨ 新增

- 三级执行体系（Windows 沙箱基线 / 沙箱扩展 / 显式宿主执行）：普通读取不限工作区、工作区写入与沙箱 Shell 自动执行；越界写入、已知敏感位置（读与写都 L2）、受保护元数据写入、联网申请与 shell 写根申请按 Level 2 逐次审批；`executionScope=host` 为显式单次批准的宿主执行，CLI 生产路径与 Desktop 注册同一 Shell 路由形态。
- 常设信任显式迁移：`networkPolicy=enabled` 与附加写根不再静默扩大授权；检测到即进入未确认态（联网与附加写根写逐次审批、OS 基线不注入），经 CLI chat 启动确认或 Desktop 设置页常设授权区块确认后作为显式持久授权运行，撤销入口随时可用；确认展示如实列出网络范围、附加读/写根与敏感位置提示。
- OS 层基线广泛读与读保护：沙箱进程 OS 读边界=用户 profile 根；C1 宿主密钥路径（trust.json/用户配置/会话存储/trust 存储）、`.codex` 授权内容、`cap_sid` 与 C2 敏感目录段经 deny-read 集随每命令同步施加、可回收（deny 集=目录段；文件级敏感由应用层保护）。
- shell 写根申请参数 `writeRoots`（≤8 个绝对路径，空格/引号/盘根拒绝）：批准后写根冻结进授权与策略身份并经 grant 通道在沙箱内生效；仅允许单次与 Turn 授权。
- 授权治理：授权生命周期与可信授权变更以必需记录 `permission-grant-event/1` 写入会话日志（旧程序拒读）；Reviewer 授权上下文补目标摘要、约束、有效期与策略修订；审批展示事实行（后端与隔离声明、文件与网络权限、有效期、活动授权摘要）在 CLI 与 Desktop 同源一致；拒绝记忆仅在可信授权变更点解除。
- 未信任工作区入口恢复：`run` 在未信任工作区保持可用（模型与只读文件工具可用、进程执行 fail-closed 拒绝），stderr 给出 /trust 指引；沙箱组合的基线读保护 pairing 校验不变。
- C2 获批读授权通道（沙箱 payload 协议 v7）：获批的敏感位置读取由不在 deny 组内的专用授权主体在单次执行窗口内完成（`authorized_read_paths` 载荷；不含授权读即普通执行，普通执行行为与旧版完全一致），获批读路径不再并入 read_roots 组通道；执行窗口跨进程串行，授权执行身份与普通执行隔离。三道前置门：helper 能力未声明 v7 即拒绝；恢复失败或进程回收状态未知即禁用授权主体并持久化；helper 侧以沙箱组不可篡改的保护清单核对 C1 保护面（缺失/解析失败/与授权读重叠即拒绝）。授权执行需沙箱组件声明 v7 能力，未声明的环境下入口拒绝（fail-closed），普通沙箱行为不变。

#### 🛠 修复

- 文件工具 grant 缺策略身份导致授权事件记录被拒的断路（文件工具计划现携带稳定策略身份）。
- CLI 守卫包装丢失网络申请标志导致的审批旁路（守卫路径网络申请恢复逐次审批）。
- 常设网络免审批收紧为双条件：需常设网络策略 `enabled` 且常设信任已确认同时成立；`restricted` 下仅确认附加写根不再连带免网络审批（PermissionEngine 新增 standingNetworkEnabled 独立门）。
- C1 保护覆盖双向重叠：申请根覆盖 C1 保护对象（如用户配置、信任文件、会话存储的父目录）同样硬拒；检查覆盖全部四个写根来源（单次申请、常设附加写根、活动 Grant、用户配置运行时根），按组合实例实际路径比对。
- 撤销常设授权即失效会话：发送入口与每次工具派发前核对组合指纹与当前可信配置（CLI 与 Desktop 同机制、两端重读配置，读取/解析失败同样失效）；失配即撤销全部授权并失效会话，提示重新打开；已启动命令沿用既有取消回收，不自动重组。
- 权限系统保护根细化：整沙箱 home 收缩为四个敏感子路径（`.sandbox-secrets`、`.sandbox`、`cap_sid`、`.sandbox-bin`）；默认 tmp/tools/nuget-cache 运行时根恢复运行时根分类与 L1 自动执行；OS ACL 与组合完整性校验仍覆盖整 home。

#### 🧪 测试

- 真实沙箱验收测试改为环境变量门禁：默认 `dotnet test` 不触机器沙箱、固定用户目录与外网；探针根必须经 `TINYHARNESS_REAL_SANDBOX_PROBE` 显式指定（无默认值，避免 %TEMP% 历史 ACL 污染），负向隔离探针先验探针根 ACL 前提（不满足记为 Inconclusive 不计通过），代理经 `TINYHARNESS_REAL_SANDBOX_PROXY` 参数化。

### English

#### ✨ Added

- Three-level execution (Windows sandbox baseline / sandbox extension / explicit host execution): ordinary reads beyond the workspace, workspace writes, and the sandboxed shell run automatically; writes outside the workspace, known sensitive locations (reads and writes at L2), protected-metadata writes, networking requests, and shell write-root requests ask per call at Level 2; `executionScope=host` is explicit single-approval host execution, with CLI production registering the same shell routing shape as Desktop.
- Explicit standing-trust migration: `networkPolicy=enabled` and additional write roots no longer expand authority silently; detection enters an unconfirmed state (per-call approvals for networking and write-root writes, OS baseline not injected) until confirmed via the CLI chat startup prompt or the Desktop standing-authorization block, after which they run as explicit persistent authorization with a revoke entry. The confirmation display lists the network scope, additional read/write roots, and the sensitive-location hint.
- OS-level broad read baseline and read protection: the sandbox process OS read boundary is the user profile root; C1 host secret paths (trust.json/user config/session store/trust store), `.codex` authorization content, `cap_sid`, and C2 sensitive directory segments ship in a deny-read set applied per command and reclaimable (the deny set is directory segments; file-level sensitive names stay protected by the application layer).
- The shell write-root request parameter `writeRoots` (≤8 absolute paths; spaces, quotes, and drive roots refused): approved roots freeze into the grant and policy identity and take effect inside the sandbox through the grant channel; only single-operation and turn grants are offered.
- Authorization governance: grant lifecycles and trusted authorization changes persist as required `permission-grant-event/1` session-log records (older programs refuse them); the reviewer context gains target summaries, constraint, expiry, and policy revision; approval prompts render fact lines (backend and isolation declaration, file and network permissions, validity, active grant summary) from one shared source across CLI and Desktop; denial memory clears only at trusted authorization changes.
- Untrusted-workspace entry restored: `run` stays usable without trust (model and read-only file tools work; process execution fails closed) with a stderr `/trust` hint; the composed sandbox keeps its baseline read-protection pairing checks unchanged.
- The approved C2 read authorization channel (sandbox payload protocol v7): approved sensitive-location reads run inside the sandbox under a dedicated authorization subject outside the deny group within a single execution window (`authorized_read_paths`; a payload without authorized reads is ordinary execution with unchanged v5 behavior), and approved read paths no longer merge into the read_roots group channel; the execution window is cross-process serialized, and the authorized execution identity is isolated from ordinary execution. Three gates: the entry is refused when the helper does not declare v7 capability; the authorization subject is persistently disabled after a failed restore or unknown reclamation; and the helper verifies the C1 protected surface from the tamper-resistant protected-paths list before executing (refusing on absence, parse failure, or overlap with the authorized reads). Authorized execution requires sandboxing components that declare v7 capability; environments without it refuse the entry (fail-closed) with ordinary sandbox execution unchanged.

#### 🛠 Fixed

- The file-tool grant audit breakage where grants without a policy identity failed grant-event validation (file-tool plans now carry a stable policy identity).
- The CLI guard wrapper dropping the network-request flag, which bypassed approval on guarded paths (guarded network requests ask for approval again).
- Standing network exemption is tightened to two conditions: an `enabled` standing network policy and confirmed standing trust must both hold; confirming only additional write roots under `restricted` no longer exempts networking from approval (an independent standing-network gate in the permission engine).
- C1 protection now covers bidirectional overlap: a request root that covers a C1 protected object (such as a parent directory of the user config, trust files, or the session store) is hard-refused as well; the check spans all four write-root sources (per-call requests, standing additional write roots, active grants, and user-configured runtime roots) against the composed instance's actual paths.
- Revoking standing authorization now invalidates the session: the send entry and every tool dispatch re-verify the composition fingerprint against the current trusted configuration (CLI and Desktop share the mechanism and both re-read configuration; read or parse failures invalidate too); on mismatch all grants are revoked and the session is invalidated with a prompt to reopen it. Started commands keep the existing cancellation reclamation without automatic recomposition.
- The permission-system root set is narrowed from the whole sandbox home to four sensitive subpaths (`.sandbox-secrets`, `.sandbox`, `cap_sid`, `.sandbox-bin`); the default tmp/tools/nuget-cache runtime roots regain their runtime-root classification and L1 automatic execution, while OS ACLs and composition integrity checks still cover the whole home.

#### 🧪 Tests

- Real-sandbox acceptance tests are now environment-gated: default `dotnet test` no longer touches the machine sandbox, fixed user directories, or the network; the probe root must be set explicitly via `TINYHARNESS_REAL_SANDBOX_PROBE` (no default, avoiding %TEMP%'s historical ACL pollution), the negative isolation probe first verifies the probe root's ACL precondition (unmet = Inconclusive, not counted as passing), and the proxy is parameterized via `TINYHARNESS_REAL_SANDBOX_PROXY`.

<a id="release-v001"></a>

## 🚀 Release v0.0.1 — Initial Prototype / 首个原型

[简体中文](#简体中文-v001) | [English](#english-v001)

Release Date / 发布日期：2026-10-23

[Download / 下载 v0.0.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.0.1)

### 简体中文 v0.0.1

这是 TinyHarness.NET 的首次公开发布，汇集从基础执行循环到多轮会话的初始能力。项目仍处于早期原型阶段，基础任务已能用于实际尝试，仍在根据使用反馈修复细节，适合体验与反馈。

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
- 包内包含主程序、沙箱 setup／runner／managed-deny-probe、独立双语使用说明、沙箱配置示例、构建来源与文件哈希，以及 `LICENSE`、`NOTICE` 和 `THIRD-PARTY-LICENSES.txt`。
- 项目采用 Apache-2.0；第三方许可按分发范围整理，合并相同条款并保留适用版权、归属与特殊声明。
- 手动 workflow 运行上传构建 artifact；匹配项目版本的 `v*` tag 构建通过后创建 Draft + Prerelease，不自动公开。
- `v0.0.1` 按正式 Release 发布；公开前核对构建结果和分发包，再取消草稿与预发布标记。
- 提供中英文 README、使用说明和[打包文档](Guides/releasing.md)，原阶段计划统一并入[历史计划](Archive/PAST_PLAN.md)。

#### 🧪 测试

- 默认测试使用模拟模型和本地夹具，不依赖真实模型服务或 API key；提供原生产物的离线工具链路 smoke。
- 打包流程包含版本与来源校验、ZIP SHA-256 校验和解压后的原生 smoke；这些检查不替代真实供应商或 Windows 隔离验收。

#### ⚠️ 已知限制

- 终端界面尚未完善，权限请求、AI 回复与用户输入缺乏清晰区分，输入编辑与运行中控制仍很简陋。
- 模型回答、摘要和工作项状态可能出错；本版不作稳定性或全面安全性保证，需核对实际文件变化和工具结果。
- 会话内容不加密且可能包含秘密；配置、命令、内置提示词和存储格式变化可能影响旧会话续聊。
- 目前以 Windows x64 为验证平台。沙箱需要单独配置组件路径并由用户明确初始化，只隔离其工具进程；其他平台及与 Codex 发布版共存仍未验证。

### English v0.0.1

This is the first public release of TinyHarness.NET, bringing together the initial execution loop and multi-turn session capabilities. The project remains an early prototype. Basic tasks can be tried in real work, with practical issues still being fixed through use and feedback. It is intended for experimentation and feedback.

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
- A package containing the main executable, sandbox setup/runner/managed-deny-probe, standalone bilingual instructions, a sandbox configuration example, build provenance and file hashes, plus `LICENSE`, `NOTICE`, and `THIRD-PARTY-LICENSES.txt`.
- Apache-2.0 licensing for the project, with third-party terms scoped to the distribution, identical terms deduplicated, and applicable copyrights, attribution, and special notices retained.
- Manual workflow runs upload build artifacts; successful builds for `v*` tags matching the project version create a Draft + Prerelease without automatically publishing it.
- `v0.0.1` is published as a regular release; build results and distribution files are checked before removing the draft and prerelease flags.
- English and Chinese READMEs, usage instructions, and [packaging documentation](Guides/releasing.md); the previous milestone plans are consolidated in the [historical plan archive](Archive/PAST_PLAN.md).

#### 🧪 Tests

- Default tests use simulated models and local fixtures without a real model service or API key; the native executable provides an offline tool-flow smoke check.
- The packaging flow includes version and source validation, ZIP SHA-256 verification, and a native smoke check after extraction; these checks do not replace real-provider or Windows-isolation acceptance.

#### ⚠️ Known Limitations

- The terminal interface is unfinished: permission requests, AI replies, and user input are not clearly separated, and input editing and runtime controls remain rudimentary.
- Model answers, summaries, and work-item statuses can be wrong; this version provides no stability or comprehensive security guarantee. Check actual file changes and tool results.
- Sessions are not encrypted and may contain secrets; changes to configuration, commands, the built-in prompt, or storage formats may affect resume compatibility.
- Windows x64 is the current validation platform. The sandbox requires separately configured component paths and explicit user provisioning, and isolates only its tool processes. Other platforms and coexistence with a Codex release installation remain unverified.
