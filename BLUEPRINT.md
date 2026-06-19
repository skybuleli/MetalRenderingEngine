# BLUEPRINT.md

本文件是项目蓝图入口，供 Agent 和开发者统一读取路径使用。

当前有效蓝图文档：

- `csharp-metal-rendering-blueprint.md`：C# → Metal 原生渲染技术方案蓝图
- `metal-engine-blueprint-phase7-12.md`：Phase 7+ 的阶段扩展说明

阅读顺序建议：

1. 先读 `csharp-metal-rendering-blueprint.md`
2. 再读 `metal-engine-blueprint-phase7-12.md`

说明：

- 之所以保留本入口文件，是为了让 `AGENTS.md`、自动化脚本和未来 Agent 始终有一个稳定入口。
- 如蓝图主文件后续改名，请同步更新本文件，而不是继续让调用方硬编码多个路径。
