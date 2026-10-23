# Windows 沙箱管理与验收记录

本页内容自中文 README 精简迁出（2026-10-13）：`sandbox` 命令的使用细节与 M11 验收记录在此维护，接入设计见 [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)。

组件源码来自 [banned2054/codex](https://github.com/banned2054/codex)，即 TinyHarness 使用的 [OpenAI Codex](https://github.com/openai/codex) fork。日常 setup／runner 从该 fork 单独构建，TinyHarness 的 .NET 发布不包含 Rust 组件构建或自动下载。

## Windows 沙箱管理命令

`tinyharness sandbox` 是 Windows 工具进程沙箱的显式管理入口，只读取用户配置中的可信设置 `settings.windowsSandbox`，不发送任何输入给模型。普通工具执行不会自动 provisioning、提权或修复账户；账户、密码和网络规则的机器级设置仍只能通过 `sandbox provision` 显式发生。`run` / `chat` 的工作区目录授权是独立的用户确认流程，见下文。

### 工作区信任与目录授权

首次在一个目录启动交互式 `run` / `chat` 时，宿主显示工作区并询问 `[t]` 信任、`[r]` 只读（默认）、`[q]` 退出。非交互输入下，未知工作区保持只读，不读取重定向输入作为授权。只读允许内置的列举、搜索和读取工具，拒绝 `apply_patch` 和全部 `shell`，也不初始化沙箱或刷新 ACL；已有命令规则和临时授权不能覆盖此限制。

信任决定保存在用户配置文件旁的 `workspace-trust.json`（独立版本 1，最多 1 MiB），绑定精确绝对路径和 Windows 卷／文件标识，不继承父目录的信任，也不从项目配置或模型输出接收授权。工作区祖先含链接或重解析点时拒绝使用；信任文件必须位于工作区和全部附加写根之外，损坏或版本不支持时拒绝覆盖。

信任不等于 ACL 已就绪。启用 Windows `workspace-write` 时，宿主只读检查既有子项的继承状态。若当前沙箱组、工作区 capability 和子项继承已就绪，无需再次取得 ACL 管理权限；首次准备则还检查普通 helper 能否管理需要传播的既有对象。不能只根据根目录已有 `WRITE_DAC` 就宣称准备完成。

需要修复时，宿主另行显示范围并询问 `[y/N]`。确认后，普通权限父进程通过现有 `cap_sid` 存储解析当前工作区映射（缺少该工作区项时可能保存新映射），再以 UAC 启动专用 C# 子进程。子进程重新核对本机 `TinyHarnessUsers` SID 和可信表中的精确 capability 映射，只为当前工作区合并这两个 SID 的可继承 Modify ACE（与 fork 的 `WRITE_ALLOW_MASK` 一致），并给**原调用账户 SID** 保留**仅工作区根目录**的 `WRITE_DAC`。随后重新提交根 DACL，触发对既有、未关闭继承的子项的 Windows 继承传播，再检查结果。即使根 ACE 已存在，也不能跳过重新传播。

修复不向原账户递归授予 `WRITE_DAC`，不接管 Owner，不授予 FullControl，不执行 setup/runner、provisioning 或项目命令，不改机器账户、密码和网络规则。既有显式子项规则和元数据 deny 保留；独立受保护 DACL 不被强制重置，继承一致也不等于所有路径均有有效写权限。修复前拒绝重解析点、硬链接文件，以及可能因新父级授权改变优先顺序的普通子项继承 deny；普通只读检查不跟随链接。树检查最多 200,000 个条目，超限明确报错，不静默截断。修复失败可能已产生部分 ACL 变更，保持只读不等于回滚。父进程保持普通权限，工具仍需原有审批。

目录授权要求发布后的 TinyHarness 可执行文件位于全部沙箱写根之外，不能从 `dotnet TinyHarness.dll` 发起。子进程通过受访问限制的命名管道接收有界请求，双方核对 PID，子进程再核对原进程启动时间、可执行路径和账户 SID，并在持有目录及祖先句柄时核对目录身份。卷根、UNC、用户主目录或其祖先、系统安装目录，以及覆盖配置、会话存储或沙箱组件的目录均拒绝授权。UAC 使用其他管理员账户时，授权目标仍是原账户；原 DACL 或提升后的账户若仍不允许修改，保持只读，不接管所有权。

在 `chat` 空闲输入边界使用 `/trust` 可重新确认或切换，只由宿主处理，不形成模型回合。切换清除临时允许并废弃旧工具准备结果，仍须另行处理已有的中断检查要求。降为只读不会撤销已存在的 Windows ACE。拒绝或失败保持只读；提权子进程超时／取消后若结果未知，本进程禁止再次授权，须检查并重启后再试，不自动执行或重放此前失败的命令。

普通 ACL refresh 只传递当前存在的 `.git`、`.agents`、`.codex`、`.aws` deny-write 目标，保留缺失即跳过的语义，避免 helper 为缺失目标创建目录；策略身份版本现为 4（包含目标环境及 TCP 环回模式）。refresh 成功后另行只读检查有效写根的既有子项继承，发现缺口时在加载凭据、启动 runner 和目标命令之前阻断，报告具体路径并提示宿主 `/trust`，不自动 UAC 或重试。此授权修复只覆盖当前工作区，附加写根的异常需单独诊断。refresh 失败包含 cwd、setup、home 和日志位置，不能把失败等同于完全没有 ACL 副作用。

初版默认验证使用离线替身，未触发真实 UAC 或 ACL 写入；当时真实授权和 PriceLab 验收尚未执行。后续实机进展见下一段，不能据离线测试宣称原故障已在 Windows 上修复。

后续用户实机验收：原账户 UAC 已补授仅根目录的 `WRITE_DAC`，Owner 保持不变，重启自动恢复 `trusted` 和 Windows workspace-write 策略；这条成功路径通过。随后用户仅批准一次 `dotnet test PriceLab.slnx --no-restore`，日志确认 refresh `errors=[]`、runner 与 .NET SDK 10.0.401 已启动，但 MSB3491 报 `src/PriceLab/obj/Debug/net10.0` 临时文件写入拒绝，退出 1、未超时，测试未开始。只读 ACL 核对发现根有 TinyHarness 组及 capability ACE，既有子项未继承。本文新增的继承修复与执行前检查尚未进行真实 UAC 或 PriceLab 复测；跨管理员验收按用户要求不继续。此记录只覆盖沙箱验收，不替代 M14 模型行为评估。

继承修复的本轮工程验证：完整回归 **905 总／903 通过／2 项链接权限跳过／0 失败**（`artifacts/test-results/workspace-inheritance-regression-final.trx`）；随后补充的沙箱就绪前置检查与其余相关用例合计 **55/55**（`workspace-inheritance-acceptance-final.trx`）。此前一次完整运行仍触发原有日志体积差值断言波动（`workspace-inheritance-regression.trx`），未修改日志实现或预算。最新 `artifacts/workspace-inheritance-nativeaot/TinyHarness.exe` NativeAOT 发布无 trimming/AOT 警告，仓库外 `--help`、`chat --help`、非法授权通道拒绝及离线 smoke 通过（Completed／17 steps／16 toolExecs）。新版只读检查在原副本报告首个缺口 `workspace/AGENTS.md`，修复树预检查通过；本轮没有写实际 ACL、启动真实沙箱或调用真实模型，原副本仍仅两个已授权源码／测试文件与初始哈希不同。复验须换用新产物，并另行确认覆盖既有子项的目录授权；初版已授予的仅根权限不会自动完成本次修复。

2026-10-20 本地验证：相关测试 **115/115**；完整回归最终 **891 通过／2 项链接权限跳过／0 失败**（`artifacts/test-results/workspace-access-final.trx`），另一次完整运行曾触发未修改的 `SessionJournalScaleTests.AppendAndRecovery_MeetsLockedScaleBudgets` 体积差值断言失败，保留于 `workspace-access-regression.trx`，未修改预算或将其视为已修复。`win-x64` NativeAOT publish 无 trimming/AOT 警告；仓库外运行原生产物 `--help`、`chat --help` 和离线 smoke 通过（Completed／17 steps／16 toolExecs）。原测试副本与初始哈希相比仍仅有已授权的两个 PriceCalculator 文件变化，PriceLab 测试没有执行。

### sandbox status（严格只读）

```powershell
tinyharness sandbox status
```

打印 Enabled、policy、setup/runner/home 路径、本 build 期望的协议版本（严格模式 setup v5、TCP 环回模式 setup v6 / IPC v6），并运行就绪检查：setup/runner 组件文件、`.sandbox\setup_marker.json`（严格模式 version==5；TCP 环回模式 version==6 且模式字段匹配）、`cap_sid` 表与 `.sandbox-secrets\sandbox_users.json`。不创建任何目录或文件、不提权。退出码：未配置或就绪为 `0`；报告任何问题为 `1`。未配置 windowsSandbox 时打印启用指引。

### sandbox provision（显式机器级修改）

```powershell
tinyharness sandbox provision [--yes]
```

校验配置并构造 provisioning payload（路径缺失、非绝对或 payload base64 超过约 23000 字符都会在提权之前拒绝），打印机器级副作用清单并请求确认，然后以 UAC 运行 `codex-windows-sandbox-setup.exe`（TinyHarness 独立命名空间 fork 构建）一次（有界等待 10 分钟）。副作用包括：本地组 `TinyHarnessUsers`；本地账户 `TinyHarnessOffline`/`TinyHarnessOnline`（随机密码、永不过期）；注册表隐藏账户；防火墙规则与 offline 账户的 WFP BLOCK 过滤器；sandbox home 目录 ACL 与 write roots 授权。这套机器级命名与 Codex 发布版完全分栈（账户、组、防火墙、WFP、mutex、注册表互不共享），但共存行为尚未验收；同一 fork 命名空间内重复 provisioning 仍会重置账户密码、影响本机其他使用该命名空间的 home。没有卸载或回滚。

确认语义：默认交互确认（默认否）；`--yes` 跳过确认；非交互且无 `--yes` 直接报错退出、不提权。用户在 UAC 对话框取消、超时/取消（状态未知，请用 `sandbox status` 检查）、helper 非 0 退出（读取 `setup_error.json` 报告错误码与消息）均退出 `1`。退出 `0` 仅当 helper 成功且事后就绪检查通过。每次提权尝试（含拒绝、失败、超时/取消）向 `<sandboxHome>\.sandbox\tinyharness-provision.jsonl` 追加一行 JSON 审计（UTC 时间、outcome、退出码、mode、refreshOnly、payload SHA-256、截断的错误摘要；不含完整 payload 或凭据），写入尽力而为、失败仅告警。

### sandbox verify（opt-in 隔离验收）

```powershell
tinyharness sandbox verify [--yes] [--case <name>[,<name>...]] [--workspace <path>]
```

这是真实系统测试：在专用验收工作区（默认 `%TEMP%\TinyHarness\sandbox-verify-<时间戳>`，`--workspace` 可覆盖）内按固定矩阵执行真实沙箱命令，每条命令一次 ACL refresh（普通权限）与一个 runner 进程。前置条件：沙箱已配置、`enabled: true` 且就绪检查通过；否则拒绝执行并提示先 `sandbox provision`，绝不自动 provisioning、绝不回退宿主执行。普通 refresh 会准备验收目录 ACL，可能保存当前工作区 capability 映射；不自动配置账户、注册表或 Firewall/WFP。应在获得授权、可丢弃或受控的 Windows 环境执行。

当前固定矩阵 17 个用例（`--case` 按稳定名称选择子集，逗号分隔）：`identity-whoami`（沙箱内 whoami 为 offline 账户）、`deny-probe`（deny-read ACL 探针：退出 0=生效、20=隔离失效、21=探针配置错误）、`write-denied-outside-roots`（写根之外写入被拒）、`readonly-workspace-write-denied`（只读策略拒工作区写入）、`metadata-protected`（`.git` 等元数据目录只读）、`workspace-write-allowed`（写入正对照）、`network-denied`（宿主正对照、IPv4/IPv6 TCP 环回按模式门控、非环回 IPv4 阻断）、`network-udp`（UDP 请求回应阻断）、`network-dns`（IPv4/IPv6 TCP/UDP 53/853 固定端口阻断）、`exit-natural-23`、`exit-natural-192`（自然退出绝不判为超时）、`timeout-finite`（有限超时 192,true）、`terminate-active`（运行中取消）、`cancel-startup`（启动阶段取消）、`runner-broken-pipe`（runner 被外部击杀后不补造退出码）、`large-output`（大输出截断与完整 artifact）、`host-exit-reclaim`（宿主被 Kill 后无镜像幸存）。全矩阵有总期限；单用例失败记录后继续其余用例。

确认语义与 provision 一致：默认交互确认（默认否）；`--yes` 跳过；非交互且无 `--yes` 直接报错退出。退出码：`0` 仅当全部所选用例 PASS 且无 INDETERMINATE/SKIPPED；`4` 存在 FAIL、INDETERMINATE 或 SKIPPED；`1` 未配置/未启用/未就绪/组合失败；`2` 参数用法错误。控制台打印逐例结果表与工作区产物路径；完整报告（时间戳、机器与 OS、离线账户名、策略种类、组件路径/大小/mtime 与 marker/setup/IPC 版本、逐例判定、limitations 局限清单）写入 `<sandboxHome>\.sandbox\tinyharness-verify-<时间戳>.json`。验收报告仅作为“新二进制已通过最小测试”的依据，不构成完整隔离验收或与 Codex 共存已验证的证明。另有一个隐藏的 `sandbox verify-worker` 子命令供 host-exit 用例内部使用，不面向用户。

### 组件打包约定

两个日常组件 exe 文件名固定为 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`，来自 Codex 仓库 Windows 沙箱的 **TinyHarness 独立命名空间 fork** 构建（文件名、payload 字段、DPAPI、IPC 协议与上游一致；机器级账户/组/防火墙/WFP/桌面前缀为 TinyHarness 专属；验收另用 `codex-windows-managed-deny-probe.exe`；`codex-windows-sandbox-service.exe` 首期不使用、不安装服务）。TinyHarness 不安装、不搜索它们：用户把 exe 放在任意自选的绝对路径，并在 user-config.json 的 `settings.windowsSandbox` 配置 `setupExecutablePath`、`runnerExecutablePath`；`sandboxHome` 可省略，缺省使用独立固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`（与 Codex 的 home 完全独立），也可显式指定绝对路径。配置后运行 `sandbox provision`；`enabled: true` 后 `run` 才会使用沙箱。marker 或凭据中仍为旧 Codex 用户名（`CodexSandboxOffline`/`CodexSandboxOnline`）的 home 会被状态检查、执行与 provisioning 一律拒绝复用（fail closed），需换独立 home 重新 provisioning。组件 exe 没有 PE 版本资源（ProductVersion/FileVersion 为空），因此版本权威校验是三处 JSON 数值：payload `version==5`（严格模式）或 `6`（TCP 环回模式）、runner 帧协议 `version==6`（IPC_PROTOCOL_VERSION）、setup marker 版本/模式/账户匹配（严格模式 v5；TCP 环回模式 v6），加上 `cap_sid` 与 `sandbox_users.json` 的可解析检查；不匹配一律拒绝执行（fail closed，不回退宿主）。UAC 启动路径（`UseShellExecute=true`）无法使用环境分块通道，payload base64 超过约 23000 字符时 provision 直接拒绝并提示减少 `additionalWriteRoots`。技术细节见 [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)。

## TCP 环回兼容模式（2026-10-20）

以下先记录准备阶段的设计与当时限制；后续真实结果和当前工具边界见下一节。准备阶段“尚未执行”不否定后来的真实结果。

SDK 10.0.401 的 VSTest/testhost 使用 `127.0.0.1` TCP 通信。现有严格模式的 WFP 通用 TCP BLOCK 覆盖环回，与这一通信需求冲突。最新一次 PriceLab 实机结果已完成两个项目构建，但 testhost 连接等待 90 秒后中止；没有用例执行结果。当前没有此次 connect 的底层跟踪，不能把特定 WFP 命中或唯一根因写成已现场证明。

用户已明确授权准备可信设置 `settings.windowsSandbox.allowTcpLoopback: true`，缺省仍为 false。该模式允许 IPv4 `127.0.0.0/8` 和 IPv6 `::1` 的 TCP，包括宿主环回服务；不是仅允许同一沙箱内部 IPC。外部/LAN TCP、UDP、DNS 和 SMB 阻断继续保留。IPv4-mapped IPv6 不作为 IPv6 环回例外。`allow_local_binding` 仍为 false，proxy 端口仍为空。

两个模式采用不同账户及规则标识，避免安装新模式时改绑旧严格规则或轮换严格账户密码：

| 契约 | 严格模式 | TCP 环回模式 |
| --- | --- | --- |
| `allowTcpLoopback` | false | true |
| 账户 | TinyHarnessOffline / TinyHarnessOnline | TinyHarnessTcpOff / TinyHarnessTcpOn |
| 缺省 home（位于 `%LOCALAPPDATA%\tinyharness`） | windows-sandbox-home | windows-sandbox-tcp-loopback-home |
| setup payload / marker | v5 | v6，且 marker 记录 allow_tcp_loopback=true |
| runner IPC / 凭据 JSON 格式 | v6 / v5 | v6 / v5 |
| WFP filters | 原 16 个 key | 独立 17 个 key，IPv4 非环回分两段 |
| Firewall 规则名 | 原名称 | 原名称加 `_tcp_loopback` |

组仍为 TinyHarnessUsers；读写根和元数据保护契约不因网络选项扩大。新模式拒绝旧 helper/marker；provisioning 在提权前检查已有 marker 和凭据的模式/账户，Rust helper 在开始修改前也检查。同模式不同 home 仍共享该模式账户及密码，不能把这一设计解释为每个 home 独立账户。Rust 库和注册服务入口保持严格模式，本选项只支持现有 Legacy setup/runner 接入；没有安装服务。

策略身份版本为 4，绑定模式和相应 setup 版本；界面显示 `network: external-blocked, tcp-loopback: allowed (host services included), udp: blocked`。旧版 `network-denied` 除了非环回连接拒绝，还要求 IPv4 TCP 环回成功；当时不覆盖 IPv6、UDP/DNS。当前覆盖及未验证边界见下一节。

目标环境保留显式白名单，APPDATA/LOCALAPPDATA 分别位于声明 tempRoot 的 AppData/Roaming、AppData/Local，DOTNET_CLI_HOME 指向 tempRoot；ProgramFiles、ProgramFiles(x86)、ProgramData 仅从系统白名单复制。可信 extras 可覆盖，knownSecrets 最后移除。路径解析的离线回归不等于真实 NuGet/PriceLab 已通过。

准备阶段当时仅准备源代码、锁定依赖构建、离线回归、NativeAOT 和离线 smoke，没有切换实际设置或运行真实沙箱。当时待办是另行授权切换、新 home provisioning 和复验，不能直接复用旧 home。详见历史产物 `artifacts/sandbox-vstest-ipc/verification.md` 及 `manual-acceptance.md`。后续已取得的真实结果见下一节，当前不要求重复 provisioning 或 PriceLab。原有 M11 严格模式记录不能作为新模式完整验收证据。

## 网络验收工具改进与验证边界（2026-10-20）

此前真实结果保留：用户已完成 TCP 环回模式切换、provisioning 和聊天重启；PriceLab 一次 `dotnet test PriceLab.slnx --no-restore` 退出 0，汇总 5 通过，Explicit 收据用例跳过。旧版 verify 为 **15 PASS / 0 FAIL / 0 INDETERMINATE / 0 SKIPPED**，证据为 `artifacts/sandbox-vstest-ipc/real-verify-20261020-346ab5/`。其 IPv4 TCP 网络项缺少宿主非环回正对照，不能将 PASS 扩大为全部网络隔离已验证。旧报告及其中过时的 limitations 原样保存；本轮不重跑 PriceLab，不改写历史结果。

当前 C# 工具以同一有界探针对同一目标执行宿主前后正对照和沙箱通信，用随机 nonce 请求和匹配 ACK 确认回应。前置没有匹配 ACK 时不启动对应沙箱命令；后置失效时不接受阻断结论。报告 `networkChecks` 分开记录宿主前置、沙箱、宿主后置、退出码、外层超时、socket 错误及逐目标判定。报告使用唯一文件名和 CreateNew，不覆盖旧报告。

| 用例 | 明确监听目标 | 沙箱期望 |
| --- | --- | --- |
| network-denied | 127.0.0.1、::1、一个本机非环回 IPv4；TCP 独占随机端口 | 严格模式全部阻断；TCP 环回模式前两个须匹配 ACK，非环回须阻断 |
| network-udp | 同上三个地址；UDP 独占随机端口 | 两种模式均不得收到匹配 ACK；发送成功本身不能证明可达 |
| network-dns | 127.0.0.1、::1；分别独占 TCP/UDP 53 和 853 | 两种模式均阻断，包括 TCP 环回模式 |

53/853 的协议范围来自实际 fork `windows-sandbox-rs/src/wfp/filter_specs.rs` 的 `User + RemotePort` 条件，没有 TCP/UDP 限定。这里使用自定义 ACK 检查固定端口可达性，不执行 DNS/TLS 协议；不覆盖非环回 IPv6、远端公网/LAN 服务、Windows 系统解析器或 DNS Client 服务代发、缓存、DoH、完整 DNS 泄漏防护、ICMP 或 SMB。本机路径失败也不能唯一归因于 WFP。

地址不可用、IPv6 不支持、固定端口占用、宿主没有匹配 ACK 均记 INDETERMINATE，不停系统服务、不接管已有服务、不改系统 DNS，不退到高端口宣称 DNS 已验证。探针异常、非零退出、错误 nonce、无效/缺字段/截断 JSON、协议失败及 Harness 外层超时均不计为隔离成功。探针内部 3 秒等待单独报告；UDP 发送但没有匹配回应，只有宿主前后对照成功且监听健康时才满足该路径的阻断观测。

CLI 确认页说明监听范围；每目标在绑定前显示地址和请求端口（0=随机），绑定后、通信前显示实际端口。明确地址仍可能触发防火墙提示，用户选择可能保存规则；工具不自动创建/删除 Firewall/WFP 规则，不要求开放公用网络。普通 refresh 会准备验收目录 ACL，可能保存当前工作区 capability 映射，报告写入现有 home 的 `.sandbox`。此前弹窗后生成的两条规则已按单独授权清理，记录 `artifacts/sandbox-vstest-ipc/firewall-cleanup-20261020-adb83e.json`；该授权不延续到新复验。

本轮仅实施和离线验证，新探针真实专项复验尚未执行。NativeAOT 产物、哈希、离线证据和待授权命令见 `artifacts/sandbox-network-acceptance-20261020/verification.md`。默认网络测试全部使用 fake backend、fake responder 和宿主通信替身，不启动真实网络监听或沙箱，不触发 UAC 或配置机器规则。未修改 Rust fork，不推进 M14/M15。

### 同日后续：新探针真实专项

工程交付后，用户另行明确授权运行一次 `sandbox verify --yes --case network-denied,network-udp,network-dns`。结果 **3 PASS / 0 FAIL / 0 INDETERMINATE / 0 SKIPPED，退出 0**；14/14 个目标通过，28 个宿主前后匹配 ACK 均核对了 nonce 和目标一致。IPv4/IPv6 TCP 环回成功；其余 TCP 目标为 AccessDenied/10013；7 个 UDP 目标均发送 API 成功但内部 3 秒内无匹配 ACK，宿主前后对照成功，无 Harness 外层超时。真实报告与审计见 `artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/`，原报告逐字节保存，没有重跑 PriceLab 或完整矩阵。

本次非环回目标实际是 Meta 接口 `198.18.0.1`，不代表物理 LAN `192.168.8.248`、远端公网或非环回 IPv6 已验证；53/853 仍只证明直接端口通信观测，不证明系统 DNS 解析器/服务代发或完整泄漏防护。用户确认网络授权后，新 exe 从 0 条应用规则变为两条持久 Public / Inbound / Allow 规则（TCP、UDP 各一条，端口/地址均 Any）。只读核对并记录于 `firewall-after.json`、`firewall-persistent.json`，本 chat 未代点弹窗、未执行配置规则命令，也未自行删除规则。实际用户配置和受保护的 1,055 个既有文件哈希不变。旧 PriceLab 5 通过、旧矩阵 15/15 与此次专项分别计数，不宣称 M14 或全部沙箱管理完成。

后续规则清理：用户另行明确授权删除上述两条新 exe 的应用规则；普通权限删除被拒后，通过一次精确 UAC 清理完成。清理后的独立只读复核确认 ActiveStore 与 PersistentStore 中该应用目标规则均为 0，未操作原有 TinyHarness 沙箱规则。证据：`artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/firewall-cleanup-uac-result.json`、`firewall-cleanup-independent-check.json`。这项授权已完成，不表示可以在未来复验时继续修改规则。当前进度和后续边界见[接力入口](current-progress.md)。

## M11 验收记录

M11 Windows 沙箱（2026-10-10，M11.1–M11.4）：M11.4 交付 `sandbox status`/`sandbox provision` 管理入口（见上文“Windows 沙箱管理命令”）。默认测试 **532/532 通过**（新增 28 个管理入口测试，全部离线，不启动真实 setup.exe、不触发 UAC）；`TinyHarness.Cli` 构建 0 警告；`win-x64` NativeAOT publish 无 trimming/AOT warning；发布产物从仓库外通过 `--help`、`sandbox status`（只读，本机未配置 windowsSandbox 如实报告并退出 0）、离线 `doctor` 与既有 smoke（Completed/17 steps/16 toolExecs），全程无 UAC、无机器修改；`sandbox provision --yes` 在未配置时于提权前拒绝（exit 1）。隔离验收（M11.5）未开始，完成前不宣称沙箱可用。

M11.5 验收工具（2026-10-10）：交付 `sandbox verify` 隔离验收入口（见上文“Windows 沙箱管理命令”）、Core 验收运行器（15 用例固定矩阵、总期限、报告落盘与进程存活扫描）与 **34 项新增离线测试**（fake backend/脚本化判定，不启动真实 setup/runner、不修改机器）。验证：`dotnet build TinyHarness.slnx --no-incremental` 0 警告 0 错误；`dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj` **568 通过 / 0 失败 / 0 跳过**；`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true`（仓库外 `-o`）NativeAOT 0 trimming/AOT 警告；仓库外原生产物 `TinyHarness.exe` smoke：`--help` 与 `sandbox --help` 含 verify 用法、`sandbox verify`（无 `--yes`、无配置）exit 1 且输出「用户配置未设置 settings.windowsSandbox」、`sandbox verify-worker` 缺参 exit 2。边界声明：以上离线验证只覆盖验收工具自身逻辑（fake backend/脚本化判定）；真实隔离验收（provision 后执行 `sandbox verify`）尚未执行，M11.5 完成前不宣称沙箱可用。

M11.5 真实隔离验收（2026-10-10，用户授权，本机 Windows 11 + TinyHarness 独立命名空间 fork 组件执行）：user-config 配置 `settings.windowsSandbox`（组件指向 fork 构建的 `codex-windows-sandbox-setup.exe`/`codex-command-runner.exe`，home 使用固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`）→ `sandbox provision --yes` 一次通过（UAC 建组 `TinyHarnessUsers`、账户 `TinyHarnessOffline`/`TinyHarnessOnline`、防火墙/WFP/ACL，事后检查就绪）→ `sandbox verify --yes` 全矩阵 **14 PASS / 1 FAIL**（报告：`<home>\.sandbox\tinyharness-verify-20261010-133539.json`）。**通过**：身份（whoami=TinyHarnessOffline）、deny-read 探针（deny-probe exit 0）、写根外写入拒绝、只读策略拒工作区写入、`.git` 元数据保护、工作区写入正对照、自然退出 23/192、有限超时（192,true）、运行中取消（OCE）、启动取消、runner 外部击杀不补造退出码、约 4MB 大输出截断与 artifact、宿主 Kill 后遗弃回收。**FAIL（如实记录）**：`network-denied`——沙箱内 curl 对 `127.0.0.1` 与本机 LAN IP 的 TCP 连接均建立成功并收到 HTTP 应答（exit 0，按契约判隔离失效）；防火墙 BLOCK 规则（`tinyharness_sandbox_offline_*`，按用户作用域 LocalUserAuthorizedList）已安装且启用但未对沙箱账户进程生效，或本机 IP 连接走 Windows 环回快速路径绕过按远程地址匹配的规则——两者该用例无法区分，已记入报告 limitations；WFP 过滤器仅覆盖 ICMP/DNS/SMB，通用 TCP/UDP 断网完全依赖上述防火墙规则。**结论边界**：文件读写/元数据隔离与进程生命周期语义已在本机真实验证；断网未验证生效，网络隔离修复并复验通过前不宣称沙箱完整可用；与 Codex 发布版的共存未验收。真实验收还修复了三个此前离线测试无法暴露的缺陷：`ConvertStringSecurityDescriptorToSecurityDescriptorW` P/Invoke 缺第 4 个可选参数（首跑即原生 0xC0000005 崩溃）、`STARTUPINFOW` 结构布局错误（7 字段截断且偏移不符）、capability SID 的 cwd 契约（写根==命令 cwd 时必须用 `workspace_by_cwd` 表的 SID——setup 刷到目录 ACL 的正是它，C# 误用 `writable_root_by_path` 导致 workspace-write 写拒绝）；另修正三处验收 fixture 自身问题（cmd /c 载荷嵌套 CRT 转义引号在宿主与沙箱同样不可解析、waitfor 依赖 SMB mailslot 被断网过滤秒退改用 powershell sleeper、对照监听改回合法 HTTP/1.0 使 curl exit 0 语义无歧义）。回归门禁：默认测试 **576/576**（新增 cap SID 双表契约等测试）、`win-x64` NativeAOT publish 0 警告、仓库外原生产物 smoke 通过。

M11.5 断网修复与全矩阵闭环（2026-10-10 晚）：fork 侧新增按账户 SID 的 WFP 通用 BLOCK 过滤器（`tinyharness_wfp_tcp_connect_v4/v6`、`tinyharness_wfp_udp_connect_v4/v6`，ALE_AUTH_CONNECT 层，条件=账户 SID + 协议；内核强制，不依赖防火墙归因与远程地址匹配，覆盖环回），setup/runner 重建（SETUP_VERSION=5 / IPC v6 不变，C# 接入层零修改）并经显式 UAC 重新 provisioning 安装规则。期间实证了已记录的跨 home 危害：Rust 侧临时验收 home 先行 provisioning 轮换了共享账户密码，导致 TinyHarness 默认 home 的 DPAPI 凭据失效（`sandbox verify` 启动 runner 即报凭据错误）；随后用项目正式 `sandbox provision --yes` 在默认 home 重建一致性（`TinyHarnessOffline`/`TinyHarnessOnline` 密码再次轮换并同步）。修复后先复验 `network-denied` 单用例 PASS（沙箱内对 `192.168.8.248` 与 `127.0.0.1` 的 TCP 连接均立即失败，curl exit 7；报告 `tinyharness-verify-20261010-225744.json`），再重跑全矩阵 **15/15 PASS（exit 0，报告 `tinyharness-verify-20261010-232828.json`）**——M11.5 隔离验收在本机闭环：身份、deny-read/deny-write、元数据保护、工作区写入、断网、退出码/超时/取消语义、断管道、大输出、宿主遗弃回收全部真实验证通过。边界：验证覆盖本机单环境；与 Codex 发布版共存未验收；Rust 临时验收 home 仍留过期凭据（不可复用，需先重新 provisioning）。
