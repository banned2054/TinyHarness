# TinyHarness.NET 计划入口

更新日期：2026-10-27。

原 MVP 至 M17 阶段计划已收口并合并到 [PAST_PLAN.md](PAST_PLAN.md)。当前定位是 **0.0.1 原型预览版**：基础流程已能用于实际任务，仍在根据使用反馈修复细节，交互、稳定性与安全性均不作成熟保证。

当前没有新的总计划。是否继续维护总 PLAN 尚未决定；后续需要设计的单项功能，可以分别维护对应计划，本文暂留为归档与阅读入口，不预先安排下一轮里程碑。

- 文档阅读入口：[中文 README 文档导航](Docs/README.md#documentation)；已完成的设计、讨论与验收见[归档索引](Docs/Archive/README.md)。
- 使用方式与已知限制：[英文 README](README.md)、[中文 README](Docs/README.md)。
- 历史范围、约束与里程碑：[MVP／M1–M11](PAST_PLAN.md#m1-m11)、[M12–M17](PAST_PLAN.md#m12-m17)。
- 已完成实现的设计参考：继续查阅归档中的[会话持久化](Docs/Archive/session-persistence-design.md)、[项目规则](Docs/Archive/project-instructions-design.md)、[系统提示词](Docs/Archive/system-prompt-design.md)、[上下文压缩](Docs/Archive/context-compaction-design.md)、[任务控制与预算](Docs/Archive/task-control-design.md)和[Windows 沙箱](Docs/Archive/windows-sandbox-management.md)文档。
- 当前本地内容与脱敏边界：[按用途和出口处理](Docs/Archive/session-persistence-design.md#44-按用途和出口处理脱敏)；本地正文与历史保留原文，诊断和审计继续脱敏。
- 进行中的单项设计：[供应商、模型管理与会话内选择](Docs/session-model-switching-design.md)（含推理强度与 CLI `/models`；Core、Desktop／CLI 交互、失效目标恢复与目录管理已接入，综合验收进行中）。
- 活动计划：[桌面 GUI 迁移](Docs/desktop-migration-plan.md)；目标、实际接入和验证范围由计划与工作区共同核对，不因已有 Desktop 项目而宣称完成。
- 进行中的单项计划：[项目实体、会话归属与多目录扩展](Docs/project-session-plan.md)；阶段一项目核心与阶段二 Core 会话归属已实现。阶段二提供严格 `project-binding` 日志、归属恢复、初始绑定及显式、可取消、带续扫游标的旧日志初始化服务；阶段三生产入口与阶段四综合验收待实施。尚未接入 CLI／Desktop 生产入口，也未对真实用户会话执行初始化；多目录执行另行实施。写入归属记录后，不支持该必需记录的旧程序会拒绝读取。
- 工作规则：[AGENTS.md](AGENTS.md)。开始具体变更时读取相关设计或单项计划，并核对实际源码和工作区状态。

归档不取消现有协议、安全、权限、数据完整性和 NativeAOT 约束。历史记录只证明当时所述范围，不能替代当前验证，也不授予修改、提交、真实模型调用、机器配置或发布权限。
