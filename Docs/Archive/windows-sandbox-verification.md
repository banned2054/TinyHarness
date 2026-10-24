# Windows 沙箱工程与实机验收记录

归档日期：2026-10-24。来源：[windows-sandbox-management.md](windows-sandbox-management.md)。

> 本页保存原设计阶段的核查、实施或验收记录，章节编号沿用原文；未收录的契约章节见来源文档。文中的「当前」「本轮」「尚未完成」等表述只指各条记录的历史时点，不是当前状态或操作授权。历史正文不持续更新；链接随目录整理调整。原始本机证据可能位于未纳入 Git 的 `artifacts/`，其他机器不一定具备。

## 工作区目录授权与继承修复

初版默认验证使用离线替身，未触发真实 UAC 或 ACL 写入；当时真实授权和 PriceLab 验收尚未执行。后续实机进展见下一段，不能据离线测试宣称原故障已在 Windows 上修复。

后续用户实机验收：原账户 UAC 已补授仅根目录的 `WRITE_DAC`，Owner 保持不变，重启自动恢复 `trusted` 和 Windows workspace-write 策略；这条成功路径通过。随后用户仅批准一次 `dotnet test PriceLab.slnx --no-restore`，日志确认 refresh `errors=[]`、runner 与 .NET SDK 10.0.401 已启动，但 MSB3491 报 `src/PriceLab/obj/Debug/net10.0` 临时文件写入拒绝，退出 1、未超时，测试未开始。只读 ACL 核对发现根有 TinyHarness 组及 capability ACE，既有子项未继承。本文新增的继承修复与执行前检查尚未进行真实 UAC 或 PriceLab 复测；跨管理员验收按用户要求不继续。此记录只覆盖沙箱验收，不替代 M14 模型行为评估。

继承修复的本轮工程验证：完整回归 **905 总／903 通过／2 项链接权限跳过／0 失败**（`artifacts/test-results/workspace-inheritance-regression-final.trx`）；随后补充的沙箱就绪前置检查与其余相关用例合计 **55/55**（`workspace-inheritance-acceptance-final.trx`）。此前一次完整运行仍触发原有日志体积差值断言波动（`workspace-inheritance-regression.trx`），未修改日志实现或预算。最新 `artifacts/workspace-inheritance-nativeaot/TinyHarness.exe` NativeAOT 发布无 trimming/AOT 警告，仓库外 `--help`、`chat --help`、非法授权通道拒绝及离线 smoke 通过（Completed／17 steps／16 toolExecs）。新版只读检查在原副本报告首个缺口 `workspace/AGENTS.md`，修复树预检查通过；本轮没有写实际 ACL、启动真实沙箱或调用真实模型，原副本仍仅两个已授权源码／测试文件与初始哈希不同。复验须换用新产物，并另行确认覆盖既有子项的目录授权；初版已授予的仅根权限不会自动完成本次修复。

2026-10-20 本地验证：相关测试 **115/115**；完整回归最终 **891 通过／2 项链接权限跳过／0 失败**（`artifacts/test-results/workspace-access-final.trx`），另一次完整运行曾触发未修改的 `SessionJournalScaleTests.AppendAndRecovery_MeetsLockedScaleBudgets` 体积差值断言失败，保留于 `workspace-access-regression.trx`，未修改预算或将其视为已修复。`win-x64` NativeAOT publish 无 trimming/AOT 警告；仓库外运行原生产物 `--help`、`chat --help` 和离线 smoke 通过（Completed／17 steps／16 toolExecs）。原测试副本与初始哈希相比仍仅有已授权的两个 PriceCalculator 文件变化，PriceLab 测试没有执行。

## TCP 环回模式准备阶段

以下先记录准备阶段的设计与当时限制；后续真实结果和当前工具边界见下一节。准备阶段“尚未执行”不否定后来的真实结果。

SDK 10.0.401 的 VSTest/testhost 使用 `127.0.0.1` TCP 通信。现有严格模式的 WFP 通用 TCP BLOCK 覆盖环回，与这一通信需求冲突。最新一次 PriceLab 实机结果已完成两个项目构建，但 testhost 连接等待 90 秒后中止；没有用例执行结果。当前没有此次 connect 的底层跟踪，不能把特定 WFP 命中或唯一根因写成已现场证明。

准备阶段当时仅准备源代码、锁定依赖构建、离线回归、NativeAOT 和离线 smoke，没有切换实际设置或运行真实沙箱。当时待办是另行授权切换、新 home provisioning 和复验，不能直接复用旧 home。详见历史产物 `artifacts/sandbox-vstest-ipc/verification.md` 及 `manual-acceptance.md`。后续已取得的真实结果见下一节，当前不要求重复 provisioning 或 PriceLab。原有 M11 严格模式记录不能作为新模式完整验收证据。

## 网络验收工具改进与验证边界（2026-10-20）

此前真实结果保留：用户已完成 TCP 环回模式切换、provisioning 和聊天重启；PriceLab 一次 `dotnet test PriceLab.slnx --no-restore` 退出 0，汇总 5 通过，Explicit 收据用例跳过。旧版 verify 为 **15 PASS / 0 FAIL / 0 INDETERMINATE / 0 SKIPPED**，证据为 `artifacts/sandbox-vstest-ipc/real-verify-20261020-346ab5/`。其 IPv4 TCP 网络项缺少宿主非环回正对照，不能将 PASS 扩大为全部网络隔离已验证。旧报告及其中过时的 limitations 原样保存；本轮不重跑 PriceLab，不改写历史结果。

本轮仅实施和离线验证，新探针真实专项复验尚未执行。NativeAOT 产物、哈希、离线证据和待授权命令见 `artifacts/sandbox-network-acceptance-20261020/verification.md`。默认网络测试全部使用 fake backend、fake responder 和宿主通信替身，不启动真实网络监听或沙箱，不触发 UAC 或配置机器规则。未修改 Rust fork，不推进 M14/M15。

### 同日后续：新探针真实专项

工程交付后，用户另行明确授权运行一次 `sandbox verify --yes --case network-denied,network-udp,network-dns`。结果 **3 PASS / 0 FAIL / 0 INDETERMINATE / 0 SKIPPED，退出 0**；14/14 个目标通过，28 个宿主前后匹配 ACK 均核对了 nonce 和目标一致。IPv4/IPv6 TCP 环回成功；其余 TCP 目标为 AccessDenied/10013；7 个 UDP 目标均发送 API 成功但内部 3 秒内无匹配 ACK，宿主前后对照成功，无 Harness 外层超时。真实报告与审计见 `artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/`，原报告逐字节保存，没有重跑 PriceLab 或完整矩阵。

本次非环回目标实际是 Meta 接口 `198.18.0.1`，不代表物理 LAN `192.168.8.248`、远端公网或非环回 IPv6 已验证；53/853 仍只证明直接端口通信观测，不证明系统 DNS 解析器/服务代发或完整泄漏防护。用户确认网络授权后，新 exe 从 0 条应用规则变为两条持久 Public / Inbound / Allow 规则（TCP、UDP 各一条，端口/地址均 Any）。只读核对并记录于 `firewall-after.json`、`firewall-persistent.json`，本 chat 未代点弹窗、未执行配置规则命令，也未自行删除规则。实际用户配置和受保护的 1,055 个既有文件哈希不变。旧 PriceLab 5 通过、旧矩阵 15/15 与此次专项分别计数，不宣称 M14 或全部沙箱管理完成。

后续规则清理：用户另行明确授权删除上述两条新 exe 的应用规则；普通权限删除被拒后，通过一次精确 UAC 清理完成。清理后的独立只读复核确认 ActiveStore 与 PersistentStore 中该应用目标规则均为 0，未操作原有 TinyHarness 沙箱规则。证据：`artifacts/sandbox-network-acceptance-20261020/real-20261020-870c1a/firewall-cleanup-uac-result.json`、`firewall-cleanup-independent-check.json`。这项授权已完成，不表示可以在未来复验时继续修改规则。当前进度和后续边界见[接力入口](../../PLAN.md)。

## M11 验收记录

M11 Windows 沙箱（2026-10-10，M11.1–M11.4）：M11.4 交付 `sandbox status`/`sandbox provision` 管理入口（见上文“Windows 沙箱管理命令”）。默认测试 **532/532 通过**（新增 28 个管理入口测试，全部离线，不启动真实 setup.exe、不触发 UAC）；`TinyHarness.Cli` 构建 0 警告；`win-x64` NativeAOT publish 无 trimming/AOT warning；发布产物从仓库外通过 `--help`、`sandbox status`（只读，本机未配置 windowsSandbox 如实报告并退出 0）、离线 `doctor` 与既有 smoke（Completed/17 steps/16 toolExecs），全程无 UAC、无机器修改；`sandbox provision --yes` 在未配置时于提权前拒绝（exit 1）。隔离验收（M11.5）未开始，完成前不宣称沙箱可用。

M11.5 验收工具（2026-10-10）：交付 `sandbox verify` 隔离验收入口（见上文“Windows 沙箱管理命令”）、Core 验收运行器（15 用例固定矩阵、总期限、报告落盘与进程存活扫描）与 **34 项新增离线测试**（fake backend/脚本化判定，不启动真实 setup/runner、不修改机器）。验证：`dotnet build TinyHarness.slnx --no-incremental` 0 警告 0 错误；`dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj` **568 通过 / 0 失败 / 0 跳过**；`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true`（仓库外 `-o`）NativeAOT 0 trimming/AOT 警告；仓库外原生产物 `TinyHarness.exe` smoke：`--help` 与 `sandbox --help` 含 verify 用法、`sandbox verify`（无 `--yes`、无配置）exit 1 且输出「用户配置未设置 settings.windowsSandbox」、`sandbox verify-worker` 缺参 exit 2。边界声明：以上离线验证只覆盖验收工具自身逻辑（fake backend/脚本化判定）；真实隔离验收（provision 后执行 `sandbox verify`）尚未执行，M11.5 完成前不宣称沙箱可用。

M11.5 真实隔离验收（2026-10-10，用户授权，本机 Windows 11 + TinyHarness 独立命名空间 fork 组件执行）：user-config 配置 `settings.windowsSandbox`（组件指向 fork 构建的 `codex-windows-sandbox-setup.exe`/`codex-command-runner.exe`，home 使用固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`）→ `sandbox provision --yes` 一次通过（UAC 建组 `TinyHarnessUsers`、账户 `TinyHarnessOffline`/`TinyHarnessOnline`、防火墙/WFP/ACL，事后检查就绪）→ `sandbox verify --yes` 全矩阵 **14 PASS / 1 FAIL**（报告：`<home>\.sandbox\tinyharness-verify-20261010-133539.json`）。**通过**：身份（whoami=TinyHarnessOffline）、deny-read 探针（deny-probe exit 0）、写根外写入拒绝、只读策略拒工作区写入、`.git` 元数据保护、工作区写入正对照、自然退出 23/192、有限超时（192,true）、运行中取消（OCE）、启动取消、runner 外部击杀不补造退出码、约 4MB 大输出截断与 artifact、宿主 Kill 后遗弃回收。**FAIL（如实记录）**：`network-denied`——沙箱内 curl 对 `127.0.0.1` 与本机 LAN IP 的 TCP 连接均建立成功并收到 HTTP 应答（exit 0，按契约判隔离失效）；防火墙 BLOCK 规则（`tinyharness_sandbox_offline_*`，按用户作用域 LocalUserAuthorizedList）已安装且启用但未对沙箱账户进程生效，或本机 IP 连接走 Windows 环回快速路径绕过按远程地址匹配的规则——两者该用例无法区分，已记入报告 limitations；WFP 过滤器仅覆盖 ICMP/DNS/SMB，通用 TCP/UDP 断网完全依赖上述防火墙规则。**结论边界**：文件读写/元数据隔离与进程生命周期语义已在本机真实验证；断网未验证生效，网络隔离修复并复验通过前不宣称沙箱完整可用；与 Codex 发布版的共存未验收。真实验收还修复了三个此前离线测试无法暴露的缺陷：`ConvertStringSecurityDescriptorToSecurityDescriptorW` P/Invoke 缺第 4 个可选参数（首跑即原生 0xC0000005 崩溃）、`STARTUPINFOW` 结构布局错误（7 字段截断且偏移不符）、capability SID 的 cwd 契约（写根==命令 cwd 时必须用 `workspace_by_cwd` 表的 SID——setup 刷到目录 ACL 的正是它，C# 误用 `writable_root_by_path` 导致 workspace-write 写拒绝）；另修正三处验收 fixture 自身问题（cmd /c 载荷嵌套 CRT 转义引号在宿主与沙箱同样不可解析、waitfor 依赖 SMB mailslot 被断网过滤秒退改用 powershell sleeper、对照监听改回合法 HTTP/1.0 使 curl exit 0 语义无歧义）。回归门禁：默认测试 **576/576**（新增 cap SID 双表契约等测试）、`win-x64` NativeAOT publish 0 警告、仓库外原生产物 smoke 通过。

M11.5 断网修复与全矩阵闭环（2026-10-10 晚）：fork 侧新增按账户 SID 的 WFP 通用 BLOCK 过滤器（`tinyharness_wfp_tcp_connect_v4/v6`、`tinyharness_wfp_udp_connect_v4/v6`，ALE_AUTH_CONNECT 层，条件=账户 SID + 协议；内核强制，不依赖防火墙归因与远程地址匹配，覆盖环回），setup/runner 重建（SETUP_VERSION=5 / IPC v6 不变，C# 接入层零修改）并经显式 UAC 重新 provisioning 安装规则。期间实证了已记录的跨 home 危害：Rust 侧临时验收 home 先行 provisioning 轮换了共享账户密码，导致 TinyHarness 默认 home 的 DPAPI 凭据失效（`sandbox verify` 启动 runner 即报凭据错误）；随后用项目正式 `sandbox provision --yes` 在默认 home 重建一致性（`TinyHarnessOffline`/`TinyHarnessOnline` 密码再次轮换并同步）。修复后先复验 `network-denied` 单用例 PASS（沙箱内对 `192.168.8.248` 与 `127.0.0.1` 的 TCP 连接均立即失败，curl exit 7；报告 `tinyharness-verify-20261010-225744.json`），再重跑全矩阵 **15/15 PASS（exit 0，报告 `tinyharness-verify-20261010-232828.json`）**——M11.5 隔离验收在本机闭环：身份、deny-read/deny-write、元数据保护、工作区写入、断网、退出码/超时/取消语义、断管道、大输出、宿主遗弃回收全部真实验证通过。边界：验证覆盖本机单环境；与 Codex 发布版共存未验收；Rust 临时验收 home 仍留过期凭据（不可复用，需先重新 provisioning）。
