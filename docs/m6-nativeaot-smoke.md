# M6 NativeAOT publish 与原生产物 smoke 记录

## 范围

本记录覆盖 M6 Context/Compaction 变更要求的独立 NativeAOT 质量门槛：发布 CLI、运行发布
目录中的原生可执行文件，并走离线 fake-client smoke path。测试不需要网络或 API key。

## 环境与命令

- 日期：2026-09-10
- 目标 RID：`win-x64`
- 发布配置：`Release`
- 发布命令：

```powershell
dotnet publish TinyHarness.Cli\TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts\m6-nativeaot
```

- 原生产物 smoke 命令：

```powershell
artifacts\m6-nativeaot\TinyHarness.exe smoke --config tinyharness.json
```

## 结果

通过。

- publish exit code：`0`
- publish 输出中的 trimming/AOT warning：`0`（未出现）
- 原生产物 smoke exit code：`0`
- smoke 结果：`Completed`，10 steps，9 次工具执行；覆盖 `list_files`、`search_text`、
  `read_file`、`apply_patch`、direct process 和 explicit shell，并通过权限流程。
