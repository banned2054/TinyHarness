# TinyHarness 代码排版参考记录

> 历史归档（2026-10-24 整理）。本页保留原日期、范围、结果与限制，不作为当前实现说明或新的操作授权。当前文档见[文档索引](../README.md#documentation)。

本文件保留本项目的历史观察，当前有效规则见 [code-style.md](../code-style.md)。
这些观察不属于跨项目母版，不能直接当作其他项目已有风格的证据。

2026-10-21 先排除了当前相对 HEAD 已修改的 C# 文件，从 267 个未修改的受跟踪文件中
搜索布局，再完整阅读了以下六个代表文件。没有使用本轮新压缩实现或生成试跑样例来反推
用户历史风格，也不能据此证明所有旧代码均由用户手工排版。

| 既有文件 | 观察 |
|---|---|
| [ChatMessage.cs](../../TinyHarness.Core/Models/ChatCompletions/ChatMessage.cs) | 多处 `=> new()` 同行，初始化器正常块缩进和属性列对齐；长函数定义也有括号后折行 |
| [ProcessExecutionPolicy.cs](../../TinyHarness.Core/Models/Runtime/ProcessExecutionPolicy.cs) | `Host() => new()` 的紧凑函数头与多行初始化器 |
| [CommandRuleJson.cs](../../TinyHarness.Core/Services/ChatCompletions/Configuration/CommandRuleJson.cs) | 参数列对齐、行尾 `||`、相关局部变量和属性列对齐；也有一行多个条件的历史排法 |
| [StreamAccumulator.cs](../../TinyHarness.Core/Services/ChatCompletions/StreamAccumulator.cs) | 短调用、简单守卫保持紧凑；较长 `if` 在行尾 `&&` 后折行 |
| [DisplaySecretRedactor.cs](../../TinyHarness.Cli/Services/DisplaySecretRedactor.cs) | 对齐的 LINQ 链与长条件；也有较碎的访问链，不能全部当作理想模板 |
| [ModelCommand.cs](../../TinyHarness.Cli/Commands/ModelCommand.cs) | 赋值右侧单独一行并保持完整表达式；也有被拆开的短链和 ConfigureAwait，需按当前偏好判断 |

历史中混有多种形式，规则按“用户明确偏好 + 既有样本支持 + 当前语义可读性”执行，
不采用一键模仿全部旧空白的方式。本次只记录规则，不格式化这些文件。
