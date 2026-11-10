# 计划、设计与历史文档归档

整理日期：2026-11-10。常用说明与活动计划见[中文 README 文档入口](../README.md#documentation)。

这里集中保存旧计划、已完成实现的设计与使用边界，以及历史讨论、调查、实施与验收记录。归档是文档分类，不表示原计划的所有目标或验收均已完成；未完成、未验证或另行实施的部分保留原意。设计参考可在后续明确任务中随实现维护；按日期保存的调查与验收保留历史原意。原日期、适用版本、通过与失败、跳过及未验证范围保持原含义；章节编号沿用原文。历史结论不自动更新为当前状态，历史授权不作为新任务授权。

2026-11-10 已清理本机 `artifacts/` 与 `.tmp/` 的旧产物和临时记录。独有结论已去重整理到下列归档，三份合成规模测量原始 JSON 保存于 `Evidence/session-scale/`。其他历史路径、哈希和执行命令只描述当时来源，不表示文件仍存在；历史正文中的“日志保留”“产物仍在”等也是当时状态。归档摘要不能替代已删除的原始日志。

## 本轮归档的旧计划与阶段报告

| 文档 | 归档时保留的状态与边界 |
| --- | --- |
| [MVP 至 M17 历史总计划](PAST_PLAN.md) | 原阶段已收口；保留历史原文和当时的验收范围 |
| [桌面 GUI 迁移](desktop-migration-plan.md) | 保留迁移契约、项目接入与后续修复记录；真实 GUI、模型及调试器实测边界按原文理解 |
| [供应商、模型管理与会话内选择](session-model-switching-design.md) | 交互与目录管理已接入；原综合验收、真实提供方兼容及完整 GUI 闭环未全部验证 |
| [项目实体、会话归属与多目录扩展](project-session-plan.md) | 项目、归属、侧栏与跨进程同步已接入；阶段四综合验收未完成，多目录执行另行实施 |
| [Phase 2 沙箱联网与开发工具链报告](phase2-codex-sandbox-report.md) | 2026-11-06 阶段快照；保留 TLS 阻断、代理限制、测试结果和后续建议，不作为当前权限契约 |

当前仍在推进的[权限架构重构](../permission-architecture-plan.md)保留在 Docs 顶层；活动入口统一见 [PLAN.md](../../PLAN.md)。

## 已完成实现的设计与使用边界

| 文档 | 范围 |
| --- | --- |
| [会话持久化](session-persistence-design.md) | 唯一日志、历史、恢复、兼容及内容出口 |
| [项目指令加载](project-instructions-design.md) | 规则发现、作用域、来源和执行边界 |
| [系统提示词](system-prompt-design.md) | 模板维护与会话提示词一致性 |
| [上下文压缩](context-compaction-design.md) | 有界交接、用户原话和检查点 |
| [任务控制与预算](task-control-design.md) | 运行控制、工作项、预算与日志扩展 |
| [Windows 沙箱管理](windows-sandbox-management.md) | 命令、目录授权、网络模式与验收工具范围 |
| [Windows 沙箱接入说明](windows-sandbox-integration-guide.md) | 组件职责和执行边界；原始版本化方案另见下节 |
| [Session 列表加载与 SQLite 索引](session-list-index-design.md) | 列表索引设计契约、验收矩阵证据、测量记录与未验证范围 |

这些文档的实现已完成，因此移出顶层；归档不表示其约束失效。当前源码与活动计划优先用于判断后续变化，历史测试数字不代表当前验证。

## 讨论与设计依据

| 文档 | 范围 | 关联文档 |
| --- | --- | --- |
| [2026-10-16 会话持久化讨论](2026-10-16-session-persistence-codex-discussion.md) | 原讨论、更正与旧计划基线 | [持久化契约](session-persistence-design.md) |
| [项目指令设计与实施记录](project-instructions-history.md) | 实施前源码核查、旧施工清单、M14.1 结果 | [项目指令契约](project-instructions-design.md) |
| [沙箱早期接入方案](windows-sandbox-dotnet-integration.md) | 原版本基线、协议模板、局部示例和源码索引；整篇保留 | [接入说明](windows-sandbox-integration-guide.md)、[管理说明](windows-sandbox-management.md) |
| [代码风格参考记录](code-style-evidence.md) | 既有源码的历史排版观察 | [当前代码风格](../code-style.md) |

## 实施与验收

| 文档 | 记录范围 | 关联文档 |
| --- | --- | --- |
| [M9 本机 MCP worker](m9-demo.md) | 离线、真实服务及 Codex 客户端验收 | [使用说明](../README.md#配置预算与集成) |
| [M10 模型协议接入](m10-model-protocol.md) | SDK 适配、两种协议与 NativeAOT 验证 | [使用说明](../README.md#配置模型) |
| [会话持久化验收](session-persistence-history.md) | 旧路径调查、统一日志切换、兼容精简、原文／脱敏复验 | [持久化契约](session-persistence-design.md) |
| [系统提示词与模型评估](system-prompt-history.md) | 本地接入与 2026-10-20 至 10-21 真实评估 | [提示词契约](system-prompt-design.md) |
| [上下文压缩验收](context-compaction-history.md) | 替换后的回归、规模、原生产物与真实样本 | [压缩契约](context-compaction-design.md) |
| [任务控制与运行预算](task-control-history.md) | 原切片顺序与 M15 工程结果 | [控制与预算契约](task-control-design.md) |
| [M16 交付证据核查](m16-verification.md) | 已收口阶段的核查矩阵、工程与模型样本 | [历史总计划](PAST_PLAN.md#m12-m17) |
| [Session 列表索引验收](session-list-index-design.md) | 阶段 E 综合验收：矩阵证据、基准对比、NativeAOT 与隔离 smoke、验收发现 | [设计契约](session-list-index-design.md#2-设计契约仍然有效) |
| [Windows 沙箱验收](windows-sandbox-verification.md) | M11、目录授权、PriceLab 和 TCP／UDP／DNS 专项 | [沙箱管理与验证边界](windows-sandbox-management.md) |
| [10 月本地验证记录补充](local-validation-records-2026-10.md) | Desktop 取消/SDK/调试器中断、CLI Ctrl+C、格式化经验；重复原件的归并去向 | [Desktop 迁移](desktop-migration-plan.md) |
| [三级执行体系实测与审查](permission-validation-2026-11-08.md) | 11 月 8 日真实 OS、离线与 AOT 检查点、二十条矩阵边界、审查关闭和遗留 | [权限主记录](../permission-architecture-plan.md) |
| [会话日志规模测量](session-scale-measurements.md) | 三份按日期冻结的合成测量 JSON、环境与口径；默认指标文件被覆盖的纠正 | [持久化验收](session-persistence-history.md) |

## 开发接力与历史计划

- [截至 2026-10-22 的接力记录](current-progress-2026-10-22.md)：提交基线、产物哈希、请求观测、展示与默认预算等历史记录；当前活动计划见[PLAN.md](../../PLAN.md)。
- [历史总计划](PAST_PLAN.md)：MVP 至 M17 原计划快照；当前计划入口见[PLAN.md](../../PLAN.md)。

- [2026-10-24 接力快照](current-progress-2026-10-24.md)与[首次整理的索引快照](documentation-index-2026-10-24.md)：保留之前的导航；现行导航统一见中文 README 与 PLAN。

旧计划、完成的设计及新增历史记录登记在本页，不再留在 Docs 顶层。归档搬迁只调整链接与归档说明，不把旧失败改成通过，也不删除仍有效的权限、恢复、协议或 NativeAOT 约束。
