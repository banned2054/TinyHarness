# 2026 年 10 月本地验证记录补充

整理日期：2026-11-10。来源为原 `artifacts/` 下的验证报告；已与既有归档去重。本页保留有诊断价值的补充事实，删除过时的接力指令、机器路径、旧产物指纹和重复命令。原二进制、TRX、临时夹具和详细日志已清理，本页不承诺提供完整原始轨迹。以下均为历史结果，整理时未重新执行。

## Desktop 取消与调试器中断

原报告依次为 `desktop-turn-cancel-20261029/validation.md`、`sdk-cancellation-20261030/validation.md` 和 `usercancel-20261030/validation.md`。职责与后续变化见[Desktop 迁移记录](desktop-migration-plan.md#debug-取消与退出取证)。

| 阶段 | 发现、处理和验证 |
| --- | --- |
| 2026-10-29 取消回调异常 | 离线复现停止命令逃出 `AggregateException`、关闭后客户端未释放；分别尝试各取消与清理步骤，最后汇总错误。Desktop 全套 151/151；Core 预算/观测相关 44/44；Desktop NativeAOT 与仓库外离线 smoke 通过。未证明这是用户真实退出的唯一根因 |
| 2026-10-30 真实 SDK 离线传输 | 使用当时 OpenAI 2.14.0、M.E.AI.OpenAI 10.11.0、System.ClientModel 1.15.0，仅替换 HTTP handler；停止/关闭为 Cancelled，读取超时为 Failed，墙钟预算为 BudgetLimitReached；部分文本保留、响应释放、可再发送。新增专项 7/7、Desktop 158/158；NativeAOT 与离线 smoke 通过；未复现真实 GUI 退出 |
| 2026-10-30 VS 中断定位 | 取得生命周期记录：一个进程取消后正常保存并手动关窗 exit 0，另一个记录止于停止请求，与调试器直接终止一致。流推进从 `ValueTask.AsTask()` 竞速改为原生 await，等待通知由伴随循环串行收尾。新增异常栈测试修复前红、修复后绿，栈不再含 `ValueTaskSourceAsTask` |
| 最后一轮回归 | SDK 取消 6/6、Desktop 取消 6/6、事件流 11/11；Desktop 159/159；Core 1408 通过、4 跳过。Core 首次曾有一个失败，随后连续两轮全量通过，根因未复现。该轮未重做 NativeAOT；不能把前一轮发布结果算作最终代码发布验证 |

Debug 生命周期日志只记录阶段、异常类型、HResult、托管栈与当时退出码，不记录模型正文、异常消息或凭据，也不把异常标为 handled。强制终止、FailFast 或原生崩溃不保证日志完整，文件中的退出码不能替代父进程取得的最终退出码。VS 真机点击停止是否不再中断，当时仍待用户实测。

## CLI Ctrl+C 输入边界

2026-10-23 原 `ctrl-c-fix-20261023/verification.md` 记录：Windows 交互输入通过控制键处理代数，将同次在途读取的临时空返回与真正 EOF 区分；关联异步控制键最多等待 250 ms。重定向和非 Windows EOF 语义保持。

输入/导航相关 42 项通过，普通离线回归 1124 通过、2 项符号链接权限跳过；NativeAOT 与隔离原生 smoke 通过。真实 ConPTY 的独立 managed 进程重现旧输入中断，并验证修复后可接受中文追问、空闲 Ctrl+C 正常退出；这不是完整 NativeAOT 模型会话验收，也未验证用户原终端的手动操作。

## 格式化的历史经验

原 ReSharper 试跑与三轮格式化报告中，仍值得保留的是：`banned甄选` profile 在当时除了排版还会重排成员、简化类型和清理语法，不能默认将 CleanupCode 等同纯空白变化。只授权排版时，需检查实际 diff，必要时对 token、字面量和注释做独立比较。目录通配曾误纳入范围外文件，后续精确文件清单才保证范围；该事故不作为以后恢复他人修改的授权。

当时试跑使用 CleanupCode 2026.2.3.1、共享 DotSettings 与 EditorConfig；候选样例两次清理哈希一致，不证明所有产品代码的主观排版偏好都可自动识别。工具曾有用户缓存访问警告，即使退出 0，也不能声称全程无警告。当前规则以[代码风格](../code-style.md)和仓库 AGENTS 为准。

## 已有归档覆盖的记录

以下原件已删除，相关结论仍在对应文档中；这些文档是历史摘要，不是旧报告、机器结果或原始日志的逐字副本。

| 原件类别 | 保留入口与删除原因 |
| --- | --- |
| `commit-batch-20261020-d17600` | [10 月 22 日接力记录](current-progress-2026-10-22.md#2-代码与-git-基线)已保存六笔提交与验证边界；旧暂存过程无须再留副本 |
| `compaction-replacement-20261021` | [压缩验收](context-compaction-history.md)已有自动压缩、恢复一致性、历史错误与未验证范围 |
| `git-shell-glm53-20261021` | [M16 验收](m16-verification.md)及历史总计划已有三个完成样本、初始达限、退出 128/1 和补丁外发未获批的区别 |
| `model-response-fix`、`model-request-observability` | [10 月 22 日接力记录](current-progress-2026-10-22.md)第 9–10 节已有完成校验、等待事件、计时与缓存元数据契约及测试结果 |
| `token-budget-disabled`、`reasoning-display`、`quiet-status`、`session-path-fix`、`unlimited-steps` | 同接力记录第 11–15 节已有缺省/零值、显式限制和离线验证边界 |
| `composer-layout` 与旧 GUI 验收步骤 | [Desktop 迁移](desktop-migration-plan.md)和[项目与会话](project-session-plan.md)已有实施与未验收范围；原夹具及脚本已不存在，旧启动步骤不可执行 |
| 自动归属实施 prompt、阶段二中断 handoff | 后续自动整理及阶段二收口已见[项目与会话](project-session-plan.md#15-实施状态)，不保留过期的工作区错误、配额与继续指令 |
| 旧七权限模式交付报告 | 已由[权限主记录](../permission-architecture-plan.md)中的三级执行体系与后续自动迁移取代 |
| v0.0.1 Release 草稿 | [更新日志](../CHANGELOG.md)与历史总计划保留版本范围；旧发布操作说明不作当前流程 |
| 七份根目录 smoke 文本 | 持久化、项目指令、系统提示词及沙箱归档已有同批结果；只有 Completed/17 steps/16 tools 的重复输出不再单独保存 |
| 11 月 7 日权限 repair/finish 的构建日志、TRX、路径清单 | 权限主记录第 10–11 节已有失败、最终通过、跳过、testhost 替代与 PATH 限制；原始日志清理不改变这些历史结果 |

11 月 7 日 TRX 核对补充：Core 首两轮分别有 2 项、1 项失败，最终 `core-verified` 为发现 1844、执行并通过 1835、0 失败；Desktop 为发现 342、执行并通过 341。进程环境对照先有 4 项失败、普通权限仍有 3 项失败，精简子进程 PATH 后同四项通过。保留这些失败到通过的区别，不将多轮结果相加，也不表述成原始运行从未失败。

三份不重复的合成规模测量另存于[规模测量归档](session-scale-measurements.md)；三级执行体系的独有实测和审查结论另见[11 月 8 日记录](permission-validation-2026-11-08.md)。两份本轮清理清单属于一次性操作记录，完成后删除。
