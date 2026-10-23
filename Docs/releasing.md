# Windows x64 打包与发布

工作流：[`.github/workflows/release.yml`](../.github/workflows/release.yml)。首版只生成 Windows x64 ZIP；以下说明工作流行为，不表示已经在 GitHub runner 上跑通或创建了发布。

## 触发方式

- `workflow_dispatch`：手动选择 TinyHarness 的 ref，只构建并上传 Actions artifact，不创建 Release。
- 推送 `v*` tag：先校验 tag 必须精确等于 CLI／Core 的 `Version` 加 `v` 前缀，例如 `v0.0.1`。全部构建和验证通过后，将同一批 ZIP 与校验文件附到 **Draft + Prerelease**，不会自动公开。
- 普通分支 push 和 PR 不触发此发布工作流。已有同名 Release 时创建会失败，不覆盖资产或修改已发布内容。

创建 tag、推送 tag、运行工作流及公开草稿分别属于实际操作，本文不自动执行这些步骤。

## 构建来源与验证

TinyHarness 使用触发本轮的 commit。沙箱来源为 [`banned2054/codex`](https://github.com/banned2054/codex)，当前固定 SHA 为 `1b647eac0adc5f31140340ffcc5d49b19a235fad`；更新组件时显式修改 YAML 中的 `CODEX_REF` 并重新核对兼容性，不追踪浮动 `main`。

构建 job 使用 `windows-2025`，只给 `contents: read`；Actions 使用 YAML 中声明的官方主版本标签，沙箱源码仍固定到完整 commit。通过 setup-dotnet 选择 .NET 10，并在 CI checkout 内临时创建 `global.json` 固定本次实际安装的 SDK，不写回仓库。Rust 工具链读取固定 fork 的 `rust-toolchain.toml`。

构建顺序：

1. 校验 CLI／Core 版本及 tag，确认 fork checkout 的 SHA。
2. Restore，运行默认离线测试，保留 TRX；测试不依赖真实模型或 provisioning。
3. `win-x64` NativeAOT publish。
4. 在 fork 的 `codex-rs` 内执行 `cargo build --locked --release --target x86_64-pc-windows-msvc -p codex-windows-sandbox --bins`。保留 fork 的 `.cargo/config.toml`，只编译沙箱 package 及其依赖，不编译完整 Codex CLI 或 service。
5. 按明确文件清单整理 ZIP，检查原生 exe 的版本，记录构建来源与各文件 SHA-256。
6. 解压 ZIP，核对文件哈希，在两个源码 checkout 之外运行原生 `--help` 和脚本模型 `smoke`。通过后才上传可发布 artifact。

这里没有真实供应商调用或 Windows 沙箱 provisioning／verify。编译与离线 smoke 成功不等于真实隔离、UAC、首次安装或所有使用场景已验收。首次 GitHub runner 执行后仍需检查具体日志和产物。

## 产物

Actions artifact 名为 `TinyHarness-<Version>-win-x64`，包含：

- `TinyHarness-<Version>-win-x64.zip`：带同名顶层目录。
- `SHA256SUMS.txt`：ZIP 的 SHA-256，Release job 下载后再次校验。
- `RELEASE_NOTES.md`：版本、来源和验证范围，供草稿使用。

ZIP 内包含 `TinyHarness.exe`、`sandbox/` 下的 setup／runner／managed-deny-probe、双语独立 `README.txt`、配置片段示例、`build-info.json`，以及根目录的三份许可材料：`LICENSE`（项目采用的 Apache-2.0 全文）、`NOTICE`（项目归属及继承的 Codex 声明）、`THIRD-PARTY-LICENSES.txt`（合并后的第三方版权、许可正文和特殊声明）。许可维护范围见[第三方声明说明](third-party-notices.md)。仅拷贝明确列出的文件，不打包源树、用户配置、凭据、会话日志、PDB 或整个 Cargo target 目录。deny-probe 与 runner 同目录，以便用户另行选择 `sandbox verify`。

`build-info.json` 记录两个仓库的 commit、SDK／Rust／Cargo 版本、构建链接和文件哈希；自身不加入内部文件哈希表，整个 ZIP 由外部校验文件覆盖。源 commit 固定不表示 ZIP 字节级可复现，SDK patch、runner 镜像和时间戳仍可能变化。

## 用户首次使用

包内说明与 [README 使用指南](README.md#-使用说明) 一致。解压目录应位于工作区和全部沙箱写根之外。配置向导先创建用户模型配置，沙箱示例仅用于合并 `settings.windowsSandbox`，其中两个组件路径需替换成用户机器上的真实绝对路径；不能覆盖整份用户配置。

解压不启用沙箱，不自动初始化或修改机器。用户选择沙箱后自行核对 `sandbox status`、明确执行 `sandbox provision` 并确认 UAC。严格模式和 TCP 环回模式的选择及副作用见[沙箱管理](windows-sandbox-management.md)。

## Release 草稿

独立 `draft-release` job 仅在版本 tag push 且构建成功时运行，使用 `contents: write` 与当前 run 的 `actions: read`，不检出或执行源码。它下载已验证 artifact、核对 ZIP checksum，再用 `gh release create --verify-tag --draft --prerelease` 创建草稿，不隐式创建 tag。

第一次使用先手动构建，下载 ZIP 检查目录、版本和说明，在获授权的环境验证实际使用，再决定 tag 和公开发布。workflow YAML 的本地静态检查不能代替 GitHub runner 的首次实际执行。
