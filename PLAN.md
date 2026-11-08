# TinyHarness.NET 计划入口

更新日期：2026-11-08。

原 MVP 至 M17 阶段计划已收口并合并到 [PAST_PLAN.md](PAST_PLAN.md)。当前定位是 **0.0.1 原型预览版**：基础流程已能用于实际任务，仍在根据使用反馈修复细节，交互、稳定性与安全性均不作成熟保证。

当前无进行中的总路线或新总项；`PLAN.md` 只保留文档入口与已完成项的最终状态入口，不据此自动启动新计划。

## 文档入口

- 文档阅读入口：[中文 README 文档导航](Docs/README.md#documentation)；已完成的设计、讨论与验收见[归档索引](Docs/Archive/README.md)。
- 使用方式与已知限制：[英文 README](README.md)、[中文 README](Docs/README.md)。
- 历史范围、约束与里程碑：[MVP／M1–M11](PAST_PLAN.md#m1-m11)、[M12–M17](PAST_PLAN.md#m12-m17)。
- 已完成实现的设计参考：继续查阅归档中的[会话持久化](Docs/Archive/session-persistence-design.md)、[项目规则](Docs/Archive/project-instructions-design.md)、[系统提示词](Docs/Archive/system-prompt-design.md)、[上下文压缩](Docs/Archive/context-compaction-design.md)、[任务控制与预算](Docs/Archive/task-control-design.md)、[Windows 沙箱](Docs/Archive/windows-sandbox-management.md)与[Session 列表加载与 SQLite 索引](Docs/Archive/session-list-index-design.md)文档。
- 当前本地内容与脱敏边界：[按用途和出口处理](Docs/Archive/session-persistence-design.md#44-按用途和出口处理脱敏)；本地正文与历史保留原文，诊断和审计继续脱敏。
- 进行中的单项设计：[供应商、模型管理与会话内选择](Docs/session-model-switching-design.md)（含推理强度与 CLI `/models`；Core、Desktop／CLI 交互、失效目标恢复与目录管理已接入，综合验收进行中）。
- 活动计划：[桌面 GUI 迁移](Docs/desktop-migration-plan.md)；目标、实际接入和验证范围由计划与工作区共同核对，不因已有 Desktop 项目而宣称完成。
- 进行中的单项计划：[权限架构重构](Docs/permission-architecture-plan.md)；当前产品契约为 2026-11-08 冻结的三级执行体系契约（由该计划第 0 节承载）。B1-B8 批次已实施（三级执行、C2 读保护、OS 层广泛读与读保护、常设信任显式迁移、shell 写根申请、grant-event 审计、拒绝记忆可信解除、审批展示事实行、两端注册一致与保护根超集），配套修复批次（入口回归/审计断路/审批展示真实性）已收敛；六项评审修复与 C2 获批读通道（应用侧+fork 分支）已实施，C2 通道未部署本机（当前能力门 fail-closed，普通沙箱行为不变）；改动在工作区待提交，尚未执行 Git 写操作。真实环境综合验收与 #14 真并发双进程探针未完成，GUI 真机交互与真实模型 Reviewer 效果保持未验证口径。
- 进行中的单项计划：[项目实体、会话归属与多目录扩展](Docs/project-session-plan.md)；项目核心、严格会话归属及 CLI／Desktop 生产入口已接入。无项目会话使用独立持久状态；空白会话项目选择器提供搜索、按侧栏顺序选择、创建项目与返回无项目；Desktop 项目可从三点菜单删除，关联会话转为无项目，清理对应 store 的项目侧栏状态并保留历史与执行目录。可恢复归档元数据不改写对话日志。Project 目录册的跨进程自动同步（15 秒周期检测加内容比较，独立于 Session 索引）已接入，Sidebar 不再依赖手动刷新感知其他 Desktop 实例的项目创建、改名与删除。阶段四综合验收待完成，多目录执行另行实施。写入新的必需会话日志记录后，不支持它的旧程序会拒绝读取。
- 工作规则：[AGENTS.md](AGENTS.md)。开始具体变更时读取相关设计或单项计划，并核对实际源码和工作区状态。

归档不取消现有协议、安全、权限、数据完整性和 NativeAOT 约束。历史记录只证明当时所述范围，不能替代当前验证，也不授予修改、提交、真实模型调用、机器配置或发布权限。

<a id="session-list-sqlite-index"></a>

## Session 列表加载与 SQLite 索引（已完成）

**状态：已完成（A–E 已实施并验收，2026-11-04）。** 默认侧栏加载成本主要取决于轻量元数据和本次变更，而非所有历史对话的总长度；核心约束——列表不恢复完整会话、索引不决定执行安全、后台维护不阻挡首屏、索引失步可发现可修复——已全部满足并验收。

- 阶段 A–D 提交：157aa02（测量与依赖验证）、8bd444b（摘要与索引核心）、80f67a1（提交与跨进程接入）、9d06851（列表与启动接入）；阶段 E 综合验收随文档轮提交。
- 设计契约、验收矩阵证据、测量记录、NativeAOT 与仓库外 smoke 证据、验收发现与未验证范围：[Session 列表加载与 SQLite 索引设计与验收记录](Docs/Archive/session-list-index-design.md)。
- 关键结论（同规模 3030 日志夹具，本机隔离）：有效索引下启动列表查询 67–81 ms／分配 3.7 MB（改造前 `ListAsync` 6408.5 ms／546 MB），首条渐进可见 87.4 ms；首建双扫描合计 ≈34.8 s 为计划内成本并单列报告；CLI、Desktop、IndexProbe 三端 NativeAOT publish 0 warning，仓库外隔离 smoke 完成建／查／更／重开。
- 验收发现并修复：`Microsoft.Data.Sqlite` 命令超时会越过 `busy_timeout`（默认 30 秒重试），索引连接串 `DefaultTimeout` 已对齐为 3 秒，跨进程锁等待真正有界并有测试固化。
- 未验证范围（真实 GUI 流畅度、断电与设备故障、真实用户日志测量、只读 ACL 变体）见归档文档第 7 节；后续历史分页与首开全读优化属独立切片，不因本项完成推定已解决。

