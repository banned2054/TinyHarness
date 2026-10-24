# Windows 沙箱管理与验收边界

> 已完成实现的文档归档（2026-10-24）。保留设计、使用边界和相关证据供维护时查阅；归档不撤销仍有效的协议、安全、权限和 NativeAOT 约束，也不代表对后续修改的授权。当前开发范围以[活动计划](../../PLAN.md)和实际源码为准。

本文维护 `sandbox` 命令、目录授权、网络模式与验收工具的当前说明。各轮工程、M11、PriceLab 和网络专项结果已迁至[验收归档](windows-sandbox-verification.md)，接入职责见[Windows 沙箱接入说明](windows-sandbox-integration-guide.md)。

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

目录授权、继承修复与 PriceLab 的各轮工程和实机结果见[验收归档](windows-sandbox-verification.md#工作区目录授权与继承修复)。不同版本的通过、失败和未复测范围分别保留，不能据离线测试扩大真实权限结论。

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

两个日常组件 exe 文件名固定为 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`，来自 Codex 仓库 Windows 沙箱的 **TinyHarness 独立命名空间 fork** 构建（文件名、payload 字段、DPAPI、IPC 协议与上游一致；机器级账户/组/防火墙/WFP/桌面前缀为 TinyHarness 专属；验收另用 `codex-windows-managed-deny-probe.exe`；`codex-windows-sandbox-service.exe` 首期不使用、不安装服务）。TinyHarness 不安装、不搜索它们：用户把 exe 放在任意自选的绝对路径，并在 user-config.json 的 `settings.windowsSandbox` 配置 `setupExecutablePath`、`runnerExecutablePath`；`sandboxHome` 可省略，缺省使用独立固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`（与 Codex 的 home 完全独立），也可显式指定绝对路径。配置后运行 `sandbox provision`；`enabled: true` 后 `run` 才会使用沙箱。marker 或凭据中仍为旧 Codex 用户名（`CodexSandboxOffline`/`CodexSandboxOnline`）的 home 会被状态检查、执行与 provisioning 一律拒绝复用（fail closed），需换独立 home 重新 provisioning。组件 exe 没有 PE 版本资源（ProductVersion/FileVersion 为空），因此版本权威校验是三处 JSON 数值：payload `version==5`（严格模式）或 `6`（TCP 环回模式）、runner 帧协议 `version==6`（IPC_PROTOCOL_VERSION）、setup marker 版本/模式/账户匹配（严格模式 v5；TCP 环回模式 v6），加上 `cap_sid` 与 `sandbox_users.json` 的可解析检查；不匹配一律拒绝执行（fail closed，不回退宿主）。UAC 启动路径（`UseShellExecute=true`）无法使用环境分块通道，payload base64 超过约 23000 字符时 provision 直接拒绝并提示减少 `additionalWriteRoots`。技术细节见 [Windows 沙箱接入方案](windows-sandbox-integration-guide.md)。

## TCP 环回兼容模式

可信用户设置使用 `settings.windowsSandbox.allowTcpLoopback: true`，缺省仍为 false。该模式允许 IPv4 `127.0.0.0/8` 和 IPv6 `::1` 的 TCP，包括宿主环回服务；不是仅允许同一沙箱内部 IPC。外部/LAN TCP、UDP、DNS 和 SMB 阻断继续保留。IPv4-mapped IPv6 不作为 IPv6 环回例外。`allow_local_binding` 仍为 false，proxy 端口仍为空。

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

策略身份版本为 4，绑定模式和相应 setup 版本；界面显示 `network: external-blocked, tcp-loopback: allowed (host services included), udp: blocked`。当前网络探针覆盖及限制见下一节；旧工具覆盖范围见验收归档。

目标环境保留显式白名单，APPDATA/LOCALAPPDATA 分别位于声明 tempRoot 的 AppData/Roaming、AppData/Local，DOTNET_CLI_HOME 指向 tempRoot；ProgramFiles、ProgramFiles(x86)、ProgramData 仅从系统白名单复制。可信 extras 可覆盖，knownSecrets 最后移除。路径解析的离线回归不等于真实 NuGet/PriceLab 已通过。

## 网络验收工具与验证边界

当前 C# 工具以同一有界探针对同一目标执行宿主前后正对照和沙箱通信，用随机 nonce 请求和匹配 ACK 确认回应。前置没有匹配 ACK 时不启动对应沙箱命令；后置失效时不接受阻断结论。报告 `networkChecks` 分开记录宿主前置、沙箱、宿主后置、退出码、外层超时、socket 错误及逐目标判定。报告使用唯一文件名和 CreateNew，不覆盖旧报告。

| 用例 | 明确监听目标 | 沙箱期望 |
| --- | --- | --- |
| network-denied | 127.0.0.1、::1、一个本机非环回 IPv4；TCP 独占随机端口 | 严格模式全部阻断；TCP 环回模式前两个须匹配 ACK，非环回须阻断 |
| network-udp | 同上三个地址；UDP 独占随机端口 | 两种模式均不得收到匹配 ACK；发送成功本身不能证明可达 |
| network-dns | 127.0.0.1、::1；分别独占 TCP/UDP 53 和 853 | 两种模式均阻断，包括 TCP 环回模式 |

53/853 的协议范围来自实际 fork `windows-sandbox-rs/src/wfp/filter_specs.rs` 的 `User + RemotePort` 条件，没有 TCP/UDP 限定。这里使用自定义 ACK 检查固定端口可达性，不执行 DNS/TLS 协议；不覆盖非环回 IPv6、远端公网/LAN 服务、Windows 系统解析器或 DNS Client 服务代发、缓存、DoH、完整 DNS 泄漏防护、ICMP 或 SMB。本机路径失败也不能唯一归因于 WFP。

地址不可用、IPv6 不支持、固定端口占用、宿主没有匹配 ACK 均记 INDETERMINATE，不停系统服务、不接管已有服务、不改系统 DNS，不退到高端口宣称 DNS 已验证。探针异常、非零退出、错误 nonce、无效/缺字段/截断 JSON、协议失败及 Harness 外层超时均不计为隔离成功。探针内部 3 秒等待单独报告；UDP 发送但没有匹配回应，只有宿主前后对照成功且监听健康时才满足该路径的阻断观测。

CLI 确认页说明监听范围；每目标在绑定前显示地址和请求端口（0=随机），绑定后、通信前显示实际端口。明确地址仍可能触发防火墙提示，用户选择可能保存规则；工具不自动创建/删除 Firewall/WFP 规则，不要求开放公用网络。普通 refresh 会准备验收目录 ACL，可能保存当前工作区 capability 映射，报告写入现有 home 的 `.sandbox`。历史应用规则创建与清理见[验收归档](windows-sandbox-verification.md)；历史清理授权不延续到新复验。

## 历史验收

[M11、目录授权和网络专项记录](windows-sandbox-verification.md)分别保存各轮通过、失败、跳过、实机目标与未验证范围；不能把旧矩阵与后续专项相加，或把本机结果外推为全面隔离保证。
