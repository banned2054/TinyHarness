# TinyHarness

## M6 验证

M6 的上下文压缩验证包括结构化状态保留规则回归测试，以及 Windows `win-x64` NativeAOT
原生产物 smoke。可复核命令和结果见 [M6 NativeAOT 验证记录](docs/m6-nativeaot-smoke.md)。

状态规则：`Goal` 非空时不可清空；文件、命令和已有决策不可删除；约束可更新；待办必须原样
保留，或用 `completed: <item>` 明确关闭。
