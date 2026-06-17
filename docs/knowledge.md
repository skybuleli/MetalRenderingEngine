# 项目知识索引

> 本文件记录项目级关键知识，帮助 AI Agent 快速定位问题与决策。
> 首次读此项目时，建议按本文索引顺序阅读。

---

## 核心文档

- **[csharp-metal-rendering-blueprint.md](csharp-metal-rendering-blueprint.md)** — 技术方案蓝图（必读）
  - §2.1 DXMT handle-based 架构参考
  - §4 bridge.h/m C ABI 设计原则
  - §5 C# SafeHandle 封装模式
  - §8 原 6 阶段路线图

- **[phase-1-6-roadmap.md](phase-1-6-roadmap.md)** — 实施路线图（含 DXMT 参考映射）
  - DXMT 参考映射章节（A–H）：winemetal.h 命名风格、obj_handle_t、Reference<T> RAII、command batch、fence
  - Phase 1-6 每阶段详细任务与验证标准
  - 风险与缓解清单

- **[slang-reflection-binding-design.md](slang-reflection-binding-design.md)** — Slang 反射 JSON + Metal 绑定模型
  - §2 反射 JSON schema（parameters/binding/type）
  - §3 Metal 槽位空间与 MSC 映射规则
  - **§3.5 MSC 4.0 实测发现（关键）**：top-level argument buffer ≠ 直接 setBuffer；buffer 索引偏移 +2；24 字节 UAV 描述符
  - §4 C# 反射数据模型类层次
  - §6 ShaderBindingContext 运行时按名称绑定

- **[phase-1-6-roadmap.md](phase-1-6-roadmap.md)** — 全量路线图（见上方）

---

## 实施中沉淀的关键知识

### MSC 4.0 argument buffer 间接寻址（Phase 1 发现）

| 知识 | 值 |
|------|-----|
| MSC 输出的 metallib 能否直接 `setBuffer(realBuffer, 0, slot)` 绑资源？ | ❌ 不行 |
| 替代方式 | `useResource:` + `setBytes({gpuAddr, len, stride}, index: 2)` |
| UAV 描述符大小（`RWStructuredBuffer<T>`） | 24 字节 = gpuAddress(u64) + length(u64) + stride(u64) |
| buffer 索引 | 实际为 **2**（reflection 的 Slot 不是 Metal 索引；buffer(0/1) 由系统保留） |
| 怎么调试这类问题 | `MTL_SHADER_VALIDATION=1 MTL_DEBUG_LAYER=1` 运行，错误信息直接说期望的索引 |
| DXMT 怎么处理 | `dxmt_context.cpp:105` 把 `buffer_alloc->gpuAddress()` 写到 entries 数组 + `makeResident<>` |

详见 `docs/slang-reflection-binding-design.md` §3.5。

### DXMT 可以直接参考的代码段

| 我们要做的事 | DXMT 文件 | 模式 |
|------------|-----------|------|
| bridge.h 函数命名 | `winemetal.h:84,90,109,204,512,602,679` | `MTLClass_method` |
| C 结构体定义 | `winemetal.h:204-293` | `WMT*Info` C 标准布局 |
| NSError 回传 | `winemetal.h:583` | `obj_handle_t *err_out` |
| Fence 双缓冲 | `winemetal.h:1856` | `updateFence/waitForFence` |
| Command batch（Phase 6） | `winemetal.h:876-1110` + `winemetal_unix.c:785-868` | `wmtcmd_base {type; next} + switch` |

### API 命名约定

- 采用 DXMT `MTLClass_methodName` / `NSClass_methodName` 风格（非蓝图原案的 `metal_xxx_yyy`）
- 句柄类型：`mtl_handle_t`（`typedef uintptr_t`），C# 端 `nuint`
- 所有 `newXxx` 函数返回 retained 句柄（`__bridge_retained`/`CFBridgingRetain`），C# SafeHandle 负责 release

---

## 文档同步指南

以下 AI Agent 内存路径会在实现过程中持续更新，内容与本文件部分重叠：

- `/Users/liliang/.claude/projects/-Users-liliang-MyGameRender/memory/msc-binding-model.md` — MSC 4.0 argument buffer 陷阱
- `/Users/liliang/.claude/projects/-Users-liliang-MyGameRender/memory/slang-reflection-binding-design.md` — 指向 `docs/` 中对应的文档

当有新发现被写入 memory 时，同步更新本文件和对应的 `docs/` 文档。