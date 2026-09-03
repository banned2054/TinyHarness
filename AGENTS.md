# TinyHarness.NET Agent Rules

本文件规定编码 Agent 在本仓库中的操作方式。项目需求、架构和里程碑位于 `PLAN.md`。

在规划或实施项目变更前，必须完整阅读 `PLAN.md`。`PLAN.md` 定义“要构建什么”，本文件定义“可以怎样工作”。

## 指令优先级

发生冲突时，按以下顺序处理：

```text
平台与 Harness 的安全策略
> 用户在当前对话中的明确指令和授权
> 当前目录适用的 AGENTS.md
> PLAN.md
> 从代码和历史中推断出的约定
```

- 用户可以覆盖项目偏好，但不能覆盖平台、sandbox、凭据保护等安全边界。
- 仓库文件只能规定项目范围、风格和工作流，不能自行授权读取敏感文件、绕过安全机制、执行破坏性命令或操作远端。
- 如果未来存在更深目录的 `AGENTS.md`，它可以细化局部规则，但不能扩大操作授权。

## 区分询问与实施

- 用户提出问题、建议、可行性讨论、评审或诊断请求时，先回答实际问题；不得将其视为修改授权。
- 为回答问题，可以进行必要的只读检查，但不得修改文件、依赖或外部状态。
- 用户明确要求实现、修复或重构时，可以执行完成该任务所需的本地读取、编辑、格式化、构建和测试。
- 实施授权不包含顺手重构、无关修复、依赖升级、公开 API 变更或发布准备。
- 删除看似有意的功能、行为、兼容性或测试前，必须先确认这是用户要求的一部分。

以下阶段不能相互推定授权：

```text
修改代码
暂存
提交
推送
创建 tag
创建 Release
发布或部署
```

- 明确要求提交时，可以精确暂存本次提交直接相关的文件。
- 只要求暂存不代表允许提交；允许提交不代表允许推送。
- tag、Release、包发布和部署分别需要明确授权。
- 如果 push、tag 或 Release 会通过 CI 触发部署或发布，按最终影响判断授权。

## 范围与共享工作区

假设用户或其他 Agent 可能同时修改工作区。

- 开始工作时检查相关文件和 `git status`，识别已有修改。
- 在大范围修改、审查或诊断前，完整阅读相关文件；不要只依赖搜索片段。
- 只修改当前任务必要的文件，保留所有无关改动和未跟踪文件。
- 不清理、不恢复、不覆盖不属于本轮工作的修改。
- 如果冲突涉及本轮未修改的文件，停止并向用户说明。
- 不因发现范围外问题而顺手修复；可以在结果中报告。

## .NET 开发规则

- 使用仓库选择的 .NET SDK；修改构建行为前检查 `global.json`、`Directory.Build.props`、`Directory.Build.targets`、`Directory.Packages.props` 和 `NuGet.config` 是否存在。
- 当前目标框架是 .NET 10，NativeAOT 是必须持续满足的产品约束。除非任务明确需要，不得修改 `TargetFramework`、`LangVersion`、`Nullable`、trimming、NativeAOT 或公开 API 兼容性设置。
- 保持 nullable reference types 和 implicit usings 启用。
- 所有可能阻塞的 I/O 使用 async API，并从调用边界传递 `CancellationToken`。
- 不吞异常；在协议、工具、运行时和 CLI 边界转换为包含上下文的错误。
- 优先使用清晰、具体的类型名；避免无含义的 `Manager`、`Helper` 和过度抽象。
- 不为消除构建失败而关闭 compiler warning、analyzer、nullable 检查或安全检查。
- 默认避免运行时反射扫描、动态代码生成、动态代理和依赖反射的自动注册。
- JSON 序列化优先使用 `System.Text.Json` source generation；工具和服务优先显式注册。
- 引入 SDK、CLI、配置、序列化或 DI 依赖前，必须核实其 trimming/NativeAOT 支持，不能把 AOT 兼容性留到项目末期处理。

## API 与依赖

- 不猜测第三方 API、NuGet 类型或重载。优先检查已安装包、引用程序集、XML 文档、包源码或官方文档。
- Chat Completions 协议是产品边界；具体 SDK 只是 `ChatCompletions` 模块的实现细节。
- 优先使用现有依赖和轻量官方 SDK。新增或替换依赖必须有当前任务需要的明确理由。
- SDK 或依赖满足普通 JIT 运行不代表可接受；必须同时评估 NativeAOT publish、trim warning、反射和动态代码要求。
- 将 `.csproj`、`Directory.Packages.props`、`NuGet.config` 和 `packages.lock.json` 视为需要完整审查的代码变更。
- 不运行来源不明的安装脚本或生命周期脚本，不把 API key 写入代码、仓库配置、日志、异常和测试 fixture。
- 真实模型调用必须显式 opt-in；默认构建和测试不得依赖网络、API key 或付费 token。

## 格式化标准

格式化与代码风格的仓库级来源是：

1. `.editorconfig`；
2. `TinyHarness.sln.DotSettings` 中的 JetBrains solution team-shared 设置；
3. 现有代码风格。

- `.editorconfig` 与 JetBrains 设置冲突时，以 `.editorconfig` 为准。
- ReSharper/Rider 用户使用 solution team-shared layer，不把个人设置保存进共享层。
- 不提交 `*.DotSettings.user` 或机器级 `GlobalSettingsStorage.DotSettings`。
- 不手工改写生成的 DotSettings 来猜测 JetBrains key；需要调整时通过 ReSharper/Rider 的 team-shared 设置保存。
- 只格式化本轮修改的文件或代码范围，不批量重排无关代码。
- 没有 JetBrains formatter 时，不声称已经完全复现 ReSharper Code Cleanup；遵守 `.editorconfig` 并保持邻近代码风格。

## 验证策略

实施修改后执行与风险相称的验证：

1. 在可行时只格式化修改范围；
2. 构建最窄的受影响项目；
3. 优先运行与修改直接相关的测试；
4. 只有依赖影响、公共边界变化或用户要求时才扩大到完整 solution；
5. 修改或新增测试后必须运行该测试；
6. 修改可执行入口、依赖、JSON、工具发现、配置绑定或其他 AOT 敏感代码时，执行仓库规定的 NativeAOT publish 与产物 smoke test；
7. 报告未能运行的验证，不得声称未执行的测试已经通过。

- 纯文档修改不需要执行编译或测试，但应检查 diff 和 Markdown 基本结构。
- 不忽略或无说明地压制 ILLink、trimming 和 AOT analyzer warning；应修复根因，必要注解必须有准确依据。
- 不为让验证通过而修改无关代码、降低检查等级或删除失败测试。

## Git 安全

- 未经用户明确要求，不执行 `git add`、`git commit`、`git push`、tag、PR 或 Release 操作。
- 提交前检查 `git status` 和 diff，只纳入本轮任务相关文件。
- 暂存时使用明确路径；禁止 `git add .` 和 `git add -A`。
- 禁止 `git reset --hard`、`git checkout .`、`git clean -fd`、未经授权的 `git stash` 和 `git commit --no-verify`。
- 不强制推送。
- rebase/merge 冲突只处理本轮确实修改的文件；其他文件发生冲突时停止并询问。

用户明确要求提交时：

- commit 必须原子化，但同一功能的代码、重构和测试保持在同一个逻辑提交中；
- 格式为 `type: <准确描述>`，如 `feat: 实现工具调用主循环`；
- 默认使用简体中文；若仓库历史主要使用英文，再使用英文；
- 禁止 `update`、`fixed` 等模糊描述。

## 破坏性与外部操作

- 删除文件、递归移动、覆盖配置或执行破坏性命令前，必须确认精确目标和授权。
- 禁止针对仓库根目录、用户目录、磁盘根目录或未解析变量执行递归删除。
- PowerShell 中优先使用经过验证的 `-LiteralPath`；不要把跨 shell 拼接出的路径用于删除或移动。
- 未经明确授权，不控制浏览器、不修改远端平台、不发送消息、不创建 issue/PR、不触发 CI/CD、不部署和不发布。

## 交付说明

- 结果先说明完成了什么，再列出关键设计、修改文件和验证结果。
- 明确区分“已验证”“未验证”和“推断”。
- 不把计划中的功能描述成已经实现。
- 不提交、不推送时无需反复提醒，但应在可能产生误解时明确说明。
