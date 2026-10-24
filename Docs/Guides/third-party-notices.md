# 许可证与第三方声明维护

项目采用 Apache-2.0。源码仓库与二进制 ZIP 使用同一套三份文件：

- [LICENSE](../../LICENSE)：Apache-2.0 完整原文，也供采用相同标准条款的第三方组件引用。
- [NOTICE](../../NOTICE)：项目归属、Codex fork 来源及适用于沙箱的上游声明。
- [THIRD-PARTY-LICENSES.txt](../../THIRD-PARTY-LICENSES.txt)：组件清单、版权归属、共享许可正文与特殊条款。

## 组织方式

第三方文件先列组件及其许可编号，再按许可证分类放置正文。每份共享正文前保留其适用组件／源文件与各自版权、归属或补充声明。同一正文仅因空白排版不同可合并；条款不同的变体分别保留。标准 Apache-2.0 正文统一引用根目录 LICENSE，Codex 原许可证中的版权仍保留。

MIT 正文共享一份。BSD-2-Clause、BSD-3-Clause 分开；ISC、Unicode、Zlib 等按实际文本分组。LLVM exception、复合许可与不能确认等价的上游文本完整保留在特殊声明部分，不根据 SPDX 名称强行改写。

## 当前基线与边界

按实际分发内容确定范围：直接或传递依赖只要带入受许可约束的代码，就保留相应版权与条款；上游声明仅选取相关部分。未使用的上游组件不因出现在上游仓库就纳入。对难以准确区分的运行库内部共享代码、内联代码及原生库归属，保守保留相关声明。

- 主程序：`net10.0/win-x64` 的 14 个生产 NuGet 依赖输入，以及 .NET NativeAOT runtime 10.0.12；组件记录保留版本、包版权和源提交。这是保守的库级归属范围，不表示每个包均有独立代码进入最终 exe。
- 沙箱：Codex fork 提交 `1b647eac0adc5f31140340ffcc5d49b19a235fad` 的 Windows x64 normal 依赖，排除仅用于 build／proc-macro／test 的路径，当前 420 个外部 crate；这是依赖范围，不是逐符号链接清单。
- Rust 运行时：保留 Rust 1.95.0 Windows x64 标准库、Unicode、相关运行时依赖／Windows 绑定及 compiler-builtins 的许可材料。

本次核对以实际 NativeAOT publish 的映射、链接响应文件和 PE 导入为依据，并与 runtime 提交 `95017c711e6afc1085133d440e42b4bd78155701` 的来源核对。链接输入包含 GC、压缩（zlib／Brotli）和全局化原生库；这些库及 BCL 内部来源无法可靠逐段拆分的部分保守保留。映射中没有某个独立符号不能单独证明代码未被内联，因此没有据此机械删除 NuGet 归属。

从 .NET 上游随附声明中排除以下八项，其余内部来源按上述边界保留。声明中的 ASP.NET、Mono、MessagePack 等名称也可能标识共享源码的来源，不表示分发了同名独立产品。

| 排除项 | 当前分发不适用的依据 |
| --- | --- |
| LinuxTracepoints | Linux 跟踪组件；Windows 原生链接输入采用 eventpipe-disabled。 |
| Apple header files | Apple 平台头文件，目标为 Windows PE。 |
| JavaScript queues | 对应 Mono 浏览器运行时的 queue.ts，未构建或分发浏览器运行时。 |
| ldap4net | DirectoryServices.Protocols 的 LDAP 集成；产物映射和原生链接输入均无此集成。 |
| MsQuic | 产物映射无 System.Net.Quic，原生链接输入也无 MsQuic。 |
| remote stack unwind / CompactUnwindInfo.cpp | Apple compact-unwind／LLDB 来源；当前使用 Windows unwind，未链接 Unix libunwind。其他无法细分的运行库来源仍保留。 |
| SharpFuzz and related samples | 上游独立 fuzzing 工具和插桩样例，不属于 publish 输入。 |
| NIST ACVP Data | 上游验证／测试数据，不属于主程序、运行库或发布文件输入。 |

Codex NOTICE 保留 OpenAI 的版权归属；所选沙箱生产源码与依赖范围不含 Ratatui／TUI，故不继承该部分声明。这里只构建沙箱组件，未把整个 Codex 产品的依赖作为本项目的分发清单。Rust 部分同样以 Windows 生产库及标准库范围保守保留，不引入全工作区、其他目标平台或仅用于测试／构建的工具清单。

部分 crate 的版本包缺少源提交记录，补充材料使用核对时固定的上游提交，清单保留这一限制。`fxhash`、`eventsource-stream` 依据包声明使用 Apache-2.0；`debugserver-types` 保留包作者元数据及标准 MIT 条款，未编造缺失的版权年份。合并文件不消除这些来源核对限制。

## 更新与分发

1. 更新 .NET 依赖或 SDK 后，根据 CLI 的 `project.assets.json` 生产目标、各包 `.nuspec` 与实际 runtime 版本更新组件及声明。执行 NativeAOT publish 时可设置 `-p:IlcGenerateMapFile=true`，结合生成的映射和链接响应文件重新核对范围；不要将本次八项排除当作以后版本的固定规则，也不要仅凭包名或符号缺失删除内部共享代码的归属。
2. 更新 Codex 时核对固定提交的 LICENSE／NOTICE、Cargo.lock 及 rust-toolchain.toml。在 fork 的 codex-rs 目录取得目标依赖：

   ```powershell
   cargo tree --locked --offline --target x86_64-pc-windows-msvc -p codex-windows-sandbox --edges normal,no-proc-macro --prefix none --format '{p}|{l}|{r}'
   ```

   对照对应版本的许可原文更新编号、版权和条款。MIT OR Apache-2.0 等替代许可可按实际授权选择一项，但嵌套第三方条款、例外与生成代码自身的归属必须保留。
3. 更新 Rust 时核对目标标准库和运行时 intrinsics 的许可，避免重新引入全平台或测试／构建工具清单。
4. 现有发布工作流将三份根目录文件复制进 ZIP，并纳入构建来源文件的哈希。自行分发 dotnet publish 产物时也需携带这三份文件，publish 不会自动生成许可材料。
