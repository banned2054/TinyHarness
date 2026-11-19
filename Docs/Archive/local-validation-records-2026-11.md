# 2026 年 11 月本地验证记录补充

整理日期：2026-11-15。承接 [11 月 10 日的整理](local-validation-records-2026-10.md)与[归档索引](README.md)，只补充之后留下的独有结果，不重复归档已有设计和实施计划。本次读取已有日志与 TRX，未重新运行测试、构建、NativeAOT、真实沙箱或模型调用。

31 份原 TRX 的时间、计数、失败用例名与原文件 SHA-256 保存在 [test-run-summary.json](Evidence/local-validation-2026-11/test-run-summary.json)。完整 TRX、失败堆栈、测试输出、二进制与临时夹具已纳入本次清理；摘要不能替代完整原始轨迹，也不能把历史通过视为当前工作区通过。各轮结果分别记录，不相加计算验收数量。

## .NET 11 升级的本地回归

以下为 2026-11-11 的历史运行；发现数、执行数与通过数保留 TRX 原始口径。

| 原 TRX（均位于原 `.tmp`） | 发现 / 执行 / 通过 / 失败 | 边界 |
| --- | --- | --- |
| `net11-diagnosis/net11-diagnosis.trx` | 2196 / 2181 / 2148 / 33 | 初次诊断存在协议、SDK 路径和进程时序等失败，不能描述成首次全绿 |
| `net11-validation/core-upgrade.trx` | 2196 / 2181 / 2177 / 4 | 三项进程／取消探针与一项 NuGet 路径探针失败 |
| `net11-validation/core-final.trx` | 2196 / 2181 / 2180 / 1 | Git 信任环境断言失败；后续 Git 环境定向 9/9、精简 PATH 定向 18/18 |
| `net11-validation/core-verified.trx` | 2196 / 2181 / 2181 / 0 | 该轮执行项全部通过，不把发现与执行差额改写为额外通过 |
| `net11-validation/desktop-verified.trx` | 380 / 379 / 378 / 1 | 草稿人工审批选择断言失败；单独重跑该项通过 |
| `net11-validation/desktop-confirmation.trx` | 380 / 379 / 379 / 0 | 后续完整 Desktop 确认轮通过 |

这些结果来自当时的离线测试。环境对照与随后通过不构成对所有失败根因、真实 GUI 或真实沙箱行为的完整证明。

## 删除提示、延迟沙箱与自动命名

功能契约与后续状态继续由[权限主记录](permission-architecture-plan.md)、[会话存储计划](../session-storage-plan.md)和[项目与会话归档](project-session-plan.md)维护；此处只记录原临时结果中的验证边界。

| 历史记录 | 核对结果 |
| --- | --- |
| 归档会话删除提示 | `archived-deletion.trx` 13/13，随后 `archived-deletion-final.trx` 35/35。属于本地回归，不是手动 GUI 验收 |
| 延迟沙箱与扫描 | `lazy-sandbox-first.trx` 6/6、`lazy-scan-first.trx` 20/20、Core 定向 282/282、Desktop 定向 44/44 |
| 延迟沙箱的 Desktop 全量轮 | `lazy-sandbox-desktop-unrestricted.trx` 发现 419、执行 418、通过 412、失败 6；六项失败随后单独重跑 6/6。没有用定向重跑拼成一次完整全绿记录 |
| 自动命名定向 | `title-core-targeted-final.trx` 75/75，`title-desktop-targeted-final.trx` 13/13；提交前另有 Core 12/12、Desktop 6/6 |
| 自动命名全量回归 | `core-final.trx` 执行 2193、通过 2190、失败 3，三项进程探针单独重跑仍失败；`desktop-final.trx` 执行 412、通过 411、失败 1，该项单独重跑通过。另两份 Desktop 对照轮分别存在 52 项、2 项失败，完整用例名保留在摘要 JSON，不省略为“全量通过” |

原生验证中，[CLI 离线 smoke](Evidence/local-validation-2026-11/session-titles-cli-smoke.log)记录 Completed、17 steps／16 tool executions；[Desktop 离线 smoke](Evidence/local-validation-2026-11/session-titles-desktop-smoke.log)记录切换、刷新、恢复、客户端释放和 `titles=2`。另有较早尝试因系统 Temp 的访问权限失败，后续运行改用了隔离临时目录；不能据此认定真实 ACL 验收已通过。

[Desktop NativeAOT 发布日志](Evidence/local-validation-2026-11/desktop-aot-warnings.log)保留两条 Avalonia DComposition 的 `UnmanagedCallersOnly`／非 blittable 参数诊断。发布产生了文件、离线 smoke 通过，不等于这些诊断已解决，也不等于 GUI 渲染和真实模型链路已验收。

## S2／S3 会话存储验证

S2 的职责调整、失败与通过已记入[会话存储计划第 7 节](../session-storage-plan.md#7-本轮验证记录)，本页不复制实施清单。原 S2 的 CLI 日志记录离线 smoke Completed、17 steps／16 tool executions、`EXIT=0`；Desktop 日志记录离线 smoke PASS、`EXIT=0`。这两份重复 smoke 文本和夹具不再另存。

2026-11-15 的 S3 原始小型日志单独保留：

- [Core 全量日志](Evidence/local-validation-2026-11/s3-core-full-tests.txt)：末尾汇总为 2212 通过、10 跳过、0 失败、总计 2222。
- [CLI NativeAOT 发布日志](Evidence/local-validation-2026-11/s3-publish-cli-aot.log)：记录 `net11.0`／`win-x64` 的原生代码生成与产物输出，未见 compiler／trimming／AOT 警告文本。
- [CLI 原生 smoke](Evidence/local-validation-2026-11/s3-cli-aot-smoke.txt)：Completed、17 steps／16 tool executions，含既有 SQLite 列表摘要校验。

按活动计划的边界，S3 未切换生产读写权威、未导入真实数据；新业务库类型在该原生产物中被裁剪，因此 smoke 不覆盖业务 schema 在 AOT 下的实际执行。本次整理没有扩大这项验收结论，也没有修改活动计划的状态。

## 规模测量与清理去向

2026-11-15 的默认规模测量 JSON 已按原字节另存于[规模测量归档](session-scale-measurements.md)。它是 .NET 11 的新一轮合成测量，不能替代 11 月 10 日或更早的原始结果。

清理前的证据来源与复制校验见 [source-inventory.json](Evidence/local-validation-2026-11/source-inventory.json)。临时源码副本 `lazy-sandbox-staged-*` 中对应受版本控制路径的 710 个文件，均在本地 Git 历史对象中找到一致 blob；旧 `lazy-sandbox-commit.patch` 的目标 blob `aaa93b6` 也存在。额外的 restore 日志与临时 NuGet 配置属于验证材料，不含需要另存的产品源码。[设置预览辅助源码](Evidence/local-validation-2026-11/settings-preview-source.cs.txt)不在产品版本历史中，按原字节保留，仅作为历史材料，不作为当前可执行验证入口。

本次清理回收 `.tmp`、`artifacts` 中已归档的日志原件、旧源码／构建副本、发布产物、缓存、合成数据及一次性清理脚本和清单；结果见[清理记录](Evidence/local-validation-2026-11/cleanup-result.json)。此前清理系统 Temp 的记录为：移除 135 条用户 PATH 测试记录、删除 2720 + 47131 + 481 个测试目录和 968 个测试文件；这些数量属于之前分别完成的操作，不计入本次仓库目录清理释放量，85 个来源未确认的历史 Temp 目录保持保留。

用户 PATH 的清理前原值备份保留在本机 `.tmp/path-cleanup-20261115/backup-20261115-075309-0973469.json`，不将机器环境备份写入版本控制归档。本次没有再次修改 PATH、删除仓库外目录、改变 ACL 或机器配置。未跟踪源码、活动计划及其他工作区改动不在清理范围。
