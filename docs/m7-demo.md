# M7 可重复演示

从仓库根目录执行：

```powershell
dotnet restore TinyHarness.slnx
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet run --project TinyHarness.Cli -- smoke --config tinyharness.json
```

`smoke` 创建无第三方依赖的临时 .NET 10 控制台 fixture，并用清空包源的临时 `NuGet.Config` 正常 restore。固定检查程序先以错误实现得到 `CHECK_RESULT=-1`、`CHECK_FAIL` 和退出码 1；scripted fake client 随后搜索、读取并只修改 `Calculator.cs`，重新构建后必须得到 `CHECK_RESULT=5`、`CHECK_PASS` 和退出码 0。构建与执行分别使用结构化 direct 模式的 `dotnet build` 和 `dotnet <fixture.dll>`，不会直接执行 DLL；其他步骤继续覆盖列举、读取、补丁、direct process、explicit shell 与自动审批。它不访问网络，也不需要 API key；临时工作区会在进程结束时清理。

真实 `run` 会在配置的 `sessionDirectory` 中生成：

- `<runId>.audit.jsonl`：运行期间追加的准备、权限、工具结果和终止记录；`run.completed` 只表示 Agent 执行到达终态，不证明快照也已保存；
- `<runId>.session.json`：完成时写入的完整消息历史、结构化状态和最终结果。

这些文件用于检查和审计，不支持自动恢复或重放有副作用的调用。CLI 只显示实际存在的文件路径。落盘副本会脱敏配置的已知密钥及明确支持的敏感字段，但不保证识别任意秘密。

NativeAOT 验证：

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/m7-nativeaot
artifacts/m7-nativeaot/TinyHarness.exe smoke --config tinyharness.json
```

本轮验证结果（2026-09-11）：

- 新增持久化回归测试 **7/7 通过**；
- 默认离线测试 **178/178 通过**；
- 隔离构建目录中的托管 smoke 从仓库根目录与仓库外目录运行均成功；
- 隔离目录中的 `win-x64` NativeAOT publish 成功，没有 trimming/AOT warning；
- 从仓库外目录运行本次新生成的原生 EXE，smoke 成功（Completed，17 steps，16 次工具执行），并生成 session/audit 文件；
- 全部 smoke 均通过 CLI 文本和退出码报告结果，没有直接执行 fixture DLL，也没有桌面应用错误或文件关联窗口。

当前 Codex Windows 沙箱无法读取用户级 NuGet 配置时，可把构建输出切换到全新的 `--artifacts-path`，并在获准的非沙箱本地环境完成 restore/publish；不得把旧二进制当作本次验证结果。
