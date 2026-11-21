# TinyHarness.NET 计划入口

更新日期：2026-11-22。

项目当前处于预览阶段，已发布版本见[更新日志](Docs/CHANGELOG.md)。本页维护当前活动计划、后续方案和文档入口。

## 当前活动计划

- [Desktop 沙箱读取策略与无项目会话工具目录隔离](Docs/desktop-sandbox-scan-and-no-project-isolation-plan.md) 是当前沙箱工作的优先项。参考 Codex，以顶层读根和精确拒读路径替换 Desktop 的全 profile 敏感名扫描。无项目会话的独立工具目录另行安排。
- [会话存储治理与删除优化](Docs/session-storage-plan.md)：S1、S2 已实施；S3 已于 2026-11-15 冻结业务 schema（`session-store.db` 版本 1）与版本化建库／升级基础；S4 已于 2026-11-15 实施分批导入、迁移标记与按批次切换生产权威（组织状态/项目目录册/会话归属），并补齐备份、校验与恢复入口、CLI 显式迁移命令与离线 smoke；2026-11-17 修复 S4 静态审查发现的 5 项缺陷（运行中宿主解绑绕开业务库、阻塞状态回退历史归属、外键损坏备份先覆盖再报错、索引更新未叠加业务库归属、续聊覆盖并发归属）、备份恢复中业务库 WAL／SHM 清理失败被当作成功的缺陷，以及追加复审发现的两项（WAL 隔离后进程退出缺少恢复机制、替换失败时忽略日志复原失败；见专项计划第 5.8 节）。同日复审追加恢复记录文件角色校验，堵住"结构合法但把业务库或会话日志写成副本／隔离名"会被删除的路径（第 5.8.1 节）。S5 已于 2026-11-17 实施业务库 schema 版本 2（会话删除墓碑）、可续作的会话删除与迁移后项目删除单事务（第 6 节）；读取与对账按墓碑排除已删除会话，中断删除由恢复入口续作。同日修复 S5 静态审查发现的 4 项缺陷：v1 业务库改由显式迁移入口在租约内就地升级（迁移标记已完成的 store 与未完成的续作都覆盖，升级前生产读写一律拒绝）、投影清理未完成时保留待续作墓碑、迁移后项目删除把实际解绑的会话 ID 交给宿主、日志已消失的墓碑索引行改由孤儿清理移除。迁移未对真实用户 store 执行，真实 GUI、双进程并发恢复与断电现场验收仍待授权；既有"进程终止"覆盖是同一测试进程内的状态构造与文本 WAL 夹具，不含真实子进程终止与 SQLite WAL 重放。范围、迁移协议与未验证边界见专项计划第 4–6 节。

## 后续方案

- [CLI 工作区 ACL 按需准备](Docs/cli-workspace-acl-preparation-plan.md) 是后续事项，非首要任务。待安排将目录 ACL 准备从打开对话移到首次需要可写沙箱 Shell 时。

## 文档入口

- 使用方式与已知限制：[英文 README](README.md)、[中文 README](Docs/README.md)。
- 文档分类与导航：[中文 README 文档导航](Docs/README.md#documentation)。
- 旧计划、设计与阶段记录：[Archive 索引](Docs/Archive/README.md)。
- 权限架构重构已归档。三级执行体系与延迟沙箱初始化仍以[权限契约](Docs/Archive/permission-architecture-plan.md#permission-product-contract)为准，C2 和双进程隔离的结果见[真机验收记录](Docs/Archive/permission-architecture-plan.md#c2-v7-acceptance-2026-11-19)。
- 历史总计划：[MVP／M1–M11](Docs/Archive/PAST_PLAN.md#m1-m11)、[M12–M17](Docs/Archive/PAST_PLAN.md#m12-m17)。
- 本轮移入归档的旧计划：[桌面 GUI 迁移](Docs/Archive/desktop-migration-plan.md)、[供应商与模型选择](Docs/Archive/session-model-switching-design.md)、[项目与会话](Docs/Archive/project-session-plan.md)。原未完成项和未验证范围保留在各文档中，多目录执行仍属另行实施范围。
- 当前本地内容与脱敏边界：[按用途和出口处理](Docs/Archive/session-persistence-design.md#44-按用途和出口处理脱敏)；本地正文与历史保留原文，诊断和审计继续脱敏。
- 持续维护的说明：[权限与审批](Docs/Guides/permission-modes.md)、[打包与发布](Docs/Guides/releasing.md)、[第三方许可维护](Docs/Guides/third-party-notices.md)。
- 工作规则：[AGENTS.md](AGENTS.md)。开始具体变更时读取相关设计或单项计划，并核对实际源码和工作区状态。

归档不取消仍有效的协议、安全、权限、数据完整性和 NativeAOT 约束，也不表示原计划的所有验收均已完成。历史记录不替代当前验证，不授予实施、真实模型调用、机器配置、Git 或发布权限。

<a id="session-list-sqlite-index"></a>

## Session 列表加载与 SQLite 索引

已完成项的最终状态、测量与未验证范围见[归档设计与验收记录](Docs/Archive/session-list-index-design.md#归档前计划入口记录)。
