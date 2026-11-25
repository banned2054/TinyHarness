# Windows 沙箱可用性收口计划

更新日期：2026-11-25。目标是将现有基于 Codex fork 的 Windows 沙箱在 CLI 与 Desktop 上收敛到**稳定、可预期、日常可用**的状态。

> 历史顶层读根实施记录（WP3b／WP5／WP6）与已退出当前范围的"按会话隔离 OS 读权限"实验记录，已归档至 [Windows 沙箱读取策略与会话隔离（已归档）](Archive/desktop-sandbox-scan-and-no-project-isolation-plan.md)。

## 1. 权限边界（与 Codex 定位对齐）

- **OS 沙箱层（Shell 进程）**：
  - **写隔离（按工作区／会话独立）**：通过 `WRITE_RESTRICTED` 与写根 `cap_sid` 限制仅可写入当前工作区与 temp 目录。
  - **网络隔离（按会话／命令独立）**：沿用现有离线、在线与代理策略。
  - **读底线（全机统一）**：用户 profile 顶层普通子项可读，固定敏感目录（`.ssh`、`.aws`、`.gnupg`、`.kube`、`.docker`、`.codex` 等）及配置派生的精确 C1 路径由 OS 拒读。**不再在当前沙箱底座上追求多会话并发不同的 OS 读黑名单。**
  - **L2 临时授权读（C2）**：沿用现有 `TinyHarnessAuthUsers` 与全局授权窗口锁（全机串行，用完即撤）。
- **应用层（内置文件工具）**：
  - `read_file`、`list_dir`、`search`、`patch` 等内置工具继续在 .NET 进程内按会话独立执行路径分类与审批策略。

## 2. 实施顺序

1. **Rust fork 清理实验残局并收口旧路径撤销修复**
   - 核对并还原 `C:\Code\Rust\codex` 中上一轮中断的实验改动，只保留已验证的两份补丁：
     - `revoke_read_deny_aces` 对已删除目标（`ERROR_FILE_NOT_FOUND (2)`／`ERROR_PATH_NOT_FOUND (3)`）视为清理完成，其余错误（如访问拒绝）仍 fail-closed 并保留簿记；
     - `setup_error.json` 对非结构化错误写出完整错误链（`{err:#}`），保留阶段、路径与 OS 错误码。
   - 重新构建干净的 `codex-windows-sandbox-setup.exe` 与 `codex-command-runner.exe`，验证删除旧路径不再阻塞首次 Shell 启动。

2. **CLI 统一切换到顶层读根与固定拒读**
   - 将 CLI 的基线读保护入口统一到与 Desktop 相同的顶层读根规划（`PlanBaselineReadProtectionTopLevel`），替代旧的全盘递归扫描。
   - 消除 CLI 全盘扫描超时风险，同时让 Desktop 与 CLI 下发的拒读口径完全一致，减少跨端簿记互相覆盖。

3. **CLI 工作区 ACL 按需延迟准备**
   - 按 [CLI 工作区 ACL 按需准备](cli-workspace-acl-preparation-plan.md) 实施：打开新目录 `chat`、纯聊天与使用只读工具时不检查或写入目录 ACL、不触发 UAC；仅在首次需要 `workspace-write` 沙箱 Shell 时按需准备（普通用户目录非提权直接通过，异常目录提供显式修复入口）。

4. **真实 Windows 端到端验收与收尾**
   - 在离线回归与 CLI／Desktop NativeAOT 发布、产物 smoke 通过后，按授权进行真实 Windows 验收，补齐原轮未验证的生产闭环。

## 3. 验收条件

| 场景 | 通过条件 |
| --- | --- |
| 旧路径删除与恢复 | 簿记含已删除历史目录时，helper 成功清理过期条目且不阻塞首次 Shell；现存不可访问旧目标仍 fail-closed，且 `setup_error.json` 含阶段、路径与 OS 错误码。 |
| CLI 与 Desktop 首次 Shell | 新目录／新会话先普通聊天（零沙箱初始化、零 UAC），再执行首次 L1 Shell：零 UAC、零人工审批、零 AI Review 读取项目外普通文件成功。 |
| OS 读保护与写隔离 | 真实 runner 确认普通文件可读、固定敏感目录 OS 拒读、工作区外写入被拒；CLI 与 Desktop 交替刷新不互相撤销固定拒读。 |
| CLI 工作区 ACL 体验 | 新目录打开 `chat` 与只读工具零 ACL 提示；普通可写目录首次可写 Shell 非提权直接准备成功；受阻目录失败不启动命令并给出显式修复路径。 |
| 回归与产物 | 受影响的 Rust 与 .NET 定向测试通过；CLI 与 Desktop `.NET 11` NativeAOT 发布零 trim/AOT 警告；产物 smoke 通过；持久化 PATH 与真实配置未被污染。 |

## 4. 明确暂缓与后续事项

- **每会话 OS 级并发不同读策略**：已退出当前范围（见归档文档 2026-11-25 收尾记录）。
- **无项目会话独立工具目录**：在当前定位下（写目录 + 内置文件工具隔离，OS 读沿用全机统一底线），列为本轮收口完成后的后续事项。
- **新平台沙箱后端（如 Windows 11 24H2+ MXC）**：待本轮现有沙箱达到稳定可用后再另行评估，不阻塞本轮收口。
