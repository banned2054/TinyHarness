# TinyHarness.NET 计划入口

更新日期：2026-11-15。

当前定位是 **0.0.1 原型预览版**：基础流程已能用于实际任务，仍在根据使用反馈修复细节，交互、稳定性与安全性均不作成熟保证。当前无新的总路线；本页只维护正在推进的单项计划和文档入口，不从历史计划自动启动任务。

## 当前活动计划

- 当前契约：[权限架构重构](Docs/permission-architecture-plan.md)，以第 0 节 2026-11-08 冻结的三级执行体系及 2026-11-12 延迟沙箱初始化补充为准。
- 权限计划已按 2026-11-14 当前源码重对账；应用侧实现与测试资产、待复验项目、真实环境边界见专项计划第 13 节。C2 helper 的本机部署状态未在本次检查中复核；#14 真并发双进程探针、当前目标框架下的 NativeAOT 与 smoke、GUI 和真实模型 Reviewer 效果仍需分别验收。
- [会话存储治理与删除优化](Docs/session-storage-plan.md)：S1、S2 已实施；S3 已于 2026-11-15 冻结业务 schema（`session-store.db` 版本 1）与版本化建库／升级基础；S4 已于 2026-11-15 实施分批导入、迁移标记与按批次切换生产权威（组织状态/项目目录册/会话归属），并补齐备份、校验与恢复入口、CLI 显式迁移命令与离线 smoke；2026-11-17 修复 S4 静态审查发现的 5 项缺陷（运行中宿主解绑绕开业务库、阻塞状态回退历史归属、外键损坏备份先覆盖再报错、索引更新未叠加业务库归属、续聊覆盖并发归属）、备份恢复中业务库 WAL／SHM 清理失败被当作成功的缺陷，以及追加复审发现的两项（WAL 隔离后进程退出缺少恢复机制、替换失败时忽略日志复原失败；见专项计划第 5.8 节）。同日复审追加恢复记录文件角色校验，堵住"结构合法但把业务库或会话日志写成副本／隔离名"会被删除的路径（第 5.8.1 节）。迁移未对真实用户 store 执行，真实 GUI、双进程并发恢复与断电现场验收仍待授权；既有"进程终止"覆盖是同一测试进程内的状态构造与文本 WAL 夹具，不含真实子进程终止与 SQLite WAL 重放。S5 仍按迁移后删除简化单独推进。范围、迁移协议与未验证边界见专项计划第 4–5 节。

## 文档入口

- 使用方式与已知限制：[英文 README](README.md)、[中文 README](Docs/README.md)。
- 文档分类与导航：[中文 README 文档导航](Docs/README.md#documentation)。
- 旧计划、设计与阶段记录：[Archive 索引](Docs/Archive/README.md)。
- 历史总计划：[MVP／M1–M11](Docs/Archive/PAST_PLAN.md#m1-m11)、[M12–M17](Docs/Archive/PAST_PLAN.md#m12-m17)。
- 本轮移入归档的旧计划：[桌面 GUI 迁移](Docs/Archive/desktop-migration-plan.md)、[供应商与模型选择](Docs/Archive/session-model-switching-design.md)、[项目与会话](Docs/Archive/project-session-plan.md)。原未完成项和未验证范围保留在各文档中，多目录执行仍属另行实施范围。
- 当前本地内容与脱敏边界：[按用途和出口处理](Docs/Archive/session-persistence-design.md#44-按用途和出口处理脱敏)；本地正文与历史保留原文，诊断和审计继续脱敏。
- 持续维护的说明：[权限与审批](Docs/Guides/permission-modes.md)、[打包与发布](Docs/Guides/releasing.md)、[第三方许可维护](Docs/Guides/third-party-notices.md)。
- 工作规则：[AGENTS.md](AGENTS.md)。开始具体变更时读取相关设计或单项计划，并核对实际源码和工作区状态。

归档不取消仍有效的协议、安全、权限、数据完整性和 NativeAOT 约束，也不表示原计划的所有验收均已完成。历史记录不替代当前验证，不授予实施、真实模型调用、机器配置、Git 或发布权限。

<a id="session-list-sqlite-index"></a>

## Session 列表加载与 SQLite 索引

已完成项的最终状态、测量与未验证范围见[归档设计与验收记录](Docs/Archive/session-list-index-design.md#归档前计划入口记录)。
