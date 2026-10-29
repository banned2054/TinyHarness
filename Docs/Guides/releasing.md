# Windows x64 打包与发布

工作流：[`.github/workflows/release.yml`](../../.github/workflows/release.yml)。同一轮构建分别生成 CLI 和 Desktop 的 Windows x64 ZIP；以下说明工作流行为，不表示已经在 GitHub runner 上跑通或创建了发布。

## 触发方式

- `workflow_dispatch`：手动选择 TinyHarness 的 ref，只构建并上传 Actions artifact，不创建 Release。
- `release.published`：由用户自行创建并发布 Release（正式版或预发布版）；仅处理 tag 以 `v` 开头的版本。先校验 tag 必须精确等于 CLI／Core 的 `Version` 加 `v` 前缀，例如 `v0.0.1`。Desktop publish 显式传入同一 `Version`，不使用项目未声明版本时的 SDK 默认值。全部构建和验证通过后，将两个 ZIP 与校验文件上传到触发本轮的现有 Release。
- 保存 Release 草稿、单独推送 tag、普通分支 push 和 PR 不触发此发布工作流。Action 不创建 Release，不修改其标题、正文或预发布标记，也不覆盖同名附件。

创建 tag、推送 tag、手动运行工作流及发布 Release 分别属于实际操作，本文不自动执行这些步骤。发布 Release 会触发构建和附件上传；构建完成前，公开页面尚无本轮生成的分发附件。

## 构建来源与验证

TinyHarness 使用触发本轮的 commit。沙箱来源为 [`banned2054/codex`](https://github.com/banned2054/codex)，当前固定 SHA 为 `1b647eac0adc5f31140340ffcc5d49b19a235fad`；更新组件时显式修改 YAML 中的 `CODEX_REF` 并重新核对兼容性，不追踪浮动 `main`。

构建 job 使用 `windows-2025`，只给 `contents: read`；Actions 使用 YAML 中声明的官方主版本标签，沙箱源码仍固定到完整 commit。通过 setup-dotnet 选择 .NET 10，并在 CI checkout 内临时创建 `global.json` 固定本次实际安装的 SDK，不写回仓库。Rust 工具链读取固定 fork 的 `rust-toolchain.toml`。

构建顺序：

1. 校验 CLI／Core 版本及 tag，确认 fork checkout 的 SHA。
2. Restore，分别运行 `TinyHarness.Tests` 和 `TinyHarness.Desktop.Tests` 默认离线测试，保留各自 TRX；测试不依赖真实模型或 provisioning。
3. 分别执行 CLI／Desktop 的 `win-x64` NativeAOT publish，输出到隔离的 `native/cli`、`native/desktop` 目录。
4. 在 fork 的 `codex-rs` 内执行 `cargo build --locked --release --target x86_64-pc-windows-msvc -p codex-windows-sandbox --bins`。保留 fork 的 `.cargo/config.toml`，只编译沙箱 package 及其依赖，不编译完整 Codex CLI 或 service。
5. 将同一批沙箱组件分别放入两端的 ZIP，检查两个原生 exe 的版本，分别记录端类型、构建来源与各文件 SHA-256，生成包含两个 ZIP 的统一校验清单。
6. 分别核对 ZIP 校验和、解压并核对文件哈希；在两个源码 checkout 之外运行 CLI 原生 `--help`、脚本模型 `smoke`，以及 Desktop 的 `--offline-smoke`。桌面入口不初始化窗口；通过 `Start-Process -Wait` 等待 WinExe 并检查退出码。两端全部通过后才上传可发布 artifact。

这里没有真实供应商调用、GUI 交互或 Windows 沙箱 provisioning／verify。编译与离线 smoke 成功不等于真实隔离、UAC、首次安装、窗口渲染或所有使用场景已验收。首次 GitHub runner 执行后仍需检查具体日志和产物。

## 产物

Actions artifact 名为 `TinyHarness-<Version>-win-x64`，包含：

- `TinyHarness-CLI-<Version>-win-x64.zip`：CLI，带同名顶层目录。
- `TinyHarness-Desktop-<Version>-win-x64.zip`：Desktop，带同名顶层目录。
- `SHA256SUMS.txt`：两个 ZIP 的 SHA-256，Release job 下载后再次逐项校验。
- `RELEASE_NOTES.md`：版本、来源和验证范围，保留在 Actions artifact 中供参考，不覆盖用户填写的 Release 正文。

两个 ZIP 都包含 `TinyHarness.exe`、`sandbox/` 下的 setup／runner／managed-deny-probe、各端的双语独立 `README.txt`、配置片段示例、`build-info.json`，以及根目录的三份许可材料：`LICENSE`（项目采用的 Apache-2.0 全文）、`NOTICE`（项目归属及继承的 Codex 声明）、`THIRD-PARTY-LICENSES.txt`（合并后的第三方版权、许可正文和特殊声明）。许可维护范围见[第三方声明说明](third-party-notices.md)。仅拷贝明确的主程序、许可／说明材料和沙箱组件；Desktop 另外按原相对路径复制其 publish 目录内的 DLL，并检查 `av_libglesv2.dll`、`libHarfBuzzSharp.dll`、`libonigwrap.dll`、`libSkiaSharp.dll` 均存在。不打包源树、用户配置、凭据、会话日志、PDB 或整个 Cargo target 目录。deny-probe 与 runner 同目录，以便用户另行选择 `sandbox verify`。

Desktop 保留内部程序集名 `TinyHarness.Desktop`，打包时只把 NativeAOT 入口文件复制为 `TinyHarness.exe`。两端的 ZIP 与顶层目录明确区分，必须分别解压，不可合并覆盖；只有 CLI 目录加入 `PATH`，Desktop 通过双击或指向完整路径的快捷方式启动。用户看到的应用名与终端命令均为 TinyHarness，不要求公开 `.Desktop` 后缀。

`build-info.json` 记录 `edition`（`CLI`／`Desktop`）、版本、两个仓库的 commit、SDK／Rust／Cargo 版本、构建链接和文件哈希；自身不加入内部文件哈希表，整个 ZIP 由外部校验文件覆盖。源 commit 固定不表示 ZIP 字节级可复现，SDK patch、runner 镜像和时间戳仍可能变化。

## 用户首次使用

CLI 按包内说明与 [README 使用指南](../README.md#-使用说明) 配置；Desktop 完整解压后双击 `TinyHarness.exe`，在设置中配置供应商与模型，选择项目目录后开始对话。Desktop 的 DLL 必须保留在原相对位置。两端默认共享用户配置与会话存储。

解压目录应位于工作区和全部沙箱写根之外。沙箱示例仅用于合并 `settings.windowsSandbox`，其中两个组件路径需替换成用户机器上的真实绝对路径；不能覆盖整份用户配置。Desktop 用户如需显式初始化或验收沙箱，另用 CLI 包执行相应命令。

解压不启用沙箱，不自动初始化或修改机器。用户选择沙箱后自行核对 `sandbox status`、明确执行 `sandbox provision` 并确认 UAC。严格模式和 TCP 环回模式的选择及副作用见[沙箱管理](../Archive/windows-sandbox-management.md)。

## Release 附件上传

独立 `upload-release-assets` job 仅在 `v` 开头的 Release 发布且两端构建成功时运行，使用 `contents: write` 与当前 run 的 `actions: read`，不检出或执行源码。它下载已验证 artifact、核对两个 ZIP 的 checksum，确认对应 Release 仍存在且已发布，再用 `gh release upload` 上传 CLI ZIP、Desktop ZIP 和 `SHA256SUMS.txt`。没有创建 Release 或 tag 的步骤，也不使用 `--clobber`；同名附件冲突时失败，不删除或替换已有附件。

发布前核对 tag 指向预期提交，并由用户填写标题、正文和是否为预发布版；正文可使用当前版本的中英文 CHANGELOG。发布后等待对应 run 成功，再核对 Release 附件、ZIP 校验和、包内版本与 build-info.json 的来源记录，以及随附的三份许可文件。构建或上传失败时 Release 仍保持用户发布时的状态，应检查日志；手动 `workflow_dispatch` 仅生成 artifact，不向既有 Release 补传附件。workflow YAML 的本地静态检查不能代替 GitHub runner 的实际执行。
