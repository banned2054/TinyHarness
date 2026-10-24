# Windows 沙箱接入说明

> 已完成实现的文档归档（2026-10-24）。保留设计、使用边界和相关证据供维护时查阅；归档不撤销仍有效的协议、安全、权限和 NativeAOT 约束，也不代表对后续修改的授权。当前开发范围以[活动计划](../../PLAN.md)和实际源码为准。

文档整理：2026-10-24。本文说明现行接入职责和查阅入口；早期可行性研究、协议模板、C# 片段与源码行号完整保留在[历史接入方案](windows-sandbox-dotnet-integration.md)。

## 组件与职责

TinyHarness 的 C# Runtime 直接编排 TinyHarness 独立命名空间的 Codex fork 组件，不通过 Codex CLI、Agent、Node Host 或新增 Rust shim 执行命令。

- `codex-windows-sandbox-setup.exe`：显式 provisioning，以及授权执行后的普通 ACL refresh。
- `codex-command-runner.exe`：以匹配沙箱账户启动，通过双管道接收请求并运行目标命令；每命令一个 runner。
- `codex-windows-managed-deny-probe.exe`：真实隔离验收使用的探针。
- `codex-windows-sandbox-service.exe`：不在本项目运行链路中，不安装服务。

ShellTool 的不可变准备计划、PermissionEngine 审批与进程后端仍是同一执行链路。沙箱只覆盖经它启动的工具进程，不隔离整个 Harness、模型 HTTP 请求或内置文件工具。

## 必须保留的执行边界

- 组件绝对路径、home、模式与额外写根来自可信用户／宿主设置，项目配置和模型参数不能扩大隔离权限。准备无副作用，执行与审批绑定同一策略身份和有效权限。
- 普通执行不自动 provisioning、UAC、修复密码或修改机器网络设置；显式选择沙箱后，组件、身份、版本或权限检查失败须拒绝，不能回退宿主执行。
- ACL、capability SID、网络账户、清理后的目标环境、私有桌面和管道身份必须匹配；凭据不进入模型、审计或异常。组件、会话和 artifact 等受保护位置不能暴露给目标命令写入。
- 管道帧大小、版本和顺序受校验；缺少命令 `exit` 帧时不能用 runner 的 OS 退出码补造命令结果。`exit_code == 192` 不等于超时，须同时检查 `timed_out`；超时也不证明进程树已回收。
- refresh、连接、启动、执行、排空和清理分别有界；取消仍须独立清理。进程树状态未知或清理失败必须阻断后续副作用，保留检查要求。
- 标准策略的根读取和临时写根必须如实说明，不能宣传为“只允许读取仓库”。只读、workspace-write、元数据保护与网络模式分别核对。
- JSON 使用 source generation，P/Invoke 和入口继续满足 NativeAOT；默认测试使用替身，不隐式触发真实模型或机器初始化。

## 构建、配置与协议参考

| 需要查阅的内容 | 当前入口 |
| --- | --- |
| 用户组件路径、home、严格／TCP 环回模式及版本匹配 | [沙箱管理](windows-sandbox-management.md) |
| 分发组件的固定源码、构建命令和 ZIP 清单 | [打包与发布](../Guides/releasing.md#构建来源与验证) |
| 早期 setup 参数、Runner v6 帧及权限 JSON 模板 | [历史协议参考](windows-sandbox-dotnet-integration.md#6-命令-runner-管道协议规范v6)；仅适用于记录中的版本基线 |
| 早期本机构建命令、产物与源码索引 | [历史构建记录](windows-sandbox-dotnet-integration.md#构建产物已在本机构建验证) |
| M11、目录授权、TCP 环回与新网络探针的实际结果 | [验收归档](windows-sandbox-verification.md) |

使用协议示例前必须核对实际 fork 与 C# 实现，不能把占位 SID、桌面名、局部编排片段当作完整生产链路。当前命令、网络模式及版本以沙箱管理说明和实际源码为准；早期的“尚未接入”不作为当前结论。

## 清理与验证

不提供自动全局卸载或失败回滚。不同 home 仍可能共享同一模式的机器账户和网络规则，重复 provisioning 会影响其他 home；独立命名也不等于与 Codex 共存已全面验收。清理须先核对精确归属和副作用，再取得对应授权。

真实 provisioning、隔离和网络验收须显式 opt-in，按[当前验收工具范围](windows-sandbox-management.md#网络验收工具与验证边界)记录环境、正对照、实际目标和未验证项。离线测试、NativeAOT smoke、指定实机样本与完整安全保证分别理解。
