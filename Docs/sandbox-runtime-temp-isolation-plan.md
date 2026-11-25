# 沙箱运行时临时写根隔离与按契约校验计划（`runtime-tmp-v2`）

状态：待实施（设计已对齐，2026-11-26）。

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
2. **临时根采用受保护显式 DACL 与契约化校验**，使上层新增继承 ACE 不再干扰沙箱自有新临时根的就绪判定，同时在普通预检与 C2 恢复两处保持一致的 fail-closed 契约。
3. **跨进程租约与句柄绑定回收**：在 CLI 与 Desktop 共用同一 `sandboxHome` 的双进程场景下，确保分配、使用、退役与回收互不干扰且对重解析点与硬链接保守处理。
4. **保持既有安全与构建约束**：普通宿主权限无 UAC 依赖、NativeAOT 兼容（`LibraryImport` + `SafeFileHandle` + 源生成 JSON）、不放松用户工作区与用户声明额外写根的既有继承校验。

### 非目标
- 不自动清理仍可能被旧进程使用的历史 `<sandboxHome>\tmp`；旧树留到确认停用后再单独处理。
- 不改变用户工作区、用户显式声明的 `AdditionalWriteRoots` 或自定义 `SandboxTempRoot` 的既有 ACL 继承校验与授权边界。

## 3. 核心设计（方案 3 + 4）

### 3.1 新容器与有界独占槽位
- 引入宿主管理的独立容器目录：`<sandboxHome>\runtime-tmp-v2`（父容器与租约记录位于沙箱写权限之外，由普通宿主创建与管理）。
- 首版采用**有界、独占的槽位池**以兼容现有持久 `cap_sid` 表与 NUL 设备 ACL 机制：
  - 每个槽位拥有固定路径键和独立 capability SID，避免无限增长的临时路径撑爆 `cap_sid` 表与 NUL 设备 ACE；
  - 每次 Shell 执行独占一个空闲槽位；该槽位在上一次执行确认退出并完成回收后，重建干净目录再投入使用；
  - 本次 runner token 仅携带本次分配槽位的 capability，父容器与其他槽位不得因共享账号、`Everyone` 或共同祖先写权限而获得同等写访问。
- 策略身份绑定分配规则（保持会话冻结身份稳定），具体槽位路径与 capability SID 绑定单次执行。

### 3.2 环境变量与共享缓存拆分
- `SandboxTargetEnvironment` 统一生成本次执行环境：
  - `TEMP` 与 `TMP` 共同指向本次独占槽位目录（及其受管子目录）；
  - 共享 `NUGET_PACKAGES` 继续复用时，必须为其显式配置独立的受管 `NUGET_SCRATCH`（独立于按次回收的槽位），避免按次切换 `TEMP` 拆散 NuGet 跨进程并发锁；
  - `DOTNET_CLI_HOME` 等期望跨命令复用的 SDK 状态应评估拆分至独立受管目录，避免每次执行重复冷初始化 `.dotnet`；
  - 可信显式环境变量覆盖保留原有授权与覆盖优先级，在线账户 Windows profile 保持独立生命周期。

### 3.3 跨进程租约与进程树生命周期
- 在宿主保护位置维护按 `sandboxHome` 键控的跨进程槽位租约（独立于拒读刷新门，不拉长刷新门持有时间）：
  - 租约记录分配、启动、退出与回收状态（使用 `System.Text.Json` 源生成）；
  - 仅在能够证明**进程树已清空**（结合 Job Object / runner 退出确认，注意现有 runner 非超时退出分支可能保留后代进程）且无 helper 继续引用该目录时，槽位才可进入退役与回收；
  - 若产品允许后台后代跨命令存活，槽位租约需跟随后代生命周期，与 Shell 返回解耦；
  - 宿主崩溃后证据不足的槽位保持保留状态（不强行删除），其他空闲槽位仍可继续服务新执行。

### 3.4 临时根的受保护显式 DACL 与契约校验
- 宿主分配新槽位目录时，为其设置**受保护的显式 DACL**（`PROTECTED_DACL_SECURITY_INFORMATION`），阻断上层 `AppData` 等目录新增的继承 ACE 渗入临时根。
- 启动前校验（普通 preflight 与 C2 恢复路径经 `SandboxPostRefreshValidation` 共享同一套契约）对宿主分配的新临时根直接核对：
  - 卷与文件 ID（防目录被替换）、owner、受保护 DACL 标志、非空 DACL、显式 deny 与传播标志；
  - 所需写/读权限及禁止出现的越权写权限（兼容 Windows 受限令牌两轮访问检查语义）；
  - 若需禁止沙箱删除槽位根本身，使用专用 ACL 模板或通过有效目录句柄生命周期保护根身份；
  - 任何必需权限缺失、归属不可证明或检查出错，均在 spawn 前 fail-closed。
- 用户工作区及用户声明的额外写根继续走既有 `WorkspaceAclInheritance.ValidateAsync`。

### 3.5 句柄绑定的保守回收
- 回收器仅认领已退役且卷/文件 ID 匹配的槽位目录；
- 所有枚举与删除操作绑定到已打开并校验的 `SafeFileHandle` 目录句柄（避免路径级 TOCTOU）；
- 对重解析点仅解除链接本身不递归跟进，对硬链接与只读属性采取保守策略（避免通过清除只读属性修改外部共享文件）；
- 为枚举深度、条目数、取消与瞬态重试设置明确上界。

## 4. 实施阶段规划

1. **阶段 1：容器、槽位租约与句柄回收基础件**
   - 实现 `runtime-tmp-v2` 容器管理、跨进程槽位租约存储与状态机、基于 `SafeFileHandle` 的句柄绑定回收器及离线单元测试。
2. **阶段 2：受保护显式 DACL 与共享后刷新契约校验**
   - 实现新临时根的受保护 DACL 初始化与直接契约校验，接入 `SandboxPostRefreshValidation`，确保普通 preflight 与 C2 恢复路径契约完全一致。
3. **阶段 3：环境拆分与执行链路接入**
   - 接入 `WindowsSandboxBackend`、`SandboxTargetEnvironment` 与独立受管 `NUGET_SCRATCH` / SDK 状态目录，将旧 `<sandboxHome>\tmp` 移出新命令热路径。
4. **阶段 4：真机与双进程验收**
   - 按第 5 节验收矩阵执行离线测试、NativeAOT publish 与真机验收；验证通过后更新 `PLAN.md` 并按流程归档。

## 5. 验收矩阵

- **离线单元测试**：槽位分配/互斥/崩溃残留隔离、租约状态机、受保护 DACL 与缺失/越权 ACE 拒绝、句柄绑定回收的重解析点/只读/上界行为、普通 preflight 与 C2 恢复校验一致性。
- **NativeAOT 验收**：发布无新增 trim/AOT 警告，`LibraryImport` 与源生成 JSON 离线 smoke 通过。
- **真机验收**（显式 opt-in）：
  1. CLI 与 Desktop 共用同一 `sandboxHome` 并发执行 Shell，互不干扰临时目录；
  2. 旧 `<sandboxHome>\tmp` 中存在旧 owner（`...-1007`）和上层新增继承 ACE 时，新命令不再被阻断，且实测新 token 对旧树无写权限；
  3. 槽位根缺少必要权限或被篡改时在 spawn 前 fail-closed；
  4. 后台后代存活与宿主异常退出场景下，租约不误删在用目录且其余空闲槽位可用；
  5. 共享 `NUGET_PACKAGES` + 独立受管 `NUGET_SCRATCH` 下的并发 `dotnet restore` 正常通过。
