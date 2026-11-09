# TinyHarness.NET 计划入口

更新日期：2026-11-10。

当前定位是 **0.0.1 原型预览版**：基础流程已能用于实际任务，仍在根据使用反馈修复细节，交互、稳定性与安全性均不作成熟保证。当前无新的总路线；本页只维护正在推进的单项计划和文档入口，不从历史计划自动启动任务。

## 当前活动计划

- 当前契约：[权限架构重构](Docs/permission-architecture-plan.md)，以第 0 节的 2026-11-08 三级执行体系冻结契约为准。
- 实施与验证状态由该计划维护；C2 获批读通道未部署本机、真实环境综合验收与 #14 真并发双进程探针未完成，GUI 真机交互与真实模型 Reviewer 效果仍未验证。上述为计划记录，本轮文档整理未重新验证实现或部署状态。
- [会话存储治理与删除优化](Docs/session-storage-plan.md)：短期先优化项目删除的重复读取与索引维护；长期分阶段统一 Core 操作入口，再迁移 SQLite、配置和组织数据。长期目标不作为本轮迁移授权；范围、验收与实施状态见专项计划。

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
