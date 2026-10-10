# Windows 沙箱 · TinyHarness .NET 直接组件接入方案

> 版本基准：本地源码 `C:\Code\Rust\codex`（2026-10-09 核对，HEAD `d650bd7c05`）
> 协议硬版本：`SETUP_VERSION = 5`，`IPC_PROTOCOL_VERSION = 6`
> 主方案：TinyHarness 的 C# Runtime 直接编排独立编译的 setup / runner 组件，不调用 Codex CLI，不新增 Rust shim
> 状态：组件已编译，协议及部分 C# 片段已验证；完整 TinyHarness 接入与实际隔离验收尚未完成
> 命名空间（2026-10-10）：接入目标改为 TinyHarness 独立命名空间 fork（§1.1）；setup/runner 已替换为 fork 构建
> 产品范围和实施顺序：[PLAN.md §22：M11](../PLAN.md#22-m11windows-工具进程沙箱)
> 目标读者：在 .NET harness 中复用 Codex Windows 沙箱模块执行不可信命令的集成开发者

---

## 1. 概述（TL;DR）

- **这不是虚拟机，也不是独立 Windows Sandbox 桌面环境。** 隔离通过本机沙箱账户、受限 token、文件 ACL、私有桌面和防火墙/WFP 实现；不提供独立内核或磁盘快照。
- 仓库内 `codex-rs/windows-sandbox-rs`（package `codex-windows-sandbox`）与 `codex-rs/windows-sandbox-service`（package `codex-windows-sandbox-service`）已独立编译出 **4 个 exe**。它们不是任意命令的通用 CLI；原本由 Rust 库编排，TinyHarness 将实现自己的 C# 编排方。
- **主方案为直接组件接入，不经过 `codex.exe`、Codex Agent、模型或会话。** 日常执行使用 setup + runner，deny-probe 用于隔离验收，service 暂不接入；首期不新增 Rust shim，也不要求修改 Codex Rust 公共 API。
- **纯 C# 接口可行性已有证据，但完整接入尚未端到端验证**：
  - 凭据 DPAPI 为 **LocalMachine 作用域**，`ProtectedData.Unprotect` 可解；
  - cap SID 为**明文 JSON**（`<codex_home>\cap_sid`）；
  - 每命令 ACL 刷新走普通权限；首次 provisioning 或后续修复只能由独立、显式管理操作触发 UAC，普通执行不得自动提权；
  - 权限解析、ACL、capability SID、网络账户、私有桌面和管道身份必须一致；合法 `permission_profile` 不证明隔离已生效。
- 下文 C# 代码是局部编排片段，不是完整生产实现。行数与工时不作未经验证的承诺。标准 Windows 策略要求有效 `root/read`，不能宣传为“只允许读取仓库”；清理目标环境也不能替代磁盘敏感文件访问控制。

### 1.1 TinyHarness 独立命名空间 fork（2026-10-10）

上游 Codex 发布版与 TinyHarness 曾共用同一组机器级命名（账户、组、防火墙规则、WFP 对象、桌面前缀），同机两个安装会静默争用：重置对方依赖的密码、把防火墙规则改写到对方账户 SID、以 delete-then-add 删掉对方的 WFP 过滤器。因此本地 `C:\Code\Rust\codex` 已改为 **TinyHarness 独立命名空间 fork**（命名统一收口在 `codex-rs/windows-sandbox-rs/src/branding.rs`），并替换 setup / runner 两个 exe；TinyHarness 的 C# 接入同步适配。

fork 改名（机器级命名）：

| 类别 | 上游 Codex | TinyHarness fork |
|---|---|---|
| 本地账户 | `CodexSandboxOffline` / `CodexSandboxOnline` | `TinyHarnessOffline` / `TinyHarnessOnline` |
| 本地组 | `CodexSandboxUsers` | `TinyHarnessUsers` |
| 私有桌面前缀 | `CodexSandboxDesktop-` | `TinyHarnessDesktop-` |
| setup / read-ACL mutex | `Global\CodexSandboxSetup` / `Local\CodexSandboxReadAcl` | `Global\TinyHarnessSandboxSetup` / `Local\TinyHarnessSandboxReadAcl` |
| 防火墙规则名 | `codex_sandbox_offline_*` | `tinyharness_sandbox_offline_*` |
| WFP provider/sublayer/filter | Codex 固定 GUID 与名称 | TinyHarness 专属 GUID（uuid5 派生，可复现可审计） |
| 注册表安装记录 | `SOFTWARE\OpenAI\Codex\WindowsSandboxService` | `SOFTWARE\TinyHarness\WindowsSandboxService` |
| provisioning 管道（service 专用，首期不用） | `\\.\pipe\OpenAI.CodexSandbox` | `\\.\pipe\TinyHarness.Sandbox` |
| 服务名（不安装） | `CodexSandboxService` | `TinyHarnessSandboxService` |

保持不变（wire 兼容）：exe 文件名（`codex-windows-sandbox-setup.exe`、`codex-command-runner.exe`、`codex-windows-managed-deny-probe.exe`）、payload/spawn 字段名（`codex_home`、`real_codex_home` 等）、`--launch-payload-env` 与 `CODEX_SANDBOX_LAUNCH_*` 分块通道、deny-probe 的 `CODEX_WINDOWS_*` 环境变量、DPAPI(LocalMachine) 凭据格式、`SETUP_VERSION=5` / `IPC_PROTOCOL_VERSION=6`。不安装 service。

TinyHarness C# 侧适配：

- setup payload 的 `offline_username` / `online_username` 为 `TinyHarnessOffline` / `TinyHarnessOnline`；
- 父进程私有桌面前缀为 `TinyHarnessDesktop-`；
- sandbox home 使用独立固定默认 `%LOCALAPPDATA%\tinyharness\windows-sandbox-home`（可信设置可显式覆盖），与 Codex 的 home 完全独立；
- marker（`setup_marker.json`）与凭据（`sandbox_users.json`）中仍为旧 Codex 用户名（`CodexSandboxOffline` / `CodexSandboxOnline`）时，状态检查、凭据读取与 provisioning 一律拒绝复用（fail closed），要求换独立 home 重新 provisioning；
- `sandbox verify` 的验收报告仅作为“新二进制已通过最小测试”的依据，不构成完整隔离验收或与 Codex 共存已验证的证明。
- 网络隔离（2026-10-10 修复闭环）：首轮流验收实测按用户作用域（LocalUserAuthorizedList）的防火墙 BLOCK 规则未拦住沙箱账户进程的 TCP 出站（本机 IP 环回快速路径亦绕过按远程地址匹配的规则），`network-denied` 如实 FAIL；随后 fork 补充按账户 SID 的 WFP 通用 BLOCK 过滤器（`tinyharness_wfp_tcp_connect_v4/v6`、`tinyharness_wfp_udp_connect_v4/v6`，ALE_AUTH_CONNECT 层、内核强制、覆盖环回），重新 provisioning 后该用例 PASS，全矩阵 15/15 PASS。防火墙规则仍保留（纵深防御），但断网不再依赖其归因行为。

### 核实范围（2026-10-09）

- 已使用本机现有构建产物中的真实 `PermissionProfile`、`FramedMessage`、`write_frame` / `read_frame` 运行临时 Rust 探针：只读与最小工作区写入 profile、完整 spawn 帧及长度前缀读写均通过往返验证；原报告的空子结构 profile 和平铺 spawn 帧均被拒绝。
- 文档修订后，已提取全部 5 个 `json` 代码块，通过真实 Rust profile serde 或消息 framing 往返验证；单独的元数据 entry 通过包装进 profile 验证其 wire 适配器。
- 已在 .NET SDK 10.0.401 下编译文档中的 C# 帧读写与 Windows 管道创建片段（0 警告、0 错误）；帧读写运行测试覆盖往返、干净 EOF、超长帧、截断帧及错误协议版本。管道片段仅编译，未连接或启动 Runner。
- 已逐路径核对超时、主动终止、正常退出及父进程状态转换；超时码 **192** 是源码明确选定的值，不是推测。
- 本次核实**没有运行真实沙箱超时或权限隔离集成测试，没有执行 provisioning，也没有修改账户、ACL 或防火墙**。协议验证不等于隔离生效验证；后者见 §7。

### 构建产物（已在本机构建验证）

```
C:\Code\Rust\codex\codex-rs\target\x86_64-pc-windows-msvc\release\
├── codex-windows-sandbox-setup.exe        (17.7 MB)  provisioning 助手（UAC）
├── codex-command-runner.exe               ( 8.2 MB)  沙箱内命令执行器（管道协议）
├── codex-windows-managed-deny-probe.exe   (244 KB)   ACL 自检探针
└── codex-windows-sandbox-service.exe      (55.7 MB)  MSIX 打包版专用服务（可不用）
```

构建命令（Rust 1.95.0，VS 18 C++ 工具集，无需 Developer PowerShell）：

```bash
cd C:/Code/Rust/codex/codex-rs
cargo build --locked --release --target x86_64-pc-windows-msvc \
  -p codex-windows-sandbox -p codex-windows-sandbox-service --bins
```

---

## 2. 架构总览

```
┌──────────────┐   显式 UAC      ┌──────────────────────────┐
│  .NET harness │ ──────────────▶ │ codex-windows-sandbox-   │
│  (编排方/父进程)│   base64 payload│ setup.exe                │
│      │        │                 │ 建账户/组/防火墙/WFP/ACL  │
│      │        │ ──────────────▶ │ （每命令前 refresh_only， │
│      │        │  普通权限静默    │   不提权）                │
│      ▼        │                 └──────────────────────────┘
│ CreateProcess │
│ WithLogonW    │  以沙箱账户启动
│      │        │ ┌──────────────────────────┐
│      └───────▶│ codex-command-runner.exe   │
│  命名管道×2    │ │ 派生受限 token → 目标命令 │
│  帧协议 v6    │ └──────────────────────────┘
└──────────────┘
```

| 组件 | 角色 | 权限要求 |
|---|---|---|
| `codex-windows-sandbox-setup.exe` | 显式 provisioning + 每命令 ACL 刷新 | provisioning/修复需显式授权提权；普通 refresh 不提权 |
| `codex-command-runner.exe` | 以沙箱账户运行，经双管道接收帧协议，派生受限 token 启动目标命令 | 必须以沙箱用户身份运行（`CreateProcessWithLogonW`） |
| `codex-windows-managed-deny-probe.exe` | ACL 生效性自检（测试用，不在生产链路） | 无 |
| `codex-windows-sandbox-service.exe` | MSIX 打包 codex 的免 UAC provisioning 服务 | 须由 SCM 启动（SYSTEM）；**自建 harness 不需要** |

两条账户模型：fork 后为 `TinyHarnessOffline`（默认断网）与 `TinyHarnessOnline`（可联网，上游 Codex 用于走代理）。**TinyHarness 首期只支持 offline；联网、代理、端口放行及 online 账户选择不向模型开放。**

### 2.1 TinyHarness 调用链与职责

```text
AgentLoop：整轮准备、授权、顺序 dispatch
  → ShellTool.Prepare：冻结命令、cwd、timeout、清理后的环境及有效隔离策略
  → PermissionEngine：审批同一 prepared plan
  → ShellTool.ExecuteAsync
      → HostProcessBackend：保留既有宿主执行
      → WindowsSandboxBackend（C#）：refresh、桌面、凭据、管道、runner 和回收
  → 输出脱敏/裁剪/artifact、工具结果和审计
```

现有接入点是 `ShellTool` 的进程启动与生命周期逻辑，由 CLI composition root 显式注入后端；不重写 AgentLoop，不让 sandbox 自动把 `Ask` 变成 `Allow`。后端契约表达命令执行会话和结果，不能强制暴露 `.NET Process`，因为 runner 的 OS 状态不是命令协议结果。

### 2.2 首期范围与兼容策略

- Windows `win-x64`；非 TTY、关闭 stdin、每命令一个 runner，不提供后台常驻进程。
- 固定的只读执行/工作区写入策略。只读表示工作区不可写，不等于所有目录不可写；运行必需的临时写根应单独展示。工作区写入保护约定的元数据目录。
- 结构化 direct 模式和经过参数/quoting 验证的显式 shell 模式；不向模型增加任意策略、组件路径或沙箱逃逸参数。
- 旧配置继续宿主执行并明确提示“无 OS 隔离”；显式选择 Windows sandbox 后，组件缺失、状态不符或隔离失败均 fail closed，绝不自动 fallback。
- 文件工具继续使用现有路径边界；MCP worker 继续只注册只读工具。本轮不隔离整个 Harness，不限制模型 HTTP 请求，也不宣称 MCP 文件工具获得 OS 隔离。
- Linux/macOS 后续独立实现相同执行契约；本轮不创建空后端或承诺支持。

---

## 3. 可执行文件参数参考

### 3.1 codex-windows-sandbox-setup.exe

| 项 | 内容 |
|---|---|
| 参数 | 恰好 1 个位置参数：Base64(JSON payload)；或 `--launch-payload-env`（payload 从环境变量分块读取） |
| 参数个数错误 | 直接失败（`HelperRequestArgsFailed`） |
| stdin/stdout | 不用于协议（父进程置 null） |
| 环境变量 | `CODEX_HOME`（仅错误兜底日志）；`CODEX_SANDBOX_LAUNCH_BYTES/COUNT/_0..N`（大 payload 分块通道，每块 ≤16KB，总量 ≤16MB） |
| 提权 | 父进程用 `ShellExecuteExW verb="runas"`；refresh 路径明确不提权（`setup.rs:391`） |
| 退出码 | 成功 0 / 失败 1；**错误详情读 `<codex_home>\.sandbox\setup_error.json`**，不靠退出码区分 |
| 同目录依赖 | 无（路径全由 payload 给出；父进程按 exe 同目录 → `codex-resources\` → PATH 查找它） |

Payload JSON 结构（serde，kebab/camel 混合以源码为准，以下为已核对的字段名）：

```jsonc
{
  "version": 5,                          // 必填，必须 == 5
  "offline_username": "TinyHarnessOffline",   // fork 命名空间
  "online_username":  "TinyHarnessOnline",
  "codex_home":  "C:\\harness\\tinyharness-sandbox-home",
  "command_cwd": "C:\\work\\repo",
  "read_roots":  ["C:\\work\\repo"],     // 必填数组
  "write_roots": ["C:\\work\\repo"],     // 必填数组
  "deny_read_paths":  [],                // 可选，默认空
  "deny_write_paths": [],                // 可选，默认空
  "proxy_ports": [],                     // 必填 Vec<u16>，无代理留空
  "allow_local_binding": false,          // 可选，默认 false
  "real_user": "YourWindowsUser",        // 必填
  "mode": "full",                        // full(默认)|interactive-provision|provision-only|read-acls-only
  "refresh_only": false,                 // 首次 false；每命令刷新 true
  "user_profile": null,                  // 可选
  "otel": null                           // 可选
}
```

> ⚠️ base64 后超过约 24000 个 UTF-16 单元时必须切换 `--launch-payload-env` 分块通道（write_roots 很多时会触发）。

### 3.2 codex-command-runner.exe

| 项 | 内容 |
|---|---|
| 参数 | `--pipe-in=<管道名>`、`--pipe-out=<管道名>`，均必填；其余忽略 |
| 管道名 | 父进程生成：`\\.\pipe\codex-runner-{随机nonce}-in`（父写）/ `-out`（父读） |
| 环境变量 | 不读任何 env（`req.env` 只传给沙箱内子进程） |
| 生命周期 | **每条命令一个 runner 进程**：收一个 `spawn_request`，运行命令，发送 `exit` 帧后按选定退出码退出（发送失败会记日志） |
| 退出码 | 正常 = 查询到的子进程退出码；等待超时 = **192**，对应 `exit.payload.timed_out:true`；早期错误通常 = 1，可能没有 `exit` 帧。详见 §6.4 |
| GUI 子系统 | 是（`windows_subsystem = "windows"`，不挂控制台，设计如此） |

### 3.3 codex-windows-managed-deny-probe.exe

| 项 | 内容 |
|---|---|
| 参数 | 无 |
| 环境变量（4 个全必填） | `CODEX_WINDOWS_ALLOWED_TEXT` / `CODEX_WINDOWS_DENIED_TEXT`（文本文件路径，前者内容须为 `ALLOW-CONTROL`）/ `CODEX_WINDOWS_ALLOWED_MODULE` / `CODEX_WINDOWS_DENIED_MODULE`（DLL 路径） |
| 输出 | stdout 按行：`allowed-read:OK`、`allowed-import:OK`、`denied-read:DENIED|UNEXPECTED_SUCCESS`、`denied-import:…` |
| 退出码 | 0 = deny 全部生效；20 = 有 deny 路径意外可访问；21 = 探针配置错误 |

### 3.4 codex-windows-sandbox-service.exe

| 项 | 内容 |
|---|---|
| 参数 | 无参数 或 `--service`；`--foreground` 仅 debug 构建有效 |
| 管道 | 服务端监听上游 `\\.\pipe\OpenAI.CodexSandbox`（fork 为 `\\.\pipe\TinyHarness.Sandbox`；打包版带包族后缀） |
| 请求 | `register_installation_request` / `provision_sandbox_request`（帧协议 version 1，一连接一帧，请求 ≤4096B，空闲 5s 超时） |
| 结论 | **自建 harness 完全可以不用它**，直接走 setup.exe 的 UAC helper 路径 |

---

## 4. 系统级副作用（provisioning 改了什么）

| 类别 | 内容 | 位置 |
|---|---|---|
| 本地组 | `TinyHarnessUsers`（fork；上游为 `CodexSandboxUsers`） | 机器级 |
| 本地账户 | `TinyHarnessOffline` / `TinyHarnessOnline`（fork；上游为 `CodexSandboxOffline` / `CodexSandboxOnline`；随机 24 位密码，`UF_DONT_EXPIRE_PASSWD`） | 机器级 |
| 注册表 | 隐藏上述账户（`HKLM\...\SpecialAccounts\UserList`） | 机器级 |
| 防火墙 | INetFwPolicy2 COM：loopback 代理端口放行 + offline 账户断网规则 | 机器级 |
| WFP | 对 offline 账户 SID 的 BLOCK 过滤器（ICMP、DNS 53/853、SMB 445，v4+v6） | 机器级 |
| 目录 ACL | `.sandbox`/`.sandbox-secrets`/`.sandbox-bin` 锁定；系统目录读授权；write_roots 授权 | home 内及命令涉及的文件系统路径 |

`<codex_home>` 下的文件布局：

```
<codex_home>\
├── cap_sid                          # 明文 JSON：capability SID 表
├── .sandbox\
│   ├── setup_marker.json            # version==5 的完成标记
│   ├── sandbox.YYYY-MM-DD.log       # 日志
│   └── setup_error.json             # 失败详情（仅失败时）
├── .sandbox-bin\                    # runner 会被自动复制到此处并加 ACL
└── .sandbox-secrets\
    └── sandbox_users.json           # 账户凭据（密码 = base64(DPAPI-LocalMachine blob)）
```

---

fork 之后，TinyHarness 与 Codex 发布版在机器级命名上完全分栈（账户、组、防火墙规则、WFP 对象、mutex、注册表记录互不共享），`codex_home`（sandbox home）也各自独立——TinyHarness 缺省使用固定默认 home。命名分栈显著降低同机共存时的互相破坏，但**不等于共存已验收**：共存行为仍未验证，系统验收使用可丢弃 Windows 环境；不能在日常机器上无提示地重复初始化。同一 fork 命名空间内部的多个 home 仍共享账户与网络规则：重复 provisioning 会重置 `TinyHarnessOffline` / `TinyHarnessOnline` 的密码，使另一个 home 的凭据失效。

## 5. .NET Harness 接入方案（主方案：C# 直接编排）

### 5.1 可信配置、准备计划与授权

组件绝对路径、sandbox home、最低隔离要求、网络上限和额外写根来自可信用户/宿主设置，**不能被目标项目 `tinyharness.json` 的普通配置优先级覆盖**。首期只提供固定策略；项目 commandRules 可以批准命令，但不能据此初始化机器、关闭隔离或授予联网权限。

Prepare 必须保持无副作用：冻结命令、cwd、timeout、清理后的目标环境及有效策略，并以稳定规范化表示计算策略摘要。fingerprint 和 session grant 绑定 backend、策略版本、实际读写范围、deny、网络、临时根、元数据保护和环境策略身份，而不是只绑定 profile 名称。相同命令不能复用旧授权去扩大权限或切换宿主执行；显式拒绝也不能因更换后端意外失效。

下述 refresh、SID 写回、桌面/管道创建和启动仅在授权后执行。执行前重新检查路径边界和可信组件，实际权限必须与 prepared plan 一致；若只能扩大权限才能启动，应失败并重新准备，不能静默修改计划。

### 5.2 环境、凭据和实现约束

- 使用经过清理、在 Prepare 冻结的目标环境；明确 PATH、TEMP/TMP、用户目录及工具链缓存。shim 不在首期链路中，setup、runner 和目标命令的环境分别限制，不能继承所有宿主环境后仅删除一个 key。
- 凭据仅在需要启动时读取，不进入工具参数、审计、异常或模型上下文。DPAPI LocalMachine 不是唯一访问边界，凭据目录 ACL 和访问限制仍必须保留。
- 输出 artifact、审计目录和可信组件不得对目标命令可写；位于工作区写根内时需要明确保护或迁移至受保护位置。
- JSON DTO 使用 `System.Text.Json` source generation，依赖显式注册，P/Invoke 与 Windows 依赖必须通过 NativeAOT；不通过启用反射或关闭 analyzer 绕过验证。

### 5.3 编排步骤

### Step 0 · 统一解析每条命令的有效权限

以同一份 `permission_profile` 和 `workspace_roots` 推导 setup 的读写根、deny 路径、capability SID 与网络账户。下列 Step 1–5 展示部分编排骨架，**不能把各处示例独立拼接后当成完整安全实现**。

- §6.2 的最小写入 profile 用 `project_roots/write` 绑定 `workspace_roots`；只读 profile 不应配上工作区写入 capability。
- 普通 elevated setup 要求有效 `root/read`；Windows 后端不支持 disabled/external profile、unrestricted managed 文件系统及全盘写权限。不要把 serde 可解析当成后端可执行。
- `permission_profile.network` 必须与启动 Runner 所用的沙箱账户、已配置的防火墙/WFP 或代理身份一致。仅发送 `"network":"restricted"` 不会令 online 账户自动断网。
- setup roots 还包含库推导的平台、运行环境等路径。以下仅有 `workspaceRoot` 的 payload 是结构示例，不是库有效 roots 的完整替代。参考 `resolved_permissions.rs:39-117`、`elevated_impl.rs:120-202` 和 `identity.rs:267-310`。

### Step 1 · 显式 provisioning（需要 UAC）

由独立管理入口触发，展示账户、ACL、注册表及网络副作用后执行。普通工具调用不运行本步骤；未初始化或需要修复时返回清晰错误。下例展示 payload 与启动方式，生产代码必须使用异步等待、取消和独立准备期限，不能直接保留同步 `WaitForExit()`。

```csharp
var payload = new {
    version = 5,
    offline_username = "TinyHarnessOffline",   // fork 命名空间
    online_username  = "TinyHarnessOnline",
    codex_home = codexHome,                 // 如 C:\harness\tinyharness-sandbox-home
    command_cwd = workspaceRoot,
    read_roots  = new[] { workspaceRoot },
    write_roots = new[] { workspaceRoot },
    deny_read_paths = Array.Empty<string>(),
    deny_write_paths = Array.Empty<string>(),
    proxy_ports = Array.Empty<ushort>(),
    allow_local_binding = false,
    real_user = Environment.UserName,
    mode = "full",
    refresh_only = false
};
string b64 = Convert.ToBase64String(
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

var psi = new ProcessStartInfo(setupExePath, b64) {
    UseShellExecute = true, Verb = "runas", CreateNoWindow = true   // UAC
};
using var p = Process.Start(psi)!;
p.WaitForExit();
// 失败：读 <codex_home>\.sandbox\setup_error.json
```

### Step 2 · 每命令前刷新 ACL（普通权限，静默）

同一 payload 构造，改 `refresh_only = true`，**不走 UAC**：

```csharp
var psi = new ProcessStartInfo(setupExePath, b64) {
    UseShellExecute = false, CreateNoWindow = true   // 无 Verb
};
// 生产实现异步等待 exit 0，并限制 refresh 期限；失败不自动转 UAC
```

### Step 3 · 读取沙箱账户密码

```csharp
// NuGet: System.Security.Cryptography.ProtectedData
var users = JsonSerializer.Deserialize<SandboxUsersFile>(
    File.ReadAllText(Path.Combine(codexHome, @".sandbox-secrets\sandbox_users.json")))!;
// {"offline":{"username":...,"password":"<base64>"},"online":{...}}
byte[] blob = Convert.FromBase64String(users.offline.password);
string password = Encoding.UTF8.GetString(
    ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine));
```

账户选择：**offline（断网，推荐）**；online 仅在有自家网络代理时使用。

### Step 4 · 确定 cap_sids

读 `<codex_home>\cap_sid`：

```jsonc
{
  "workspace": "S-1-5-21-…",
  "readonly":  "S-1-5-21-…",
  "workspace_by_cwd":      { "<规范路径>": "S-1-5-21-…" },
  "writable_root_by_path": { "<规范路径>": "S-1-5-21-…" }
}
```

规则（`elevated_impl.rs:157-176` 已核对）：

- 只读命令：`cap_sids = [readonly]`
- 可写命令：对**有效写入根**逐个在 `writable_root_by_path` 查 SID；这些根由已解析权限、cwd、env、codex_home 及 overrides 推导，不一定仅为 `workspace_roots`（例如内置策略还允许临时根）。无条目则生成
  `S-1-5-21-{4 个随机 uint}` 插入并**写回文件**（get-or-create 语义，`cap.rs:103-114`）；对应 SID 必须与 setup 刷新到这些根上的 ACL 一致。

库在父进程用首个 capability SID 给 `\Device\Null` 加 allow ACE（`elevated_impl.rs:180-182`）；当前 Runner 在启动子进程前也会为传入的 capability SIDs 调用 `allow_null_device`（`bin/command_runner/win.rs:301-307`）。完整复刻父进程链路时应对齐这一动作，不要误认为只有父进程执行了它；NUL 访问仍须在集成测试中验证。

### Step 4a · 创建并保持父进程拥有的私有桌面（CLI 也必需）

当前 Runner 在进入 TTY / 非 TTY 分支之前调用 `LaunchDesktop::open_private`；`private_desktop_name` 缺失就失败（`bin/command_runner/win.rs:310-320`）。它不会替父进程创建桌面，也不会回退到 Default 桌面。

父进程应对齐 `desktop.rs:263-348` 的编排：

1. 生成 `TinyHarnessDesktop-`（fork 前缀；上游为 `CodexSandboxDesktop-`）加 32 位随机十六进制后缀，通过 `CreateDesktopW` 创建私有桌面；请求字段传**裸桌面名**，不带 `Winsta0\` 前缀。
2. DACL 将 `DESKTOP_ALL_ACCESS` 授予父进程用户 SID，将 `DESKTOP_PARTICIPANT_ACCESS` 授予所选沙箱账户 SID。后者排除 `WRITE_DAC`、`WRITE_OWNER`、`DELETE`（精确定义见 `desktop.rs:69-84`）；不要以共用的 logon SID 代替父进程用户 SID 授予 ACL 管理权。
3. 将实际名称写入 `spawn_request.payload.private_desktop_name`，保持父进程桌面句柄存活，至少覆盖命令完整生命周期。
4. 若跨命令复用桌面，只允许相同沙箱账户及相同有效安全策略复用。库的缓存键同时包含 capability SIDs、网络策略、有效读写根及 deny 路径，不能为不同权限命令共用一个全局桌面。

> 这是原接入步骤遗漏的必需环节。§6 示例中的桌面名和 SID 都是占位值；即使通过 JSON 解析，未创建/授权桌面或未配置对应 ACL，仍不能正常或安全执行。

### Step 5 · 建管道对并以沙箱账户启动 runner

```csharp
string nonce = Guid.NewGuid().ToString("N");
string inName  = $"codex-runner-{nonce}-in";     // .NET 构造器使用短名，父写
string outName = $"codex-runner-{nonce}-out";    // .NET 构造器使用短名，父读
string runnerInName  = $@"\\.\pipe\{inName}";
string runnerOutName = $@"\\.\pipe\{outName}";

var sec = new PipeSecurity();
sec.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(offlineUserSid),               // 沙箱用户 SID
    PipeAccessRights.ReadWrite, AccessControlType.Allow));

// .NET Windows，引用 System.IO.Pipes.AccessControl
var inPipe  = NamedPipeServerStreamAcl.Create(inName, PipeDirection.Out, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
var outPipe = NamedPipeServerStreamAcl.Create(outName, PipeDirection.In, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);

// P/Invoke advapi32!CreateProcessWithLogonW：
//   userName="TinyHarnessOffline"（fork 命名空间）, domain=".", password=Step3,
//   cmdline = "\"…\codex-command-runner.exe\" --pipe-in=<runnerInName> --pipe-out=<runnerOutName>",
//   creationFlags = CREATE_NO_WINDOW(0x08000000) | CREATE_UNICODE_ENVIRONMENT(0x400)
//   environment = NULL（继承父 env）
// 随后 inPipe/outPipe 各 WaitForConnection()，15 秒超时
```

**TinyHarness 的实现要求**：连接后校验 `GetNamedPipeClientProcessId == runner PID`，防管道抢连（库内 `runner_pipe.rs:54-133` 同款）。使用完整管道 DACL 限定宿主和所选沙箱账户访问，并检查创建/连接错误。上例继承父 env、同步连接及局部安全描述符都不是生产代码的完整替代。

### Step 6 · 帧协议

见下一节规范。C# 侧读写帧：

```csharp
// using System.Buffers.Binary;
// using System.Text.Json.Nodes;
const int MaxFrameLength = 8 * 1024 * 1024;

static async Task WriteFrameAsync(Stream s, JsonObject msg, CancellationToken ct) {
    msg["version"] = 6;
    byte[] body = Encoding.UTF8.GetBytes(msg.ToJsonString());
    if (body.Length > MaxFrameLength)
        throw new InvalidDataException("Frame exceeds 8 MiB");
    var lenBuf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)body.Length);
    await s.WriteAsync(lenBuf, ct);
    await s.WriteAsync(body, ct);
    await s.FlushAsync(ct);
}
static async Task<JsonObject?> ReadFrameAsync(Stream s, CancellationToken ct) {
    var lenBuf = new byte[4];
    int first = await s.ReadAsync(lenBuf.AsMemory(0, 1), ct);
    if (first == 0) return null;
    await s.ReadExactlyAsync(lenBuf.AsMemory(1), ct);
    uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
    if (len > MaxFrameLength)
        throw new InvalidDataException("Frame exceeds 8 MiB");
    var body = new byte[(int)len];
    await s.ReadExactlyAsync(body, ct);
    var msg = JsonNode.Parse(body)?.AsObject()
        ?? throw new InvalidDataException("Expected JSON object");
    if (msg["version"]?.GetValue<int>() != 6)
        throw new InvalidDataException("Unexpected protocol version");
    return msg;
}
```

---

## 6. 命令 Runner 管道协议规范（v6）

### 6.1 帧与消息外层

**帧**：4 字节小端 u32 长度 + UTF-8 JSON，长度按字节计算，JSON 单帧上限 8 MiB（`framed_io.rs:19-46`）。
**外层**：所有消息均为 `{"version":6,"type":"消息类型","payload":{消息字段}}`；只有 `Message` 被 flatten，消息自己的 `payload` **不被 flatten**（`ipc_framed.rs:25-48`）。

| type | 方向 | `payload` 内字段 |
|---|---|---|
| `spawn_request` | 父→runner | `command: string[]`，`cwd`，`env: {string:string}`，`permission_profile`（§6.2），`workspace_roots: string[]`，`codex_home`（传 `<codex_home>\.sandbox`，`elevated_impl.rs:209`），`real_codex_home`，`cap_sids: string[]`（非空），`network_proxy_restricting_sid`（可空），`timeout_ms`（可空），`tty: bool`，`stdin_open: bool`，`private_desktop_name`（实际启动必需，见 Step 4a） |
| `spawn_ready` | runner→父 | `process_id: number` |
| `output` | runner→父 | `data_b64: string`，`stream: "stdout"\|"stderr"` |
| `stdin` | 父→runner | `data_b64: string` |
| `close_stdin` | 父→runner | `{}` |
| `resize` | 父→runner | `rows: u16`，`cols: u16`（tty 时） |
| `terminate` | 父→runner | `{}` |
| `exit` | runner→父 | `exit_code: i32`，`timed_out: boolean` |
| `error` | runner→父 | `message`，`stage: "read_spawn_request"\|"spawn_child"\|"write_spawn_ready"`，`windows_error_code`（可空） |

空控制消息也保留 payload，例如：

```json
{"version":6,"type":"terminate","payload":{}}
```

时序（单连接、每命令一进程）：

```
父 → spawn_request
父 ← spawn_ready
父 ← output × N        （stdout/stderr 交错流式）
父 → stdin/close_stdin/resize/terminate   （会话期间随时）
父 ← exit              （payload 内含 exit_code、timed_out）
```

连接与启动握手有独立于命令的超时；库连接等待为 15s。启动失败可能收到 `error` 或管道断开而无 `exit`；不得把这种情况当成命令正常结束。所有发送端应串行化整帧写入，避免 stdin、resize、terminate 的长度前缀与消息体互相交错。

### 6.2 `permission_profile` 精确结构

以下只读 profile 已使用真实 Rust serde API 验证，与 `PermissionProfile::read_only()` 完全相同：

```json
{
  "type": "managed",
  "file_system": {
    "type": "restricted",
    "entries": [
      {
        "path": {"type": "special", "value": {"kind": "root"}},
        "access": "read"
      }
    ]
  },
  "network": "restricted"
}
```

- `network` 是字符串枚举：`"restricted"` 或 `"enabled"`，不是对象。
- `file_system.type` 及 restricted 结构的 `entries` 必需；`entries` 不会因缺失而自动变成空数组。
- `root/read` 是策略层面的根读取权限，不是“仅允许读取仓库”。标准 elevated setup 要求有效 root read；实际可访问性还由 Windows token/ACL 决定。
- 在 entries 中添加 `{"path":{"type":"special","value":{"kind":"project_roots"}},"access":"write"}`，就得到 §6.3 使用的最小工作区写入 profile；该结构也通过了 serde 往返验证。
- `project_roots` 对请求提供的**每一个** `workspace_roots` 展开。`workspace_roots` 本身不授予写入；空数组不会在这个 Runner 路径自动回退为 `cwd`。
- 显式路径可写作 `{"type":"path","path":"C:\\work\\repo"}`，放在 entry 的 `path` 字段中。这一 serde 接口使用原生 Windows 路径字符串，**不是 file URI**；显式路径不依赖 `workspace_roots`。
- `PermissionProfile::default()` 或兼容格式 `{}` 是空受限权限，不等于 `read_only()`，也不能替代标准 elevated setup 所需的 root read。

**最小示例不等于 Codex 内置 `workspace_write()`。** 内置构造器还添加以下 entries：

| 特殊路径 | access | 附加字段 |
|---|---|---|
| `slash_tmp` | `write` | 无 |
| `tmpdir` | `write` | 无 |
| `project_roots` 的 `.git`、`.agents`、`.codex`、`.aws` subpath（各一条） | `read` | `missing_path_behavior: "skip"` |

元数据条目的精确形状例如：

```json
{
  "path": {"type":"special","value":{"kind":"project_roots","subpath":".git"}},
  "access": "read",
  "missing_path_behavior": "skip"
}
```

`workspace_write_with(..., true, true)` 仅移除两类临时目录写入条目，仍保留元数据只读条目。若要精确复用默认策略，应直接序列化内置构造器；不要把两条目示例当作完整默认策略。

源码：`protocol/src/models.rs:295-375,417-435,482-553,714-800`，`protocol/src/permissions.rs:81-204,464-565,817-874,2483-2517`；workspace 展开见 `windows-sandbox-rs/src/resolved_permissions.rs:82-93`。

### 6.3 完整 `spawn_request` 协议模板

以下 JSON 已通过实际 `FramedMessage` 反序列化、序列化和长度前缀读写往返验证。它授予策略层面的根读取与指定工作区写入，**不包含临时目录写入或元数据保护条目**。

```json
{
  "version": 6,
  "type": "spawn_request",
  "payload": {
    "command": ["cmd.exe", "/c", "whoami"],
    "cwd": "C:\\work\\repo",
    "env": {},
    "permission_profile": {
      "type": "managed",
      "file_system": {
        "type": "restricted",
        "entries": [
          {
            "path": {"type": "special", "value": {"kind": "root"}},
            "access": "read"
          },
          {
            "path": {"type": "special", "value": {"kind": "project_roots"}},
            "access": "write"
          }
        ]
      },
      "network": "restricted"
    },
    "workspace_roots": ["C:\\work\\repo"],
    "codex_home": "C:\\harness\\tinyharness-sandbox-home\\.sandbox",
    "real_codex_home": "C:\\harness\\tinyharness-sandbox-home",
    "cap_sids": ["S-1-5-21-100-200-300-400"],
    "network_proxy_restricting_sid": null,
    "timeout_ms": 30000,
    "tty": false,
    "stdin_open": false,
    "private_desktop_name": "TinyHarnessDesktop-0123456789abcdef0123456789abcdef"
  }
}
```

**示例 SID 与桌面名是占位值，不是已 provision 的资源。** 实际发送前必须使用刷新 ACL 所对应的 capability SIDs，使用实际创建并授权的桌面，以及与网络策略匹配的沙箱账户。完整 env 的处理也应与库保持一致，示例 `{}` 不承诺能运行所有工具链。

### 6.4 超时、主动终止与退出码

`win.rs:657-730` 的 TTY / 非 TTY 路径共用以下逻辑：首次 `WaitForSingleObject` 返回 `WAIT_TIMEOUT` 才设置 `timed_out=true`；超时后请求终止 job，必要时回退到终止根进程，再等待根进程最多 5 秒。**超时分支直接选定 `128 + 64 = 192`，不会再查询子进程退出码**；该值既用于 `ExitPayload`，也用于 Runner 自身退出。

| 情况 | `exit.payload.exit_code` | `timed_out` |
|---|---|---|
| 正常结束 | `GetExitCodeProcess` 返回的 u32 转 i32 | `false` |
| Runner 首次等待超时 | **192** | **`true`** |
| 提前收到 `terminate`，终止生效且等待未超时 | 通常 **1** | `false` |
| 子进程自行以 192 退出 | **192** | `false` |
| 早期协议或启动错误，未收到 `exit` | 无命令结果；按协议/执行失败处理 | 不得推断 |

超时帧的精确形状（已做 serde 往返验证，但不是运行时超时实测）：

```json
{"version":6,"type":"exit","payload":{"exit_code":192,"timed_out":true}}
```

.NET 侧必须保留 `exit_code` 和 `timed_out` 两个字段，并区分“命令结果”和“没有 exit 帧的协议失败”：

- **判断 Runner 超时只看 `payload.timed_out`**，不能用 `exit_code == 192` 替代。
- Job/root 终止使用的 Windows 退出码是 **1**；它不会覆盖 Runner 超时分支选定的 **192**。主动 `terminate` 不单独设置超时标志；如果终止未在等待期限内完成，仍可能返回 `192,true`。
- **`192,true` 只证明 Runner 已判定超时，不证明进程树已经全部成功终止。** 根进程终止后 5 秒仍未退出时会记录诊断日志。输出线程 join、ConPTY 清理及写出 exit 帧也可能耗时，因此收到结果的总时间不保证小于 `timeout_ms + 5s`；父进程仍需独立的通信和故障回收期限。
- 非超时分支将查询缓冲区初始化为 **1**，且未检查 `GetExitCodeProcess` 的返回值；API 失败且未改写缓冲区时可得到 1。这是查询兜底，不是超时码。
- Runner 早期错误通常按 Rust `Result` 失败路径以 1 退出，但不会因此合成命令 `ExitPayload`。库 capture 路径对缺失 exit 报错，unified exec 对缺失/读取失败使用 -1；.NET 不应拿 Runner OS 状态补成业务退出码。
- Codex 更上层 capture 执行路径将超时规范化为 **124**（`core/src/exec.rs:805-825`）。直接消费本节 Runner 协议得到的是 **192**；不要混淆层级。
- `timeout_ms` 在 Runner 中由 u64 直接转 u32；null 使用 Windows `INFINITE`。发送端应将有限超时约束为 `0..4294967294` 毫秒，避免截断以及 `4294967295` 被视为无限等待。

终止辅助路径：`win.rs:420-438,533-535`；job 终止码：`utils/pty/src/win/job.rs:235-252`；父进程结果处理：`elevated_impl.rs:252-299`、`unified_exec/backends/windows_common.rs:125-142`。

### 6.5 获取真实模板：用 serde，不是抄 `.sandbox` 日志

**当前代码不把完整 spawn_request 写入 `.sandbox` 日志。** 发送端只序列化并写入管道；日志记录 START / SUCCESS / FAILURE 与诊断信息，命令预览限制为 200 字节。`SBX_DEBUG=1` 也不是完整请求转储开关。

因此无需先运行 Codex 沙箱来“抄日志帧”。可靠方式是构造真实 Rust 类型，再用 `serde_json::to_string_pretty` 序列化；`codex-windows-sandbox` 公共导出 `FramedMessage`、`SpawnRequest`、`Message`，权限构造器由 `codex_protocol::models::PermissionProfile` 提供。也可直接参考 §6.2–6.3 的已验证 wire 模板。

依据：`elevated/runner_client.rs:179-188`、`framed_io.rs:19-28`、`logging.rs:15-40,119-145`。现有 spawn serde 测试：`elevated/ipc_framed.rs:185-233`。

---

## 7. 验证与实施门槛

### 7.1 默认离线验证

默认测试不运行 provisioning、不修改账户/ACL/防火墙。使用 fake backend、模拟管道与预设模型测试：

- prepared 策略及环境不可变；fingerprint/session grant 隔离，无权限扩大和无 fallback。
- 帧大小、版本、截断、断管道、错误状态转换、缺失/重复终态；没有 exit 不补造命令退出码。
- 跨帧 UTF-8 解码与脱敏、stdout/stderr、大输出与有界内存；输出或 artifact 不完整必须明确报告。
- refresh、连接、启动、执行、排空与回收分别有界；取消使用独立清理期限，不复用已取消 token 跳过清理。
- 命令失败可以作为工具结果返回；回收失败或进程树状态未知必须记录审计并终止后续副作用。用户取消仍终止任务。
- direct 参数及显式 shell 的空参数、引号、尾反斜杠、管道和组合语法；现有 cmd 的 raw `/d /s /c` 行为不能未经验证直接改成普通 argv 拼接。

涉及配置、DTO、P/Invoke 和可执行入口后，执行 `win-x64` NativeAOT publish，要求无未解释的 trimming/AOT warning；从仓库外运行发布产物的 CLI smoke 和 MCP 只读边界回归。普通 smoke 不隐式初始化机器。

### 7.2 Opt-in Windows 隔离验收

系统验收必须另行获授权，在可丢弃、受控 Windows 环境执行，并记录实际组件版本及环境。把 deny-probe 当作一条普通沙箱命令走完整链路执行：

1. 准备 4 个文件/DLL（allowed 文本内容必须是 `ALLOW-CONTROL`）；
2. 设 4 个环境变量 `CODEX_WINDOWS_ALLOWED_TEXT/DENIED_TEXT/ALLOWED_MODULE/DENIED_MODULE`（放进 `spawn_request.payload.env`）；
3. 退出码 **0** = deny-read ACL 真实生效；**20** = 有 deny 路径意外可访问（沙箱失效）；**21** = 探针自身配置错误。

另可做冒烟对照：沙箱内 `whoami` 应显示所选 offline 账户；在有效允许写入根（包括临时根）之外选择受控路径做写入拒绝对照。只读模式应拒绝工作区写入；内置 workspace-write 模式还应验证现有元数据目录受到只读保护。

超时/退出对照至少包括：自然退出 23、自然退出 192（必须 `timed_out:false`）、有限等待超时（预期 `192,true`）、等待期限前主动 terminate、启动阶段取消、启动失败或断管道且没有 exit。另验断网、受控 deny-read、元数据保护、大输出及宿主异常退出。进程树存活状态必须独立确认，不能仅凭 `192,true` 判定回收成功；首期验收非 TTY，TTY 将来另行支持和验证。

当前 Codex Job 有允许 breakaway、正常退出保留后代的路径（`utils/pty/src/win/job.rs:44-59,221-253`）。TinyHarness 首期要求命令结束后不遗留后台后代；现有 runner 是否能可靠满足，以及异常退出时是否可完整回收，属于真实集成的阻断性验收项。不能以回收 runner 或根进程代替证明。若既有 EXE 无法满足，应报告并重新评估方案，不在本轮计划中预先授权修改 Rust 模块。

> 本次补充验证仅覆盖序列化与源码语义，未执行本节测试。真实 Windows 集成测试会 provision 账户并修改 ACL/防火墙；应在受控测试环境执行，而不是把它当作无副作用的协议探针。

---

## 8. 卸载与清理

**exe 没有 uninstall 入口**（`SetupMode` 仅 4 种，无卸载模式）。TinyHarness 首期不暴露一键全局卸载，也不在执行失败时调用 cleanup。

`clean_up_legacy_windows_sandbox()` 是机器级 legacy cleanup，可能处理共享沙箱账户、相关进程、网络规则和注册表，影响其他客户端；源码明确保留部分 home 文件、缓存和 ACL（`uninstall_windows.rs:21-40`）。**它不是按 TinyHarness 实例完整回滚，也不保证撤销所有文件系统变化。**

需要清理时，先检查实际账户、规则、进程、目录归属及共存客户端，对精确目标和系统副作用单独取得授权；不能按名称包含 `Codex` 就批量删除。不提供未经验证的“10 行完整卸载”或目录通配符删除步骤。系统测试优先通过销毁可丢弃测试环境恢复状态。

---

## 9. 已知坑位与风险

| # | 坑 | 说明 |
|---|---|---|
| 1 | 版本钉死 | `SETUP_VERSION=5`、`IPC_PROTOCOL_VERSION=6` 均硬校验；升级 codex 源码后 harness 必须同步改 |
| 2 | payload 超 argv 限制 | base64 后 >约 24000 UTF-16 单元须切 `--launch-payload-env` 分块通道（16KB/块，16MB 上限） |
| 3 | 管道方向 | `-in` 父写（`PipeDirection.Out`），`-out` 父读（`PipeDirection.In`），别接反 |
| 4 | NUL 设备 ACE | 库父进程及当前 Runner 都调用 `allow_null_device`；对齐完整编排并验证 NUL 访问，不要忽略这些 ACL 动作 |
| 5 | 账户修复不是普通执行 | Rust 库遇登录失败可能刷新密码重试（`runner_client.rs:118-177`）；C# 主方案不自动复刻该自愈，改为错误报告及显式管理操作 |
| 6 | 每命令开销 | 一条命令 = 一次 refresh（setup.exe 进程）+ 一个 runner 进程；批量短命令注意复用与节流 |
| 7 | 机器级共享状态 | fork 已与 Codex 分栈命名（账户/组/防火墙/WFP 互不共享），但共存尚未验收；同一 fork 命名空间内不同 home 仍共享账户密码与网络规则 |
| 8 | offline 断网靠 WFP+防火墙 | 若你后续自定义防火墙规则，注意别把 offline 账户的阻断规则冲掉 |
| 9 | 私有桌面为必需资源 | CLI / TTY / GUI 都要实际创建、授权并保持父进程拥有的私有桌面；不同账户或有效策略不可共享桌面 |
| 10 | runner OS 退出码不能替代命令帧 | 早期错误可能以 1 退出而没有 exit；超时帧为 192,true。保留两个 payload 字段，区分协议失败、命令失败及超时 |
| 11 | 合法 JSON 不等于安全执行 | Runner 不代替父进程完成 setup ACL、网络账户及 capability 编排；这些必须与 profile 一致 |
| 12 | 超时不保证成功回收 | 192,true 是超时决定，不是杀树成功证明；超时总耗时也不保证限于 timeout_ms + 5 秒 |

---

## 10. 实施路线与后续平台

详细里程碑见 [PLAN.md §22](../PLAN.md#22-m11windows-工具进程沙箱)，本节只记录技术落点：

1. 提取现有宿主进程 backend，建立 prepared execution、输出和终态契约，保留现有 shell 行为与默认测试。
2. 实现 C# Windows 编排及 fake 管道测试；直接使用已编译 setup/runner，不调用 Codex CLI，不新增 Rust shim。
3. 贯通可信配置、固定断网策略、fingerprint/session grant、审批提示和结果审计；无 fallback。
4. 增加显式状态检查/provisioning 与组件版本/打包约定，验证 NativeAOT 和原生产物。
5. 在获授权的可丢弃 Windows 环境完成隔离及生命周期验收，再声明 Windows sandbox 支持。

Linux/macOS 后续各自实现进程执行后端，复用有效策略语义、授权和结果契约，不复用 Windows SID/桌面/管道模型。不支持的隔离能力必须拒绝，不能静默忽略；每个平台完成其 NativeAOT RID 和真实隔离验收后才能列为支持。

### 历史备选：Rust shim（不纳入首期）

Rust shim 是将来维护成本发生变化时才重新评估的替代方案，不是当前的依赖或交付项。现有公共 capture/unified exec 路径还存在自动 setup/UAC、缓冲输出或丢失超时元数据等边界，不能简单包装后声称达到本方案的执行契约；这些限制不构成 C# 直接消费 runner 协议的前置条件。任何改走库封装、修改 Rust 模块或增加全局 cleanup 入口，都需要另行确定范围。

---

## 附录 A · 源码索引（file:line 速查）

| 主题 | 位置（相对 `codex-rs/`） |
|---|---|
| setup 参数解析 / payload 结构 | `windows-sandbox-rs/src/setup_provisioning.rs:514-656`（payload 字段 `:110-147`） |
| setup 提权 / refresh 不提权 | `windows-sandbox-rs/src/setup.rs:942-1079` / `:391` |
| 大 payload env 分块 | `windows-sandbox-rs/src/launch_environment.rs:15-37`、`environment_transport.rs:11-16` |
| 账户/组创建 | `windows-sandbox-rs/src/setup_provisioning/sandbox_users.rs:66-180` |
| DPAPI（LocalMachine） | `windows-sandbox-rs/src/dpapi.rs:34,68` |
| cap SID 文件与 get-or-create | `windows-sandbox-rs/src/cap.rs:35-114` |
| cap_sids 选择规则 / NUL ACE | `windows-sandbox-rs/src/elevated_impl.rs:157-182`、`bin/command_runner/win.rs:301-307` |
| 帧协议定义 / spawn payload | `windows-sandbox-rs/src/elevated/ipc_framed.rs:25-70`、`framed_io.rs:19-46` |
| profile 展开 / Windows 可执行约束 | `windows-sandbox-rs/src/resolved_permissions.rs:39-117` |
| 私有桌面创建 / ACL / 策略隔离 | `windows-sandbox-rs/src/desktop.rs:69-145,225-348`、`elevated/runner_client.rs:363-369` |
| Runner 超时 / ExitPayload / OS 退出 | `windows-sandbox-rs/src/bin/command_runner/win.rs:657-730` |
| 终止辅助 / job 终止码 | `windows-sandbox-rs/src/bin/command_runner/win.rs:420-438,533-535`、`utils/pty/src/win/job.rs:235-252` |
| capture 超时规范化 124 | `core/src/exec.rs:805-825` |
| 日志内容 / 非完整 spawn 转储 | `windows-sandbox-rs/src/logging.rs:15-40,119-145`、`elevated/runner_client.rs:179-188` |
| 管道创建 / DACL / PID 校验 | `windows-sandbox-rs/src/elevated/runner_pipe.rs:46-133` |
| runner 端实现 | `windows-sandbox-rs/src/bin/command_runner/win.rs:547-731` |
| CreateProcessWithLogonW 启动 runner | `windows-sandbox-rs/src/elevated/runner_client.rs:346-480` |
| deny-probe | `windows-sandbox-rs/src/bin/managed_deny_probe/win.rs:37-100` |
| 服务 IPC / 鉴权 | `windows-sandbox-service/src/ipc*.rs`、`provisioning_protocol.rs:14-76` |
| 卸载 | `windows-sandbox-rs/src/uninstall_windows.rs:22-203` |
| PermissionProfile serde / 构造器 | `protocol/src/models.rs:295-375,417-435,482-553,714-800` |
| 权限路径 wire / workspace 内置条目 | `protocol/src/permissions.rs:464-565,817-874,2483-2517` |
| spawn serde 往返测试 | `windows-sandbox-rs/src/elevated/ipc_framed.rs:185-233` |
| workspace 展开 / 后端约束测试 | `windows-sandbox-rs/src/resolved_permissions.rs:351-682` |
| 集成测试（需受控环境，含系统副作用） | `core/tests/windows_sandbox.rs` |
