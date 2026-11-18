# Changelog / 更新日志

本文件记录 TinyHarness.NET 各版本的重要变化。每个版本包含内容对应的简体中文与英文说明。

All notable changes to TinyHarness.NET are documented here, with matching Simplified Chinese and English notes for each version.

## 📘 Versions / 版本

- [🚧 Unreleased / 未发布](#unreleased)
- [🚀 Release v0.1.0-alpha.1 — Desktop Preview / 桌面预览](#release-v010-alpha1)
- [🚀 Release v0.0.1 — Initial Prototype / 首个原型](#release-v001)

<a id="unreleased"></a>

## 🚧 Unreleased / 未发布

（暂无）/(Nothing yet.)

<a id="release-v010-alpha1"></a>

## 🚀 Release v0.1.0-alpha.1 — Desktop Preview / 桌面预览

[简体中文](#简体中文-v010-alpha1) | [English](#english-v010-alpha1)

Release Date / 发布日期：2026-11-18

[Download / 下载 v0.1.0-alpha.1](https://github.com/banned2054/TinyHarness/releases/tag/v0.1.0-alpha.1)

### 简体中文 v0.1.0-alpha.1

这是 TinyHarness.NET 的第一个预发布版本（alpha），也是首个包含 Desktop 桌面客户端的发布。自 v0.0.1 以来，权限体系重构为三级执行模型并引入授权治理，会话存储引入业务库与显式迁移，新增项目实体、受控联网与 Desktop 客户端。alpha 阶段命令、配置和存储格式仍可能变化；升级前请备份用户配置与会话存储。

#### ✨ 新增

**Desktop 桌面应用（首个版本）**

- 新增基于 Avalonia 的 Windows 桌面客户端，与 CLI 共享 Core 运行时、用户配置与会话存储：项目侧栏（分类视图、手动排序、置顶、展开状态持久化）、会话列表首屏先行与逐批展开、归档会话与可恢复项目删除、会话自动命名与失败重试。
- 对话视图：流式输出按批调度、执行过程分组、思考内容展示、底部自动跟随、Markdown 列表标记对齐；用户气泡支持文本选择与悬停复制，助手回复支持整轮复制。
- 供应商与模型管理：供应商编辑卡与 API 密钥输入、从供应商获取模型目录并多选导入、编辑卡内删除模型、供应商删除确认与共享凭据保护；设置分区选择即自动保存；多模态输入能力配置项。

**权限与沙箱执行**

- 三级执行体系（Windows 沙箱基线 / 沙箱扩展 / 显式宿主执行）：普通读取不限工作区、工作区写入与沙箱 Shell 自动执行；越界写入、敏感位置、受保护元数据写入、联网申请与 shell 写根申请（`writeRoots`）按 Level 2 逐次审批；`executionScope=host` 为显式单次批准的宿主执行。
- 权限引擎重写为 Grant 决策链：单次／Turn／会话／常设授权，授权生命周期与可信授权变更以必需记录 `permission-grant-event/1` 写入会话日志（旧程序拒读）；组合指纹守卫在发送入口与每次工具派发前核对当前可信配置，失配即撤销全部授权并失效会话；审批展示事实行在 CLI 与 Desktop 同源一致，审批授权上下文聚合展示。
- 常设信任显式迁移：`networkPolicy=enabled` 与附加写根不再静默扩大授权；检测到即进入未确认态，经 CLI chat 启动确认或 Desktop 设置页确认后作为显式持久授权运行，撤销入口随时可用。
- OS 层读基线与读防护：沙箱进程 OS 读边界为用户 profile 根；宿主密钥路径、`.codex` 授权内容、`cap_sid` 与敏感目录段经 deny-read 集随每命令同步施加并可回收；C1 保护覆盖双向重叠，申请根覆盖保护对象同样硬拒。
- C2 获批读通道（沙箱 payload 协议 v7）：获批的敏感位置读取由专用授权主体在单次执行窗口内完成，执行窗口跨进程串行，授权执行身份与普通执行隔离；helper 能力声明、恢复失败禁用与保护清单核对三道前置门，未声明环境下入口拒绝（fail-closed）。
- Desktop 审批与沙箱界面：「请求批准」与「帮我批准」（AI 审核）双模式、审批三分展示、独立 AI 审批总期限（CLI 与 Desktop 同源）、沙箱设置分区与延迟初始化、沙箱目录权限修复入口。

**受控联网**

- 沙箱网络三态模型（禁用／受限／启用）：代理支持、NuGet 缓存共享、`sandbox verify --network` 验收模式；按任务意图判断必要联网查询授权；常设网络免审批收紧为双条件（常设网络策略 `enabled` 且常设信任已确认）；未信任工作区 `run` 保持可用（模型与只读文件工具可用、进程执行 fail-closed）。
- Desktop 网络设置：系统代理、直连或已保存的 HTTP／HTTPS／SOCKS5 代理，供应商可继承或单独指定；代理凭据保存在系统凭据存储。

**项目实体与会话归属**

- 项目核心模型与受保护目录册：会话归属项目并在旧日志上幂等初始化，启动时自动为旧会话补齐归属；目录册变化检测与跨进程自动同步（内容语义比较）；可恢复项目删除与提示设置。

**会话列表索引与存储迁移**

- 会话列表 SQLite 摘要索引（Microsoft.Data.Sqlite）：首屏先行、定向整理、跨进程监视同步与对账、侧栏轻刷新，显著降低大会话库的列表加载耗时。
- 会话存储业务库（`session-store.db`）与显式迁移：冻结业务库 schema 并提供版本化迁移协议（v1→v2）、分批导入与迁移标记、业务库权威接管（组织状态、项目目录册、会话归属投影）、备份／校验／恢复／回退保护、业务库 WAL/SHM 隔离与恢复记录；新增 CLI 显式迁移管理命令。
- 永久删除会话支持中断续作（墓碑机制）；迁移后项目删除在单事务中完成会话解绑。

**模型与续聊**

- 会话内切换模型、推理强度与原文持久化；推理档位对齐官方全集（含 `xhigh` 规范拼写），传输使用 raw representation；旧会话恢复按保存绑定回退。
- 可配置模型请求超时策略：流空闲超时、总响应超时与 AI 审批总期限（`settings.modelTimeouts`）。
- CLI 交互式会话新建与切换（`/new`、`/sessions`、`/switch`）；`search_text` 支持正则过滤与输出增强。

#### 🛠 修复

- 修复文件工具 grant 缺策略身份导致授权事件记录被拒的断路；修复 CLI 守卫包装丢失网络申请标志导致的审批旁路。
- 非交互输入不得授予审批权限或解除检查阻断；派发前重新核对指令与组合守卫。
- 更换 endpoint 来源后不再沿用旧凭据引用，复用旧密钥前需确认；无动词调用支持前置 `--config`。
- 修复 Desktop 停止时的取消异常中断，完善取消清理与生命周期取证；修复 Windows Ctrl+C 将交互输入误判为结束。
- 修复旧会话切换模型时的空引用异常；自动刷新桌面会话的供应商和模型配置。
- 修复会话索引归档投影同值行切断 `data_version` 反馈环。
- 旧版权限配置与会话自动迁移至人工审查；修复旧版权限选择记录与历史摘要解析。
- 沙箱目录 ACL 异常时阻断启动命令；CLI 可经受限 helper 修复目录 ACL，Desktop 提供修复入口。
- `config set` 校验预算与所选模型窗口一致性，支持逐项修复超限配置；忽略工作目录中的应用配置。
- 权限系统保护根细化：整沙箱 home 收缩为四个敏感子路径，默认 tmp/tools/nuget-cache 运行时根恢复 L1 自动执行；OS ACL 与组合完整性校验仍覆盖整 home。

#### 📦 构建与分发

- 运行时升级至 .NET 11（`net11.0`），NativeAOT 与 trimming 约束持续满足；发布工作流改为 CLI 与 Desktop 两个同构 ZIP，各自包含沙箱组件与许可材料，并分别执行原生 smoke（CLI `smoke`、Desktop `--offline-smoke`）。
- CLI 随 Microsoft.Data.Sqlite 携带 `e_sqlite3.dll`；Desktop 额外携带 Avalonia 渲染原生库。
- 依赖升级：Avalonia 12.1.6、LiveMarkdown.Avalonia 2.5.1、Microsoft.Extensions.AI.OpenAI 11.0.0、OpenAI 2.14.0、Microsoft.Data.Sqlite 11.0.0、System.Security.Cryptography.ProtectedData 11.0.0。

#### 🧪 测试

- 真实沙箱验收测试改为环境变量门禁：默认 `dotnet test` 不触机器沙箱、固定用户目录与外网；探针根经 `TINYHARNESS_REAL_SANDBOX_PROBE` 显式指定（无默认值），负向隔离探针先验 ACL 前提（不满足记为 Inconclusive 不计通过），代理经 `TINYHARNESS_REAL_SANDBOX_PROXY` 参数化。
- 新增会话索引 IndexProbe 探针、列表加载基准与侧栏投影链路基准测试；打包流程增加两端独立版本与原生依赖检查、`build-info.json` 来源与哈希清单。

#### ⚠️ 已知限制

- CLI 的编辑与运行控制仍较基础，工具没有交互式终端；Desktop 为首个版本，界面与交互仍在打磨。
- 模型回答、摘要、自动命名与 AI 审批建议可能出错；本版不作稳定性或全面安全性保证，需核对实际文件变化和工具结果。
- 会话内容不加密且可能包含秘密。会话存储自动迁移尚未在真实用户数据上完成验收；迁移前请关闭旧版程序并备份会话目录。旧程序可能无法打开新版写入的日志（如 `session-title`、`permission-grant-event`）。
- 目前以 Windows x64 为验证平台。沙箱需要单独配置组件路径并由用户明确初始化，只隔离其工具进程；其他平台及与 Codex 发布版共存仍未验证。

### English v0.1.0-alpha.1

This is TinyHarness.NET's first prerelease (alpha) and the first release that ships the Desktop client. Since v0.0.1, the permission system has been reworked into a three-level execution model with grant governance, session storage gained a business store with explicit migration, and project entities, controlled networking, and the Desktop client were added. Commands, settings, and storage formats may still change during alpha; back up your user configuration and session store before upgrading.

#### ✨ Added

**Desktop application (first release)**

- A new Avalonia-based Windows desktop client sharing the Core runtime, user configuration, and session store with the CLI: project sidebar (category view, manual ordering, pinning, persisted expansion state), session list with first-screen loading and batched expansion, archived sessions with recoverable project deletion, and automatic session titles with retries.
- Conversation view: batched streaming output, process grouping, reasoning display, bottom auto-follow, and aligned Markdown list markers; user bubbles support text selection with a hover copy button, and assistant replies support full-turn copy.
- Provider and model management: provider edit cards with API key input, fetching provider model catalogs with multi-select import, deleting models inside an edit card, provider deletion confirmation with shared-credential protection; settings save on selection; multimodal input capability configuration.

**Permissions and sandboxed execution**

- Three-level execution (Windows sandbox baseline / sandbox extension / explicit host execution): ordinary reads beyond the workspace, workspace writes, and the sandboxed shell run automatically; writes outside the workspace, sensitive locations, protected-metadata writes, networking requests, and shell write-root requests (`writeRoots`) ask per call at Level 2; `executionScope=host` is explicit single-approval host execution.
- The permission engine was rewritten as a Grant decision chain: one-time / turn / session / standing grants, with grant lifecycles and trusted authorization changes persisted as required `permission-grant-event/1` session-log records (older programs refuse them); a composition fingerprint guard re-verifies the current trusted configuration on send and before every tool dispatch, revoking all grants and invalidating the session on mismatch; approval fact lines render from one shared source across CLI and Desktop, with the approval context presented in an aggregated window.
- Explicit standing-trust migration: `networkPolicy=enabled` and additional write roots no longer expand authority silently; detection enters an unconfirmed state until confirmed via the CLI chat startup prompt or the Desktop settings page, after which they run as explicit persistent authorization with a revoke entry.
- OS-level read baseline and read protection: the sandbox process OS read boundary is the user profile root; host secret paths, `.codex` authorization content, `cap_sid`, and sensitive directory segments ship in a deny-read set applied per command and reclaimable; C1 protection covers bidirectional overlap, hard-refusing request roots that cover protected objects.
- The approved C2 read channel (sandbox payload protocol v7): approved sensitive-location reads run under a dedicated authorization subject within a single execution window that is cross-process serialized, with the authorized execution identity isolated from ordinary execution; three gates (helper capability declaration, persistent disable after failed restore, and protected-manifest verification) refuse the entry fail-closed where capability is undeclared.
- Desktop approval and sandbox UI: the "Ask for approval" and "Approve for me" (AI reviewer) modes, three-part approval display, an independent AI-review deadline shared by CLI and Desktop, a sandbox settings section with delayed initialization, and a sandbox directory-permission repair entry.

**Controlled networking**

- A three-state sandbox network model (disabled / restricted / enabled): proxy support, shared NuGet caching, and a `sandbox verify --network` acceptance mode; networking-query authorization follows task intent; standing-network exemption now requires both an `enabled` standing network policy and confirmed standing trust; `run` stays usable in untrusted workspaces (model and read-only file tools work; process execution fails closed).
- Desktop network settings: system proxy, direct connection, or saved HTTP/HTTPS/SOCKS5 proxies, inherited or overridden per provider; proxy credentials stay in the system credential store.

**Projects and session ownership**

- Project core model and the protected project catalog: sessions belong to projects, old logs initialize idempotently, and startup automatically backfills ownership for old sessions; catalog change detection with cross-process automatic sync (semantic content comparison); recoverable project deletion with prompt settings.

**Session list index and storage migration**

- A SQLite summary index for the session list (Microsoft.Data.Sqlite): first-screen loading, targeted compaction, cross-process watcher sync with reconciliation, and lightweight sidebar refresh, sharply reducing list load times for large stores.
- The session business store (`session-store.db`) and explicit migration: a frozen business schema with a versioned migration protocol (v1→v2), batched import with migration markers, business-store authority for organization state, the project catalog, and session ownership projection, backup/verification/restore/rollback protection, and business-store WAL/SHM quarantine with recovery records; a new explicit CLI migration command.
- Permanent session deletion supports resumable continuation (tombstones); project deletion after migration unbinds sessions in a single transaction.

**Models and resume**

- In-session model switching, reasoning effort, and verbatim persistence; the reasoning effort set is aligned with the official full set (including the canonical `xhigh` spelling) sent as the raw representation; old sessions fall back along their saved bindings on resume.
- Configurable model request timeout policy: stream idle timeout, total response timeout, and an independent AI-review deadline (`settings.modelTimeouts`).
- Interactive session creation and switching in the CLI (`/new`, `/sessions`, `/switch`); `search_text` supports regex filtering and richer output.

#### 🛠 Fixed

- Fixed the file-tool grant audit breakage where grants without a policy identity failed grant-event validation; fixed the CLI guard wrapper dropping the network-request flag, which bypassed approval.
- Non-interactive input can no longer grant approval authority or clear inspection blocks; instructions and the composition guard are re-verified before dispatch.
- Changing an endpoint origin no longer reuses the old credential reference and requires confirmation before key reuse; verb-less invocations accept a leading `--config`.
- Fixed the Desktop stop cancellation exception, with better cancellation cleanup and lifecycle forensics; fixed Windows Ctrl+C misreading interactive input as EOF.
- Fixed a null-reference crash when switching models in old sessions; provider and model configuration refresh automatically for desktop sessions.
- Fixed the session-index archive projection breaking the `data_version` feedback loop on same-value rows.
- Legacy permission configuration and sessions migrate automatically to human review; fixed parsing of legacy permission records and historical summaries.
- Sandbox directory ACL anomalies now block command startup; the CLI can repair directory ACLs through the restricted helper and Desktop offers a repair entry.
- `config set` validates budget consistency with the selected model's context window and supports per-item fixes; application configs in working directories are ignored.
- Permission-system roots narrowed: the whole sandbox home shrinks to four sensitive subpaths while default tmp/tools/nuget-cache runtime roots regain L1 automatic execution; OS ACLs and composition integrity checks still cover the whole home.

#### 📦 Build and Distribution

- The runtime upgraded to .NET 11 (`net11.0`) with NativeAOT and trimming constraints still enforced; the release workflow now produces two isomorphic ZIPs (CLI and Desktop), each with sandbox components and license materials, and runs native smoke per edition (CLI `smoke`, Desktop `--offline-smoke`).
- The CLI carries `e_sqlite3.dll` with Microsoft.Data.Sqlite; Desktop additionally carries Avalonia rendering native libraries.
- Dependency upgrades: Avalonia 12.1.6, LiveMarkdown.Avalonia 2.5.1, Microsoft.Extensions.AI.OpenAI 11.0.0, OpenAI 2.14.0, Microsoft.Data.Sqlite 11.0.0, System.Security.Cryptography.ProtectedData 11.0.0.

#### 🧪 Tests

- Real-sandbox acceptance tests are environment-gated: default `dotnet test` no longer touches the machine sandbox, fixed user directories, or the network; the probe root is explicit via `TINYHARNESS_REAL_SANDBOX_PROBE` (no default), the negative isolation probe verifies its ACL precondition first (unmet = Inconclusive, not counted as passing), and the proxy is parameterized via `TINYHARNESS_REAL_SANDBOX_PROXY`.
- New session-index IndexProbe, list-load benchmarks, and sidebar projection benchmarks; packaging adds per-edition version and native-dependency checks plus a `build-info.json` provenance and hash manifest.

#### ⚠️ Known Limitations

- CLI input and run controls remain basic and tools have no interactive terminal; Desktop is a first release with UI and interactions still being polished.
- Model answers, summaries, automatic titles, and AI-review suggestions can be wrong; this version provides no stability or comprehensive security guarantee. Check actual file changes and tool results.
- Sessions are not encrypted and may contain secrets. Session-store migration has not been accepted against real user data; close older clients and back up the session directory before migrating. Older binaries may refuse logs written by newer versions (such as `session-title` and `permission-grant-event`).
- Windows x64 remains the validated platform. The sandbox requires separately configured component paths and explicit user provisioning and isolates only its tool processes; other platforms and coexistence with a Codex release installation remain unverified.

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
