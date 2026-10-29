TinyHarness.NET Desktop - prototype preview / 原型预览版

English
-------
This Windows x64 package contains the TinyHarness desktop app (TinyHarness.exe,
NativeAOT), its native DLLs, and three Windows sandbox components built from
https://github.com/banned2054/codex, a fork of https://github.com/openai/codex.
The .NET runtime is not required. Tools you ask the agent to run still need to
be installed. TinyHarness.NET is licensed under Apache-2.0; see LICENSE.
Third-party terms are in NOTICE and THIRD-PARTY-LICENSES.txt. build-info.json
records the edition, version, source commits, toolchains, and packaged hashes.
Verify this ZIP against its entry in SHA256SUMS.txt.

Basic use
1. Extract the entire package to a stable directory outside your target workspace
   and any sandbox write roots. Keep all DLLs, sandbox/, and license files beside
   TinyHarness.exe. Do not copy the executable out on its own.
2. Double-click TinyHarness.exe, or create a shortcut to its full path.
   Keep CLI and Desktop in separate directories: both use TinyHarness.exe.
   Only add the CLI package directory to PATH; do not add the Desktop directory.
3. Open Settings to configure a provider and model, including a key reference and
   context window. Environment-variable key references must be set before launch.
4. Choose a project folder, select a model, and send your request.
   Model requests send conversation/tool content to the provider and may cost money.
   Review permissions and tool approvals before allowing changes or commands.

CLI and Desktop share user configuration and the session store by default.
Configuration: %APPDATA%\tinyharness\user-config.json
Sessions: %LOCALAPPDATA%\tinyharness\sessions
TINYHARNESS_USER_CONFIG_DIR and TINYHARNESS_SESSION_STORE_DIR override those paths;
settings.sessionStoreDirectory can also select a session store.

Optional Windows sandbox
Extraction and app launch do not provision the sandbox. Configure the actual
absolute component paths using ONLY settings.windowsSandbox from the supplied
sandbox-settings.example.json; merge it into your existing settings and retain
provider/key references. C:\Tools\TinyHarness in the example is a placeholder.
Use the separately downloaded CLI package for sandbox status, explicit sandbox
provision, and optional sandbox verify. Provisioning requests UAC and changes
local accounts, ACLs, and Firewall/WFP rules; there is no uninstall/rollback command.
The example blocks external networking and TCP loopback for sandboxed tools.
allowTcpLoopback=true also permits access to host TCP loopback services.
Harness model requests are not routed through the tool-process sandbox.

Limits
This is a prototype preview. Replies may be wrong; sessions are not encrypted and
may contain secrets. Automated tests and --offline-smoke do not verify interactive
GUI behavior, real providers, or real Windows isolation. Session formats and
interaction details may change between versions.
Source and full documentation: https://github.com/banned2054/TinyHarness

简体中文
--------
本包适用于 Windows x64，包含桌面端 TinyHarness.exe（NativeAOT）、必需的原生
DLL，以及来自 banned2054/codex fork 的三个沙箱组件。运行主程序无需安装 .NET
runtime，Agent 使用的开发工具仍需自行安装。项目采用 Apache-2.0，见 LICENSE；
第三方条款见 NOTICE、THIRD-PARTY-LICENSES.txt。build-info.json 记录端类型、
版本、源码提交、工具链和文件哈希；请按 SHA256SUMS.txt 中的对应条目校验 ZIP。

首次使用
1. 完整解压到目标工作区和沙箱写根之外的固定目录，保留所有 DLL、sandbox/ 和
   许可文件，不要单独复制 TinyHarness.exe。
2. 双击 TinyHarness.exe，或创建指向其完整路径的快捷方式。CLI 与 Desktop 的
   程序都叫 TinyHarness.exe，必须分别解压；仅将 CLI 目录加入 PATH。
3. 在设置中配置供应商、模型、密钥引用和上下文窗口。使用环境变量密钥引用时，
   在启动应用前设置对应变量。
4. 选择项目目录与模型后发送请求。模型请求会向供应商发送对话与工具内容，可能
   产生费用；允许修改或执行命令前，请核对权限与工具审批内容。

CLI 与 Desktop 默认共用用户配置和会话存储。配置文件位于
%APPDATA%\tinyharness\user-config.json，会话位于
%LOCALAPPDATA%\tinyharness\sessions。两个路径分别可由
TINYHARNESS_USER_CONFIG_DIR、TINYHARNESS_SESSION_STORE_DIR 覆盖；
settings.sessionStoreDirectory 也可指定会话目录。

可选沙箱
解压和启动应用不会初始化沙箱。只将示例中的 settings.windowsSandbox 合并到
已有 settings，保留供应商与密钥引用；两个组件路径均需替换为实际绝对路径，
示例 C:\Tools\TinyHarness 只是占位值。请使用另行下载的 CLI 包运行 sandbox
status、明确选择执行 sandbox provision，以及按需执行 sandbox verify。
provision 请求 UAC，修改机器账户、ACL、Firewall/WFP，且没有卸载或回滚命令。
示例阻断外部网络和 TCP 环回；allowTcpLoopback=true 也允许访问宿主 TCP 环回
服务。模型请求不受此工具进程沙箱隔离。

本版仍为原型预览，回答可能出错，会话不加密且可能含秘密。自动测试与离线
smoke 不代表真实 GUI、真实供应商或 Windows 隔离验收通过，后续版本可能调整
会话格式和交互。
