# 项目实体、会话归属与多目录扩展

更新日期：2026-11-05。

**状态：项目实体、会话归属、Desktop／CLI 接入、明确无项目会话、会话归档与项目删除已接入；Project 目录册跨进程自动同步已接入（见 §16）；阶段四综合验收待完成。** 本文记录独立 Project、会话归属、Desktop／CLI 接入和后续多目录执行的方案；已完成部分与验证见[实施状态](#15-实施状态)。CLI／Desktop 新建持久化会话保存项目归属，恢复旧日志在原 writer 内补齐；列表与历史只读，旧日志归属由 Desktop 在启动、生效 store 变化与“刷新历史”时自动分批整理，CLI 保留 `session initialize-projects` 显式命令。明确无项目的新会话使用单独日志记录，项目视图以底部“最近”分类直接列出无项目会话，空白会话的项目选择器支持搜索与创建项目；归档状态保存在 session store 的独立元数据中，不改写对话日志。删除项目会将其会话归属追加为无项目状态，保留会话历史与工作目录。

入口见 [PLAN.md](../PLAN.md)。既有契约见[会话持久化](Archive/session-persistence-design.md)、[项目规则](Archive/project-instructions-design.md)、[桌面 GUI 迁移](desktop-migration-plan.md)和[Windows 沙箱管理](Archive/windows-sandbox-management.md)。操作授权以用户当前要求和 [AGENTS.md](../AGENTS.md) 为准，保存计划不构成代码实施、实际数据接入、Git 或发布授权。

## 1. 目标与范围

建立具有稳定身份、独立名称和目录配置的 Project，让会话按项目组织。现有会话按各自保存的完整执行目录归入单目录项目；第一版保持单目录执行，在数据模型和职责上预留多目录扩展点。

第一版交付：

- 项目持久化、目录精确匹配、创建或复用、改名。
- 一个项目包含多个会话；会话可以属于一个项目，也可以明确选择无项目。
- 现有统一日志的归属初始化，支持中断后幂等重试。
- Desktop 项目分组、项目内草稿与新会话、历史浏览。
- Desktop 空白新对话页可明确选择无项目；无项目会话仍保留自己的执行目录和权限边界。
- Desktop 可归档与恢复空闲会话；归档只影响默认列表展示，不删除对话历史。
- Desktop 可删除项目；关联会话转为无项目，保留日志和原执行目录，并清除该项目的侧栏持久状态。
- CLI 与 Desktop 共用归属逻辑；运行入口建立归属，历史检查展示归属。
- 重启后保留项目身份、名称、归属和会话原执行环境。

第一版不开放多目录项目入口，不实施已有会话执行目录调整、会话跨项目转移、跨 session store 聚合、Git worktree 管理、标签、自定义分类和分类排序。Desktop 侧栏可对项目置顶并手动排序；这些显示偏好与 Project 身份和项目目录册分离。多目录执行另行实施，增加 `Roots[]` 不代表执行能力已经完成。

## 2. 当前实现基线

2026-10-26 只读核查确认以下接入位置；它们是方案依据，不是新功能已通过的验证。

| 位置 | 当前职责与约束 |
| --- | --- |
| [ChatSession](../TinyHarness.Core/Models/Session/ChatSession.cs) | 固定保存一个 `WorkspaceRoot`，没有独立项目归属 |
| [SessionLogIdentity](../TinyHarness.Core/Models/Persistence/SessionLogBatch.cs) | 身份日志保存原执行根目录 |
| [PersistedSession](../TinyHarness.Core/Models/Persistence/PersistedSession.cs) | 从日志重建会话数据 |
| [SessionResume](../TinyHarness.Core/Services/Session/SessionResume.cs) | 恢复时检查当前目录与保存目录一致 |
| [Workspace](../TinyHarness.Core/Services/Runtime/Workspace.cs) | 路径解析与链接边界围绕单个 root |
| [WorkspaceAccessState](../TinyHarness.Core/Services/Permissions/WorkspaceAccessState.cs) | 绑定目录身份、只读状态与工具准备版本 |
| [SessionLogStore](../TinyHarness.Core/Services/Persistence/SessionLogStore.cs) | 唯一 JSONL、排他 writer、严格恢复与未知必需记录拒绝读取 |
| [MainWindowViewModel](../TinyHarness.Desktop/ViewModels/MainWindowViewModel.cs) | 当前按会话列举、选择与加载历史 |
| [DesktopSessionFactory](../TinyHarness.Desktop.Runtime/DesktopSessionFactory.cs) | 组装模型、单根工作区、权限、工具、规则和 writer |
| [DesktopSessionManager](../TinyHarness.Desktop.Runtime/DesktopSessionManager.cs) | 按 SessionId 管理独立运行资源 |
| [CLI Program](../TinyHarness.Cli/Program.cs) | 会话新建、恢复与导航的生产入口 |

实施前重新核对工作区和实际源码，不把本文基线当作永久不变的事实。

## 3. 职责与不变量

| 层次 | 职责 | 不承担什么 |
| --- | --- | --- |
| Project | ID、名称、关联目录、主目录；新会话默认配置 | 工具执行、模型客户端、审批和运行资源 |
| Session | 对话身份、ProjectId、本会话执行环境和历史 | 自动采用项目最新目录列表 |
| 运行权限 | 目录身份、只读限制、审批、沙箱和执行边界 | 根据项目归属自动授予权限 |

必须保持：

1. 项目有独立 ID，名称和路径不是身份；改名不改变执行环境。
2. 项目目录配置只作为新会话默认值，已有会话保留自己的执行配置。
3. 项目归属和目录选择不构成写入授权；同项目会话不共享批准。
4. 模型、上下文、工具、权限、审批、writer 和运行控制继续按会话隔离。
5. 历史查看不取模型凭据、不调用模型、不执行工具、不自动续跑。
6. 现有 `project-instructions` 仍表示规则来源事实，新增 Project 不替代这套记录。

## 4. 数据模型

### 4.1 Project

```text
Project
  Id                 稳定的随机 ID
  Name               用户可修改的显示名称
  Roots[]            有序的关联目录列表
  PrimaryRoot        明确指定的主目录路径
  Revision           项目配置修订号
  CreatedUtc
  UpdatedUtc

ProjectRoot
  Path               规范化的绝对路径
```

- `Id` 不由路径或名称计算，可靠保存后保持稳定。
- `Roots` 非空并按路径比较规则去重；`PrimaryRoot` 必须属于 `Roots`。
- 主目录显式保存，调整目录顺序不改变主目录。
- 第一版创建／编辑服务只接受一个 root；数组结构用于后续扩展。
- 当前运行端读到不支持的多目录配置时明确报告，不能只取第一项启动。
- 默认名称取目录名；卷根等没有可用末级名称时使用可辨认的路径显示值。
- 改名增加项目 `Revision` 和 `UpdatedUtc`，不改变 ID、归属、模型或权限。
- 暂不加入任意 `Metadata` 字典、标签或物理目录身份；项目与会话置顶由 Core 按 session store 保存在 `pinned-items.json`，不写入 Project、项目目录册或会话日志。Desktop 的视图模式、普通项目手动顺序和会话手动顺序继续保存在桌面偏好文件。

### 4.2 Session

```text
Session
  Id
  ProjectId?                 可选项目身份
  ProjectAssignmentState     PendingInitialization / Project / NoProject
  WorkspaceRoot
  ……已有字段
```

读取未绑定的旧会话时 `ProjectId` 可缺失且状态为 `PendingInitialization`；新建持久化会话必须在初始身份批次内明确保存 `Project` 或 `NoProject`。无项目仅表示没有 Project 归属，仍保留 `WorkspaceRoot`、模型、权限和工具能力；它不表示没有工作区。纯内存和测试入口按其既有职责处理，不为填充字段写入真实用户目录。

保留 `WorkspaceRoot` 当前“本会话固定执行根目录”的语义，不替换为动态查询 `Project.PrimaryRoot`。第一版绑定建立后固定，不提供转移入口。项目名称从 ProjectId 解析，不在会话中维护第二份权威名称。

## 5. 目录匹配与项目复用

使用最终配置解析出的 `WorkspaceRoot`，不是直接使用进程 cwd，也不向上寻找 `.git` 后改用仓库根目录。转为完整绝对路径，统一尾部分隔符，沿用现有平台路径比较约定；Windows 沿用现有大小写不敏感规则，不新增未经验证的文件系统物理等价推断。

| 情况 | 处理 |
| --- | --- |
| 同一目录的等价词法路径 | 同一个项目 |
| 同名、完整路径不同 | 不同项目，通过路径提示区分 |
| 父目录和子目录 | 不自动合并 |
| 同一 Git 仓库的不同工作目录 | 不自动合并 |
| 不同路径通过链接指向同一位置 | 不通过物理目标自动合并 |
| 原目录不存在 | 保留项目和历史，执行时报告不可用 |

`GetOrCreate` 在目录册锁内重新匹配，避免并发生成两个项目。第一版不提供同目录创建多个项目的入口，也不把路径永久唯一作为项目身份定义。未来开放重复目录归属时显式选择 ProjectId；当前读到多个候选应报告歧义。

归属匹配不替代目录身份捕获、链接检查、工作区信任或运行授权。

## 6. 持久化与兼容

### 6.1 存储与唯一权威来源

```text
当前会话存储目录/
  projects.json
  projects.json.lock
  pinned-items.json
  pinned-items.json.lock
  archived-sessions.json
  archived-sessions.json.lock
  <session-id>.session.jsonl
  ……现有文件
```

目录册跟随当前 session store，包括已有配置和环境覆盖解析的位置，不跨 store 聚合。项目数据不放入仓库 `tinyharness.json`，不与模型供应商目录混存。

| 数据 | 唯一权威来源 |
| --- | --- |
| 项目名称、目录、主目录、修订号 | `projects.json` |
| 会话项目归属 | 会话 JSONL 的 `project-binding` 或 `project-assignment` |
| 会话执行目录 | 原有会话身份记录 |
| 会话归档状态 | `archived-sessions.json` 中的 session ID 集合 |
| 项目成员列表、最近对话活动 | 从会话记录生成的投影 |
| 置顶项目顺序、置顶会话 ID | Core 的 `pinned-items.json` |

目录册不维护权威 `SessionIds[]`，避免双向归属冲突。缓存可以重建，不能成为归属事实的唯一来源。

目录册采用显式 schema，例如 `tinyharness.projects/1`，包含目录册修订号和项目列表；项目修订号用于编辑冲突判断。使用显式 DTO 和 `System.Text.Json` source generation，不新增数据库或反射注册依赖。

### 6.2 会话归属记录

```text
kind: project-binding

payload:
  schema: tinyharness.project-binding/1
  projectId: ...

kind: project-assignment

payload:
  schema: tinyharness.project-assignment/1
  state: no-project
```

- 新会话在初始身份批次内且只提交一条归属事实：有项目时写 `project-binding`，明确无项目时写 `project-assignment`，不先请求模型再补写。
- 旧会话仅追加归属记录，不重写原有 JSONL 字节前缀、消息、规则、检查点和执行事实。
- 旧会话项目初始化只将 `PendingInitialization` 变为 `Project`；明确无项目的会话跳过自动初始化。
- `project-binding` 或 `project-assignment` 建立初始归属；删除项目时可追加 `project-unassignment`，只允许从匹配项目转为 `NoProject`，不允许重新绑定或转移到其他项目。
- 幂等重试在追加前读取当前归属；已有任一归属就跳过。reader 拒绝重复或冲突记录，不采用最后一条覆盖。
- scanner、writer、恢复投影和 source-generated JSON 元数据共同接入，严格校验 schema 与 ID。
- 归属追加不改变执行目录、协议消息或检查阻断。
- `LastActivityUtc` 排除仅包含归属维护记录的批次；归属初始化和项目删除解绑均不伪造新对话活动。

沿用 `tinyharness.session-log/1` envelope，以显式版本的新必需记录族扩展。

**兼容边界：新 reader 支持无归属记录的旧统一日志；写入 `project-binding`、`project-assignment` 或 `project-unassignment` 后，不支持对应必需记录的旧程序会拒绝读取该会话。** 初始化与交付说明必须明确此影响；不忽略未知必需记录，不复活旧快照读取器，不将“前缀未重写”描述为“旧程序兼容”。

### 6.3 缺失与损坏

- 目录册损坏或版本不支持时保留文件并报告，不能按空目录册覆盖。
- 目录册缺失但日志已有 ProjectId 时，按项目信息缺失处理，不能按目录重建并换绑。
- 会话引用项目缺失时保留原 ID、执行目录和历史，显示“项目信息缺失”。
- 历史与既有会话续聊仍依据日志、原执行目录和原有校验；项目缺失不授予权限，也不通过猜测项目更改环境。
- 新建项目／会话所依赖的目录册保存失败时明确失败并保留草稿，不假装归属已保存。

## 7. 保存、并发和保护

```text
获取跨进程保存锁
  → 锁内重读并检查版本／修订号
  → 校验候选与目录匹配
  → 写同目录临时文件并完成必要 flush
  → 安全替换
  → 返回已保存状态
```

- 借鉴 `UserConfigStore` 的锁和基线检查思路，但必须满足受保护数据的路径、链接与访问边界。
- 锁等待、文件读取和写入有界并响应取消；实施时明确目录册字节、条目、名称和路径预算，验证超限行为。
- 并发为同目录创建项目时，锁内复查并复用同一 ID；过期改名明确失败，重载后重试。
- 锁文件、临时文件和最终文件都要校验；目录册不能放在执行环境可篡改的位置，保护检查覆盖实际可写根。
- 不同时持有目录册锁和会话 writer 锁。删除先在目录册锁内持久标记 `IsDeleting` 并释放锁，再逐会话获取 writer；全部解绑及侧栏状态清理成功后，最后从目录册移除项目。
- `IsDeleting` 阻止新会话或改名继续使用该项目；标记、逐日志解绑、偏好清理或最终目录册提交中断时保留标记，用户可从项目菜单继续。已成功解绑的日志重试时跳过，writer 占用或日志损坏时不删除项目。
- 不声称跨文件原子事务：删除依靠可恢复的目录册标记及逐会话追加事实完成；不得在仍有日志引用项目时移除目录册实体。
- 普通 flush 和安全替换不单独证明断电持久性；进程退出、替换故障和设备故障保证分别验证。

### 7.1 目录册保存与读取

目录册采用单一的托管文件 API 实现，不引入目录册专用的 P/Invoke、原生结构体或平台快速路径：

1. 写者持有同目录保存锁，在锁内重读、校验目录册与编辑修订号。
2. 通过 `FileMode.CreateNew` 创建同目录随机临时文件，异步写入完整内容并 flush，关闭写句柄后再提交。
3. 重新校验目标叶子后，已有文件用 `File.Replace(tempPath, filePath, null)` 提交，首次创建用 `File.Move(tempPath, filePath)`；不自行删除目标，不编写原生替换或复制删除回退。同目录约束避免跨卷复制路径，不把任意文件系统上的原子性或断电持久性作为无条件保证。Windows 实测中，覆盖式 `File.Move` 无法在旧读句柄保持打开时完成，而 `File.Replace` 可以，后者仍可能存在短暂的路径不可访问窗口。
4. Windows 的共享或锁冲突可有界等待并响应取消；其他错误立即传播。未提交时只尽力清理本次成功创建的临时文件，不覆盖原始异常。
5. 正常读者通过共享读取与删除的异步句柄读取快照；长度预算和正文读取使用同一句柄，合作写者只整体替换、不原地改写。共享标志不作为 Unix 上阻止外部写入的安全保证。
6. 无锁读取遇到目录册缺失时，先以只读方式打开既有保存锁并与写者互斥，再复查是否确实缺失；锁文件也不存在时才按尚无保存者处理。读取不创建目录或锁文件，不按“两次探测间隔足够长”猜测替换已经结束。写者已持锁时调用内部读取路径，避免重复获取自己的锁。锁等待或快照打开重试耗尽时报错，取消继续传播。

验收分别检查并发创建、并发读写、读者持有旧句柄时提交、替换阻塞时取消、失败保留原字节和临时文件清理。测试数据使用当前平台的绝对路径；Windows 专属共享语义测试显式限定平台。Windows 的验证结果不替代 Linux／macOS 实测。

## 8. 现有会话初始化

首次启用项目功能时由独立初始化服务处理，不把写入隐藏在 reader、历史分页或 `session list/show` 中。

```text
读取当前 store 支持的统一日志
  → 找出未绑定的有效会话
  → 按保存的 WorkspaceRoot 分组
  → 创建或复用项目并可靠保存
  → 逐会话获取排他 writer 并重新读取
  → 尚未绑定时仅追加归属
  → 展示已完成、待处理和失败结果
```

- 只处理支持的 `.session.jsonl`，不扫描或迁移旧快照和独立审计文件。
- 已绑定会话保持原值；目录缺失不阻止历史归属。
- 两个写入边界内分别复查，不能依赖启动时的过期快照。
- 被外部 writer 占用时标为待处理，之后重试，不抢锁或停止会话。
- 本进程已有宿主持有 writer 时，交由原宿主在安全空闲边界处理，不另开第二 writer。
- 损坏、截断、超预算和非法身份日志单独报告，不自动修复或截断。
- 不确定追加后遵守 poison 与严格读取规则，检查可靠事实后决定是否重试。
- 不解析凭据、不创建模型客户端、不发模型请求、不执行旧工具，不恢复批准、不清除 NeedsInspection。
- 可临时按目录展示待处理组，但必须区别推断与已持久化归属。
- 支持分批与取消；`maxSessions` 限制本次实际检查的日志数量，`afterSessionId`／`NextCursor` 与 `HasMore` 支持续扫。游标只跨过具有处理结果的连续前缀，取消不得跳过未完成项；Pending／Failed 可以继续后续批次，末批结束后从头重试这些条目。预取消或枚举中取消未检查完目录时保留原游标并以 `HasMore=true` 表示尚未确认完成。
- 文件名仍全量枚举、排序，只保留轻量 ID 元数据；正文逐日志读取并释放投影，不累计整个 store 的会话内容。分批不代表恒定枚举成本，单日志继续受既有 256 MiB／批次 8 MiB 预算限制。枚举逐项响应取消。
- 取消令牌从调用边界传入追加；写中取消沿用 writer 的 poison／不确定写入契约，保留已完成事实并停止，当轮不重试。全局完成标记不替代逐会话事实检查，之后发现未绑定日志仍可继续处理。

追加归属会增加提交序号和 `UpdatedUtc`，但不是新对话。增加派生 `LastActivityUtc`，排除仅项目归属维护的批次；保留原“最近提交时间”语义，不让初始化将全部旧会话排到最前面。

## 9. Desktop 交互

侧边栏采用项目分组，组内展示会话，例如：

```text
TinyHarness
  修复模型选择
  讨论项目设计

mixagents
  分析任务调度
  调整界面
```

| 操作 | 第一版行为 |
| --- | --- |
| 打开目录 | 选择目录，创建或打开对应项目 |
| 项目内新建对话 | 把唯一空白新对话页切到该项目，保留输入；不新建草稿页 |
| 项目改名 | 保存名称和修订号 |
| 删除项目 | 项目三点菜单提供删除确认；通用设置中的「项目删除提示」默认开启，可关闭以直接开始删除。确认面板左边缘对齐窗口内容左缘，垂直中心对齐目标项目行。关联会话转为无项目，日志和工作目录保留；清除该 store 的项目置顶、手动顺序及折叠状态。中断后可继续删除 |
| 选择历史会话 | 读取历史，展示该会话自己的执行目录 |
| 折叠／切换项目 | 只改变展示，不取消运行或释放宿主 |
| 侧栏视图 | 默认按项目分类；通过模式菜单在项目分类与纯会话列表间选择，菜单标出当前模式；偏好在桌面启动后恢复 |
| 项目分类与置顶 | 项目模式显示「置顶」和「项目」分类；置顶分类先显示置顶项目，再显示脱离项目列表的置顶会话；置顶状态由 Core 按 session store 持久化，普通项目的默认顺序由 Desktop 偏好维护 |
| 项目排序模式 | 可选按最近会话活动排列普通项目；手动模式下新项目插入「项目」分类顶部，已有顺序不变；切回手动模式恢复保存顺序，置顶项目仍保留置顶顺序 |
| 会话排序 | 运行中的会话按最近活动置于空闲会话之前；新建或近期活动的会话进入手动顺序顶部。其余空闲会话可在同一侧栏分类内拖动，顺序按 session store 保存在 Desktop 偏好中；后续分页加载的历史会话追加在已有顺序之后 |
| 纯会话模式 | 会话置顶在两种视图中通用；纯会话模式以同级的「置顶」和「会话」分类显示，两个分类均先列运行中的会话，再按手动顺序排列空闲会话 |
| 无项目会话 | 空白新对话页默认无项目并提供项目选择；项目模式将明确无项目会话放在固定的「无项目」分组中，仍使用各自保存的执行目录 |
| 会话归档 | 空闲会话可归档或恢复；默认项目／会话列表隐藏已归档项，「已归档」视图集中列出，可从该视图恢复 |

- 旧版 Desktop 偏好中的置顶项目顺序与会话 ID 首次加载对应 store 时迁入 Core；Core 文件保存成功后清除旧字段。视图模式与普通项目顺序继续由 Desktop 保存。

- 显式创建项目允许空项目；草稿不创建会话日志或宿主，首次发送才建立会话并提交归属。
- 桌面保持单一可复用的空白新对话页：顶部「新对话」与项目组「+」进入同一份未发送草稿，空白页不出现在正式会话列表、项目成员或侧栏投影中；发送被接受（取得真实 SessionId）即提升为正式会话行且仅登记一次，无真实 ID 的失败保持草稿并可重试。仅当下一份未被选中、无文字且无任何显式预选时才无损回收空白槽；其他被换出的失败草稿留在进程内去重列表，经显式「返回未发送草稿」逐份轮转访问，不覆盖当前空白页输入或已恢复页的修改，不新增持久化或正式侧栏草稿行。
- 未发送草稿切换项目时保留输入并刷新适用默认模型配置；异步发送继续绑定原会话和草稿版本。菜单、项目「+」、打开目录与自动 store 重绑共用每页意图代际，操作开始时固定页面，异步结果仅在意图、可选目录状态及 store 仍适用时应用；旧结果不改目标、默认模型或选择。自动刷新在配置 I/O 前捕获代际，显式解析在途或之后出现用户意图时不抢占。空白页目标仅在尚无绑定、生效 store 真正变化或显式选择时解析，store 变化时丢弃旧 store 的项目 ID 并按当前目录册只读重解析；正式会话的执行根与 store 固定。
- 正式会话第一版归属和执行目录固定，不提供移动入口。
- 无项目会话使用自身 `WorkspaceRoot`，不提供专属目录；固定的「无项目」分组位于项目列表底部，不提供自定义分类或分类拖动排序。
- 目录缺失仍可查看历史；发送报告不可用，不自动切到其他路径。
- 同名项目在组头与空白页项目选择菜单中均通过完整路径提示区分；按成员最近对话活动排序，空项目按创建时间，改名不制造活动。
- 旧日志归属自动整理：Desktop 启动、生效 session store 真正变化和用户点击“刷新历史”时自动执行一轮分批整理（每批最多检查 100 份日志，幂等续扫、占用跳过、损坏只报告不修复）；选择会话、历史分页、折叠展开与普通只读刷新不触发。成功静默，异常才显示简短摘要，未完成的旧日志保留只读目录组并标注原因；空白新对话页不属于侧栏投影。CLI `session initialize-projects [after-session-id]` 保留为显式入口。
- 不顺带加入草稿持久化、会话标题编辑等额外功能。

继续由 `DesktopSessionManager` 按 SessionId 管理宿主，不新增项目级工具宿主。模型、writer、审批、取消和关闭清理仍按会话隔离，切页不丢弃另一会话的草稿、保存警告或待决审批。

## 10. Core 与 CLI 接入

归属逻辑放在 Core，CLI 和 Desktop 显式调用，ViewModel 不实现第二份路径规范化、存储或迁移逻辑。

| 拟议组件 | 唯一职责 |
| --- | --- |
| Project／ProjectRoot | 数据和不变量 |
| ProjectStore | 受保护目录册、版本和跨进程保存 |
| 项目匹配／创建服务 | 按最终 WorkspaceRoot 精确创建或复用 |
| 会话归属初始化服务 | 协调未绑定日志的幂等追加与失败结果 |
| 会话日志 reader／writer | 唯一归属提交和投影链路 |
| SessionStorePinStore | 会话存储目录内的置顶项目顺序与会话 ID，使用独立 sidecar，不改会话日志 |
| SessionStoreArchiveStore | 会话存储目录内的归档 session ID 集合，独立于对话日志保存并支持恢复 |
| Desktop 项目投影 | 分组、草稿选择、改名和状态展示 |

名称实施时按规模确定，不为表格强行增加接口或通用框架。

CLI 行为：

- `run` 和新 `chat` 在实际创建持久化会话时，按最终生效的 WorkspaceRoot 解析归属。
- 恢复入口保留已绑定值；未绑定时仅在排他 writer 的安全边界补齐。
- `session list/show` 展示项目名称／ID，严格只读，目录册故障不触发写入。
- 普通空白交互和模型草稿选择不提前创建会话或 writer。
- **2026-10-28 起，显式 `/new` 与 Desktop 未发送草稿对齐：只进入未发送空白态**，释放当前宿主但不创建会话、写入端或项目；首次普通输入才建立会话并在初始批次绑定归属。此前“显式新建即保存空会话”的行为已按用户要求移除。
- 暂不增加完整 CLI 项目管理命令平台；遇到归属歧义明确报告。
- ProjectId 和项目名称不参与凭据路由；模型选择、协议校验、规则加载与恢复检查继续生效。

## 11. 未来多目录边界

未来引入独立的会话工作区配置：

```text
SessionWorkspace
  WorkingDirectory
  Roots[]
  Revision
```

旧单目录会话可明确解释为 `WorkingDirectory = WorkspaceRoot`、`Roots = [WorkspaceRoot]`。新记录格式、连续修订和兼容策略在多目录切片设计，不在第一版先写入未生效配置。

新会话从项目当前配置生成自己的环境。项目增加 B 时，原来只使用 A 的会话继续使用 A；已有会话显式更新必须发生在安全边界，可靠提交新修订，并使旧准备、审批和权限缓存失效。

| 部分 | 多目录切片必须完成 |
| --- | --- |
| 文件工具 | 定位有效 root，校验词法与最终链接目标，不能把公共祖先当工作区 |
| 相对路径与 cwd | 以明确 WorkingDirectory 为基准，其他 root 可明确定位，不随排序漂移 |
| Shell | cwd、工具边界与实际后端权限一致 |
| 模型上下文 | 列出全部有效 roots、cwd 和权限，不能只展示第一项 |
| 项目规则 | 各目录规则有来源和作用域，兄弟仓库不互相覆盖；加载时点与预算另行确定 |
| 配置发现 | 明确主目录及其他目录配置处理，不隐式合并冲突配置 |
| 沙箱与权限 | 覆盖所有实际访问根，变更使旧授权失效，保护存储位于全部可写根之外 |
| 持久化与恢复 | 恢复保存的环境，不套用 Project 最新目录列表 |
| 更新失败 | 保留原有效环境，不能出现日志、提示词、工具与沙箱各用不同根目录的半更新 |

仅向沙箱配置增加可写目录，不能代替上述能力。

## 12. 实施顺序

阶段一至三已实现；本轮补充无项目会话、会话归档与项目删除，阶段四综合验收仍待完成；不要求碎片提交。

| 阶段 | 工作 | 完成标准 |
| --- | --- | --- |
| 一：项目核心（已完成） | 模型、目录册、路径匹配、创建复用、改名、保护 | 重启一致、并发不重复、过期编辑不覆盖、损坏不被当空配置 |
| 二：会话归属（已完成） | 日志记录、严格读取、投影、初始绑定、旧日志初始化 | 归属可恢复、接入幂等、历史和阻断保真；生产入口尚未接线 |
| 三：生产入口（已实现） | CLI／Desktop 接入，分组、草稿、改名、删除与状态 | 两端一致、生产新会话都有归属、项目操作不干扰活动会话；本轮证据见 §15 |
| 四：综合验收 | 故障测试、真实 GUI、NativeAOT、离线产物 smoke、文档 | 数据、交互、并发与产物分别有证据 |

同一功能必需的代码、局部重构和测试完整交付；阶段不是 Git 授权，也不要求碎片提交。主要改动在 Core 项目与持久化模块、CLI 会话入口、Desktop.Runtime 创建入口和桌面列表投影；第一版不改文件工具执行边界和规则发现算法。

## 13. 验收矩阵

以下为完整功能的验收要求；阶段一与二的已执行结果见 §15，涉及生产入口、真实 GUI 与综合故障的项目仍待后续阶段执行。

| 场景 | 必须观察的结果 |
| --- | --- |
| 同目录多会话 | 只创建一个项目，所有有效未绑定会话指向同一 ID |
| 同名路径、父子目录、同仓库子目录 | 不误合并，路径可区分 |
| 等价路径和链接别名 | 按约定匹配，不推断物理等价或改变执行身份 |
| 并发创建和改名 | 不重复、不丢其他改动、过期基线失败 |
| 初始化中断／取消后重跑 | 不重复项目或绑定，已提交部分有效 |
| writer 占用 | 标为待处理，其他会话继续，无第二 writer |
| 损坏／截断／超限日志 | 单独报告，不修复覆盖或跳过关键事实后追加 |
| 接入前后历史 | 原字节前缀、协议、opaque、规则、检查点与展示不变 |
| 未知副作用 | 不确认旧执行、不清除 NeedsInspection、不重放工具 |
| 目录册损坏／丢失／版本未知 | 不覆盖重建、不自动换绑，历史按原日志可读 |
| 执行目录消失 | 历史可看，执行报告不可用，不切换目录 |
| 项目配置变化 | 旧会话执行根保持保存值，归属不授予权限 |
| 新旧格式兼容 | 新 reader 读未绑定日志；旧 reader 拒绝新必需记录 |
| 活动排序 | 初始化与项目解绑不伪造新对话，改名不改变对话活动时间 |
| 项目删除 | 删除标记阻止新会话使用该项目；全部会话日志解绑并清理侧栏持久状态后才移除项目，失败可重试且不删除日志或工作目录 |
| Desktop 跨项目切换 | 运行、审批、取消与保存归原会话，草稿不串页 |
| 草稿与空交互 | 不新增空日志；CLI `/new` 只进入空白态，首次发送才创建（2026-10-28 起） |
| 模型／凭据失效 | 列表历史可浏览，初始化不读取 key、不请求模型 |
| CLI／Desktop 一致 | 同一 store 和最终根目录解析到同一项目 |
| 明确无项目会话 | 初始批次保存 `project-assignment`；恢复、历史分页和自动项目初始化均保留无项目状态 |
| 会话归档 | 独立状态文件往返、并发目标状态更新与恢复入口正确；正文日志字节不变 |
| NativeAOT | 受影响入口发布和离线产物 smoke 通过，无被忽略的新增 trimming／AOT warning |

先运行直接受影响测试；持久化公共边界和两个生产入口变化时扩大到相应回归。真实 GUI 交互单独记录，fake 测试不代替 GUI、真实模型、断电或 OS 沙箱隔离验证。

## 14. 文档与交付

- 实施时维护状态、实际文件、关键偏差、验证命令和未验证范围。
- 同步格式扩展与旧程序读取限制，不能把计划写成已实现行为。
- 明确无项目日志记录、项目解除归属记录与独立归档 sidecar 分别维护兼容、保护和恢复契约；项目删除期间保留项目目录册标记以支持恢复。
- 第一版新旧契约衔接时同步持久化和桌面计划，不改写历史验收结论。
- 完成后按仓库规则归档本计划，更新 PLAN 和文档导航。

## 15. 实施状态

2026-10-26 完成阶段一（项目核心）。全部为新增文件，未修改既有代码与任何运行入口；阶段二至四尚未开始。

- 新增文件：
  - [ProjectStoreException](../TinyHarness.Core/Exceptions/ProjectStoreException.cs)：目录册操作失败异常。
  - [Project](../TinyHarness.Core/Models/Project/Project.cs)：项目数据与不变量；`CreateNew` 生成随机 ID 与默认名称，`Validate` 严格校验 ID、名称、root 数量与规范化形态、主目录归属和修订号。
  - [ProjectRoot](../TinyHarness.Core/Models/Project/ProjectRoot.cs)：`Normalize` 转绝对路径并统一尾部分隔符（卷根保留分隔符），`AreEqual` 按平台大小写规则词法比较，`DefaultDisplayName` 取目录名、卷根退回完整路径。
  - [ProjectCatalog](../TinyHarness.Core/Models/Project/ProjectCatalog.cs)：schema `tinyharness.projects/1`、目录册修订号、项目数量预算、跨项目 ID 与目录唯一性（歧义即报错）。
  - [ProjectStore](../TinyHarness.Core/Services/Persistence/ProjectStore.cs)：缺失文件返回空目录册；读取先校验叶子（非目录、非链接）与 1 MiB 字节预算；损坏、未知 schema、未知字段、缺字段、超预算抛 `ProjectStoreException` 并保留原文件；保存走同目录临时文件安全替换；`GetOrCreateAsync` 在跨进程锁内重读并精确匹配，`RenameAsync` 按调用方修订号拒绝过期基线；JSON 使用 source-generated `ProjectJsonContext`（camelCase、拒绝未映射成员、缩进写出）。
  - [ProjectStoreTests](../TinyHarness.Tests/ProjectStoreTests.cs)：19 项测试。
- 关键偏差：目录册服务放入 `Services.Persistence`（与 `SessionStorePaths` 同域），不设 `Services/Project` 命名空间，避免与 `Project` 类型名冲突；目录册文件自身的链接校验由 `ProjectStore` 承担，存储目录对工作区的保护仍由调用方经 `SessionStorePaths.ResolveProtected` 保证，在阶段三接入时生效。
- 已验证：`dotnet test --filter ProjectStoreTests` 19/19 通过，覆盖重启一致、等价词法路径与 Windows 大小写匹配、父子目录与同名目录不合并、并发创建共享单项目、卷根命名、改名与目录册修订号递增、过期基线失败不覆盖、空白根与非法名称拒绝、空文件／损坏 JSON／未知 schema／超预算文件／跨项目重复目录／未知字段／缺字段／非规范 root 的报告且不覆盖；CLI NativeAOT publish 无新增警告，发布产物 `smoke` 通过。
- 未验证：跨进程并发、断电与设备故障、真实 GUI；阶段二（会话归属记录与初始化）、阶段三（CLI／Desktop 接入）与阶段四（综合验收）未实施。

2026-10-27 调整阶段一目录册保存设计（见 §7.1），未推进后续阶段：

- `ProjectStore` 移除目录册专用 P/Invoke、原生结构体与 NT 路径转换；已有文件使用托管 `File.Replace`，首次创建使用 `File.Move`。临时文件保持同目录独占创建、异步写入、flush 与关闭后提交，仅清理本次成功创建且未提交的文件。
- 读取使用异步快照句柄；正常读取无锁，缺失时只读打开既有保存锁并复查，不创建文件或目录，不把替换窗口误判为空目录册。写路径在已持有保存锁时使用内部读取流程，避免重复获取自己的锁。
- 保留既有测试并修正通用测试的 Windows 路径假设；新增旧读句柄保留快照、替换冲突取消、只读目标失败保留原字节、缺失时等待保存者四项测试，取消断言接受 `OperationCanceledException` 的派生类型。
- 已验证（Windows x64）：目录册专项共 37 项，35 通过、2 项因缺少符号链接创建权限跳过；子代理将专项测试重复运行 10 轮、并发读写测试另跑 10 轮，均无失败；主代理复核后重跑专项结果相同。Core／CLI 全部离线回归共 1,276 项，1,272 通过、4 项环境相关跳过、0 失败。`dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/project-store-managed-nativeaot-20261027` 成功，无 compiler／trimming／AOT 警告；从仓库外运行该原生产物的 `smoke --config <仓库绝对路径>/tinyharness.json` 成功。产物 smoke 验证既有离线入口，不代表尚未接入入口的目录册流程已经在原生产物中执行。
- 未验证：Linux／macOS、真实跨进程并发、断电与设备故障。未修改凭据、进程或 Windows 沙箱现有的原生调用；本次无新增依赖、schema 或公开方法签名变化。

2026-10-27 完成阶段二（Core 会话归属）：

- `SessionLogBatch.cs` 增加 `SessionLogProjectBinding`，payload schema 为 `tinyharness.project-binding/1`；严格校验字段集合、类型、重复属性、schema 与项目 ID。`SessionLogScanner` 跨批次拒绝重复／冲突绑定，分页与全量读取共用校验。
- `SessionStoreWriter` 在初始身份批次内提交归属，旧日志通过通用追加链路绑定一次；成功落盘后才更新归属缓存，校验失败不推进状态，写入故障沿用 poison。后续会话提交不得转移或丢弃已绑定归属。新增内部强类型打开结果，仅在锁打开阶段将 Windows 已确定共享／锁冲突标为待处理；预期存储／格式错误返回失败，取消与其他意外异常传播，非成功退出均释放句柄。
- `ChatSession` 增加只读 `ProjectId`、项目感知构造重载与一次性 `BindProject`；原四参数 CLR 构造签名保留。`PersistedSession` 与 `SessionResume` 保留归属，`LastActivityUtc` 排除纯绑定维护批次，`UpdatedUtc` 原语义不变。新增记录使用现有 source-generated JSON 元数据，不增加依赖。
- 新增 `SessionProjectInitializer`：只处理当前 store 的统一日志；按原执行根精确创建／复用项目，先可靠保存项目再锁内重读并追加归属。已绑定日志不依赖目录册可用性、不重建换绑；目录缺失不阻止归属；非法根、损坏／截断／超限日志与目录册预期故障逐项报告且不修复。占用只返回 Pending，不抢占原宿主。API 支持正文检查数量上限和续扫游标，取消后游标不跨过未完成项；文件名枚举／排序仍为全量轻量操作。调用方必须先解析并验证受保护 store，生产接入留至阶段三。
- 新增 `SessionProjectBindingTests` 20 项、`SessionProjectInitializerTests` 24 项，专项 44/44 通过。覆盖初始／旧日志绑定、重复和非法载荷、写入故障与取消、幂等字节、原前缀／历史／opaque／规则／非空压缩检查点／检查阻断保真、活动时间、旧构造兼容、Windows 等价路径分组、分批／占用续扫、取消后按返回游标恢复（含交错目录分组）、非法绝对／相对路径隔离、目录册丢失／损坏／IO、锁释放及并发测试注入隔离。
- 已验证（Windows x64、SDK 10.0.402）：`dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore` 共 1,323 项，1,319 通过、4 项因符号链接环境能力跳过、0 失败；`dotnet test TinyHarness.Desktop.Tests/TinyHarness.Desktop.Tests.csproj --no-restore` 56/56 通过。最终 TRX 位于 `artifacts/session-binding-validation/`。
- `dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/session-binding-nativeaot-20261027` 成功，无 compiler／trimming／AOT 警告；从仓库外运行该产物 `smoke --config C:/Code/.Net/TinyHarness/tinyharness.json` 成功（离线 17 steps、16 tool executions）。此 smoke 验证既有产物路径，不代表未接入口的归属初始化服务已经在原生产物中执行。
- 兼容边界：新 reader 支持未绑定统一日志；写入 `project-binding` 后，旧程序按未知必需记录拒绝读取。保留字节前缀不等于旧程序兼容。
- 未实施／未验证：CLI／Desktop 生产接线、项目分组 UI、真实用户日志初始化、真实 GUI、真实模型、Linux／macOS、跨进程并发、断电／设备故障；阶段三与四仍待实施。

2026-10-27 完成阶段三（生产入口接入）：

- Core 新增 `SessionProjectBinding`，两端按最终执行根复用项目并在新日志初始批次提交归属；恢复旧日志先严格校验，再使用原 writer 可靠追加归属，成功后才更新内存。已有归属不查询目录册，不因目录册缺失或损坏而换绑。初始化额外验证每份旧日志保存目录的存储保护边界。
- CLI `run`、首次发送和显式 `/new` 均接入；空白交互与模型草稿不创建项目或会话日志。`session list/show` 只读展示项目名称／ID、未绑定或项目信息缺失，目录册故障不阻止历史检查；列表按 `LastActivityUtc` 排序。新增 `session initialize-projects [after-session-id]`，每批最多检查 100 个日志，输出兼容提示、逐项 Bound／AlreadyBound／Pending／Failed、取消状态与续扫游标；末批后可从头重试占用／失败项。
- Desktop 新增 `ProjectViewModel` 与项目分组侧栏，支持打开目录、空项目、项目内独立草稿、名称修订检查与折叠。切换未发送草稿的项目保留输入并刷新默认模型；首次发送才建立宿主。原有请求／页面构造签名保留，项目与预期存储通过附加属性或重载传递，防止配置变化后发送到其他 store。运行、审批、取消与保存继续属于原 SessionId；项目缺失保留原 ID 与历史。旧日志初始化为显式操作，可取消、续扫并展示逐项结果；历史浏览不触发写入。
- 新增 8 项 `DesktopProjectEntryTests`，覆盖草稿与空项目、同名不同路径、项目复用、初始归属／重启、固定执行根、改名冲突、运行中改名／折叠／切换与草稿隔离、只读历史、初始化前缀保真／幂等、101 份日志分批续扫、目录册损坏及预期 store／项目不匹配。新增 4 项 CLI／共享恢复测试，覆盖显式初始化、只读 list/show、损坏目录册、原 writer 恢复归属和游标参数。
- 已验证（Windows x64、.NET SDK 10.0.402）：Core／CLI 完整离线回归 1,326 项中 1,322 通过、4 项符号链接能力相关跳过；之后补充游标大小写校验，最终 `SessionCommandTests` 16/16 通过。Desktop 最终完整回归 64/64 通过。TRX 与隔离原生产物验证脚本位于 `artifacts/project-entry-validation/`。
- CLI 与 Desktop 均完成 Release `win-x64` NativeAOT publish，无新增 compiler／trimming／AOT warning；产物分别位于 `artifacts/project-entry-cli-nativeaot-20261027/` 与 `artifacts/project-entry-desktop-nativeaot-20261027/`。从仓库外运行 CLI `smoke --config <仓库>/tinyharness.json` 通过（17 steps、16 tool executions）；Desktop `--offline-smoke` 验证同目录两会话共享可靠项目、并行、模型切换与恢复。隔离 CLI 原生产物验证覆盖空白交互无日志、`/new` 两次复用同项目、初始批次归属、项目 list/show、显式初始化以及损坏目录册时的历史／已绑定续聊。均使用临时夹具、假凭据与离线模型，没有初始化真实用户日志。
- 环境说明：继承的 PATH 过长使 cmd 无法发现 `where`／`ping`；缩短验证进程的 PATH 后，相关进程测试及 NativeAOT 构建通过。受限执行环境会阻止本地 HTTP listener、祖先目录身份检查或 Avalonia 本机许可目录读取，最终相应验证在本机权限下运行；未关闭检查、修改依赖或更改机器配置。按 `banned甄选` profile 仅清理本轮 C# 文件，并人工核对新增逻辑排版。
- 阶段四仍待完成：真实 GUI 交互、完整故障矩阵、真实跨进程并发、Linux／macOS、断电／设备故障及实际用户数据接入均未验证；多目录执行未实施。本阶段的原生产物离线结果不替代这些验收。

2026-10-27 阶段四离线验收与两项 P2 修复（GUI 受阻，阶段四未完成）：

- 修复 Desktop 投影两处 P2 问题：历史与列表刷新改为按 store 与会话 ID 匹配，原地更新归属与活动时间，保留宿主草稿与既有历史；新建草稿按当前 store 重新解析归属，不复制旧绑定；异步归属绑定增加 `CanChooseWorkspace` 守卫。新增两项回归测试在旧实现失败、修复后通过；Desktop 最终回归 66/66 通过。
- Core／CLI 最终离线回归 1,327 项：1,323 通过、4 项符号链接能力相关跳过、0 失败。首轮全量与两次专项复跑中 `SessionJournalScaleTests.AppendAndRecovery_MeetsLockedScaleBudgets` 失败（6>4），最终全量通过；初步定位为 WrittenUtc 小数秒尾零序列化字节抖动，相关代码本轮未改动、问题未修复，该结果不构成可靠全绿。
- CLI 与 Desktop 均完成 Release `win-x64` NativeAOT publish，产物为 native code，0 compiler／trimming／AOT warning；分别位于 `artifacts/project-stage4-cli-nativeaot-20261027/` 与 `artifacts/project-stage4-desktop-nativeaot-20261027/`。CLI `smoke` 离线通过（17 steps、16 tool executions）；隔离夹具 `artifacts/project-stage4-validation/stage4_native_entries.py` 对原生产物 PASS：空白交互无日志、`/new` 两次复用同项目、初始批次归属、`session list/show` 与显式 `initialize-projects`、损坏目录册时历史展示与已绑定续聊。Desktop 修复后产物（r2）`--offline-smoke` PASS：parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6。本轮证据位于 `artifacts/project-stage4-validation/`。
- 真实 GUI 验收受阻：以 `powershell -NoProfile -File` 执行隔离启动脚本并带 `-ReuseFixture` 时，被本机 PowerShell 执行策略拒绝（UnauthorizedAccess"禁止运行脚本"），应用未启动；未绕过策略、未修改机器配置，启动脚本与步骤文档示例中的 `-ExecutionPolicy Bypass` 参数已移除。GUI 交互（打开目录、改名、切页、草稿保留、折叠、刷新）仍未验证。
- 阶段四仍未完成：真实 GUI 交互、真实跨进程并发、Linux／macOS、断电／设备故障、真实模型与实际用户数据接入均未验证；未对真实用户会话执行归属初始化。

2026-10-28 完成 Desktop 旧日志自动整理接入：

- Desktop 改为启动即自动整理：窗口 `Opened` 后先做轻量配置与模型准备，后台按每批最多检查 100 份日志自动执行一轮归属整理，结束后统一只读加载项目与历史；生效 session store 真正变化和点击“刷新历史”时再各启动一轮。选择会话、历史分页、折叠展开、未知项目刷新与整理后的刷新不触发新一轮。同一时刻只运行一轮，切 store 取消旧轮并以运行代次和 `ProjectRoots` 路径比对防护迟到结果，关闭取消并等待整轮结束。侧栏移除“旧会话项目归属”手动初始化区域，未绑定草稿独立展示为“草稿”组；成功静默，异常才显示简短摘要并给未完成目录组标注原因，已归属草稿计入真实项目分组。
- 新增 `DesktopStartupOrganizeTests` 16 项，覆盖 101 份日志多批扫完、二次启动幂等、空 store 无副作用、草稿不被扫描阻塞、占用跳过、损坏日志报告不修复、取消即时停止且仍完成统一加载、同 store 重复触发合并、异常轮清算明细并标注未完成组、切 store 后不误标旧 store 分组、`HasOrganizeNotice` 属性通知等。
- 实施后经三轮 Open Code Review（delegation 模式）收敛：首轮修复 4 项（异常摘要缺少属性通知、取消误报游标停滞、异常轮明细残留、取消后跳过统一加载），次轮修复 1 项（未完成标注未按本轮 store 过滤），第三轮无新发现；修复均附红／绿验证，未削弱既有只读、保护与生命周期断言。
- 已验证（Windows x64）：`dotnet test TinyHarness.Desktop.Tests/TinyHarness.Desktop.Tests.csproj` 87/87 通过、0 警告；`dotnet publish TinyHarness.Desktop/TinyHarness.Desktop.csproj -c Release -r win-x64` NativeAOT 成功，0 compiler／trimming／AOT 警告；原生产物 `--offline-smoke` 两次运行均 PASS（退出码 0：parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）。
- 未验证：真实 GUI 交互（App 启动入口无受支持的隔离参数，默认配置指向真实用户数据，按真实数据保护要求未启动 GUI，自动启动接线仅由离线测试与代码审查覆盖）、真实用户日志初始化、真实模型、Linux／macOS、断电／设备故障；阶段四仍未完成。

2026-10-28 修复自动整理接入的三项评审缺陷（P1×2、P2×1）：

- 修复侧栏分组重建：`Group()` 改为本轮每 key 首次遇到时清一次成员；此前同组每次追加成员前都被清空，同项目多会话与项目内草稿只剩排序末位成员，组间活动排序随之错误。
- 自动整轮在进入 Core 初始化前复用与显式入口一致的 `ResolveProtectedDirectoryAsync` 完整保护校验（含 Windows 沙箱附加写根；Core 仅逐日志校验各自执行根，不覆盖附加写根）；保护失败走异常轮提示并保持只读加载，不创建目录册或追加日志。解析结果与本轮捕获 store 不一致时跳过写入并给出可见原因，迟到轮不覆盖新轮展示。
- 未归属旧日志的侧栏分组键纳入会话 store（长度前缀分隔 store 与执行根，避免路径内合法分隔符字符造成键歧义），整理明细按行 store＋执行根查询；切换 store 后两个 store 的同一执行根不再合并成组，也不串用整理原因。
- 新增 5 项回归：同项目多成员／草稿／SidebarRows 铺平与组序、重建保留分组实例与展开态、store 位于沙箱附加写根内时拒绝写入且文件集字节不变、解析 drift 拦截并可见（旧实现对不一致目录静默跳过，红点为缺少可观察原因而非发生写入）、同根跨 store 分组与原因隔离；修复前 5/5 失败，修复后通过。既有 pending 键断言同步新键格式。
- 已验证（Windows x64）：`dotnet test TinyHarness.Desktop.Tests/TinyHarness.Desktop.Tests.csproj` 92/92 通过、0 警告；Release `win-x64` NativeAOT publish 无 compiler／trimming／AOT 警告，产物 `--offline-smoke` PASS（parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）。证据在 `artifacts/startup-organize-bugfix-20261028/`。真实 GUI 交互与真实用户日志初始化仍未验证，阶段四未完成。
- 用户实测反馈（2026-10-28）：已有的未绑定项目旧会话已成功自动补齐项目归属；此为用户提供的验证结果，Agent 未读取真实用户日志复核，其余阶段四未验证项不变。

2026-10-28 接入单一空白新对话页首版（对齐 DshDesktop blank session 交互，后续评审修复与最终结果见下一条）：

- Desktop 以唯一空白页承载未发送草稿：启动即选中该页；顶部「新对话」与项目组「+」进入同一份，反复点击不堆积，空白页不进入 Sessions、项目成员或 SidebarRows；空白页在输入区上缘提供项目／目录状态与切换入口（弹层列已有项目并复用「打开目录」，正式页为只读标签，行常驻等高防跳位）。侧栏「草稿」分组随之取消。
- 首次发送被接受（真实 SessionId 与宿主建立）即把该页登记为正式会话行且仅一次，不等待模型整轮完成；尚无真实 ID 的打开失败保持草稿、文字与显式预选并可重试（仍是唯一空白页），已取得真实 ID 的失败保留会话行与重试引用。在途目标冻结：期间「新对话」得到独立的下一份空白页；失败页在下一份无任何用户预选（文字、显式项目／模型／权限均无；自动绑定不算）时无损回收空白槽且不抢选中，否则经侧栏「返回未发送草稿」显式恢复。
- 空白页目标解析收窄为：页面尚无绑定（初始）或生效 store 真正变化（丢弃旧 store 项目 ID、按当前目录册重解析）或用户显式选择；同 store 刷新不再重解析。`OpenProjectAsync` 保留创建／复用项目的既有行为；「新对话」与项目「+」只选择已有项目。
- 新增 `DesktopBlankConversationTests` 9 项（复用无行无日志、跨历史／项目保留预选、提升一次、有 ID 失败保留行重试复用、无 ID 失败保持可重试、在途迟到不抢页不改目标、pristine 回收不抢历史页、显式预选不被静默覆盖、非 pristine 显式恢复）；调整被新 UX 取代的草稿侧栏行断言（DesktopSidebarTests 2 项改用提升后的正式行、DesktopStartupOrganizeTests 5 项、DesktopProjectEntryTests 2 项改写为空白页 store 重解析与项目「+」切换语义），保留 store／只读／保护／归属覆盖。
- 已验证（Windows x64）：`dotnet test TinyHarness.Desktop.Tests/TinyHarness.Desktop.Tests.csproj` 101/101 通过、0 警告；Release `win-x64` NativeAOT publish 0 compiler／trimming／AOT 警告，产物 `--offline-smoke` PASS（parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）。TRX、publish 日志与 smoke 记录在 `artifacts/blank-session-20261028/`。真实 GUI 交互（弹层布局、按钮激活态、转正不跳位的实际观感）未验证，App 启动入口无隔离参数、默认配置指向真实用户数据，按真实数据保护要求未启动；阶段四其余未验证项不变。

2026-10-28 空白新对话页评审修复与接手验证 r2（后续独立复评修复及最终结果见下一条；阶段四仍未完成）：

- 修复选中页无损性：后台首发打开失败时，只有未被选中、无文字且无显式项目／模型／权限预选的下一份才可回收空白槽；用户正查看的 pristine 页继续留在槽位，失败页从恢复入口访问，失败后继续输入、切历史再回「新对话」仍可到达原输入。
- 失败草稿改为进程内去重列表，「返回未发送草稿」轮转访问各页；当前空白槽保持独立，恢复页上的编辑与显式预选保留，真实会话建立后从恢复列表移除。两份首发在 manager gate 上排队并先后失败时，两份文字与下一份输入均可到达；不新增持久化或正式侧栏草稿行。
- 菜单、组头「+」、打开目录与自动重绑统一使用每页意图代际：显式操作开始时固定页面并标记在途，所有目标 I/O 后检查意图、`CanChooseWorkspace` 与 store 适用性；自动刷新在配置 I/O 前捕获代际，不给已在途或后来出现的用户意图制造更高优先级。`OpenProjectAsync` 不在迟到时改取另一页，目标默认模型刷新也防护旧意图。项目选择按当前 store 的目录册只读解析，不跨 store 复制项目 ID；正式会话的根与 store 仍固定。`InternalsVisibleTo` 仅供 Desktop 测试访问现有受保护目录解析顺序注入点，不改变依赖、目标框架或 AOT／trimming／reflection-disabled 设置。
- 确定性证据：接手工作树已含 P1 与多失败列表的部分修复，T3 原样执行已绿（`desktop-blank-handoff-t3.trx`），未回退制造红结果。补强后实际剩余目标竞态 5 项先红（`desktop-blank-remaining-red.trx`），修复后 18/18 绿；随后确认「用户显式解析先于自动刷新」1 红／1 绿（`desktop-blank-auto-intent-red.trx`），修复后空白页专项 **19/19** 绿（`desktop-blank-intent-final-green.trx`）。挂起钩子均有超时和退出放行，无 sleep 概率时序。
- 已验证（Windows x64、SDK 10.0.402）：最终 `dotnet test TinyHarness.Desktop.Tests/TinyHarness.Desktop.Tests.csproj` **111/111** 通过、0 失败／跳过、0 编译警告（`desktop-blank-final-r2.trx`）；`dotnet publish TinyHarness.Desktop/TinyHarness.Desktop.csproj -c Release -r win-x64 -o artifacts/blank-session-20261028/desktop-nativeaot-final` 生成 native code、退出码 0，0 compiler／trimming／AOT 警告（`publish-desktop-final-r2.log`）。从仓库外 TEMP 运行最终产物 `--offline-smoke` 退出码 0（`offline-smoke-final-r2.txt`：parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6），仅使用进程级短 PATH，未改机器设置。首版 101/101 与中间 110/110、前一轮产物日志保留为历史证据，不作为最终结果。证据目录为 `artifacts/blank-session-20261028/`。
- 未验证：真实 GUI 的弹层布局、激活态、键盘与转正视觉连续性。生产启动入口没有受支持的隔离参数，默认触及真实用户数据，本轮没有启动 GUI、真实模型或用户日志初始化，也没有新增隔离入口或绕过策略；阶段四其余未验证项不变。

2026-10-28 空白页独立复评修复与最终验证 r3：

- `OpenProjectAsync` 保留自行发现新 store 的既有流程：store 代次未变时可继续；自动刷新推进代次后，解析结果仍等于当前 store 且本页意图未变、仍可选择目录时也继续，不因代次推进静默丢弃显式打开。真正旧 store、更新用户意图或原页已转正仍拒绝。
- 权限菜单的真实选择入口记录显式预选，包括重选默认「只读」同值；不以所有程序性 setter 赋值作为用户意图。离页后首发后台失败不再将该下一页当作 pristine 回收，原只读预选与失败页均可恢复。
- 空白页项目菜单将组头完整路径提示传给选项并绑定 `ToolTip.Tip`，同名不同目录可辨识；保留已有选项构造签名，不新增页面或改变布局。回归通过真实菜单命令核对两项路径提示及各自 ProjectId／执行根。
- 确定性红绿：前两项共 3 个用例先红（`desktop-blank-review-r3-red.trx`：缓存 S1→S2、启动未缓存 store、权限同值重选），路径提示 1 项先红（`desktop-blank-project-tips-r3-red.trx`），修复后空白页专项 **23/23** 通过（`desktop-blank-r3-green.trx`）。首轮绿跑的启动夹具在 VM 自动读配置时替换配置触发过一次共享冲突（`desktop-blank-review-r3-green.trx`）；夹具改为先配置再构造 VM，未改 Core 或压制异常，随后 22/22 及最终 23/23 通过。
- 已验证（Windows x64、SDK 10.0.402）：最终 Desktop 全套 **115/115** 通过、0 失败／跳过、0 编译警告（`desktop-blank-final-r3.trx`）。最终 `desktop-nativeaot-final` Release `win-x64` publish 生成 native code、退出码 0、0 compiler／trimming／AOT 警告（`publish-desktop-final-r3.log`）；从仓库外 TEMP 运行产物 `--offline-smoke` PASS、退出码 0（`offline-smoke-final-r3.txt`：parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6），仅使用进程级短 PATH。r2 的 111/111 与之前日志保留，不作为 r3 最终证据。证据目录仍为 `artifacts/blank-session-20261028/`。
- 未验证边界不变：真实 GUI／用户数据／真实模型均未启动，tooltip 实际观感不由 XAML 编译和 ViewModel 测试推定；没有新增 GUI 隔离入口或绕过策略，阶段四仍未完成。

2026-10-29 Desktop 空白页评审修复：

- 未绑定页面的目标标签只回退自身目录，避免 `null` 项目 ID 匹配侧栏无项目展示组。首次真实会话登记时主动检查未知归属，并在当前 store 的统一加载结束后有界只读补载，解决直接首发创建的项目被误标为缺失；每个未知 ID 自动补查一次，手动刷新仍正常重读目录册。
- 改名与目录册投影更新前后比较目标标签，只向值变化的正式页、唯一空白页及恢复草稿发通知，不增加页面对分组的长期订阅。
- 项目菜单补齐 DshDesktop 原有 `ScrollViewer MaxHeight="288"` 结构，列表独立滚动，目录标题和打开目录入口固定在滚动区外；弹窗与按钮左对齐。
- 新增 4 项 ViewModel 回归与 1 项菜单结构回归；原实现下 3 项 ViewModel 和 1 项结构测试先红，修复后专项 **28/28**、Desktop 全套 **120/120** 通过，0 失败／跳过。Release `win-x64` NativeAOT publish 无 compiler／trimming／AOT 警告，原生产物 `--offline-smoke` 通过。按共享 `banned甄选` profile 清理后人工仅保留改动范围的排版，完整验证证据在 `artifacts/review-fixes-20261028/`。
- 最终静态复审未发现阻断项。未运行真实 GUI、真实模型、用户日志初始化或完整 solution 回归；结构测试不替代滚轮、键盘和对齐实际观感验收，阶段四仍未完成。

2026-10-28 CLI `/new` 对齐 Desktop 空白草稿行为：

- `ChatSessionConsole` 的 `/new` 不再调用会话工厂：释放当前宿主并进入未发送空白态，不创建会话 ID、写入端、日志或项目；首次普通输入沿用既有惰性工厂建立会话并在初始批次绑定归属。运行中排队取消、终态持久化等待与保存警告拒绝丢弃的守卫全部保留；`/switch`／`--resume` 打开既有日志的语义不变；空白态命令、空输入与退出不落盘。本任务不涉及 Core、公开 API 或 session-log 协议变更。
- 测试：改写 `SessionNavigationTests` 中断言旧行为的四项（空白两次 `/new` 无产物且首发恰好创建一个、运行中 `/new` 与审批取消进入空白态），新增五项（既有会话 `/new` 退出无新增日志、空白态 `/switch` 恢复、`/new` 后预检失败保持可重试、`/new` 后空闲取消无产物、`/models` 草稿经工厂随首发应用）；空列表提示文案同步更新。
- 已验证（Windows x64）：`SessionNavigationTests` 20/20（首轮一次失败未复现且未捕获测试名，其后 17 次复跑全绿）、`ChatSessionConsoleTests|ChatModelSelectionTests` 45/45、`SessionCommandTests|ChatCancellationTests|ChatConsoleInputReaderTests` 及合并回归 95/95 通过；CLI Release `win-x64` NativeAOT publish 0 warning，产物 smoke 与隔离原生 chat 脚本（`/new`×2＋命令＋EOF 无日志无目录册、首发创建含归属）通过，证据在 `artifacts/cli-blank-session-20261028/`。
- 范围外发现：`ChatInputRouterTests.RealConsoleHostRoute_AppliesAmendmentDuringInlineApproval` 单测复现 8 轮 2 次间歇失败（Amendments 为空，`ChatInputRouterTests.cs:45`），该路径不经过本次修改代码，未处理。真实模型与真实用户数据未接入。

2026-10-29 CLI 首发凭据预检修复与复核：

- 首次发送的预检边界捕获 `ConfigException`，草稿模型缺凭据时报告错误而不退出输入循环，保留空白态供重选或重试。新增用例先红后绿；补强五类失败切换测试，先建立真实会话再切换失败，断言原 SessionId、前后两轮上下文与唯一宿主保留。
- 最终 CLI 定向离线回归 **96/96** 通过，Release `win-x64` NativeAOT publish 无 compiler／trimming／AOT 警告；原生 CLI `smoke`、空白 `/new` 不落盘及缺凭据首发后继续执行 `/sessions` 均通过。证据在 `artifacts/review-fixes-20261028/`。静态复审无生产阻断项，未执行真实模型或完整 solution 回归。
- 仍有既有工厂顺序限制：真实 CLI 在解析草稿凭据前建立 writer 并保存项目，首发缺凭据可能留下 0 字节日志、锁文件及空项目；本轮仅修复异常逃逸，未重排这条持久化链路。离线注入工厂的无额外日志断言不代表真实工厂也无这些产物。

2026-11-01 Desktop 侧栏分类与排序：

- 添加纯会话／项目分类视图切换；默认按项目显示。项目模式支持项目置顶、同类鼠标拖动排序及按最近会话活动排序；手动项目顺序不随已有项目新增会话而变化。会话置顶在两种视图中通用，项目模式下置顶会话从原项目成员列表移出，显示在置顶项目之后，并在会话行左侧显示置顶气泡标记以区别于项目；气泡隐藏时保留槽位，使标题与项目文件夹标题对齐。纯会话模式以同级的「置顶」和「会话」分类显示，两个分类均按最近活动排序。
- 视图模式与普通项目顺序保存在用户配置目录旁的 Desktop 偏好文件中。项目置顶顺序和会话置顶 ID 由 Core 的 session-store sidecar 持久化，旧 Desktop 置顶字段按 store 首次访问时迁入；不扩展项目目录册或会话日志。
- 未实施多目录项目、自定义分类、分类排序及无项目会话专属底部分类。
- 验证：ReSharper `banned甄选` 清理完成；Release `win-x64` 构建通过，NativeAOT 发布通过。发布时以进程级 MSBuild 属性跳过 Avalonia 统计任务（其许可票据目录受 sandbox 限制），并从 Visual Studio 开发环境加载 C++ 链接工具链。未运行测试；真实 GUI 拖动和布局尚未验证。
- 补充置顶会话行左侧的气泡标记；桌面项目 Debug 构建通过（0 警告、0 错误）。本轮未运行测试或进行 GUI 视觉检查。

2026-11-01 Core 置顶持久化接入：

- 新增 `SessionStorePins` 与 `SessionStorePinStore`，在每个受保护 session store 中用 `pinned-items.json` 保存置顶项目顺序和置顶会话 ID；采用独立 schema、锁内更新和同目录替换，不写入项目目录册或会话日志，供后续 CLI／TUI 读取。Core 提供原子 toggle、幂等目标状态设置和项目顺序调整 API。
- Desktop 在首次加载各 store 时迁移旧 `desktop-sidebar.json` 中的置顶列表；Core sidecar 保存迁移标记并合并尚未导入的旧 ID，成功后 Desktop 清空旧字段。置顶状态读写前复用受保护 store 校验；视图模式与普通项目手动顺序继续留在 Desktop 偏好中。
- 已验证：Core 与 Desktop Debug 构建均 0 警告／错误；Core 定向回归 8/8、Desktop 迁移回归 1/1 通过。CLI 与 Desktop Release `win-x64` NativeAOT 发布成功且无 compiler／trimming／AOT 警告；原生 CLI `smoke` 为 17 步／16 次工具执行，Desktop `--offline-smoke` PASS。Avalonia 统计任务通过进程级 `UsedAvaloniaProducts=` 跳过，以避开 sandbox 不可读的本机许可票据目录。ReSharper CleanupCode 在受限环境中未能完成（访问 JetBrains 用户级文化配置受拒后挂起）；按 `.editorconfig` 与邻近代码手工检查，真实 GUI 和跨进程即时 UI 刷新未验证。

2026-11-01 明确无项目会话与可恢复归档：

- Desktop 空白新对话默认保存为 `NoProject`，可从项目菜单选择已有项目或改回无项目；无项目会话仍保留执行目录和原权限边界。项目模式在底部显示无项目分组，CLI `session list/show` 显示 `no project`。
- 会话日志新增必需 kind `project-assignment`，在初始身份批次保存 `no-project`；scanner、历史分页、恢复投影、writer 与旧日志初始化共用严格状态校验。旧程序不识别该 kind 时会拒绝读取。
- `archived-sessions.json` 独立保存最多 10,000 个 session ID（文件上限 1 MiB），用跨进程锁内重读与同目录替换；Desktop 默认列表隐藏已归档会话，顶部归档视图可恢复。写入前重验受保护 store，store 消失时不创建目录；读取失败会报告并保留已有展示状态。
- 已验证：Desktop Debug 构建（含 Core／Desktop.Runtime）与 CLI Debug 构建均成功，0 警告／错误；CLI 与 Desktop `win-x64` NativeAOT publish 均生成原生代码，0 compiler／trimming／AOT warning。Desktop publish 使用进程级 `UsedAvaloniaProducts=` 并从 VS 18 Community 开发环境加载链接工具链，产物分别位于 `artifacts/session-no-project-archive-20261101/cli-aot/` 与 `desktop-aot/`。
- 未验证：本轮未运行测试、离线 smoke 或真实 GUI。ReSharper CleanupCode 因环境拒绝 JetBrains 用户级配置访问而无法完成；未改共享设置，按 `.editorconfig` 与邻近代码人工检查。

2026-11-02 项目选择器与无项目会话侧栏分类：

- 项目视图底部增加“最近”分类，直接列出未归档、未置顶的无项目会话，不使用项目组分页。置顶会话仍留在“置顶”分类；归档会话仍由归档视图展示。
- 空白对话在未选项目时显示“选择项目”；选择器顶部支持按名称搜索，项目顺序沿用侧栏；底部提供“创建项目”和“不在项目中工作”。“创建项目”复用文件夹选择，选择目录后将其加入或复用为项目并切换当前草稿；选择项目后显示文件夹图标，悬停时显示独立圆形清除按钮。
- 已验证：Desktop Debug 构建通过；本轮未运行测试、NativeAOT publish 或真实 GUI 检查。

2026-11-02 侧栏项目与会话手动排序：

- 手动项目顺序中，新项目插入普通「项目」分类顶部，已有项目位置保持不变。运行中的会话按最近活动排在前面；新建或近期活动的会话进入所属 session store 手动顺序顶部，其余空闲会话可在同一侧栏分类内拖动，后续分页加载的历史会话追加在已有顺序之后。
- 已验证：Desktop Debug 构建通过，0 警告／错误；Release `win-x64` NativeAOT publish 成功。未运行测试、产物 smoke 或真实 GUI 拖动检查。

2026-11-02 Desktop 项目删除：

- 项目三点菜单加入“删除项目”与中断后“继续删除项目”入口及确认弹窗。项目先在目录册中持久标记删除中，再逐一追加匹配的 `project-unassignment` 日志记录；会话变为 `NoProject`，原日志历史、工作目录、权限和执行根保留。运行中的会话或正在切换目标的空白草稿会阻止开始删除。
- 所有关联日志完成解绑后，清除对应 session store 的项目置顶状态，以及 Desktop 偏好里的手动顺序、折叠状态和遗留置顶引用，最后从目录册移除项目。中断、占用或无法读取的日志保留删除标记并可重试；source 生成 reader、scanner 和 writer 均校验解除归属只能匹配当前项目且只能转为无项目。删除项目期间禁止新建会话和改名。
- 通用设置增加默认开启的「项目删除提示」，保存在 `desktop-sidebar.json`；关闭时直接执行删除，确认面板左缘与窗口内容左缘对齐，并垂直居中于目标项目行。
- 已验证：`dotnet build TinyHarness.Desktop/TinyHarness.Desktop.csproj -c Debug --no-restore` 成功，0 警告／错误；Release `win-x64` NativeAOT publish 成功，产物在 `artifacts/project-deletion-20261102/desktop-aot-final/`；`git diff --check` 通过。未运行测试、产物 smoke 或真实 GUI 交互。

2026-11-05 接入 Project 目录册跨进程自动同步（设计见 §16）：

- Core 新增 [ProjectCatalogMonitor](../TinyHarness.Core/Services/Persistence/ProjectCatalogMonitor.cs)（文件元数据基线 + `LoadAsync` 三态检查 + 调用方显式确认基线），`Project`／`ProjectCatalog` 新增 `ContentEquals` 内容语义比较；不改目录册 schema、锁协议与 `ProjectStore` 公开契约，无新增依赖。
- Desktop 新增 15 秒周期独立同步循环：stat 变化才加载目录册，与内存已应用列表逐项目比较，相等仅确认基线（覆盖本地改名／删除手改内存、同内容重写与重复事件），不等才替换 `_projects` 并 `RebuildProjects()`；实例复用、展开态、选中与草稿保持沿用既有逻辑，整理轮／删除流程进行中让位并下轮收敛。失败保留同 store 最后成功快照、有界退避（×1/×2/×4/×8）、通知去重、循环不因目录册或索引故障退出；store 切换作废基线并以 store 比对丢弃迟到结果。`PrepareAsync` 读取失败不再清空 `_projects`（store 真实变化时仍清空，避免旧 store 项目冒充新 store 目录册）；关闭时取消并等待同步循环退出。
- 测试：Core 新增 19 项（Monitor 语义、恢复、取消、并发、平台守卫、`ContentEquals` 全字段）；Desktop 新增 23 项（外部创建／改名／删除、双实例同 store、内容未变不重建、损坏／缺失／共享冲突恢复、同长度改写、连续更新收敛、刷新／删除／整理交错、启动幂等、关闭退出、store 切换迟到丢弃、读取失败保留快照红绿）；[IndexProbe](../TinyHarness.IndexProbe/Program.cs) 新增 `catalog-create`／`catalog-rename` 模式并配 2 项真实子进程测试。
- 独立 Review（并发／持久化／安全，UI／生命周期／回归各一）均无必须修复项；建议项已落实：`InitializeProjectsAsync` 补基线作废、Unchanged 复位失败状态、store 切换失败清空旧快照、移除双重脱敏、Desktop.Tests 补 IndexProbe 引用。本地改名与在途应用的交错回闪、stat 法修改时间精度边界记录为已知限制（§16.2）。
- 已验证（Windows x64）：`TinyHarness.Tests` 1599 通过／4 跳过；`TinyHarness.Desktop.Tests` 299 通过；CLI 与 Desktop Release `win-x64` NativeAOT publish 0 compiler／trimming／AOT 警告，产物位于 `artifacts/project-sync-20261105/`，仓库外运行 CLI `smoke`（17 steps、16 tool executions）与 Desktop `--offline-smoke`（parallel=2、switched=A/B/A/B、refreshed=true、resumedTurns=4、disposedClients=6）均通过；IndexProbe AOT 产物 `catalog-create`／`catalog-rename` 手工验证通过。
- 范围外记录：`DesktopStartupOrganizeTests.ExceptionRoundAfterStoreSwitchDoesNotMarkOldStoreUnboundGroups` 枚举 `Projects` 无快照复制，存在与本轮无关的偶发"Collection was modified"竞态（单独重跑通过）。
- 未验证：真实 GUI 交互、Linux／macOS、断电与设备故障、真实用户数据。

## 16. Project 目录册跨进程自动同步

Desktop 侧栏项目分组来自当前 session store 的 `projects.json`；此前跨进程发现依赖手动刷新。本节记录已实施方案，与 §7 的保存／并发契约衔接。

### 16.1 方案与理由

采用轻量周期检测而非 FileSystemWatcher：Session 索引同步已是 15 秒周期节奏，目录册操作低频，跨进程感知延迟与既有会话同步一致；Watcher 的事件丢失、缓冲区溢出、替换窗口瞬时状态、store 切换后的旧事件与跨平台差异会引入与其收益不成比例的生命周期复杂度。目录册写入全部经 ProjectStore 锁内整体替换，任何一次保存都会推进文件修改时间，"存在性 + 长度 + LastWriteTimeUtc"足以作为快速变化线索；可疑时经 `ProjectStore.LoadAsync` 读取并与内存已应用列表逐项目比较，只有真实内容变化才触发 UI 重建。

- `ProjectCatalogMonitor` 封装 stat 基线与三态检查（Unchanged／Loaded／Failed）；基线确认由调用方显式 `Confirm`，应用被跳过时不得吞掉变化；`Reset` 在 store 切换时作废基线。检查先 stat 后 load：加载期间的新写入会使下轮 stat 偏离基线而再次检查，最终一致。
- Desktop 单例循环与 store 解耦，每 tick 读取当前生效 store；独立于 SQLite Session 索引的故障边界——索引失效停循环不影响项目同步，目录册失败也不退出循环。
- 全部同步路径只读：缺失目录册只读打开既有锁且绝不创建文件；应用仅内存替换与分组重建，不改写目录册或会话日志，不触发整理轮与 Session 重载。

### 16.2 已知限制

- 同长度且处于修改时间精度窗口内的外部改写可能漏检，直到下次写入（NTFS 主平台不可达；FAT／部分网络盘 1–2 秒粒度存在理论窗口；保留 mtime 的备份还原工具同理）。该方案不声称严格无遗漏。
- 本地改名与在途检查交错时，UI 可能回闪旧名称最长一个周期，下一轮检查收敛（≤15 秒）。
- 跨进程感知延迟为一个周期（退避时最长 2 分钟），不追求即时性；`ProjectCatalogMonitor.CheckAsync` 的 Unchanged 结果依赖元数据比较，不提供内容级保证。

### 16.3 验证边界

已验证范围见 §15 2026-11-05 条目；真实 GUI、Linux／macOS、断电与设备故障、真实用户数据未验证，不因离线测试与产物 smoke 推定已完成。
