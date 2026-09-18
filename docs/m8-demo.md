# M8 配置与命令入口验证

本轮目标是让首次使用者不必手工编辑 JSON，并且能看到配置来源、模型预算和凭据是否可用。管理命令只处理本地配置，不会把输入发送给模型。

## 离线测试

```powershell
dotnet test TinyHarness.Tests/TinyHarness.Tests.csproj --no-restore
dotnet build TinyHarness.Cli/TinyHarness.Cli.csproj --no-restore
```

结果：默认离线测试 **225/225 通过**，CLI 构建 **0 警告、0 错误**。新增命令执行回归覆盖
`init`、`config show/set`、provider、model、auth 和离线 `doctor`。

## 隔离用户配置目录

在仓库外目录设置 `TINYHARNESS_USER_CONFIG_DIR`，按提示运行：

```powershell
$env:TINYHARNESS_USER_CONFIG_DIR = 'C:\Temp\tinyharness-m8'
TinyHarness.exe init
TinyHarness.exe config show
TinyHarness.exe provider list
TinyHarness.exe model list
TinyHarness.exe auth set work --env TINYHARNESS_WORK_KEY
TinyHarness.exe config set maxAgentSteps 60
TinyHarness.exe doctor
```

验证了：

- `init` 创建 `user-config.json`，写入 profile、endpoint、环境变量引用、模型和显式上下文窗口；
- 重新启动后的 `provider list`、`model list` 和 `config show` 能发现同一份配置；
- `config set` 只更新用户配置；项目目录配置存在时，运行配置仍遵循项目配置优先且不隐式合并；
- `doctor` 默认离线，只报告 endpoint、模型、路径、预算和凭据可用性；`--connect` 才会进入联网确认；
- JSON、终端输出和错误信息不包含 API key 值。

## NativeAOT

```powershell
dotnet publish TinyHarness.Cli/TinyHarness.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/m8-nativeaot
artifacts/m8-nativeaot/TinyHarness.exe --help
artifacts/m8-nativeaot/TinyHarness.exe doctor
artifacts/m8-nativeaot/TinyHarness.exe smoke --config tinyharness.json
```

结果：`win-x64` NativeAOT 发布无 trimming/AOT warning；从仓库外目录运行原生程序时，`--help`、离线 `doctor` 和既有 smoke 均成功。真实 provider/model 尚未加入 tested 清单。
