TinyHarness.NET 0.0.1 - initial release / 首次发布

English
-------
This Windows x64 package contains TinyHarness.exe (NativeAOT) and three Windows
sandbox components built from https://github.com/banned2054/codex, a fork of
https://github.com/openai/codex. The .NET runtime and the full Codex CLI are not
required to launch TinyHarness. Tools you ask it to run still need to be installed.
TinyHarness.NET is licensed under Apache-2.0; see LICENSE. Third-party attribution
and terms are in NOTICE and THIRD-PARTY-LICENSES.txt. build-info.json records both
source commits, toolchains, and packaged file hashes. Verify the ZIP with SHA256SUMS.txt.

Basic use
1. Extract the whole directory to a stable location outside your target workspace
   and any sandbox write roots. Keep sandbox/, LICENSE, NOTICE and
   THIRD-PARTY-LICENSES.txt beside TinyHarness.exe.
2. Add the extracted directory to PATH, then open PowerShell in the target project.
   Without PATH, invoke TinyHarness.exe by its full path, keeping the target project
   as the current working directory. The command is tinyharness, without a hyphen.
3. Run: tinyharness init
   Configure your provider endpoint, key reference, model, and context window.
   For an environment-variable key reference, set that variable before running.
4. Run: tinyharness config show
   Run: tinyharness doctor
   Both are offline checks; doctor --connect makes a real model request.
5. Run: tinyharness chat
   Or:  tinyharness run -- "Explain this project"
   Model requests send conversation/tool content to the provider and may cost money.
   A current-directory tinyharness.json takes precedence over user model settings.

Optional Windows sandbox
Unpacking does NOT enable or provision the sandbox. Without it, approved commands
run on the host. To opt in:
1. After init, open %APPDATA%\tinyharness\user-config.json (or the directory selected
   by TINYHARNESS_USER_CONFIG_DIR).
2. Merge ONLY settings.windowsSandbox from sandbox-settings.example.json into your
   existing settings. Do not replace the whole user config or lose provider/key
   references. Change BOTH component paths to the actual absolute extracted paths.
   The example C:\Tools\TinyHarness is a placeholder, not automatic discovery.
3. Run: tinyharness sandbox status
   A not-provisioned report is expected before the next step.
4. Review the reported machine changes, then explicitly run:
   tinyharness sandbox provision
   This requests UAC and changes local accounts, ACLs, and Firewall/WFP rules.
   It has no uninstall/rollback command. Nothing in this package runs it for you.
5. Run: tinyharness sandbox status
   It should report ready before sandboxed tool execution.
   sandbox verify is a separate, real system isolation test; it is not an offline
   self-check and can prepare test-directory ACLs and trigger firewall prompts.

The example blocks external networking and TCP loopback for sandboxed commands.
Tools that need local TCP (including some .NET tests) may require an explicit
allowTcpLoopback=true choice; it allows access to host TCP loopback services too.
Harness model requests are not routed through this tool-process sandbox.

Limits
The terminal UI and runtime controls are unfinished. Replies and summaries may be
wrong. Read tool approvals carefully. Sessions are not encrypted and can contain
secrets. CI build/offline-smoke success is not real-provider or OS isolation proof.
Session formats and commands may change between prototype versions.
History: tinyharness session list / tinyharness session show <id>
Resume:  tinyharness chat --session <id>
Help:    tinyharness help <command>
Source and full documentation: https://github.com/banned2054/TinyHarness

简体中文
--------
本包适用于 Windows x64，包含 NativeAOT 的 TinyHarness.exe，以及来自
banned2054/codex fork 的三个沙箱组件；运行主程序不需要 .NET runtime 或完整
Codex CLI。Agent 调用的开发工具仍需自行安装。项目采用 Apache-2.0，见 LICENSE；
第三方归属和条款见 NOTICE 与 THIRD-PARTY-LICENSES.txt。构建来源与文件哈希在
build-info.json，ZIP 校验文件为 SHA256SUMS.txt。

首次使用
1. 完整解压到目标工作区和沙箱写根之外的固定目录，保留 sandbox/ 及
   LICENSE、NOTICE、THIRD-PARTY-LICENSES.txt。
2. 把解压目录加入 PATH，再进入要处理的项目目录；也可保持项目为当前目录，
   使用 TinyHarness.exe 的绝对路径启动。命令写作 tinyharness，不带连字符。
3. 运行 tinyharness init，配置服务、密钥引用、模型与上下文窗口。选择环境变量
   密钥时，启动前自行设置该变量。
4. 运行 tinyharness config show 和 tinyharness doctor，均为离线检查。
5. 用 tinyharness chat 开始对话，或 tinyharness run -- "解释这个项目"。
   模型请求会向供应商发送对话与工具内容，可能产生费用。当前目录若有
   tinyharness.json，会优先于用户模型配置。

可选沙箱
解压不会自动启用或初始化沙箱；未启用时，获准的命令在宿主执行。
先 init，再打开 %APPDATA%\tinyharness\user-config.json；若设置了
TINYHARNESS_USER_CONFIG_DIR，则使用该目录。仅将示例中的 settings.windowsSandbox
合并到已有 settings，保留 provider 和密钥引用，不要覆盖整份用户配置。
把 setupExecutablePath、runnerExecutablePath 都改成解压后组件的实际绝对路径，
示例 C:\Tools\TinyHarness 只是占位路径，程序不会自动查找组件。

先运行 tinyharness sandbox status；未初始化时报告未就绪是正常的。核对副作用后，
由用户明确执行 tinyharness sandbox provision。它请求 UAC，修改机器账户、ACL、
Firewall/WFP，且没有卸载或回滚命令。随后再用 sandbox status 确认就绪。
sandbox verify 是另行选择的真实隔离验收，不是离线自检，可能准备测试目录 ACL
并触发防火墙提示。工作区信任与目录授权是独立确认流程，工具执行仍需审批。

示例阻断外部网络和 TCP 环回；部分 .NET 测试等工具需要本地 TCP，用户可另行
选择 allowTcpLoopback=true，但这也允许访问宿主 TCP 环回服务。模型请求不受此
工具进程沙箱隔离。终端界面与运行中控制尚未完善，回答和摘要可能出错，会话
文件不加密且可能含秘密；CI 编译和离线 smoke 不证明真实模型或 OS 隔离已通过。
