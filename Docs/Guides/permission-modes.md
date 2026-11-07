# 权限与审批

> 随权限架构重构更新（冻结契约 D1–D10，2026-11-07）。实现与验证进度见
> [权限架构重构任务记录](../permission-architecture-plan.md)；离线测试不替代真实模型和机器沙箱验收。

审批只有两种模式，工具执行按三级分类路由；授权以显式 Grant 生存、消费或失效。
本页同时说明网络真实边界、search_text 新参数、旧配置迁移与已知限制。

| 审批模式 | 自动执行 | 需审批的操作 | 审批人 |
|---|---|---|---|
| 人工审批（HumanApproval，缺省） | 沙箱基线操作 | 全部 Level 2 附加能力与 Host 申请 | 用户 |
| AI 审核（AIAutoReview） | 沙箱基线操作 | 同上，先交独立 AI 审核员 | AI，仅升级决定转交用户 |

审批模式只选择审批人，不扩大或收窄任何资源授权；它不携带旧的"只读/项目内读写"语义，
也不再提供完全开放模式。

## 三级执行分类

执行后端只有两种：`WindowsSandbox`（Windows 沙箱账户与 ACL 隔离）与 `HostProcess`
（TinyHarness 进程身份直接执行，无操作系统隔离）。每个待审操作按资源分类归入三级；
Level 是展示与审计分类，不是授权来源。

| 级别 | 内容 | 处理 |
|---|---|---|
| Level 1 沙箱基线 | 工作区读写、可信附加只读根的读取、沙箱内 Shell | 自动执行，不询问 |
| Level 2 沙箱内附加能力 | 工作区外路径读写、受保护元数据（.git/.agents/.codex/.aws 等）写入、网络 `enabled` 申请 | 按当前审批模式询问 |
| Level 3 宿主执行 | `HostProcess` 后端的单次显式申请，绑定完整调用 | 始终询问，仅允许单次授权 |

新基线意味着：**工作区内的读写与沙箱内 Shell 默认自动执行**。这是迁移确认时会明确告知的
基线变化；附加能力（出界路径、受保护元数据、联网、Host）仍需逐次审批。
受保护元数据写入的 grant 在新工作区的可用性取决于 deny ACE 历史
（限制详情见[权限架构重构任务记录](../permission-architecture-plan.md)第 7 节）。

沙箱未配置或不可用时，`process.execute` 一律拒绝（fail closed），Host 申请同样拒绝，
绝不回退宿主执行；文件工具不受影响。执行入口在运行前复核（Revalidate），
授权凭据与冻结计划不一致即拒绝。

## 审批与 Grant 生命周期

每次批准建立显式 Grant：`Grant { GrantId, Scope, Capability, Constraint, Targets,
PolicyIdentity, CreatedUtc, ExpiresUtc, State }`。授权范围三档：

- **SingleOperation**：只覆盖与指纹完全匹配的一次调用，原子消费，并发请求不共用；
- **Turn**：存活到本次运行终态即撤销，不跨重启恢复；
- **Session**：仅当前运行实例内存活，需用户显式选择并明示"仅当前运行实例"，
  不持久化为可执行权限，不跨重启恢复。

可提供的范围按能力类别限定：Host 只允许单次；Level 2 各类默认单次、可选择 Turn；
Session 必须显式选择。参数、目录、环境、后端、策略或工作区修订任一变化都会使旧 Grant
失效；补充输入生效、信任变化、模式切换或用户撤销都会显式撤销。撤销不回滚已发生的修改，
界面如实展示。拒绝记录绑定操作指纹，同一张审批卡不会反复弹出；不同工具间的语义等价
不做声称。人工审批是独立、无工具的请求：待审核的参数、工具输出等不可信内容不是授权来源，
不能伪造用户授权。

## AI 审核三决策与失败处理

AI 审核员收到结构化输入（用户发言、已生效补充、撤销、可信策略、系统说明、待审操作、
工具证据与授权历史，均带来源标记与 RecordId），输出严格协议
`{"decision":"allow|deny|escalate","riskLevel","userAuthorization","reason"}`：

- **allow** → 放行本次调用并建立 Grant；
- **deny** → 记录拒绝、保持未执行，**绝不转交人工审批卡**；
- **escalate** → 同一冻结计划交人工裁决，人工批准只覆盖原计划。

技术失败（审核不可用、响应非法、超时、取消）保持未执行、不转人工、状态明确，
可手动重试审核。`riskLevel` 与 `userAuthorization` 是审核维度，不构成授权。
用户明确禁止的操作必须拒绝；用户撤销优先于旧授权。

## 网络真实边界

Shell 工具的可选参数 `networkAccess`（`default` 或 `enabled`）申请该次调用在沙箱内联网：

- `enabled` 是**沙箱内的全网访问**（可能经用户配置的本地代理，如 127.0.0.1:7890），
  不是仅限 NuGet、某个域名或某种协议的窄授权；审批文本以 `[联网]` 前缀如实标记。
- 窄域名/端口授权**未实现**（Codex proxy-enforced 与 restricting SID 能力未启用），
  不提供窄授权接口；窄请求只会得到明确错误，绝不静默放宽。
- 可信设置的 `settings.windowsSandbox.networkPolicy` 仍是独立维度：`restricted`（缺省）
  下每次 `enabled` 申请都按审批模式路由；`enabled`（常设信任）下不再逐次询问。
  `proxy`、附加读写根设置原样平移。

拒绝、超时或取消都使整条命令不执行，不存在"降级为离线执行"的回退。
host 作用域没有沙箱隔离，网络参数被忽略。HTTP 可达不代表 HTTPS 或 NuGet restore 已通过；
TLS 与真实工具链限制单独记录（见[阶段交付报告](../Development/phase2-codex-sandbox-report.md)第 7 节）。

## 文件工具与 search_text

四个文件工具（list_files / search_text / read_file / apply_patch）在宿主进程内执行，
统一经 `ResourceAccessPolicy` 分类：权限系统保护路径（用户配置、会话存储、沙箱组件与
凭据）硬拒且不可授权。该策略是**可信计算边界而非 OS 强制**，如实声明；
工作区内部的秘密文件不自动识别，集中分类器只覆盖已知保护类。

search_text 支持按行搜索（不做跨行正则与替换）：

| 参数 | 语义 |
|---|---|
| `mode` | `literal`（缺省，子串匹配，行为与旧版一致）或 `regex`（.NET NonBacktracking 引擎） |
| `pattern` | ≤ 512 字符；NonBacktracking 不支持环视、反向引用、条件等，不支持的表达式明确报错，不静默换引擎 |
| `include` / `exclude` | glob（`*`/`**`/`?`/`[...]`）按工作区相对路径过滤；只作用于已授权扫描集，不扩大访问范围；exclude 优先 |
| `caseSensitive` | 缺省 false；同时控制 regex IgnoreCase 与 glob 大小写 |
| `contextBefore` / `contextAfter` | 0–5 行；相邻或重叠窗口合并为一块，块间以 `--` 分隔 |
| `outputMode` | `content`（缺省，兼容旧格式）／`files`（只列文件）／`count`（每文件计数） |
| `maxResults` | 1–500，封顶匹配数而非文件数 |

预算与标注：扫描上限 3000 文件、单文件 4MB、行显示 240 列；总输出预算 64k 字符；
正则匹配超时。任何提前停止（截断、超时、部分计数）都在结果中显式标注，
绝不显示为权威的"无匹配"或完整统计。策略排除与权限系统保护的文件不出现在
文件名、计数或片段中。

## 旧模式迁移

旧的七种模式（逐次确认、只读可申请、项目内读写、完全开放、逐项 AI 审核、只读 AI 审批、
项目内读写 AI 审批）按映射仅供迁移与展示：三个 AI 审核预设 → `AIAutoReview`，
其余（含完全开放）→ `HumanApproval`。

- 用户配置检测到旧 `defaultMode`/`composerModes` 即置迁移待确认态，**不拒载其余配置**；
  确认后改写为 `{defaultApprovalMode, useCurrentModel, reviewerProfile, reviewerModel}`。
- 会话恢复时，含旧 selection 记录或无记录但有历史运行的会话进入 `MigrationPending`：
  **确认前所有模型工具能力一律拒绝（含只读，无例外，固定理由
  "permission migration pending; confirm to enable tool execution"）**；
  工具保持注册，文本对话轮正常，确认后恢复。
- 迁移确认必须如实展示：新基线允许工作区读写与沙箱 Shell；附加权限走两种审批；
  FullAccess 已删除，不恢复自动宿主执行；旧临时授权与 commandRules 免审批规则
  **不自动取得新权限**；旧常设网络策略平移并在确认中展示。
- `settings.commandRules` 保留解析但运行时不再消费——旧命令免审批规则不能成为隐蔽授权。
- 迁移失败时保持阻断状态，可重试确认。

配置与会话分别确认。Desktop 在设置 → 权限与审批中提供“确认迁移权限配置”；普通保存不会
解除配置门禁。旧会话在会话页显示通知条，确认操作只打开并保存该会话，不发送模型请求。
CLI 在交互式 `chat` 启动时提示配置迁移，在恢复旧会话时另行提示会话迁移，均默认否；
重定向输入不能代替用户确认。`run` 遇到未确认的旧配置会明确退出，保留原文件。

## 会话内切换与恢复

- CLI `/approval human|ai`：在空闲边界切换会话审批模式，与 Desktop 同机制，
  经 selection 记录持久化；模式切换不能代替旧会话迁移确认。
- 模式选择以必需记录写入会话 JSONL：新 schema `tinyharness.permission-selection/2`
  { Revision, ApprovalMode }；迁移完成写入 `permission-migration/1`
  { Revision, CompletedUtc, PreviousMode?, ApprovalMode, PolicyVersion }。
  写入新必需记录后，不支持它的旧程序会拒绝读取这些日志。
- 单次与运行期 Grant 不跨重启恢复，也不重放历史工具；重启恢复最后审批模式，
  不使用全局默认值覆盖。

## 已知限制

- AI 审核的系统说明（如发言窗口说明）由程序以独立类型记录（SystemNotice，带 RecordId），
  取代旧文本窗口：用户发言不能伪造系统说明的身份，待审核字段均是不可信数据，
  不能授予、扩大或撤销授权。跨工具语义等价不做声称。
- 审批调用可能产生额外模型用量；AI 审核失败可手动重试，但重试同样消耗模型请求。
- Host 执行无操作系统隔离；审批卡如实展示后端身份，不弱化风险描述。
- 窄网络授权明确未实现；`enabled` 之外不存在部分联网档位。
- **[Verified，真实 OS]** 元数据写 Level 2 在新工作区可用性取决于 deny ACE 历史：工作区一旦被
  默认策略 refresh 过（含同运行内先行的负对照），`.git` 等元数据目录的 deny ACE 持久存在且
  跨会话/跨运行不回收（Rust setup helper 不回收 deny ACE，与写根 ACE 收敛行为不对称，
  icacls 证据：capability SID 显式 DENY 对象级+可继承两条）；此后 metadata 写 grant 在该工作区
  不可用，直至 deny ACE 被外部清理或 helper 增加回收语义。该限制为可用性缺口，安全方向无缺口
  （失败方向=拒绝）。修复需修改外部 fork（codex-windows-sandbox-setup deny-ACE 回收），
  属单独立项，本任务不实施。
