# TinyHarness.NET 计划入口

更新日期：2026-11-26。

项目当前处于预览阶段，已发布版本见[更新日志](Docs/CHANGELOG.md)。本页维护当前活动计划、后续方案和文档入口。

## 当前活动计划

- [沙箱运行时临时写根隔离与按契约校验计划（`runtime-tmp-v2`）](Docs/sandbox-runtime-temp-isolation-plan.md)：待实施（2026-11-26 设计已对齐）。采用每次 Shell 执行独占槽位、跨进程租约、受保护显式 DACL 与句柄绑定回收，使旧 `<sandboxHome>\tmp` 退出新命令热路径，并在普通预检与 C2 恢复路径保持一致的 fail-closed 契约。

## 后续方案

- 无项目会话独立工具目录（写目录与内置文件工具隔离）及新平台沙箱后端评估（如 Windows 11 24H2+ MXC）。

## 文档入口

- 使用方式与已知限制：[英文 README](README.md)、[中文 README](Docs/README.md)。
- 文档分类与导航：[中文 README 文档导航](Docs/README.md#documentation)。
- 旧计划、设计与阶段记录：[Archive 索引](Docs/Archive/README.md)。
- [Windows 沙箱可用性收口计划](Docs/Archive/windows-sandbox-readiness-plan.md) 与 [CLI 工作区 ACL 按需准备](Docs/Archive/cli-workspace-acl-preparation-plan.md) 已于 2026-11-25 完成实施与主闭环真机验收并归档（残留的受阻目录真实 UAC 与已删除旧路径真实簿记清理未真机实测，保留于归档边界记录）。
- [会话存储治理与删除优化](Docs/Archive/session-storage-plan.md) 已于 2026-11-25 归档（S1–S5 实施、审查修复与离线／NativeAOT smoke 已于 2026-11-17 完成；真实用户 store 迁移、真实 GUI、双进程并发恢复与断电现场验收仍待授权，保留于归档边界记录）。
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
