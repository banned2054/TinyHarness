# 权限架构重构任务记录

状态：三级执行体系实施进行中；当前产品契约为 2026-11-08 冻结契约（见第 0 节），旧章节中被
替代的条款已在原处标注。既有权限重构改动已全部提交（master @ 31ce3d8）；冻结契约的
B1-B8 实施、本轮六项评审修复与 C2 获批读通道（应用侧+fork 分支）在工作区待提交，
尚未执行 Git 写操作；C2 通道未部署本机（能力门 fail-closed），真实环境综合验收未完成。
本文档是权限架构重构任务的主记录与契约源；
状态区分"已验证（真实环境）/ 离线验证 / 未验证 / 推断"。

2026-11-08 冻结契约实施批次的独有实测、验收矩阵与审查关闭记录，已从临时目录去重整理为
[历史验证归档](Archive/permission-validation-2026-11-08.md)。该检查点不替代本页后续修复和部署状态。

## 旧权限自动迁移（2026-11-09）

当前迁移契约以本节为准：旧七模式配置与旧会话统一自动转为人工审批，不再要求用户确认迁移。
Desktop 打开和 CLI 恢复会话时原子保存迁移记录与 /2 人工审批选择，保存失败保持工具阻断。
已有当前格式的明确审批选择不变，常设信任仍独立确认。下文旧迁移确认流程作为历史记录保留。

## 0. 当前产品契约（2026-11-08 冻结）

2026-11-12 初始化时机调整：Desktop 创建或恢复会话时只构造工具定义、应用层分类与配置指纹，
不扫描用户目录、不组合沙箱。首次 workspace Shell 工具在可取消的异步准备阶段完成敏感名扫描与
沙箱组合，再冻结策略身份、进入审批和派发；成功组合在本会话内复用。普通聊天与宿主进程内的
文件工具不依赖 OS 沙箱初始化。扫描逐条枚举并检查当前运行的取消令牌，取消或预算耗尽不返回
部分保护集；初始化失败不执行请求、不自动回退宿主。三级权限、C1/C2/C3 与配置漂移失效规则不变。

本节为 2026-11-08 冻结的三级执行体系契约的忠实转录，是当前权威产品契约；与本文件旧章节
冲突时以本节为准。冻结基线：master @ 31ce3d8，工作区干净。

### 0.1 三级资源与执行矩阵（最终语义）

| 维度 | L1 沙箱基线（默认，自动执行） | L2 沙箱扩展（获批） | L3 宿主（获批） |
|---|---|---|---|
| 执行后端 | WindowsSandbox，同一沙箱账户身份 | WindowsSandbox，同一沙箱账户身份 | HostProcess 宿主身份 |
| 普通文件读 | 广泛可读（不限于工作区）：文件工具应用层不限；沙箱进程 OS 层读边界=用户 profile 根（GAP-1 冻结参数，非 profile 位置 OS 层不读） | 不变（读已广泛） | 按宿主账户权限 |
| 普通文件写 | 工作区内可写 | 获批写入根范围内可写（见 GAP-2） | 按宿主账户权限 |
| 危险资源 | C1 硬拒；C2 读拒；C3 仅禁写 | C1 仍硬拒（不可授权）；C2/C3 可开放 | 无 OS 沙箱隔离，路径检查为应用层可信边界 |
| 网络 | 阻断 | 沙箱内全网：已配置代理时经该代理且坏代理快速失败不回退直连（实测 exit 7），未配置代理时为直连全网；须逐次或显式持久授权 | 按宿主账户网络权限 |
| 审批 | 无 | Human→用户；AI→Reviewer 三决策 | 同左，仅单次授权 |
| 隔离声明 | — | 审批展示"同一沙箱身份、非管理员" | 审批展示"无 OS 沙箱隔离、按宿主账户实际权限、无 UAC" |

- 每次新操作默认按 L1 判断（引擎已无状态逐调用分类，保持）。
- L2/L3 默认 SingleOperation；Turn/Session 须审批卡显式选择；不得自动创建；不跨重启恢复；撤销不回滚。
- 执行完成不得把会话默认层永久改成 L2/L3（现状已满足，加测试固化）。
- 一次请求可直接申请 L2（networkAccess=enabled、越界写请求）或 L3（executionScope=host）；无自动升层；结果未知不重放。
- 批准后不得替换命令/工作目录/后端/扩大资源范围（冻结计划+Revalidate 骨架不变）。

### 0.2 两种审批模式与结果矩阵（与执行层级正交，零改动复用 AiRoutedApprovalProvider/AiReviewer）

| 输入 | HumanApproval | AIAutoReview |
|---|---|---|
| L1 | 自动执行 | 自动执行 |
| L2/L3 | 用户批/拒 | Allow→批准冻结请求（可直接批 L2/L3）；Deny→未执行、不弹人工卡、不换后端重放；EscalateToUser→同一冻结请求交用户（只换审批人不换层级）；超时/不可用/非法→未执行、不自动转人工、不写业务拒绝记忆；取消按取消传播 |

- 超时契约保持：每审核开始解析一次独立总期限并冻结；迟到 Allow 不执行；用户取消不伪装成超时/拒绝。
- 读取授权与外发授权分别判断（能力轴已分，保持）。

### 0.3 危险资源保护分类（三类，写入计划并如实展示）

- **C1 不可授权保护**（读+写均硬拒，不可经任何批准开放）：沙箱账户凭据、执行组件、授权/可信配置、用户配置、会话存储（PermissionSystemProtected 现集合）。作为"广泛读写"的明确例外在审批文案与文档展示。
- OS 读保护例外的真实构成（基线广泛读根=用户 profile 根下的三层如实展示）：① **payload deny_read 集**——C1 宿主密钥路径（trust.json/用户配置/会话存储）、沙箱 home 的 cap_sid 授权映射表（与 SID 表存储同一解析来源 `<home>\cap_sid`）、`.codex` 授权内容（上游 Codex CLI home：auth.json、cap_sid、归档与备份等，整目录 deny；沙箱链路只使用独立命名空间 home，对其无读/执行依赖）及 C2 目录段，随每命令 refresh 同步施加、集合收缩可回收；② **provisioning 既有锁**——`.sandbox-secrets` 由 provisioning 对沙箱组永久 DENY，不进可回收的 deny 簿记（避免集合收缩时被 revoke 撤锁）；③ **执行组件 exe 不做 OS 读拒**——沙箱执行链路需要读取组件，其保护为写完整性（组合 fail-closed + provisioning 写锁）；此为必须显式写明的隐藏例外消解。deny 集为组合期快照：会话内新建的敏感目录不在 OS deny 语义内，文件工具仍由应用层分类器保护；缺失条目由 helper 物化为空目录（无上游 Codex 的机器会留下带组 deny ACE 的空 `.codex`，已知行为）。
- **C2 读保护敏感资源**（L1 禁读，获批 L2 可开放；写同样 L2）：KnownSensitive 命中且非 C1/元数据的越界位置（SensitivePathClassifier 具体名单：.ssh/.aws/.docker/.gnupg/.kube/.sandbox-secrets 等目录段、.env/credentials/id_rsa 等精确文件名、.pem/.key 等扩展名）。新增 ResourceClass 档（如 SensitiveLocation），替换现状"越界读一律 L2"。名单式、不宣称识别所有秘密。OS 读保护精度（2026-11-08 修复轮更新，取代 M4 旧口径）：deny_read 集仍只含目录段级条目，但**基线 deny 集来自组合期的有界敏感名扫描**——枚举读根（用户 profile），把已存在的嵌套敏感目录段与文件名/扩展名级命中一并并入 deny 集（分类器末段盲点已修：以敏感目录本身结尾的路径按敏感判定），沙箱 home 子树与已有 deny 祖先覆盖的命中跳过，重解析点只词法判定不下降，不可读子树跳过（名单如实"不声称完整"）。枚举预算耗尽即组合失败（fail-closed）。**快照边界如实声明**：扫描是组合期快照，会话中途新建的敏感路径要等下次组合进入 OS deny，期间仅应用层文件工具保护；指纹取值器显式传空扫描（快照是文件系统成分而非配置成分，逐次重扫会因文件增删制造失配误撤销授权；快照漂移由策略身份的 denyReadPaths 段在授权复核层表达）。
- C2 不升档的两个边界（契约行为）：附加读/写根内命中敏感名不升档（用户显式信任面；常设信任迁移确认时展示"持久根可能包含敏感位置"）；工作区内敏感文件维持基线+注记，不自动识别工作区内的秘密。
- **C3 仅禁写元数据**（读=L1 基线，写=获批 L2）：.git/.agents/.codex/.aws 四目录（ProtectedMetadata，保持）。
- 不得把所有点目录或含 secret 字样的文件粗暴归类；分类名单≠完整秘密识别，文档说明覆盖范围。
- C1 在 OS 组合器/解析器的 fail-closed 校验保持；deny ACE 白名单参数化仅限支持 C2 的 OS 读保护（见 GAP-1）。

### 0.4 授权默认范围、失效与持久信任迁移

- Grant 模型、三档 Scope、失效矩阵（参数/目录/环境/后端/策略/工作区修订任一变化）沿用 D3，不变。
- **常设设置迁移（新）**：`settings.windowsSandbox.networkPolicy=enabled` 与 `AdditionalWriteRoots` 现状静默扩大 L1，违约。改为：
  1) 引擎不再把常设 networkPolicy=enabled 当作免审批通道（`_networkStandingEnabled` 直通移除）；未确认迁移前，联网/越界写一律逐次 L2 审批。
  2) 配置层检测到常设网络信任或非空附加写根 → 显式迁移待确认态（复刻 permission-migration 修订链模式）：确认界面如实展示授权范围（网络表述与 0.1"网络"行一致；写根列表，并提示持久根可能包含敏感位置）、有效期（持久至撤销）、当前状态；确认后这些设置作为**显式持久授权**运行（引擎按已授权持久信任消费，审批展示与设置页可见、可撤销=清除设置）。
  3) 普通保存设置不解禁；迁移失败保持原数据与阻断。附加读根不触发确认门禁（应用层文件工具读在广泛读基线下不变），但随 Confirmed 注入沙箱并在 OS 层真实扩大读面（profile 外的根可读）——确认展示如实列出读根列表（2026-11-08 B7R-M2 修正；早期"无效果遗留字段"表述作废）。
  4) 派生运行时写根（nuget-cache/tools/tempRoot）注入 ResourceAccessPolicy 为新档 SandboxRuntimeRoot → L1 自动（消除 OS/应用层错位），文档如实说明。

### 0.5 授权治理补齐（阶段 E）

- Grant 生命周期审计：消费 GrantRegistry.DrainEvents（现无消费者），持久化新必需记录 `permission-grant-event/1`（Granted/Consumed/Revoked/Expired/Invalidated + Scope/Capability/Targets 摘要/Reason/Utc；沿用既有脱敏边界）。旧程序拒读符合仓库必需记录哲学。**审计面等价关系（2026-11-08 收口）**：审批卡 SingleOperation 批准不建立登记表 Grant，其审计面=`tool.permission` 记录（outcome 含 grant 提及/单次批准）；`permission-grant-event/1`=登记表 Grant 的生命周期（Granted/Consumed/Revoked/Expired/Invalidated）与可信授权变更（authorization-changed），二者互补、不重复。运行终态 drain 失败以 DisplayWarning 如实呈现（不阻断收尾、不静默吞掉）。
- Reviewer 授权上下文：GrantHistory 条目补 targets 摘要、constraint、ExpiresUtc、策略修订（预算内截断）。
- 拒绝记忆解除：新增显式 API，仅可信授权变更触发（用户批准同一绑定操作、显式撤销/重授、策略身份修订变更、迁移确认）；任意新消息不清空。"重试已拒绝操作"显式入口列延期（粘滞拒绝语义下批准触发解除通常休眠，迁移确认为当下实际生效解除点）。
- 审批展示补齐：申请层级+真实后端；L2"同一沙箱身份、非管理员"与 L3"无 OS 沙箱隔离、按宿主账户实际权限、无 UAC"声明；具体操作+工作目录；实际文件权限（L1 广泛读基线说明、本次批准写根、元数据写）与网络权限（无/全网经代理）；有效期（Scope+ExpiresUtc）；已有活动 Session/Turn Grant 的范围与状态；AI 转人工原因。CLI 与 Desktop 一致（展示事实行由 ApprovalDisplay 单一来源生成；文件工具档如实展示宿主进程内执行、应用层可信边界）。

### 0.6 两端一致（阶段 D）

- CLI run/chat 生产路径注册 PermissionShellTool（沙箱可用时），L3 在 CLI 可达；离线 smoke 组装与生产对齐或显式声明差异；两份 WorkspacePrompts 同步。
- 统一两端保护根集合（Desktop 补 trust.json 保护，取超集）。
- 旧配置/历史会话恢复迁移语义沿用 D9；新常设信任迁移叠加其上（普通保存不解禁、失败保持阻断、不批量重写历史日志、不恢复历史可执行 Grant）。

### 0.7 延期与阻塞（如实记录，不得静默收窄目标）

- **GAP-1 OS 层广泛读与读保护（调查完成，已裁决放行实施）**：只读调查核实外部 helper（fork `banned2054/codex`，pinned `1b647eac`）已具备所需能力，无需修改外部 fork。证据：setup payload `deny_read_paths` 字段被 serde 反序列化并在每命令 refresh 通道同步消费（`setup_provisioning.rs:956-964` 调用 `sync_persistent_deny_read_acls`）；deny-read ACE 按 SID 簿记（`deny_read_acl_state.json`），先应用新集合再撤销 stale 条目，可回收；`read_roots` 经后台 helper 给沙箱用户组授 RX ACL。helper 语义已按 pinned ref 复核：缺失路径物化为目录、拒绝文件系统根、单次失败回滚本轮新增 ACE。冻结参数：基线广泛读根=用户 profile 根（走既有 read_roots 组 RX 通道，沙箱启用时随基线组合，非 grant）；本轮不引入盘根级 read root，非 profile 位置 OS 层不读（文档如实说明；文件工具应用层仍不限工作区广泛读）。deny_read_paths 接线：C1 具体保护路径（用户配置目录/会话存储/trust.json/沙箱 home 与组件/凭据目录，按两端各自保护根集合）+ C2 具体已知敏感目录（分类器目录段命中，遵循 helper 物化或跳过语义）；文件名/扩展名级 C2 无法 OS 化，该维度仅应用层覆盖，文档如实声明。组合校验：read root 与 C1/C2 重叠时必须配对 deny 条目；deny 应用失败 fail-closed（Rust 侧已 error，已核实）；deny 集不得含盘根。
  - 注记 a（引号参数宿主同形畸变，B3e 实测）：含空格/引号的路径作为 argv 元素时，runner 的 Rust-std 风格引号转义（内层引号→`\"`，`winutil.rs:76-121`）+ cmd `/s` 剥外层引号 + CRT 把 `\"` 解析为字面引号三层叠加，产生 `C:\C:\` 类畸变路径与 INVALID_NAME/PATH_NOT_FOUND；**宿主同形命令行同败**（B3e hostctl.cmd 对照），属 cmd/argv 固有语义而非沙箱隔离缺口。验收纪律=CmdSafePath（WindowsSandboxAcceptanceRunner.cs:1320 既有防御：验收路径禁止空格/引号、裸嵌入 commandText）。
  - 注记 b（deny-read 回收范围）：簿记按 codex_home 分立（`<home>\.sandbox\deny_read_acl_state.json`），回收仅覆盖**同一 codex_home 簿记内**的条目；跨 home（如 TCP 环回变体 home）运行或簿记外来源（手动/中断遗留）的物理 ACE 不被回收——保护性物理残留，已实测（C3-P2c：空 deny payload 清簿记、物理 ACE 残留）。同管道重加同条目幂等（`deny_ace_already_present` 不重复施加、路径仍入簿记），无冲突。
  - 端到端归档：RS-02a（沙箱内读宿主创建文件）已由 B3e 达成——`type`/`findstr`/`dir` 裸绝对路径 exit=0（含内容匹配与目录列表），负例 `type <profile>\.codex\auth.json` exit=1 拒绝访问；正式复测归档=C3-P2d。
- **G1 已知限制（真实 OS 表征）**：① setup helper 日志句柄独占（并发读取诊断日志受阻）；② 陈旧 `setup_error.json` 易误导诊断（历史失败报告与新结果并存时以新写入为准判读）；③ 大目录树的继承 ACE 传播可超过 10 分钟（icacls 递归传播耗时，验收等待需留界）。④ deny-read 簿记漂移：空 deny payload 的运行（如 verify）会清空簿记而物理 ACE 残留（见 GAP-1 注记 b）；重加幂等；方向=保护性残留。
- **GAP-2 L2 全范围广泛写（真实 OS 实测修正）**：广泛写与 C1 保护在现 fail-closed 组合校验下互斥（WindowsSandboxComposer.cs:296-316；deny ACE 仅 4 元数据子路径且不可参数化）。实测修正：grant 写 ACE **不存在收敛**——撤销与无 grant 的 refresh 均不回收已落盘写 ACE，残留无限期（此前"写根 ACE 有收敛行为"的记录不成立）；残留对无对应 capability SID 的 token 惰性，隔离由 token 侧维持（实测 2/2 拒绝）。本轮交付相应为：获批写入根机制（含 Shell 写根请求参数，已交付：shell 的可选 `writeRoots` 参数按 Level 2 逐次审批、只允许单次与 Turn 授权、根经 ResolveGrantScope 同一 fail-closed 校验面并入 grant 范围与策略身份）+ 回 L1 隔离的 token+快照级验证；OS 残留如实披露。验收第 15 条按此执行：token+快照级断言 + OS 残留披露，不以 OS ACE 剥离为验收条件。全范围无预声明写=延期，阻塞=外部 helper 无写 ACE 撤销语义（单独立项）。
- **GAP-3** deny ACE 回收（元数据 grant 可用性）——既有外部限制，保持记录。
- **GAP-4** 沙箱 TLS SEC_E_NO_CREDENTIALS——保持记录。
- **GAP-5** 真实模型 Reviewer 效果验收——未获准真实模型调用，保留未验证。
- **GAP-6** GUI 真机交互验证——仅离线 smoke。

### 0.8 验收标准（任务书第八节 20 条按交付范围适配，C 据此设计）

1. 两审批模式 L1 均不请求额外批准；
2. L1 普通越界读成功（应用层；OS 层按 GAP-1 结论）；
3. L1 工作区普通写成功；
4. L1 越界写/C1/C2 读/网络按契约阻断（C2 读阻断=L2 语义）；
5. L2 未获批不执行；
6. L2 获批后写批准根+联网成功且仍为沙箱身份（全范围广泛写按 GAP-2 如实标注）；
7. L3 未获批不执行、获批仅绑定调用；
8. AI 三决策严格分离；
9. Deny 不弹人工卡、技术失败不转人工；
10. 升级不改层级/计划；
11. 单次批准原子消费；
12. 参数/目录/策略/授权修订变化失效旧计划；
13. 取消后旧审批 ID 不可复用；
14. A 会话授权不扩 B 会话；
15. 回 L1 不携带 L2 能力（token+快照级断言；OS grant 写 ACE 残留按 GAP-2 实测如实披露，不作为验收条件）；
16. 子进程取消/回收、不确定即阻断重放；
17. 旧配置/恢复/迁移失败/重启不恢复执行授权（含常设信任迁移门禁）；
18. CLI 正常入口与 Desktop 一致（真实 run/chat 入口，非特制 smoke）；
19. 文件工具与 Shell 资源语义一致；
20. 超时策略与既有迁移/搜索不回归。

验证分类报告：静态检查/离线 fake/真实 Windows 沙箱（专用测试目录）/GUI/真实模型（未授权，保留未验证）。
遗留验证（2026-11-08 收口）：#14"A 会话授权不扩 B 会话"的**真并发双进程探针**列遗留——当前覆盖为
实现静态保证（GrantRegistry 按会话实例隔离）+ 单进程多宿主测试；跨进程并发探针未执行，不声称已验。
实现层已知限制（Low，2026-11-08 D 复查记录、按"记录不修"处理）：① writeRoots 申请含工作区内根时
仍触发 L2 逐次审批（文案"outside the workspace"对该根不准确，fail 方向安全）；② Reviewer 授权上下文
的 targets 摘要未经脱敏直出（审批展示面走 redact，两侧不对称；targets 属授权上下文本就进入该面）；
③ 运行终态 drain 以 CancellationToken.None 在 finally 内等待，writer 卡死时无超时（取消路径收尾可被
延迟，审计可靠性优先的取舍）；④ ScopeRevision 归一化对相对路径依赖进程 CWD（设置加载端其他面有
绝对校验；漂移方向 fail-closed=Unconfirmed）。

## 1. 背景与目标

将现有七种权限模式（逐次确认、只读可申请、项目内读写、完全开放、逐项 AI 审核、只读 AI 审批、
项目内读写 AI 审批）重构为更简单的语义模型，任务要点：

- **双审批模式**：人工审批（HumanApproval）与 AI 自动审核（AIAutoReview）。
- **三级执行分类**：沙箱基线自动、沙箱内附加能力、Host 单次显式申请；Level 是展示/审计分类，非授权来源。
- **Grant 生命周期**：审批授权（Grant）具备显式生命周期与三种范围，取代"临时允许"与单次批准的隐式约定。
- **Reviewer 三决策**：审批人对单次请求给出允许、拒绝、升级人工三种决策；AI 技术失败不转人工。
- **search_text 增强**：检索工具补齐 regex/glob/上下文/输出模式与预算语义，与权限模型解耦。
- **旧模式迁移**：用户配置、会话记录中的旧模式值迁移到新模型；旧会话进入迁移待确认态，确认前禁止模型工具执行。

## 2. 基线

- 仓库：`C:\Code\.Net\TinyHarness`，git `master` @ `a71177c`（"docs: 补充沙箱网络权限与阶段交付记录"）。
- `master` 领先 `origin/master` 4 个提交；Phase 0 起点工作区干净（2026-11-07 确认）。
- .NET 10 + NativeAOT 是必须持续满足的产品约束；所有变更不得回归 trimming/AOT warning。
- 相关既有文档：[Docs/Guides/permission-modes.md](Guides/permission-modes.md)、
  [Phase 2 沙箱联网与开发工具链报告](Archive/phase2-codex-sandbox-report.md)、
  [PAST_PLAN.md](Archive/PAST_PLAN.md)。

## 3. 已确认架构决策（主 Agent 冻结契约，2026-11-07）

以下 D1–D9 为主 Agent 冻结决策的如实转录；D10（文件所有权）记录于第 4 节。

### D1 审批模式

删除 7 值 `SessionPermissionMode` 运行时。新增 `SessionApprovalMode { HumanApproval, AIAutoReview }`，
标签"人工审批"/"AI 审核"，默认 `HumanApproval`。引擎取消 `Mode=null` 传统路径，CLI 同引擎。
`composerModes` 概念取消。

### D2 执行后端与三级分类

`ExecutionBackend = WindowsSandbox | HostProcess`（复用 `ProcessExecutionBackendKind`）。
Level1 = 沙箱基线自动（工作区读写、读根读取、沙箱内 Shell）；
Level2 = 沙箱内附加能力（工作区外路径 Read/Write、受保护元数据写入、网络 enabled）；
Level3 = Host 单次显式申请（绑定完整调用，仅 SingleOperation）。
Level 是展示/审计分类，非授权来源。沙箱未配置/不可用 → `process.execute` 一律 Deny（fail closed），
Host 申请同样拒绝；文件工具仍可用。

> **取代说明（2026-11-08 契约）**：本节三级定义已被第 0.1 节"三级资源与执行矩阵"替代。
> L1 普通文件改为广泛可读（不限于工作区；危险资源按第 0.3 节三类保护）；L2 语义为获批后的
> 沙箱扩展档，全范围广泛写受 GAP-2 外部限制，本轮交付获批写入根机制；工作区外普通读取不再
> 仅因越界归入 L2。"Level 是展示/审计分类，非授权来源"与沙箱不可用 fail-closed 拒绝（含 Host
> 申请、文件工具仍可用）的约定继续有效。

### D3 Grant

`Grant { GrantId, Scope, Capability, Constraint, Targets, PolicyIdentity, CreatedUtc, ExpiresUtc, State }`；
`GrantScope { SingleOperation, Turn, Session }`；
`GrantState { Active, Consumed, Revoked, Expired, Superseded }`。
Turn = 一次已接受的用户任务及其工具循环（run 终态撤销）；
Session 仅当前运行实例、不跨重启、恢复不自动续；
单次授权原子消费（lock，并发不共用）；
参数/目录/环境/后端/策略/工作区修订任一变化 → 旧 Grant 失效；
撤销不回滚已发生修改（如实展示）。Host = 仅 SingleOperation。

### D4 审批协调

`IApprovalProvider` 改形 `PromptAsync(ApprovalRequest) → ApprovalResolution`
（`Granted{Scope}` / `Denied` / `Unavailable`）；
`AllowedScopes` 按能力类别（host 仅单次；元数据写/工作区外文件/网络 `[SingleOperation, Turn]` 默认单次；
Session 需显式选择并明示"仅当前运行实例"）。
AI Reviewer 进 Core：结构化输入（`UserUtterance`/`EffectiveAmendment`/`Revocation`/`TrustedPolicy`/
`SystemNotice`/`PendingOperation`/`ToolEvidence`/`GrantHistory`，带 `RecordId`）
+ 严格输出协议 `{"decision":"allow|deny|escalate","riskLevel","userAuthorization","reason"}`；
Allow → 放行 + Grant；Deny → 拒绝记录、保持未执行、绝不弹人工卡；
EscalateToUser → 同一冻结计划交人审，人批只覆盖原计划；
技术失败 `{ReviewUnavailable, InvalidResponse, Timeout, Cancelled}` → 不执行、不转人工、状态明确、
可手动重试审核。`RiskLevel { Low, Medium, High, Critical }` 与
`UserAuthorization { High, Medium, Low, Unknown }` 是审核维度非授权。
用户明确禁止必须拒绝；拒绝记录绑定操作指纹抑制同卡重弹（跨工具语义等价不声称）。

### D5 引擎决策链

硬限制（worker/未信任只读/NeedsInspection/权限系统资源）→ deny 记忆 →
资源分类（`ResourceClass`：`BaselineWorkspace`/`AdditionalReadRoot`/`AdditionalWriteRoot`/
`ProtectedMetadata`/`PermissionSystemProtected`/`OutsideWorkspace`；
Level2 通道 = grant 驱动沙箱 ACL 扩展，grant 根并入 PolicyIdentity，凭据目录重叠校验沿用）→
Level1 Allow → Grant 匹配 → Ask 按 ApprovalMode 路由。未知能力 Ask fail-closed。
执行入口 Revalidate 复核。

> **取代说明（2026-11-08 契约）**：`OutsideWorkspace` 的读取分类已被第 0.1/0.3 节替代——
> 非保护、非敏感的工作区外普通读取归入 L1 广泛可读基线（OS 层读边界按 GAP-1 结论处理）；
> 已知敏感位置新增 C2 读保护档，L1 禁读、获批 L2 开放；派生运行时写根回注为新档，
> L1 自动（第 0.4 节）。固定决策链、Grant 匹配、Ask 路由与执行入口 Revalidate 复核骨架不变。

### D6 网络真实边界

仅 enabled = 沙箱内全网（经用户代理）唯一可执行档；窄域名授权未实现
（Codex proxy-enforced/restricting SID 未启用），不添加假接口；
审批与文档如实写明全网范围；现有 `networkPolicy`/`proxy`/附加读写根设置原样保留为独立维度。

> **取代说明（2026-11-08 契约）**：常设 `settings.windowsSandbox.networkPolicy=enabled` 与
> `AdditionalWriteRoots` 的静默消费方式已被第 0.4 节替代——引擎不再把常设网络信任当作免审批
> 通道；配置层检测到常设网络信任或非空附加写根即进入显式迁移待确认态，确认后作为显式持久授权
> 运行，普通保存设置不解禁。`enabled` 逐次申请按审批路由、全网（经用户代理）范围表述与
> 窄授权未实现的约定不变。

### D7 文件工具 = 可信宿主代理

四工具（list_files/search_text/read_file/apply_patch）保持宿主进程内执行，
统一经 `ResourceAccessPolicy`（`PermissionSystemProtected` 硬拒）；
文档如实声明该策略属可信计算边界、非 OS 强制；
工作区内秘密文件不自动识别（集中分类器仅覆盖已知保护类）。

### D8 search_text 契约

`mode` `literal|regex`（缺省 literal 行为不变）；
regex = `System.Text.RegularExpressions` NonBacktracking 引擎
（文档列明支持语法子集：无环视/反向引用/条件等；不支持的表达式明确报错，不静默换引擎）；
`pattern` ≤ 512 字符；include/exclude glob（`*`/`**`/`?`/`[...]`），
只过滤已授权扫描集、不扩大访问范围；`caseSensitive` 缺省 false
（同时控制 regex IgnoreCase 与 glob 大小写）；
`contextBefore`/`contextAfter` 0–5 行，相邻重叠合并；
`outputMode` `content|files|count`（缺省 content 兼容现有格式）；
按行匹配，不做跨行正则与替换；
预算 = 现有常量（3000 文件/4MB/240 列显）+ 总输出预算（64k 字符截断标记）+ regex MatchTimeout；
超时/截断/部分计数必须显式标注，不得显示为"无匹配"；
files/count 仍受读取权限约束，策略排除文件不出现在文件名/计数/片段中。

### D9 迁移

旧 7 模式 → 映射（`Review*`/`AutoReview` → `AIAutoReview`，其余 → `HumanApproval`）仅供迁移/展示；
新 selection schema `tinyharness.permission-selection/2 { Revision, ApprovalMode }`；
新必需记录 kind `permission-migration/1 { Revision, CompletedUtc, PreviousMode?, ApprovalMode, PolicyVersion }`
（旧程序自动拒读）；
恢复判定：含 /1 selection 或无 selection 且有历史 run → `MigrationPending`，确认前禁止模型工具执行；
迁移确认展示：新基线允许工作区读写与沙箱 Shell、附加权限走两种审批、FullAccess 已删除、
旧临时授权与 commandRules 免审批规则不自动取得新权限；
旧配置 `defaultMode`/`composerModes` → 检测即置迁移待确认态（不拒载其余配置），
确认后改写 `settings.permissions` 为 `{defaultApprovalMode, useCurrentModel, reviewerProfile, reviewerModel}`；
commandRules 保留解析但运行时不再消费；
networkPolicy/附加读写根/代理设置平移；
`SessionIndexStore` 版本 bump 重建 + 容忍解析；
OfflineSmoke 同步改两模式。

### 裁决补充（2026-11-07 批 1–4 验收后）

- **摘要显示层**：`SessionSummary.PermissionMode` 保留字符串字段 + 容忍映射——新值显示
  "人工审批"/"AI 审核"，旧值经 `LegacyPermissionSelectionMapper` 显示旧标签，未知值显示
  "旧版模式"；仅展示，绝不作为运行时授权。模型类型归 S2，S3 接线。
- **迁移确认 UX**：Desktop = 会话视图通知条 + "确认迁移"按钮（对齐 NeedsInspection 确认交互）；
  CLI = chat 启动检测到待迁移会话时交互 y/n 提示，非交互为明确阻断状态。
- **CLI 冻结新增** `/approval human|ai`：空闲边界切换会话审批模式，经 selection 记录持久化，
  与 Desktop 同机制。
- **验收矩阵校准**：任务书第十五节实际共 51 场景（非 30），按五组对位第 9 节。
- **批 1–4 验收通过**；两处机械适配（索引版本常量化、旧形状往返补 ComposerModes）接受并记录在案。

## 4. 文件所有权（D10，唯一写入者）

| 范围 | 文件 |
|---|---|
| S1（执行侧） | `Services/Runtime/**` 与 `Models/Runtime/**` 全部（含 WindowsSandbox）、`ResourceAccessPolicy.cs`、`SensitivePathClassifier.cs`、`Services/Tools/{ShellTool, PermissionShellTool, ITool, ToolRegistry, ToolArgs, JsonArgs, ReadFileTool, ListFilesTool, ApplyPatchTool, PatchApplier, PatchParser}.cs`、`Models/Tools/**`、`Services/Agent/AgentLoop.cs`（执行派发侧）、CLI `SandboxVerifyCommand` 扩展、执行侧测试 |
| S2（授权侧） | `Models/Permissions/**`、`Services/Permissions/**`、`AgentLoop.Permissions.cs`（AuthorizeAsync）、`ChatSessionHost*.cs` 权限点、`Desktop.Runtime/{DesktopApprovalProvider, DesktopAutoApprovalProvider, DesktopSessionFactory.Permissions}.cs`、CLI `ConsoleApprovalProvider.cs`、权限持久化模型（selection/2、migration 记录类型）、授权侧测试 |
| S3（产品侧） | `SearchTextTool.cs` 全文件、ReadOnlyToolsTests/SearchText 新测试、`Models/Configuration/{PermissionSettings, UserConfigStore, UserConfigValidator}.cs`、`SessionLogStore/SessionLogScanner/SessionResume/SessionIndexStore/SessionSummary*`（selection/2 与 migration 接线、版本门）、`Models/Persistence` 接线与 JsonContext 注册、Desktop UI（PermissionSettingsViewModel/View、SessionViewModel*.cs、PermissionSelectorView、MainWindow 审批卡、WindowsSandbox 设置节联动）、`DesktopSessionFactory.cs`、`DesktopSession.cs`、`OfflineSmoke.cs`、CLI `Program.cs`/`HelpText.cs`/`ChatSessionConsole` 迁移交互、全部文档（本计划、permission-modes.md 重写、Docs/README、中英 README、CHANGELOG、PLAN.md 入口）、产品侧测试 |

共享规则：`AgentLoop.cs` = S1、`AgentLoop.Permissions.cs` = S2；
`Models/Persistence` 记录模型 = S2 写、S3 接线；冲突一律经主 Agent。
批 2 的 `WorkspacePrompts`（CLI 与 Desktop.Runtime 两份）按任务分派由 S3 更新 search_text 描述。

## 5. 进度

- **Phase 0（调查与记录）**：已完成。产品面、配置面、持久化面、search_text、
  SessionPermissionMode 引用清单、迁移矩阵草案调查报告已交付主 Agent。
- **Phase 1（契约冻结）**：已完成。主 Agent 冻结 D1–D10，S3 已转录至第 3、4 节与验收矩阵。
- **Phase 3（S3 产品侧切片）**：批 1–4 已完成并验证。
  - 批1：契约转录 + PLAN.md 入口行（已完成）。
  - 批2：`SearchTextTool` 全契约（mode/regex/glob/context/outputMode/预算与部分标注）+
    `ResourceAccessPolicy` 接线（CLI 与 Desktop 调用点）+ 两份 WorkspacePrompts 描述 +
    `SearchTextContractTests`（已完成）。
  - 批3：`PermissionSettings` 双形状（legacy `DefaultMode`/`ComposerModes` 可空 ⟺ 旧形状；
    `DefaultApprovalMode`/`PendingMigration`）+ `UserConfigStore` 双形状读取与形状随实例写出 +
    桌面消费点适配（已完成；S2 的 `SessionApprovalMode` 已落地，直接使用真实枚举，无需字符串过渡）。
  - 批4：`SessionIndexStore` `CurrentSchemaVersion` 1→2、`NeedsRebuild` 状态、`RebuildSchemaAsync`、
    `permission_mode` 列容忍解析；Updater/Reconciler/Desktop 侧栏联动（已完成）。
  - 批5（文档轮，验收裁决后）：permission-modes.md 重写草稿（文首标注 Phase 5 终稿）、
    第 9 节按 51 场景五组重排、Docs/README 导航与中英 README 权限段落草稿、
    3 个补充测试（全七模式映射、非法正则语法、中文/Unicode 边界）。
- **Phase 4（S3 存储接线批）**：已完成实现并验证（存储/判定/门禁全绿；详见第 6 节归因）。
  - `SessionLogStore.ValidateControlRecord` 按 schema 分发接受 selection/2，新增
    `permission-migration` kind（`PermissionPersistenceJsonContext`）；扫描器同步接受。
  - 投影构建器：/1 与 /2 共用一条连续修订链（迁移记录与伴随 /2 选择共享修订号，两种提交顺序都接受，
    重复迁移拒绝）；`PersistedSession` 新增 `PermissionSelectionV2`/`PermissionMigration`。
  - `SessionResume` 应用 /2 与迁移记录并按 `PermissionSelectionMigration.Evaluate` 计算判定
    （已迁移的 /1 不再触发待确认）；`ChatSession` 新增 `PermissionSelectionV2`/`PermissionMigration`/
    `PermissionMigrationPending`。
  - 宿主最小接线（S2 文件，协调者分派的恢复判定接线所需，请主 Agent 复核）：ComposeRunLoop 优先 /2；
    迁移待确认门禁按验收裁决的严格口径实现——所有模型工具能力一律 Deny（含只读，无例外），固定理由
    "permission migration pending; confirm to enable tool execution"，经工具门在工具边界生效
    （工具保持注册、模型视图不变、work items 持久化与历史恢复不受影响），确认成功解除；
    ChangeApprovalModeAsync/ConfirmPermissionMigrationAsync 修订链取 /1+/2 最大值并镜像会话事实。
  - 新测试 `PermissionSelectionPersistenceTests`（/2 往返、混合链、迁移修订共享与重复拒绝、
    恢复判定矩阵、确认解除门禁）；`ChatSessionPersistenceTests` 两处旧场景补迁移确认（D9 语义）。
- **Phase 2（S1 执行侧 / S2 授权侧）**：并行进行中；其 batch 1 已在同一工作区落地
  （SessionApprovalMode、Grant/GrantScope/GrantState、GrantRegistry、ApprovalRequest、
  PermissionEngine SetApprovalMode/Evaluate/Revalidate、ResourceAccessPolicy 接线），
  S2 仍在对齐 IApprovalProvider 及其测试；S3 未代写其文件。
- **Phase 4–7（迁移收口、验收、发布准备）**：待办，范围由主 Agent 分派后填写。

## 6. 测试命令与结果

| 批次 | 命令 | 结果 | 状态 |
|---|---|---|---|
| 批1 | 纯文档，diff 与 Markdown 结构检查 | 小节齐全、表格渲染正常 | 已验证 |
| 批2 | `dotnet test TinyHarness.Tests --filter SearchTextContractTests` | 22/22 通过 | 已验证（离线） |
| 批2 | `dotnet test TinyHarness.Tests --no-build --filter ReadOnlyToolsTests` | 35/35 通过（literal 兼容回归） | 已验证（离线） |
| 批3+4 | `dotnet test TinyHarness.Tests --filter "PermissionSettingsMigrationTests&#124;SessionIndexStoreVersionTests"` | 12/12 通过 | 已验证（离线） |
| 回归 | 上述 8 个套件合计（含既有 UserConfigStore/SessionIndexStore/Reconciler/SessionPermissionMode 套件） | 143/143 通过（含两处机械适配，见第 7 节） | 已验证（离线） |
| 构建 | `dotnet build TinyHarness.Core / TinyHarness.Tests / TinyHarness.Desktop / TinyHarness.Cli` | 全部 0 error | 已验证（离线） |
| 批5 | `dotnet test TinyHarness.Tests --filter "SearchTextContractTests&#124;PermissionSettingsMigrationTests"`（含 3 个新测试） | 39/39 通过 | 已验证（离线） |
| 批5 | 纯文档 diff 与 Markdown 结构检查（指南/计划/双语 README） | 小节齐全、表格渲染正常 | 已验证 |
| Phase4 | `dotnet test TinyHarness.Tests --no-build --filter "PermissionSelectionPersistenceTests&#124;ChatSessionPersistenceTests&#124;SessionJournalFaultTests&#124;SessionWorkItemTests&#124;SessionPermissionModeTests&#124;UserConfigStoreTests&#124;SearchTextContractTests&#124;SessionIndexStoreVersionTests&#124;ReadOnlyToolsTests"` | 151/151 通过 | 已验证（离线） |
| Phase4 | `dotnet test TinyHarness.Tests --no-build`（全量） | 1785/1791，2 失败＝S2 新增"当前审批模式"系统提示注入与压缩恢复/提示词测试未对齐（CompactionRecoveryTests、WorkspacePromptTests） | 部分验证（失败非 S3 改动面） |
| Phase4 | `dotnet test TinyHarness.Desktop.Tests --no-build`（门禁严格口径后） | 336/343，7 失败＝5 个 AiReview 用例 + ModeSwitchPersists/ProjectExternalWrite，全部位于 S2 进行中的 AI 审核进 Core 与 legacy 剥离面（其已将工厂开场写入切到 /2），非 S3 改动面 | 部分验证 |
| Phase4 | 门禁严格口径复验：`dotnet test TinyHarness.Tests --no-build --filter "PermissionSelectionPersistenceTests&#124;SessionWorkItemTests&#124;SessionJournalFaultTests&#124;ChatSessionPersistenceTests"` | 47/47 通过（含待确认期只读工具同样拒绝、理由固定、确认后恢复） | 已验证（离线） |
| Phase4 | `dotnet test TinyHarness.Tests --no-build`（门禁严格口径后全量） | 1785/1791，仅剩 2 失败＝S2 "当前审批模式"系统提示注入与压缩恢复/提示词测试未对齐（非 S3 改动面） | 部分验证 |
| Phase4 | `dotnet build TinyHarness.Core / Tests / Desktop / Desktop.Tests / Cli` | 全部 0 error | 已验证（离线） |

regex MatchTimeout 的实际触发路径无确定性单测（NonBacktracking 线性引擎难以稳定构造超时；
构造期携带 MatchTimeout 由 `RegexCompilationAndExecute_AgreeOnTimeoutBudget` 覆盖合法路径），
超时标注代码路径以审查为准，记为未验证。
NativeAOT publish 未在本批执行（后续综合验收切片覆盖）。

## 7. 阻塞与风险

- 任务书第十五节 51 场景已按五组校准进第 9 节（主 Agent 2026-11-07 裁决）。
- **[Verified，真实 OS，S1 原文转录]** 元数据写 Level 2 在新工作区可用性取决于 deny ACE 历史：
  工作区一旦被默认策略 refresh 过（含同运行内先行的负对照），`.git` 等元数据目录的 deny ACE
  持久存在且跨会话/跨运行不回收（Rust setup helper 不回收 deny ACE，与写根 ACE 收敛行为不对称，
  icacls 证据：capability SID 显式 DENY 对象级+可继承两条）；此后 metadata 写 grant 在该工作区
  不可用，直至 deny ACE 被外部清理或 helper 增加回收语义。该限制为可用性缺口，安全方向无缺口
  （失败方向=拒绝）。修复需修改外部 fork（codex-windows-sandbox-setup deny-ACE 回收），
  属单独立项，本任务不实施。
- 共享工作区并行编辑：S2 中途重塑 `IApprovalProvider`（PromptAsync(ApprovalRequest)）期间
  Core/Tests 构建短暂中断，S3 轮询等待其落地后完成验证；期间未触碰 S1/S2 文件。
- 为保持既有套件与契约一致，S3 做了两处跨文件机械适配（均为既有断言与 D9 直接冲突），
  请主 Agent 复核：① `SessionIndexStoreTests.RemoveAndClear_KeepSchemaAndMeta` 的硬编码
  schema_version "1" 改为 `CurrentSchemaVersion` 常量；② `SessionPermissionModeTests.
  ConfigurationRoundTripPreservesAllModesAndReviewer` 的旧形状构造补显式
  `ComposerModes = SessionPermissionModes.All`（`ComposerModes` 现为形状判别字段，旧缺省值已移除）。
- 批 4 语义变化：索引旧版本（v1）从"停用等人工介入"改为"自动整库重建（仅索引，不改写日志）"，
  是 D9 的直接要求；更高版本仍保持 Unsupported 拒绝。
- `DesktopSessionFactory.cs` 与 CLI `Program.cs` 在本批期间被 S1/S2 接入 ResourceAccessPolicy；
  S3 的 search_text 接线基于其改动追加，未回退任何并行修改。
- 迁移门禁的契约后果（D9 严格口径）：任何"无审批模式选择且有历史运行"或"含未迁移 /1 选择"的会话，
  恢复后确认前所有模型工具能力一律拒绝（含只读）。既有测试中恢复旧会话后调用工具的场景需先确认迁移
  （已适配：ChatSessionPersistenceTests 两处、SessionJournalFaultTests 一处、SessionWorkItemTests 一处）；
  桌面新会话仍在 Phase 4 保持 /1 开场写入，其迁移待确认语义与 /2 工厂切换随 Phase 5 落地。
- S2 的临时诊断文件 `TempReproDiag.cs` 在共享工作区未完成期间阻塞 Tests 编译，S3 轮询等待其清理，
  不代写不删除。

## 8. 下一步动作

- S2 的 AllowOnce/审批管线与 legacy 剥离在验证窗口内持续演进：门禁测试的 post-confirm 执行断言
  曾因其中间态失败，S2 最新落地后已通过；Desktop 剩余 7 个失败（AiReview 与 /1 开场切换语义）
  属其进行中面，待其收口后复核。
- Phase 5（待主 Agent 在 Phase 4 垂直集成贯通后分派，勿提前动会话流）：
  Desktop 双模式菜单与迁移确认通知条、"确认迁移"按钮、selection/2 与 permission-migration/1
  接线、恢复 MigrationPending 门、OfflineSmoke 双模式适配、CLI `/approval human|ai` 与
  chat 迁移 y/n 交互。
- 摘要显示层映射接线待 S2 的 `SessionSummary` 模型改为字符串字段后由 S3 落地
  （新值"人工审批"/"AI 审核"，旧值旧标签，未知值"旧版模式"，仅展示）。

## 9. 验收矩阵

> **取代说明（2026-11-08 契约）**：本节为 2026-11-07 旧契约（D1–D10）的验收矩阵，仅作历史与
> 待复跑跟踪，不作为当前权威；当前权威验收标准见第 0.8 节，行项映射由测试设计承接。

按主 Agent 校准后的任务书第十五节 51 场景排列（五组）；列"状态"区分
已验证（离线）／部分验证／待办／待复跑（重构前真实 OS 验收通过，重构后需重新执行）。

### 组一：权限与后端（13）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| P1 | 基础工作区读写成功（Level 1 基线自动） | PermissionEngineTests（Level 1 放行） | 离线 | S2 | 待办 |
| P2 | 未授权区域写入失败 | PermissionEngineTests（OutsideWorkspace 路由） | 离线 | S2 | 待办 |
| P3 | 已知受保护资源读取或写入失败 | ResourceAccessPolicyTests + SearchTextContractTests.FileResolvingIntoProtectedPath | 离线 | S1/S3 | 部分验证（search_text 侧已验证；其余工具归 S1） |
| P4 | 子进程不能越过相同资源边界 | WindowsSandboxAcceptanceRunner（metadata-protected/write-denied 用例） | 真实 OS | S1 | 待复跑（既有真实 OS 验收通过） |
| P5 | Sandbox 不可用时不启动 Host（process.execute Deny fail closed） | SandboxFailClosedTests（新）+ sandbox verify 扩展 | 离线 + 真实 OS | S1 | 待办 |
| P6 | Level2 始终留在 Sandbox；metadata-write-grant-allowed | PermissionDecisionLevelTests（新）+ 真实 OS grant 用例（grant-write-positive / grant-revoked-write-denied / grant-identity-changes） | 离线 + 真实 OS | S2/S1 | 部分实现（新工作区可用性取决于 deny ACE 历史，真实 OS 验证 BlockedExternal）；grant-write-positive/grant-revoked-write-denied/grant-identity-changes 三用例真实 OS PASS |
| P7 | Host 必须有对应单次授权 | HostScopeGrantTests / GrantLifecycleTests | 离线 | S2 | 待办 |
| P8 | 参数/目录/环境/后端/策略变化使旧授权失效 | GrantLifecycleTests（失效矩阵）；grant-identity-changes 真实 OS PASS（S1） | 离线 + 真实 OS | S2/S1 | 待办（S2 离线用例；真实 OS 对照已 PASS） |
| P9 | 用户取消后不执行不重放 | SessionRunControlTests + 审批卡 UI 断言 | 离线 | S2/S3 | 待办 |
| P10 | 单次授权不能被两个并发请求消费 | GrantLifecycleTests（并发消费） | 离线 | S2 | 待办 |
| P11 | A Session 授权不能被 B Session 使用 | GrantRegistryTests（实例隔离） | 离线 | S2 | 待办 |
| P12 | Turn 到期后新执行不携带旧能力 | GrantLifecycleTests（Turn 终态） | 离线 | S2 | 待办 |
| P13 | 结果未知时不自动重试 | AgentLoop 执行派发测试 | 离线 | S1/S2 | 待办 |

### 组二：网络（8）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| N1 | 离线负对照 | sandbox verify --network restricted | 真实 OS | S1 | 待复跑（既有 17/17 真实 OS 验收通过） |
| N2 | 获准网络目标正对照 | sandbox verify --network enabled | 真实 OS | S1 | 待复跑（既有 8/8） |
| N3 | 未获准目标负对照 | verify restricted 拒绝用例 | 真实 OS | S1 | 待复跑（既有） |
| N4 | 直接 IP/绕过代理/环回等协议路径验证 | verify 网络用例（AcceptanceLimits 端点） | 真实 OS | S1 | 待复跑（既有部分覆盖） |
| N5 | 代理失败不退回直连 | verify 坏代理快速失败用例（非挂起） | 真实 OS | S1 | 待复跑（既有 exit 7 对照） |
| N6 | 窄网络授权不会变成全网（窄请求→明确错误，绝不静默放宽） | 审批协议无窄授权字段断言 + 组合期 fail-closed | 离线 | S2/S1 | 待办 |
| N7 | TLS 与真实工具链问题单独记录 | phase2 交付报告 §7 + 指南保留 | 文档 | S1/S3 | 已记录（文档） |
| N8 | HTTP 可达不代表 HTTPS/NuGet restore 已通过 | 指南网络节如实声明 | 文档检查 | S3 | 已验证（本批文档草稿） |

### 组三：文件与搜索（12）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| F1 | read/list/search/patch 权限一致 | ResourceAccessPolicyTests（四工具）+ S3 search 侧 | 离线 | S1/S3 | 部分验证（search_text 侧已验证） |
| F2 | glob 不扩大目录范围 | SearchTextContractTests.IncludeGlob_NarrowsScanSet / ExcludeGlob_Removes | 离线 | S3 | 已验证（本批） |
| F3 | regex 不改变资源访问范围 | RegexMode 用例 + 既有 SearchText_Prepare_RejectsEscape 回归 | 离线 | S3 | 已验证（本批 + 回归） |
| F4 | literal 默认行为兼容 | LiteralExplicitMode_MatchesDefaultBehavior + ReadOnlyToolsTests 35/35 | 离线 | S3 | 已验证（本批） |
| F5 | 合法/非法/不支持正则 | RegexMode_MatchesPerLine / InvalidSyntax / UnsupportedSyntax / Over512Characters | 离线 | S3 | 已验证（本批） |
| F6 | 大小写/中文/Unicode 边界 | CaseSensitiveToggle_IgnoreCaseFollows + UnicodeAndChinesePatterns_MatchPerLine | 离线 | S3 | 已验证（本批） |
| F7 | 上下文合并与输出截断 | ContextLines_MergeAdjacentWindowsAndMarkMatches + OutputBudgetExceeded_TruncatesWithExplicitMarker | 离线 | S3 | 已验证（本批） |
| F8 | content/files/count 语义 | OutputModeFiles / OutputModeCount 用例 | 离线 | S3 | 已验证（本批） |
| F9 | 超时/取消/超长行/增长文件/扫描预算 | 预算截断、240 列显、部分计数已验；MatchTimeout/取消/增长文件无确定性单测 | 离线 | S3 | 部分验证（余项代码审查 + 综合验收） |
| F10 | 敏感结果不经文件名/计数/上下文泄露 | FileResolvingIntoProtectedPath_IsSkippedCountedAndNeverNamed + 既有 worker 排除用例 | 离线 | S3/S1 | 已验证（search 侧） |
| F11 | 链接/重解析点/目标变化 | 既有 symlink 用例（随 35/35 运行）+ ResourceAccessPolicy 最终路径比对 | 离线 + 真实 OS | S3/S1 | 已验证（离线） |
| F12 | 计数截断不显示为完整统计 | OutputModeCount_PartialCountIsExplicitlyMarked | 离线 | S3 | 已验证（本批） |

### 组四：Reviewer（10）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| R1 | 合理小范围请求可 Allow | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R2 | 用户明确禁止时 Deny | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R3 | 相关但超委托范围时 EscalateToUser | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R4 | Deny 不弹人工批准 | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R5 | Escalate 只执行原计划 | AiReviewerRoutingTests（新） | 离线 | S2 | 待办 |
| R6 | 超时/非法 JSON/模型不可用保持未执行 | AiReviewerFailureTests（新） | 离线 | S2 | 待办 |
| R7 | 不可信内容不能伪造用户授权 | AiReviewerProtocolTests（来源标记 + RecordId 注入断言） | 离线 | S2 | 待办 |
| R8 | 用户撤销优先于旧授权 | AiReviewerRoutingTests（撤销顺序） | 离线 | S2 | 待办 |
| R9 | 文件读取与外发授权分开判断 | AiReviewerProtocolTests（分开判断断言） | 离线 | S2 | 待办 |
| R10 | 不把 mock 输出当作真实模型抗注入证明 | 验证诚实性条目：指南声明 + 真实模型 opt-in 综合验收 | 文档 + 真实模型 | S3/主 Agent | 部分验证（指南已声明；真实模型验收待综合阶段） |

### 组五：迁移（8）

| # | 场景 | 测试名/验证方式 | 验证方式 | 负责 | 状态 |
|---|---|---|---|---|---|
| M1 | 七旧模式全部有明确处理 | PermissionSettingsMigrationTests.LegacyDefault_AllSevenModesMapExplicitly | 离线 | S3 | 已验证（本批） |
| M2 | ReadOnly 不静默获得写权限；迁移确认展示新基线允许工作区写入 | PermissionSettingsMigrationTests + 迁移确认 UI（Phase 5） | 离线 | S3 | 部分验证（配置层已验；UI 待 Phase 5） |
| M3 | FullAccess 不恢复自动 Host | 引擎 Host 意图测试（S2）+ 指南声明 | 离线 | S2/S3 | 待办 |
| M4 | 旧常设网络与命令规则不成为隐蔽授权（networkPolicy 平移须展示、commandRules 不再消费） | 引擎不消费 commandRules 断言（S2）+ networkPolicy 平移测试（S3） | 离线 | S2/S3 | 部分验证（配置平移随 UserConfigStoreTests；引擎断言待 S2 切换后） |
| M5 | 历史日志仍可展示 | SessionIndexStoreVersionTests（容忍解析）+ 旧日志展示映射接线（Phase 5） | 离线 | S3 | 部分验证（索引侧已验） |
| M6 | 迁移失败保持阻断 | MigrationGateTests（新，Phase 5） | 离线 | S3/S2 | 待办 |
| M7 | 运行期 Grant 不跨重启自动恢复 | GrantRegistryTests（不持久化断言） | 离线 | S2 | 待办 |
| M8 | Legacy 解析代码不能成为旧模式运行入口 | 引擎 SetApprovalMode 全量切换后编译 + 引用断言（S2）；配置层旧形状仅映射不进运行时（S3 已验） | 离线 | S2/S3 | 部分验证（配置侧已验） |

## 10. 提交拆分与本轮修复（2026-11-07）

本节为 2026-11-07 的复核结果；以上批次状态保留为历史实施记录，不代表整项功能已经验收。

### 已提交的独立部分

- `e0d4eb5`：记录权限架构契约与实施进度。
- `bcf6265`：独立搜索增强，包含搜索契约测试与 CLI/Desktop 提示词；不含新资源策略接线。
- 其余权限、执行、迁移及产品适配当时继续留在工作区，本轮未提交；该批改动后续已全部提交
  （截至 master @ 31ce3d8，工作区干净）。

### 本轮修复

- 执行前复核必须具有对应的有效授权；不再因找不到活动 Grant 而直接放行。
  已获批调用绑定一次性内部派发凭据及授权修订，撤销、模式变化、授权到期或并发消费失去授权时不执行。
- 冻结能力与当前快照必须一致；新增或撤销能力均使旧计划失效，不能继续携带已撤销的路径权限。
- 相同审批模式的重复应用保持幂等，不撤销 Session Grant；显式切换仍撤销授权。
  相同已保存选择不重复追加修订，迁移重复确认在宿主空闲锁内拒绝。
- Host 不接受 Turn/Session Grant；网络申请不会随普通文件基线自动放行，也不覆盖系统保护路径的硬拒绝。
- 迁移测试先构造并关闭旧日志写入端，再恢复宿主接管唯一 writer；不放宽生产存储锁。
- Desktop 测试改为双审批模式契约；旧权限断言区分基线允许与 Grant 匹配，不恢复旧七模式运行行为。
- CLI 离线 smoke 使用基线文件策略及六次显式 Host 单次审批；保留工具数和审批数断言，失败时报告实际计数。
  该脚本测试组合不是机器沙箱可用性或 OS 隔离证明。
- 仅对本轮修改的 C# 文件执行 `banned甄选` cleanup 并复核；没有修改共享格式、依赖或 AOT 设置。

### 验证结果

| 验证 | 本轮结果 |
|---|---|
| Solution Debug 构建 | 0 error；8 条 CS4014 来自基线已有测试，不属于本轮新增警告 |
| 权限与迁移针对性回归 | 135/135 通过，包含 7 个新增授权回归用例 |
| Core/CLI 全量测试 | 1796 项：1784 通过、3 失败、9 跳过 |
| Desktop 全量测试 | 342 项：341 通过、0 失败、1 跳过 |
| CLI 最后增量回归 | 命令行、审批与提示词 40/40 通过 |
| CLI/Desktop win-x64 NativeAOT | 两端本地 publish 成功，未见 AOT/trimming warning |
| 原生产物离线 smoke | 源码目录外 CLI `--help`、完整 `smoke` 与 Desktop `--offline-smoke` 均 exit 0 |
| Diff | `git diff --check` 通过，第三组暂存区为空 |

标准 `dotnet test` 的 testhost 在本环境因监控父进程时拒绝访问而中止；上述测试通过安装包内
`NUnit.Framework.Api.NUnitTestAssemblyRunner` 直接执行原程序集、原夹具与断言，不等同于标准 testhost 已通过。
Core 剩余三项为 `ShellToolTests` 的超时/取消和 `WindowsSandboxAcceptanceTests.SurvivorsAfterCancellation_FailTheCase`；
在未含权限重构的对照副本中同样失败，当前环境的 `cmd` 无法解析其依赖的 `ping` 命令。
本轮没有为得到全绿而修改这些既有进程夹具或机器配置。

CLI 原生 smoke 通过了工具、补丁、进程、日志恢复、项目指令、SQLite 索引、任务控制、预算与响应恢复检查；
Desktop smoke 不初始化窗口，不能替代真实 GUI 验收。具体日志与测试 XML 位于本地
原 `.tmp/permission-repair-20261107/`；原始日志已于 2026-11-10 清理，历史结果保留在本节，
多轮失败与最终通过的补充核对见[去重归档](Archive/local-validation-records-2026-10.md)。

### 仍未完成的范围

截至本节记录时，迁移确认通知条与按钮、CLI `/approval` 和迁移交互、历史模式展示仍待产品收口；
后续工作区变化以第 11 节为准。
真实 GUI、真实模型 Reviewer 效果及新权限链路的机器沙箱综合验收尚未完成。
第 7 节记录的外部 setup helper 元数据 deny ACE 回收限制仍存在，本轮未修改外部 fork 或机器安全配置。
因此，不能以本轮修复和离线结果宣称整项权限架构任务完整完成。

## 11. 当前工作区可提交性收口（2026-11-07）

> **状态更新（2026-11-08）**：本节所述工作区改动后续已全部提交（master @ 31ce3d8，工作区干净）；
> 本节保留为当时记录，其中"未执行 Git 写操作、暂存区为空"等为当时工作区快照表述。
> 未验证事项照实保留。

本轮最终范围为修复现有工作区并完成必要本地验证，不继续按长计划补齐功能或综合验收。
先前已写入工作区的迁移通知、CLI `/approval`、历史标签和审批范围接线保留；
其存在不代表 Phase 5 或整项权限架构已经验收完成。第 5–10 节保留当时记录。

### 修复与核对

- 修正新增接线中的类型、命名空间、跨程序集可访问性及旧菜单引用错误；保留显式 null 超时语义。
- CLI 的当前宿主使用独立 GrantRegistry，执行工具与引擎接入同源授权及沙箱冻结上下文；
  候选宿主失败时保留旧引擎与 Shell 配对。`t/turn` 正确进入审批回复通道。
- 配置和会话各自确认迁移；普通保存不解除配置门禁，失败保持阻断。
  旧配置字段显式为 null 时仍按旧形状处理，混合形状拒绝。
- 待迁移会话不能通过直接保存 /2 模式选择绕过重启后的门禁，必须先原子保存迁移确认记录。
- 修正取消异常断言及恢复夹具的 writer 归属，恢复读回断言使用实际持久化数据。
- 本轮涉及的 C# 文件使用 `banned甄选` cleanup，并检查实际输出和 diff；未修改共享格式设置。

### 验证边界

| 验证 | 最终结果 |
|---|---|
| Solution Debug 构建 | 0 error；8 条既有测试 CS4014 warning |
| Core/CLI 标准全量测试 | 发现 1844 项，执行的 1835 项全部通过；其余 9 项为跳过或显式用例，0 失败 |
| Desktop 标准全量测试 | 发现 342 项，执行的 341 项全部通过；1 项显式测量未运行，0 失败 |
| 迁移门禁与 CLI 审核针对性回归 | 17/17 通过，包含待迁移模式切换拒绝、持久化读回和取消传播 |
| CLI/Desktop win-x64 NativeAOT | 最终源码两端 publish 成功，未见 AOT/trimming warning |
| 最终原生产物离线 smoke | 源码目录外执行 CLI `--help`、完整 `smoke` 与 Desktop `--offline-smoke`，均 exit 0 |
| Diff 与暂存区 | `git diff --check` 通过；当时未执行 Git 写操作、暂存区为空（该批内容后续已提交，master @ 31ce3d8） |

测试计数按 TRX 的发现与执行数记录。CLI smoke 验证 16 次工具执行与明确审批的脚本闭环；
Desktop smoke 验证并行会话、切换、恢复和释放，不初始化窗口。

标准 `dotnet test` 在本轮环境可用。验证进程继承的 PATH 过长，会使 `cmd` 无法解析
`ping` / `where`；仅精简本轮验证子进程的 PATH 后，原进程夹具和 NativeAOT 工具链可正常运行。
没有修改机器环境变量、沙箱账户、ACL、网络策略或现有进程夹具。

真实 GUI 操作、真实模型 Reviewer 效果、机器沙箱综合验收和外部 setup helper 的 deny ACE
回收仍未完成。沙箱可用性是组合时快照；初始组合失败后在同一入口通过 `/trust` 修复，
不保证旧引擎同步启用进程执行，失败方向为拒绝。这些边界不能被离线 smoke 替代。
验证日志与 TRX 原保存在 `.tmp/permission-finish-20261107/`，已于 2026-11-10 清理；
本节保留结果及边界，补充核对见[去重归档](Archive/local-validation-records-2026-10.md)。

## 12. 六项评审修复与 C2 获批读通道（2026-11-08，工作区待提交）

本节记录冻结契约 B1-B8 实施之后的评审修复批次与 C2 获批读授权通道；全部改动在工作区待提交，
尚未执行 Git 写操作。

### 12.1 六项评审修复（已实施，定向+离线测试通过）

1. **常设网络免审批双条件**：网络免审批要求"常设网络策略 enabled 且常设信任 Confirmed"
   同时成立；`restricted` 下仅写根确认不再免网络审批（此前写根确认状态被网络免审批复用）。
   PermissionEngine 新增 standingNetworkEnabled 独立门。
2. **C1 保护双向重叠**：申请根**覆盖** C1 保护对象（用户配置/信任文件/会话存储的父目录等）
   一并硬拒，不再只查申请根自身；检查覆盖四个写根来源（per-call 申请、常设附加写根、
   活动 Grant、用户配置的运行时根），全部按组合实例实际路径比对。
3. **撤销常设授权的会话失效（组合指纹守卫）**：发送入口与每次工具派发前核对组合时指纹与
   当前可信配置（CLI 与 Desktop 两端均重读可信配置；读取/解析失败同样失效）；失配即
   `PermissionEngine.RevokeGrants()`（递增授权修订+撤销 Grant+一次性派发凭据失效）+
   会话失效闩+提示重新打开会话；已启动进程沿用现有取消回收，不自动重组合。指纹覆盖所有
   决定授权与执行边界的配置，不含模型/UI 等无关项。
4. **permissionSystemRoots 细化**：整沙箱 home 收缩为四个敏感子路径（`.sandbox-secrets`/
   `.sandbox`/`cap_sid`/`.sandbox-bin`，取自 components 实际属性）；默认 tmp/tools/nuget-cache
   运行时根恢复 SandboxRuntimeRoot 分类与 L1 免审批；OS ACL 与组合期完整性校验仍保整 home，
   两层集合分离。
5. **真实沙箱测试门禁**：`CliProductionEntryRealSandboxTests` 改环境变量门禁
   （`TINYHARNESS_REAL_SANDBOX_TESTS`）；探针根必须显式指定（`TINYHARNESS_REAL_SANDBOX_PROBE`，
   无默认值——%TEMP% 有历史 ACL 污染记录）；负向隔离探针先验探针根 ACL 前提
   （不满足=Inconclusive，不计通过）；代理参数化（`TINYHARNESS_REAL_SANDBOX_PROXY`，
   默认 `http://127.0.0.1:7890`）。默认 `dotnet test` 不触机器沙箱、固定用户目录与外网。
6. **C2 获批读授权通道**：见 12.2。

### 12.2 C2 获批读授权通道（协议 v7）

**动机与主体**：沙箱组 DENY 与 read 授权同主体冲突且显式 DENY 压过 ALLOW，获批 C2 读不能经
既有组通道放行。引入不在 deny 组内的专用授权主体（fork 侧新账户对 TinyHarnessAuthOff/On
与组 TinyHarnessAuthUsers，仅授权执行时使用）。

**协议 v7 语义**：payload 新增 `authorized_read_paths`（空=普通执行；普通执行仍 version 5
行为零变化）；helper（fork 分支 `tinyharness/c2-authorized-read`，基于 pinned `1b647eac`）
接受 5/6/7。获批 C2 读路径不再并入 read_roots 组通道（语义收敛到授权通道）。

**helper 侧 C1 可信名单**：授权执行前 helper 从 `<home>\.sandbox-secrets\protected_paths.json`
（应用写入；沙箱组对该文件永久 DENY，不可篡改）读取保护清单；缺失/解析失败/与授权读双向
重叠（规范化+最终落点比对）即拒绝执行——不信任 payload 自报名单。

**应用侧三道门**：

- 能力门：`sandbox_users.json` 版本 ≥7 才放行授权执行；helper 能力未声明即拒绝。
- 禁用门：恢复失败或进程回收状态未知→禁用授权主体并持久化，后续授权执行入口阻断，
  仅成功恢复清除。
- 串行门：跨进程 Global 互斥；窗口=施加授权→执行→确认目标及子进程回收→恢复 refresh→
  释放；恢复=空授权 v7 payload 撤销全部授权 RX。

**应用侧 C1 断言**：authorized 集与组合期 C1 实例双向重叠即拒绝。

**身份隔离**：授权执行的 policy identity 含授权集；审批复用键不与普通执行混用。

**部署与回退方案草稿（未执行）**：替换 helper+runner 二进制 → Full provisioning → 应用
能力门放行；回退=换回 v5 二进制；残留清理=uninstall 路径。

**部署状态（如实）**：fork 改动在本地分支完成构建与单元测试（5 个失败均为环境项：符号链接
权限/本机无 pinned runner/锁级联），未部署本机——本机 helper 仍为 pinned v5、生产 home
marker 仍 v5；当前未部署状态下授权执行在能力门被拒绝（fail-closed，普通沙箱行为不变）。
真机端到端验收未做。已知机器残留：fork 测试套件的 elevated 测试在本机自动创建了授权
账户对/组/5 条防火墙规则（惰性状态，不影响生产 v5 工作流），待部署决策时复用或清理。

**2026-11-08 修复轮补记（工作区待提交）**：① 授权执行账户已接启动链——后端按同一份
deny/获批分流纯函数为获批读执行选择专用授权账户对（离线↔TinyHarnessAuthOff、生效在线↔
TinyHarnessAuthOn），凭据读取器要求 v7 代与专用条目（缺失/降代明确失败，绝不回退普通沙箱
账户；普通执行绝不使用授权账户）；管道 ACL 与私有桌面按返回账户 SID 构造，授权离线账户
携带 fork 侧离线防火墙规则面，读授权不随带开放网络。此前 Rust `authorized_sandbox_creds`
与授权账户对无任何真实调用方、C# 凭据模型缺 authorized 记录——已修。② fork 恢复顺序
回归修复：grants 重排到 deny 重同步**之前**（revoke_ace 按 SID 无差别删除显式 ACE，旧顺序
会在撤销陈旧授权时连带删掉刚恢复的 deny 且报告成功）；撤销失败向上传播且簿记保留（下次
同步重试，revoke 幂等），授权授予的回滚失败并入错误上下文。应用侧恢复失败→禁用门的既有
链路不变，现在能收到真实失败。

### 12.3 验证口径与未完成事项（如实）

- 已验证：应用侧定向+离线测试集全绿、六项目构建 0 错误；fork 侧=cargo build+单元测试通过
  （5 个失败均为环境项）。全量套件由主会话执行中，结果见验证记录，本文不代记。
- 未验证：全量套件、NativeAOT publish、原生产物 smoke、真实沙箱、GUI。
- 真实环境综合验收与 #14 真并发双进程探针保持未完成；本轮为二者准备了环境门禁+显式探针根+
  ACL 前提的载体（12.1 第 5 项），执行待部署后真机验收。
- L2 全范围广泛写延期缺口不变（GAP-2）：本轮获批写根机制不改变该延期状态，验收仍按 0.8
  第 6/15 条口径执行，不将获批写根表述为全范围广泛写。
