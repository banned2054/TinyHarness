# Windows 沙箱管理与验收记录

本页内容自中文 README 精简迁出（2026-10-13）：`sandbox` 命令的使用细节与 M11 验收记录在此维护，接入设计见 [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)。

## Windows 沙箱管理命令

`tinyharness sandbox` 是 Windows 工具进程沙箱的显式管理入口，只读取用户配置中的可信设置 `settings.windowsSandbox`，不发送任何输入给模型。普通入口（`run`、`smoke`、`doctor`、`mcp`）不会触发 provisioning、账户修复或 UAC；机器级修改只能通过 `sandbox provision` 显式发生。

### sandbox status（严格只读）

```powershell
tinyharness sandbox status
```

打印 Enabled、policy、setup/runner/home 路径、本 build 期望的协议版本（setup v5 / IPC v6），并运行就绪检查：setup/runner 组件文件、`.sandbox\setup_marker.json`（version==5）、`cap_sid` 表与 `.sandbox-secrets\sandbox_users.json`。不创建任何目录或文件、不提权。退出码：未配置或就绪为 `0`；报告任何问题为 `1`。未配置 windowsSandbox 时打印启用指引。

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

这是真实系统测试：在专用验收工作区（默认 `%TEMP%\TinyHarness\sandbox-verify-<时间戳>`，`--workspace` 可覆盖）内按固定矩阵执行真实沙箱命令，每条命令一次 ACL refresh（普通权限）与一个 runner 进程。前置条件：沙箱已配置、`enabled: true` 且就绪检查通过；否则拒绝执行并提示先 `sandbox provision`，绝不自动 provisioning、绝不回退宿主执行，本命令自身也不修改机器配置（账户/ACL/注册表/防火墙）。应在获得授权、可丢弃或受控的 Windows 环境执行。

固定矩阵 15 个用例（`--case` 按稳定名称选择子集，逗号分隔）：`identity-whoami`（沙箱内 whoami 为 offline 账户）、`deny-probe`（deny-read ACL 探针：退出 0=生效、20=隔离失效、21=探针配置错误）、`write-denied-outside-roots`（写根之外写入被拒）、`readonly-workspace-write-denied`（只读策略拒工作区写入）、`metadata-protected`（`.git` 等元数据目录只读）、`workspace-write-allowed`（写入正对照）、`network-denied`（非回环连接被阻为门控，回环仅作观测）、`exit-natural-23`、`exit-natural-192`（自然退出绝不判为超时）、`timeout-finite`（有限超时 192,true）、`terminate-active`（运行中取消）、`cancel-startup`（启动阶段取消）、`runner-broken-pipe`（runner 被外部击杀后不补造退出码）、`large-output`（大输出截断与完整 artifact）、`host-exit-reclaim`（宿主被 Kill 后无镜像幸存）。全矩阵有总期限；单用例失败记录后继续其余用例。

确认语义与 provision 一致：默认交互确认（默认否）；`--yes` 跳过；非交互且无 `--yes` 直接报错退出。退出码：`0` 仅当全部所选用例 PASS 且无 INDETERMINATE/SKIPPED；`4` 存在 FAIL、INDETERMINATE 或 SKIPPED；`1` 未配置/未启用/未就绪/组合失败；`2` 参数用法错误。控制台打印逐例结果表与工作区产物路径；完整报告（时间戳、机器与 OS、离线账户名、策略种类、组件路径/大小/mtime 与 marker/setup/IPC 版本、逐例判定、limitations 局限清单）写入 `<sandboxHome>\.sandbox\tinyharness-verify-<时间戳>.json`。验收报告仅作为“新二进制已通过最小测试”的依据，不构成完整隔离验收或与 Codex 共存已验证的证明。另有一个隐藏的 `sandbox verify-worker` 子命令供 host-exit 用例内部使用，不面向用户。

### 组件打包约定

两个日常组件 exe 文件名固定为 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`，来自 Codex 仓库 Windows 沙箱的 **TinyHarness 独立命名空间 fork** 构建（文件名、payload 字段、DPAPI、IPC 协议与上游一致；机器级账户/组/防火墙/WFP/桌面前缀为 TinyHarness 专属；验收另用 `codex-windows-managed-deny-probe.exe`；`codex-windows-sandbox-service.exe` 首期不使用、不安装服务）。TinyHarness 不安装、不搜索它们：用户把 exe 放在任意自选的绝对路径，并在 user-config.json 的 `settings.windowsSandbox` 配置 `setupExecutablePath`、`runnerExecutablePath`；`sandboxHome` 可省略，缺省使用独立固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`（与 Codex 的 home 完全独立），也可显式指定绝对路径。配置后运行 `sandbox provision`；`enabled: true` 后 `run` 才会使用沙箱。marker 或凭据中仍为旧 Codex 用户名（`CodexSandboxOffline`/`CodexSandboxOnline`）的 home 会被状态检查、执行与 provisioning 一律拒绝复用（fail closed），需换独立 home 重新 provisioning。组件 exe 没有 PE 版本资源（ProductVersion/FileVersion 为空），因此版本权威校验是三处 JSON 数值：payload `version==5`（SETUP_VERSION）、runner 帧协议 `version==6`（IPC_PROTOCOL_VERSION）、setup marker `version==5`，加上 `cap_sid` 与 `sandbox_users.json` 的可解析检查；不匹配一律拒绝执行（fail closed，不回退宿主）。UAC 启动路径（`UseShellExecute=true`）无法使用环境分块通道，payload base64 超过约 23000 字符时 provision 直接拒绝并提示减少 `additionalWriteRoots`。技术细节见 [Windows 沙箱接入方案](windows-sandbox-dotnet-integration.md)。

## M11 验收记录

M11 Windows 沙箱（2026-10-10，M11.1–M11.4）：M11.4 交付 `sandbox status`/`sandbox provision` 管理入口（见上文“Windows 沙箱管理命令”）。默认测试 **532/532 通过**（新增 28 个管理入口测试，全部离线，不启动真实 setup.exe、不触发 UAC）；`TinyHarness.Cli` 构建 0 警告；`win-x64` NativeAOT publish 无 trimming/AOT warning；发布产物从仓库外通过 `--help`、`sandbox status`（只读，本机未配置 windowsSandbox 如实报告并退出 0）、离线 `doctor` 与既有 smoke（Completed/17 steps/16 toolExecs），全程无 UAC、无机器修改；`sandbox provision --yes` 在未配置时于提权前拒绝（exit 1）。隔离验收（M11.5）未开始，完成前不宣称沙箱可用。

M11.5 验收工具（2026-10-10）：交付 `sandbox verify` 隔离验收入口（见上文“Windows 沙箱管理命令”）、Core 验收运行器（15 用例固定矩阵、总期限、报告落盘与进程存活扫描）与 **34 项新增离线测试**（fake backend/脚本化判定，不启动真实 setup/runner、不修改机器）。验证：`dotnet build TinyHarness.slnx --no-incremental` 0 警告 0 错误；`dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj` **568 通过 / 0 失败 / 0 跳过**；`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true`（仓库外 `-o`）NativeAOT 0 trimming/AOT 警告；仓库外原生产物 `TinyHarness.exe` smoke：`--help` 与 `sandbox --help` 含 verify 用法、`sandbox verify`（无 `--yes`、无配置）exit 1 且输出「用户配置未设置 settings.windowsSandbox」、`sandbox verify-worker` 缺参 exit 2。边界声明：以上离线验证只覆盖验收工具自身逻辑（fake backend/脚本化判定）；真实隔离验收（provision 后执行 `sandbox verify`）尚未执行，M11.5 完成前不宣称沙箱可用。

M11.5 真实隔离验收（2026-10-10，用户授权，本机 Windows 11 + TinyHarness 独立命名空间 fork 组件执行）：user-config 配置 `settings.windowsSandbox`（组件指向 fork 构建的 `codex-windows-sandbox-setup.exe`/`codex-command-runner.exe`，home 使用固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`）→ `sandbox provision --yes` 一次通过（UAC 建组 `TinyHarnessUsers`、账户 `TinyHarnessOffline`/`TinyHarnessOnline`、防火墙/WFP/ACL，事后检查就绪）→ `sandbox verify --yes` 全矩阵 **14 PASS / 1 FAIL**（报告：`<home>\.sandbox\tinyharness-verify-20261010-133539.json`）。**通过**：身份（whoami=TinyHarnessOffline）、deny-read 探针（deny-probe exit 0）、写根外写入拒绝、只读策略拒工作区写入、`.git` 元数据保护、工作区写入正对照、自然退出 23/192、有限超时（192,true）、运行中取消（OCE）、启动取消、runner 外部击杀不补造退出码、约 4MB 大输出截断与 artifact、宿主 Kill 后遗弃回收。**FAIL（如实记录）**：`network-denied`——沙箱内 curl 对 `127.0.0.1` 与本机 LAN IP 的 TCP 连接均建立成功并收到 HTTP 应答（exit 0，按契约判隔离失效）；防火墙 BLOCK 规则（`tinyharness_sandbox_offline_*`，按用户作用域 LocalUserAuthorizedList）已安装且启用但未对沙箱账户进程生效，或本机 IP 连接走 Windows 环回快速路径绕过按远程地址匹配的规则——两者该用例无法区分，已记入报告 limitations；WFP 过滤器仅覆盖 ICMP/DNS/SMB，通用 TCP/UDP 断网完全依赖上述防火墙规则。**结论边界**：文件读写/元数据隔离与进程生命周期语义已在本机真实验证；断网未验证生效，网络隔离修复并复验通过前不宣称沙箱完整可用；与 Codex 发布版的共存未验收。真实验收还修复了三个此前离线测试无法暴露的缺陷：`ConvertStringSecurityDescriptorToSecurityDescriptorW` P/Invoke 缺第 4 个可选参数（首跑即原生 0xC0000005 崩溃）、`STARTUPINFOW` 结构布局错误（7 字段截断且偏移不符）、capability SID 的 cwd 契约（写根==命令 cwd 时必须用 `workspace_by_cwd` 表的 SID——setup 刷到目录 ACL 的正是它，C# 误用 `writable_root_by_path` 导致 workspace-write 写拒绝）；另修正三处验收 fixture 自身问题（cmd /c 载荷嵌套 CRT 转义引号在宿主与沙箱同样不可解析、waitfor 依赖 SMB mailslot 被断网过滤秒退改用 powershell sleeper、对照监听改回合法 HTTP/1.0 使 curl exit 0 语义无歧义）。回归门禁：默认测试 **576/576**（新增 cap SID 双表契约等测试）、`win-x64` NativeAOT publish 0 警告、仓库外原生产物 smoke 通过。

M11.5 断网修复与全矩阵闭环（2026-10-10 晚）：fork 侧新增按账户 SID 的 WFP 通用 BLOCK 过滤器（`tinyharness_wfp_tcp_connect_v4/v6`、`tinyharness_wfp_udp_connect_v4/v6`，ALE_AUTH_CONNECT 层，条件=账户 SID + 协议；内核强制，不依赖防火墙归因与远程地址匹配，覆盖环回），setup/runner 重建（SETUP_VERSION=5 / IPC v6 不变，C# 接入层零修改）并经显式 UAC 重新 provisioning 安装规则。期间实证了已记录的跨 home 危害：Rust 侧临时验收 home 先行 provisioning 轮换了共享账户密码，导致 TinyHarness 默认 home 的 DPAPI 凭据失效（`sandbox verify` 启动 runner 即报凭据错误）；随后用项目正式 `sandbox provision --yes` 在默认 home 重建一致性（`TinyHarnessOffline`/`TinyHarnessOnline` 密码再次轮换并同步）。修复后先复验 `network-denied` 单用例 PASS（沙箱内对 `192.168.8.248` 与 `127.0.0.1` 的 TCP 连接均立即失败，curl exit 7；报告 `tinyharness-verify-20261010-225744.json`），再重跑全矩阵 **15/15 PASS（exit 0，报告 `tinyharness-verify-20261010-232828.json`）**——M11.5 隔离验收在本机闭环：身份、deny-read/deny-write、元数据保护、工作区写入、断网、退出码/超时/取消语义、断管道、大输出、宿主遗弃回收全部真实验证通过。边界：验证覆盖本机单环境；与 Codex 发布版共存未验收；Rust 临时验收 home 仍留过期凭据（不可复用，需先重新 provisioning）。
