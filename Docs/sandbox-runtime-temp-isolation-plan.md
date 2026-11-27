# 沙箱运行时临时写根隔离与按契约校验计划（`runtime-tmp-v2`）

状态：待实施（施工规格已对齐，2026-11-26）。

## 1. 背景与问题

当前所有共享同一个 `windows-sandbox-home` 的会话与进程共用同一个 `<sandboxHome>\tmp` 作为默认临时写根（同时承载 `TEMP`、`TMP`、`DOTNET_CLI_HOME`、`HOME`、`USERPROFILE` 与两类 `AppData`）。

2026-11-25 与 2026-11-26 真机运行中暴露出以下问题：
1. 当上层目录（如 `AppData`）新增可继承 allow ACE 后，`tmp` 下残留的历史旧子目录（例如 `tmp\.dotnet`、`AppData`、`NuGetScratch`，owner 可能为旧沙箱账号 SID）若未同步继承该 ACE，会触发刷新后写继承校验（`WorkspaceAclInheritance.ValidateAsync`）fail-closed，阻断所有工作区 Shell 启动。
2. 对共享 `tmp` 直接执行“失败即清扫整树”或“按路径定点删首错”均不成立：
   - **并发误伤**：多个会话、并发命令及后台后代进程共享同一目录，刷新门释放后已启动的 runner 仍可关闭并按路径重开文件；缺少归属与进程树退出证明时删除会破坏其他正常执行。
   - **TOCTOU 与硬链接**：先按路径检查重解析点再按路径枚举/改属性/删除存在竞态，清除只读属性还可能通过硬链接波及 `tmp` 之外的文件。
   - **入口不一致**：普通 preflight 与 C2 恢复路径（`AuthorizedReadCoordinator`）共用 `SandboxPostRefreshValidation`，必须统一契约，不能只在单侧补丁自愈。

## 2. 目标与非目标

### 目标
1. **每次 Shell 执行独占一个沙箱自有临时写根**，旧 `<sandboxHome>\tmp` 退出新命令的环境、写根与热路径扫描。
2. **宿主单独管理槽位受保护显式 DACL 与契约校验**：槽位不再进入 Rust helper 的 `write_roots`，使上层新增继承 ACE 不再干扰沙箱自有新临时根的就绪判定；所有实际 spawn（含普通执行与 C2 内层执行）在启动前经同一契约校验并 fail-closed。
3. **进程树退出证明、跨进程租约与句柄绑定回收**：采用 Windows 嵌套外层 Job 跟踪整棵进程树退出；在 CLI 与 Desktop 共用同一 `sandboxHome` 的双进程场景下，确保分配、使用、后台后代占用、退役与回收互不干扰且对重解析点与硬链接保守处理。
4. **保持既有安全与构建约束**：普通宿主权限无 UAC 依赖、NativeAOT 兼容（`LibraryImport` + `SafeFileHandle` + 源生成 JSON）、保留 `ReadOnly` 对默认临时目录的只读边界、不放松用户工作区与用户声明额外写根的既有继承校验。

### 非目标
- 不自动清理仍可能被旧进程使用的历史 `<sandboxHome>\tmp`；旧树留到确认停用后再单独处理。
- 不改变用户工作区、用户显式声明的 `AdditionalWriteRoots` 或自定义 `SandboxTempRoot` 的既有 ACL 继承校验与授权边界。
- 首版不默认将 `DOTNET_CLI_HOME` 拆分为常驻目录，待测过冷启动代价后再评估。
- 首版不主动改写既有 `<sandboxHome>\nuget-cache` 与 `<sandboxHome>\tools` 的既有目录结构（两者若受上层继承漂移影响，登记为已知边界或后续按同类受保护 DACL 契约治理）。
- 首版不修改固定版本 Rust runner，也不改变单沙箱账户共享模型；因此**不提供整棵子树对独立 DACL 子项的硬隔离**（见 3.1 与 3.3 已接受边界，该需求若后续成为发布门槛须另行设计独立方案）。

## 3. 核心设计（方案 3 + 4）

### 3.1 新容器、有界独占槽位与 SID 归属
- 引入宿主管理的独立容器与元数据目录：
  - 槽位容器与两层槽位结构：`<sandboxHome>\runtime-tmp-v2\slots\slot-XX`（父容器与槽位根 `slot-XX` 均位于沙箱写权限之外，显式拒绝 `WriteBits` 含 `FILE_ADD_FILE` 与 `FILE_WRITE_ATTRIBUTES`，杜绝运行期将槽位根原地改为重解析点）；沙箱实际可写根位于受管子目录 `<sandboxHome>\runtime-tmp-v2\slots\slot-XX\work`；
  - 租约与槽位元数据目录：纳入 `WindowsSandboxComposer` 的完整性保护路径与写根互斥校验（与 `SandboxProvisionLeaseStore` 同级，禁止任何用户配置写根重叠）。
- 采用**有界、独占的槽位池**，同一次分配原子产出本次执行的槽位路径、capability SID 与目标环境：
  - 每个槽位拥有固定路径键和独立 capability SID；
  - 若确认 Rust runner 启动受限 token 仅消费 `spawn_request.capability_sids` 而不反查 `<sandboxHome>\cap_sid`，槽位 SID 表独立持久化在宿主受保护的 `runtime-tmp-v2` 元数据中，避免与 Rust helper 争抢 `<sandboxHome>\cap_sid` 与 `DenyReadRefreshGate`；否则槽位 SID 的 get-or-create 须严格在 `DenyReadRefreshGate` 内完成；
  - `WorkspaceWrite` 下，`capability_sids[0]` 仍保留工作区根 SID（维持宿主侧首项约定），本次槽位 SID 追加于其后；固定版本 runner 会对 `capability_sids` 中每个 SID 调用 `allow_null_device`，因此新增的槽位 SID 也会进入 NUL 设备 ACL 更新路径，NUL 设备 ACE 数量的增长上界由**有界槽位池与固定每槽位持久化 SID**控制，而非依赖排序避免写入；
  - `ReadOnly` 下不向 spawn token 追加槽位写 SID，完整保留 `SandboxPolicyResolver` 既有只读边界，本次默认临时根必须保持只读；
  - 本次 runner token 仅携带本次分配槽位的 capability，父容器与其他槽位不得因共享账号、`Everyone` 或共同祖先写权限而获得同等写访问。在单沙箱账户 + 固定版本 Rust runner（`WRITE_RESTRICTED` 且沙箱账户 SID 位于 restricting SIDs）模型下，跨槽写隔离覆盖**槽位根以及沿用槽位写权限规则的子项**（通过槽位根向子项传播 `OWNER RIGHTS (S-1-3-4: READ_CONTROL)` 剥夺子项所有者的默认隐式 `WRITE_DAC`）；若槽位内进程主动为子项设置受保护 DACL（`SE_DACL_PROTECTED` 阻断继承）或通过显式 ACE 改写写授权，此类独立 DACL 子项不在根目录 ACL 约束范围内，登记为共享账户模型下已接受的边界。
- 会话的 `PolicyIdentity`（递增 `WindowsSandboxComposer.PolicyIdentityVersion`）记录分配规则与可信配置（保持会话冻结身份稳定），具体槽位路径与 capability SID 绑定单次执行；应用层 `DeriveRuntimeWriteRoots` 同步更新为新容器与常驻受管目录边界。

### 3.2 外层嵌套 Job 与进程树退出语义
- 启动 runner 时使用外层宿主 Job Object（兼容 Windows 8+ 嵌套 Job，与 Rust runner 内部 Job 嵌套）：
  - `WindowsSandboxRunnerLauncher` 以 `CREATE_SUSPENDED` 启动 `CreateProcessWithLogonW` → `AssignProcessToJobObject` 将 runner 挂入宿主外层 Job → `ResumeThread`，消除 runner 抢先派生逃逸子进程的竞态；
  - 第一阶段真机优先验证：普通宿主权限下 `CreateProcessWithLogonW` 跨账户返回的进程句柄能否可靠挂载外层 Job，并与 Rust runner 内部 Job 正常嵌套。
- 区分普通 Shell 与 C2 授权执行的后台后代语义：
  - **普通 Shell**：保留 Rust runner 在非超时退出分支调用 `preserve_descendants()` 的既有后台兼容行为；命令返回后，宿主后台追踪器继续持有外层 Job 句柄，槽位保持占用状态，直到外层 Job 确认整棵进程树清空（`ActiveProcesses == 0`）且无 helper 引用该目录后，才进入退役与回收；
  - **C2 授权执行**：以**整棵进程树退出**作为授权窗口正常结束的前提——`ReclaimRunnerAsync` 后继续限时确认外层 Job 清空，超时未空则主动终止外层 Job 并再次确认；若仍无法确认整树退出，则抛出 `ToolExecutionUncertainException`，触发 `AuthorizedReadCoordinator` 持久化禁用。
- **宿主崩溃与证据不足**：跨进程新宿主无法接管已崩溃旧宿主的匿名 Job；凡退出证据不足的槽位保持**保留状态**（不强行删除或复用），其余空闲槽位继续服务新执行。

### 3.3 宿主独管受保护显式 DACL 与统一契约校验
- 槽位**不进入** Rust helper 的 `write_roots`，避免 helper 为其补刷通用写 ACE 或触发全树继承扫描；由宿主在分配/重建槽位时直接设置**受保护的显式 DACL**（`PROTECTED_DACL_SECURITY_INFORMATION`），阻断上层 `AppData` 新增继承 ACE 渗入。
- 槽位 DACL 与契约校验核对项（普通执行与 C2 内层执行在分配槽位后、spawn 前共用同一校验实现，任一异常均 fail-closed）：
  - 64 位卷序列号与 128 位文件 ID（`FileIdInfo`，兼容 NTFS 与 ReFS，防目录被替换）、owner、受保护 DACL 标志、非空 DACL、显式 deny 与传播标志；
  - 所需写/读权限下限与上限（拒绝白名单主体在根或子项上被扩大到模板之外的越权位如 `WRITE_DAC`/`WRITE_OWNER`/`FILE_DELETE_CHILD`）、规范 ACE 顺序、`OWNER RIGHTS (S-1-3-4)` 子项继承约束及禁止出现的越权权限（按固定版本 runner 实际 `WRITE_RESTRICTED` 令牌与沙箱账户 SID 语义评估，覆盖可写根与沿用槽位写权限规则的子项；子项自行创建受保护 DACL 或显式 ACE 改写授权属于单账户共享模型下已接受的已知边界，根目录校验不涵盖此类独立子项 DACL）；
  - 防止沙箱删除或原地重解析点化槽位根自身（`slot-XX` 对沙箱不可写且显式拒绝全部 `WriteBits`，可写面落于 `slot-XX\work`，即使子项被改为重解析点，回收器也仅解除子项链接而不泄漏槽位）。
- **C2 恢复路径与槽位边界**：
  - 旧 `<sandboxHome>\tmp` 退出默认静态写根后，`AuthorizedReadCoordinator` 的入口恢复（`RecoverInsideWindowAsync`）与退出恢复（`RestoreAsync`）均不分配也不接触槽位；
  - `SandboxPostRefreshValidation` 在入口恢复与普通 preflight 中继续校验静态写根（工作区、用户 `AdditionalWriteRoots`、显式 `SandboxTempRoot` 及受管常驻写根）与组读根就绪；
  - 本次独占槽位的契约校验统一发生在内层 `WindowsSandboxBackend.ExecuteAsync` 拿到槽位之后、实际 spawn 之前，确保所有 spawn 必过校验且普通/C2 语义完全一致。

### 3.4 环境变量与共享缓存拆分
- `SandboxTargetEnvironment` 在单次执行期按本次槽位生成环境：
  - 默认 `TEMP`、`TMP`、`USERPROFILE`、`HOME`、`APPDATA`、`LOCALAPPDATA` 与首版 `DOTNET_CLI_HOME` 均指向本次独占槽位（及其受管子目录）；
  - **受管 `NUGET_SCRATCH`**：共用同一个 `NUGET_PACKAGES` 的进程必须共用同一个受管 `NUGET_SCRATCH`（包括通过可信 `extraEnvironment` 显式指向外部同一包缓存的情况，按规范化 `NUGET_PACKAGES` 路径稳定映射），避免按次切换 `TEMP` 拆散 NuGet 跨进程并发锁；
  - 新增受管常驻目录（如 `NUGET_SCRATCH`）须建立自身独立的受保护显式 DACL 与契约校验，避免重蹈 `<sandboxHome>\tmp` 上层继承漂移的覆辙；
  - 可信显式环境变量覆盖保留原有授权与覆盖优先级，在线账户 Windows profile 保持独立生命周期。

### 3.5 跨进程槽位池、耗尽处理与句柄绑定保守回收
- **池耗尽规则**：所有槽位均在用或处于保留态时，在 `DenyReadRefreshGate` **之外**限时等待（支持 `CancellationToken`，绝不持拒读刷新门等槽位）；超时仍无可用槽位则 fail-closed，并明确报出在用槽位数与因退出证据不足而被保留的槽位数。
- **跨进程租约**：复用 `SandboxProvisionLeaseStore` 已验证的原子临时文件替换写入、系统 BootId 与进程身份（PID + 64 位 UTC 创建时间）识别模式；租约独立于拒读刷新门，记录槽位的分配、启动、后台占用、退役与回收状态（使用 `System.Text.Json` 源生成）。
- **句柄绑定保守回收**：
  - 回收器仅认领已确认整树退出且卷/文件 ID 匹配的退役槽位目录；
  - 所有枚举与删除操作绑定到已打开并校验的 `SafeFileHandle` 目录句柄（避免路径级 TOCTOU）；
  - 对重解析点仅解除链接本身不递归跟进，对硬链接与只读属性采取保守策略（避免通过清除只读属性修改外部共享文件）；
  - 为枚举深度、条目数、取消与瞬态重试设置明确上界。

## 4. 实施阶段规划

1. **阶段 1：单槽位垂直闭环与核心假设真机验证**
   - 先落地单槽位垂直闭环：槽位目录初始化（宿主受保护显式 DACL + 契约校验）、`CREATE_SUSPENDED` 外层嵌套 Job 挂载与进程树退出判定（普通后台保留 / C2 强制整树退出）、`SafeFileHandle` 句柄绑定保守回收与离线单测；
   - 真机优先验证外层 Job 与 Rust runner 内部 Job 的嵌套兼容性、跨账户 `AssignProcessToJobObject` 权限以及同沙箱账户跨槽写隔离，确认无需修改 Rust runner 后再进入下一阶段。
2. **阶段 2：跨进程槽位池、租约状态机与统一校验接线**
   - 将单槽位扩展为有界跨进程槽位池与租约状态机（池耗尽在拒读刷新门外限时等待、崩溃保留态与诊断信息）；
   - 接入 `WindowsSandboxBackend`、`AuthorizedReadCoordinator` 与 `SandboxPostRefreshValidation`，落定“C2 恢复不碰槽位 + 所有实际 spawn 统一校验本次槽位 + `ReadOnly` 不带槽位写 SID”契约。
3. **阶段 3：环境拆分、受管 `NUGET_SCRATCH` 与策略身份收口**
   - 接入 `WindowsSandboxPolicyPlanner`、`WindowsSandboxComposer`（含 `PolicyIdentityVersion` 递增、完整性路径互斥、`DeriveRuntimeWriteRoots` 更新）与 `SandboxTargetEnvironment`；
   - 实现按 `NUGET_PACKAGES` 共享的受管 `NUGET_SCRATCH` 及其独立 ACL 契约，使旧 `<sandboxHome>\tmp` 彻底退出新命令热路径；评估并实测 `DOTNET_CLI_HOME` 随槽冷启动代价。
4. **阶段 4：全量回归、NativeAOT 与双进程真机验收**
   - 按第 5 节验收矩阵执行离线回归、NativeAOT publish 与真机验收；验证通过后更新 `PLAN.md` 并按流程归档。

## 5. 验收矩阵

- **离线单元测试**：单槽位与多槽位分配/互斥/门外限时等待/崩溃残留保留、租约状态机、受保护 DACL 与缺失/越权 ACE 拒绝、`ReadOnly` 权限边界、句柄绑定回收的重解析点/只读/上界行为、普通执行与 C2 恢复/执行校验一致性、`NUGET_SCRATCH` 按 `NUGET_PACKAGES` 映射一致性。
- **NativeAOT 验收**：CLI 与 Desktop 发布无新增 trim/AOT 警告，`LibraryImport` 与源生成 JSON 离线 smoke 通过。
- **真机验收**（显式 opt-in）：
  1. 外层嵌套 Job 挂载 `CreateProcessWithLogonW` 启动的 Rust runner 正常运作：普通命令后台后代保持槽位占用并在后代退出后自动回收；C2 命令在整树退出前不释放授权窗口；
  2. 同一沙箱账户下并发占用不同槽位时，实测 A 槽位 token 无法向 B 槽位根、父容器或沿用槽位写权限规则的 B 槽位子项写入或通过隐式 `WRITE_DAC` 改写 DACL（子项主动设置独立受保护 DACL / 显式 ACE 登记为单账户已知边界）；
  3. `ReadOnly` 策略下单独验收：本次命令的默认临时根保持只读，无法写入槽位根或其子项；
  4. CLI 与 Desktop 共用同一 `sandboxHome` 并发执行 Shell 与并发回收，互不干扰临时目录；
  5. 旧 `<sandboxHome>\tmp` 中存在旧 owner（`...-1007`）和上层新增继承 ACE 时，新命令不再被阻断，且实测新 token 对旧树无写权限；
  6. 槽位根缺少必要权限、存在超模板上限的越权权限、ACE 顺序异常、缺少 `OWNER RIGHTS` 继承约束或被篡改时在 spawn 前 fail-closed；且实测沙箱令牌在运行期既无法删除 `slot-XX` 根，也无法对其执行 `FSCTL_SET_REPARSE_POINT` 原地重解析点化；
  7. 宿主异常退出后，证据不足的槽位不被误删且其余空闲槽位可用，池耗尽时给出清晰原因；
  8. 共享 `NUGET_PACKAGES`（含默认与外部可信配置覆盖）+ 对应受管 `NUGET_SCRATCH` 下的并发 `dotnet restore` 正常通过。
