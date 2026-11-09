# Phase 2：基于 Codex Windows Sandbox 的联网与开发工具链支持——交付报告

> 历史归档（2026-11-09 整理）。保留原计划、实施记录、未完成项与验证边界；归档不表示全部完成，也不自动启动后续任务。当前活动项见 [PLAN.md](../../PLAN.md)。

日期：2026-11-06。基线：`master` @ `758e53a`（Phase 1 完成状态）。本报告由主智能体汇总三个实施 Subagent 与 OCR 评审的记录写成；所有状态区分"已验证（真实 OS）/ 离线验证 / 未验证 / 推断"。

---

## 1. 实施背景

本阶段在 Phase 1（审批授权窗口、宿主执行审批契约收紧）完成基线上继续使用既有 Codex Windows Sandbox fork 组件（`codex-command-runner.exe` + `codex-windows-sandbox-setup.exe`，pinned ref `1b647eac`），目标是为沙箱增加受控联网、本地代理支持、NuGet 缓存与 .NET 开发工具链支持，并保持七种权限模式与 NativeAOT 约束不回归。**全程未修改任何 Codex Rust 源码，未创建分支，未提交，未推送。**

## 2. 具体实现

### 2.1 Online/Offline 账户

- Codex 模型：`TinyHarnessOffline`（WFP 16 条 BLOCK + Windows 防火墙阻断，均绑定账户 SID）与 `TinyHarnessOnline`（零网络策略对象 → 无条件可联网）；两账户组员/令牌/文件 ACL 完全相同。runner 由哪个账户启动决定子进程网络能力；**运行中进程不可改网络策略**（runner IPC 无 reconfigure 消息）。
- C# 接线：`SandboxCredentialReader` 增加 `LoadOnlineAccount`（与 Offline 对称：版本校验、外来账户名拒绝、DPAPI LocalMachine、SID 映射）；`WindowsSandboxBackend` 按生效网络选择账户与 spawn profile 的 `network` 字段（`"restricted"|"enabled"`，线上协议原本就支持）；`WindowsSandboxStateInspector` 增加 `InspectOnlineAccountAsync`（marker 双账户 + users 文件 online 条目 + SID 可映射），生效在线时 preflight 失败即拒绝，**绝不降级离线、绝不回退宿主**。
- Profile 加载：online 启动传 `LOGON_WITH_PROFILE`（`RunnerLaunchRequest.LoadUserProfile`），offline 保持 0 零回归。已知权衡：账户自身 profile 目录（如 `C:\Users\TinyHarnessOnline`）成为该机器本地独立账户的可写面。
- 组合守卫：`NetworkPolicy=Enabled` 与 `AllowTcpLoopback` 互斥（Compose fail-closed）；tcp-loopback 模式下的逐次 `networkAccess=enabled` 也在 backend 启动期以 `ProcessExecutionStartException` 拒绝（该模式账户对 TcpOff/TcpOn 无外联能力，避免"审批说全网、实际只有环回"的语义失真）。

### 2.2 网络策略

- 三态模型：可信设置 `windowsSandbox.networkPolicy`（`restricted`=默认，逐次审批可联网；`enabled`=常设信任，全部沙箱命令以 Online 执行）+ shell 工具可选参数 `networkAccess`（`"default"|"enabled"`，单次申请）。
- 权限决策：`PermissionEngine` 新增 `sandboxNetworkStandingEnabled` 参数；规则 `RequiresNetworkAccess && !standing → Ask`，位于硬 deny / NeedsInspection / worker 保护之后、ReadOnly/ProjectWrite 文件自动放行之前（网络申请绝不因文件自动放行而静默通过）；host 作用域（FullAccess 或 scope=host）恒不携带网络申请。
- 冻结与绑定：`networkAccess=enabled` 在 `ShellTool.Prepare` 冻结进 `PreparedProcessExecution.NetworkAccessEnabled`，grant constraint 追加 `network-access: enabled` 行（AllowOnce 单次语义 + AllowSession 跨网络状态不可复用，由既有约束精确匹配保证）；摘要前缀 `[联网] `。审批负载新增 `requiresNetworkAccess`。
- 身份串：`PolicyIdentityVersion` 4→5；`network=` 行反映常设策略；非空代理 env 时追加 `proxyEnvironment=<SHA-256>` 行；新增 `readRoots=` 行。
- 拒绝/超时/取消/审批异常 → 整条命令不执行，不降级离线。
- Desktop AI 审批提示词新增网络段：明确"沙箱内全网访问（可能经用户本地代理，如 127.0.0.1:7890），不是仅 NuGet 或特定域名的窄授权；窄授权从严"。审批卡沿用 Summary（含 `[联网]` 前缀）+ ExecutionPolicy 展示，未新增承诺文案。

### 2.3 代理支持

- 新增可信设置 `windowsSandbox.proxy`：`FollowAppDefault`（默认，复用应用默认路由解析，失败按无代理并记诊断）/ `System`（`HttpClient.DefaultProxy` 同源）/ `Direct` / `Named`（按名称引用既有 `network.proxies` 条目，缺失/重名 fail-closed）。
- 注入：仅生效在线时合并进子进程 env——http/https 代理 → `HTTP_PROXY`+`HTTPS_PROXY`；socks5 → `ALL_PROXY`（.NET 工具不读，已知限制）；`NO_PROXY` 恒 `localhost,127.0.0.1,::1`。restricted 执行默认派生路径下 env 无任何代理键（acceptance `env-proxy-echo` 两模式正反断言固化）。可信 `extraEnvironment` 优先级最高、可为受限子进程显式注入代理键（不推荐，文档已注明）。
- 凭据安全：代理 URL（含 userinfo）不进入身份串（仅 SHA-256）、DisplayName、DiagnosticLog（仅 count/type）、异常消息与审批文本；env 值仅由 Host/Port 构造。需认证代理在沙箱内不可用（无凭据存储通道），已注释为已知限制。
- 系统代理模式实测：本机 `ProxyEnable=0` → 解析为空、如实 FAIL 记录（解析语义：无系统代理时注入空环境）。

### 2.4 NuGet 缓存

- 默认派生 `NUGET_PACKAGES=<sandboxHome>\nuget-cache`（稳定、会话间保留），自动建目录并作为额外写根；可信 `extraEnvironment` 中的 `NUGET_PACKAGES` 优先并接管缓存根。
- 新增通用可信 `AdditionalReadRoots` → setup payload `read_roots` → TinyHarnessUsers 组 RX ACL（OS 级只读）。
- **宿主缓存只读复用配方（已实证）**：`additionalReadRoots=["%USERPROFILE%\\.nuget\\packages"]` + 沙箱 APPDATA 重定向处放 `NuGet.Config`（`<clear/>` + `fallbackPackageFolders` 指向宿主缓存）→ restricted 沙箱内 `dotnet restore`/`build` 离线成功（95ms，零网络痕迹，包从 fallback 原地消费）。Caveat：新增读根后首次命令有约 2 分钟 ACL 继承传播瞬态（十万级目录树），先跑 warm-up 或重跑即可。
- 安全边界：读根、写根（含派生 nuget-cache/tools/tempRoot/用户写根）与 `extraEnvironment` 派生的缓存根均不得与凭据目录 `<sandboxHome>\.sandbox-secrets` 任一方向重叠（含 `GetFinalPathNameByHandle` 规范化后的最终路径比对，抗 8.3 短名/junction/symlink 绕过），违者 fail-closed。

### 2.5 dotnet 工具链

- 默认派生 `<sandboxHome>\tools`：自动建目录、作为额外写根、PATH 追加（沙箱内 `dotnet tool install --tool-path` 的目标位置）。
- 宿主工具复用（已实证）：`additionalReadRoots` 加 `%USERPROFILE%\.dotnet\tools` 后，沙箱内以绝对路径运行宿主 `jb.exe --help` 成功（帮助文本完整）。裸 `jb` 经 PATH 解析失败的原因是宿主 PATH 中 `%USERPROFILE%` 变量在沙箱 env 内展开为重定向 profile——可通过 `extraEnvironment` 显式覆盖 PATH 或使用绝对路径。
- 沙箱内在线安装（`dotnet tool install`）被 TLS 问题阻断（见 §4/§7）。

### 2.6 UI / CLI 接入

- Desktop 设置面板新增"Windows 沙箱"节（此前只能手改 user-config.json）：Enabled、NetworkPolicy（受限/常设）、AllowTcpLoopback、代理四模式 + 从既有代理列表按名称选择、AdditionalReadRoots/AdditionalWriteRoots/ExtraEnvironment；节内注明"下次会话/下次组合生效，不影响正在运行的命令；proxy 凭据不显示"。
- CLI：`ComposeAgentAsync`/`SandboxCliComposition.TryCompose` 传入 NetworkSettings；Engine standing 两端接线一致；`sandbox verify` 新增 `--network <restricted|enabled>`（默认 restricted 行为零变化；enabled 确认前概览打印实际探测 URL、代理 host:port 与宿主环回目标；verify-worker 新增同构 `--network` 传参，与父进程组合同源）。`HelpText` 探测端点动态引用 `WindowsSandboxAcceptanceLimits` 常量。
- 配置向后兼容：旧 user-config.json 无新字段加载为 Restricted/FollowAppDefault（测试固化）。

## 3. Codex 源码调查结论

- 调查范围：`codex-rs/windows-sandbox-service`、`windows-sandbox-rs` 及引用到的 `core/exec`、`sandboxing`、`network-proxy`、`cli/debug_sandbox`、`protocol/config_types`。
- **实际复用的 Codex 机制**：双账户 + WFP/防火墙按账户 SID 阻断、`SandboxPermissionProfile.network` 线上枚举、`SandboxSetupPayload.online_username`、marker `online_username`、users 文件双条目 + DPAPI LocalMachine、`CreateProcessWithLogonW` + `CreateProcessAsUserW` + 受限令牌（capability SID）、read/write/deny ACL 体系、`allow_tcp_loopback` 独立账户对与 WFP key。
- **TinyHarness 原本缺失的接线**（本阶段补齐，全部 C#）：在线凭据读取与在线就绪检查、按生效网络的账户与 profile 选择、`LOGON_WITH_PROFILE`、代理 env 解析与注入、`NUGET_PACKAGES`/tools 根/PATH 派生、`AdditionalReadRoots` 通道、审批链网络维度、设置面板、verify 网络模式。
- **未启用的 Codex 原生能力**（记录为后续路径）：`proxy_ports`/`allow_local_binding` marker 防火墙豁免、managed network 的 per-session restricting SID（`SpawnRequest.network_proxy_restricting_sid`，保持 null）、TcpOff/TcpOn 之外的账户编排。
- **Rust 源码未修改**：所有协议字段在 pinned fork 中已存在；runner/setup 二进制未重建。

## 4. 安全边界

- **在线执行保持沙箱隔离**：Online 与 Offline 文件 ACL 等价（同一组员/令牌属性/ACL 体系）；enabled 验收矩阵复用 metadata-protected、write-denied-outside-roots、credentials-dir-unreadable、deny-probe 全部 PASS（真实 OS）。
- **WFP/防火墙**：未新增或修改任何规则对象；`proxy_ports`/`allow_local_binding` 保持空/false；restricted 下沙箱外网在 socket 层被阻断（restore 对照实测 NU1301 "以 socket 权限不允许的方式访问"，5.8s 快速失败）。
- **网络权限与文件权限分离**：网络是计划属性维度，不进入 host/workspace scope 语义；申请绝不因文件自动放行而自动通过。
- **Loopback**：restricted 下本地代理不能成为绕过通道（socket 阻断 + firewall loopback block 双重覆盖，实测外网/环回均失败）；enabled 下 Online 账户可达宿主 127.0.0.1:7890（Clash 返回 HTTP 400 直连证据 + CONNECT 隧道 200）。**未宣称端口级隔离**：enabled 的网络粒度是"全网（经代理）"，审批文本如实描述。
- **凭据保护**：凭据目录对读写根双向重叠 fail-closed（词法 + 最终路径）；凭据内容未进入任何日志、报告、身份串；DPAPI 解密失败不泄漏明文。
- **审批-执行一致**：计划冻结后 backend 只消费冻结值与构造期快照，无设置重读；账户/profile/代理均按审批时形态执行。
- **未解决的网络绕过风险**：无已知绕过。残余限制见 §7（TLS、认证代理、端口粒度）。

## 5. 测试结果

| 项 | 结果 | 状态 |
|---|---|---|
| TinyHarness.Tests 全量 | **1674 通过 / 0 失败 / 4 跳过**（跳过为 junction/symlink 与基准类） | Offline validated |
| TinyHarness.Desktop.Tests 全量 | **344 通过 / 0 失败**（`DesktopBlankConversationTests` 一例为已知负载 flake，与本轮无关，复跑通过） | Offline validated |
| 新增测试 | 4 个新文件 + 多轮增补：网络后端行为、七模式 × 网络决策矩阵、环境派生/配置兼容、验收网络模式（scripted fake） | Offline validated |
| 真实 OS restricted | `sandbox verify --network restricted` **17/17 PASS**（含 host-exit-reclaim worker 链路） | Runtime validated |
| 真实 OS enabled | `sandbox verify --network enabled` **8/8 PASS**（TinyHarnessOnline、代理注入、外网探测、宿主环回、安全不变量复用） | Runtime validated |
| 代理 | 127.0.0.1:7890 注入实证（env 回显 + CONNECT 200 + netstat 旁证）；坏代理 127.0.0.1:1 快速失败（curl exit 7，~2s，非挂起）；restricted 无代理键 | Runtime validated |
| 负向 fail-closed | 写根/读根指向凭据目录、tempRoot 指向凭据目录、Named 代理缺失、在线凭据缺失 → 全部组合期/启动期拒绝，无沙箱命令执行 | Runtime validated |
| NuGet | 离线 restore+build 配方成功（fallback folders）；在线 restore 被 TLS 阻断（见 §7）；restricted restore socket 级阻断对照 | Runtime validated（分向） |
| dotnet tools | 宿主 jb 绝对路径沙箱内可执行；沙箱内在线安装被 TLS 阻断 | Runtime validated（分向） |
| NativeAOT | CLI + Desktop `publish -c Release -r win-x64` 双端 **0 warning**，产物存在 | Offline validated |
| 原生 smoke | CLI `smoke` + Desktop `--offline-smoke` 双 **exit 0**（TMP 重定向、产物仓库外运行） | Runtime validated |
| `git diff --check` | 空 | 已验证 |

验证环境说明：真实沙箱验证一律使用隔离配置目录（`TINYHARNESS_USER_CONFIG_DIR`）与 %TEMP% 新建工作区，**用户真实配置未改动**。已知环境坑（记录）：① 发布产物摆放在 %TEMP% 时沙箱账户无 ACE，verify 的探针用例会失败——verify 应从仓库树或沙箱组可读位置运行；② 仓库根与 %TEMP% 存在历史遗留的沙箱组显式 ACE（`TinyHarnessUsers`/`CodexSandboxUsers`），验收工作区放在这些子树内会使"读根之外不授予"的 deny 模型失效——属机器历史状态，未清理。

## 6. OCR Code Review

方式：`open-code-review` delegation 模式（`ocr delegate preview/rule` 提供清单与规则，评审智能由本会话执行）。**覆盖 39 个评审文件 + 2 个 OCR 排除的 axaml（轻审），覆盖率 100%，42/42 reviewed，0 skipped。** 随后对修复 diff 做了两轮聚焦复核（第二轮复核 5/5 PASS，无新缺陷）。

**第一轮发现与处置（8 项）**：

| 级别 | 数量 | 内容与结果 |
|---|---|---|
| Critical | 0 | — |
| High | 0 | — |
| Medium | 3 | ① 写根无凭据重叠校验（DPAPI 凭据可经写根暴露）→ 已修；② 重叠校验纯词法可被 8.3/junction/symlink 绕过 → 新增 `SandboxFinalPathResolver` 最终路径比对，已修；③ HelpText 探测端点失实（api.github.com/zen vs 实际 example.com）→ 动态化并打印实际 URL，已修 |
| Low | 5 | ④ tcp-loopback 逐次联网审批语义失真 → fail-closed 拒绝，已修；⑤ ExtraEnvironment 代理键与文档矛盾 → 文档对齐，已修；⑥ networkAccess 非字符串静默回落 → 严格解析抛 InvalidDataException，已修；⑦ credentials-dir-unreadable 空洞通过 → 正对照，已修；⑧ verify 逐 case 接线无覆盖 → internal 缝隙补测试，已修 |

**第二轮复核发现与处置（5 项）**：① `SandboxTempRoot` 绕过凭据重叠校验（Medium）→ Resolver 单点拦截，已修；② Named 代理配置下 verify 预览与 worker 传 null 误抛"代理不存在"（Medium）→ 三处与生产同源传参 + worker `--network` 同构链路，已修；③ tcp-loopback 拒绝异常类型不一致（Low-Med）→ 包装 `ProcessExecutionStartException`，已修；④ 过宽 catch（`SystemException` 吞掉前两者）→ 收窄，已修（`SandboxCredentialReader.cs` L120 同形态为 Phase 基线代码，未动，记录）；⑤ AdditionalReadRoots 文档措辞不精确 → 已修。

**关键反证结论**（两轮均确认不成立）：审批绕过、Host Scope 回退、代理凭据泄漏（身份串/DisplayName/日志/异常全查）、计划冻结后被改写、生效网络误判、降级宿主/离线、并发污染、非 Windows 回归、P/Invoke 契约错误、测试 fake 掩盖生产缺陷。

**最终剩余风险（Low，接受并记录）**：verify 的 credentials-dir-unreadable 判定在目录缺失时仍为"拒绝"语义（无泄露路径）；`network-loopback-host` 对非 HTTP 服务会误记 FAIL（探测目标固定为 HTTP 代理端点，实际影响有限）；代理 `System`/`FollowAppDefault` 模式在无系统代理时静默注入空环境（文档化语义）。

## 7. 仍未完成的功能（如实声明）

1. **沙箱账户 schannel TLS 不可用**（本阶段最重要的未解限制）：online 模式下 curl 与 .NET 全部 `AcquireCredentialsHandle: SEC_E_NO_CREDENTIALS`。已诊断链：`LOGON_WITH_PROFILE` 生效、profile 目录创建、`HKU\<sid>` hive 挂载、HKCU/证书库可读、无 write-restricted token，但二级登录会话缺 userinit 初始化与 `AppData\Local\Microsoft\Crypto` 树，schannel 仍拒。**影响**：https 外网、NuGet 在线 restore、`dotnet tool install` 在线安装、jb 沙箱内安装均不可用；代理隧道本身正常（CONNECT 200），纯 HTTP 可达。**缓解**：离线 restore 配方（§2.4）覆盖"依赖已在宿主缓存"的全部常规开发场景。
2. 需认证代理在沙箱内不可用（无凭据注入通道）。
3. 系统代理在宿主 `ProxyEnable=0` 时解析为空（语义如此，非缺陷；Named 模式为推荐路径）。
4. Codex 原生 proxy-enforced 模式（offline + 仅代理端口豁免 + restricting SID 归因）未启用；端口级/域名级网络授权明确不做。
5. Desktop 设置面板的真实 GUI 交互（保存/同步/显示）仅离线 smoke 与单元覆盖，未做真机视觉验证。
6. 沙箱内 `dotnet tool install --tool-path <home>\tools` 机制就绪但在线下载被 TLS 阻断，未端到端实证。

## 8. 改动文件与代码规模

- **总计 44 项：34 个修改文件（+2105/−211）+ 10 个新文件（2569 行），合计约 4670 新增行**。
- 生产代码：Core（Models/Runtime/WindowsSandbox ×3 新、Services/Runtime/WindowsSandbox ×12、Models/Configuration、Models/Tools、Services/Tools ×2、Services/Permissions、Services/ChatCompletions/Configuration）、CLI（Commands ×3、Models、Program）、Desktop.Runtime（×2）、Desktop（ViewModel ×2、View ×2 + SettingsPanel/MainWindow 接线）。
- 测试：TinyHarness.Tests 新增 4 文件（1222 行）+ 8 个既有文件增补；Desktop.Tests 1 文件机械适配。
- 文档：`Docs/Guides/permission-modes.md` 新增网络申请小节；本报告。
- **未新增任何依赖**；**未修改 Codex Rust 组件**；未改动 `TargetFramework`/AOT/trimming/公开 API 兼容性设置。

## 9. 后续建议（仅下一步相关，未实施）

1. **TLS 根因收尾**：对 TinyHarnessOnline 做宿主侧 `runas` 同账户对照实验，区分"二级登录会话缺 userinit/Crypto 树"与"LSASS 凭据隔离"；若确认前者，可评估在 runner fork 中为 online 路径补充 profile 初始化（需按任务第十二节的交付路径要求单独立项：构建、版本化、许可与上游升级影响）。
2. 记录 verify 产物摆放约束（沙箱组需对宿主 exe 有读/执行 ACE）到 sandbox 管理文档。
3. 机器状态清理建议：审计并回收仓库根与 %TEMP% 的历史沙箱组显式 ACE（`TinyHarnessUsers`/`CodexSandboxUsers`），恢复验收工作区可放任意位置的前提。
4. ACL 首次传播瞬态（大缓存树约 2 分钟）可在设置文档或 verify 概览中提示 warm-up。
5. 若后续启用 Codex 原生 proxy-enforced/restricting-SID 能力，可在此基础上实现"仅经代理联网"的受限在线档位，作为网络粒度收紧路径。
