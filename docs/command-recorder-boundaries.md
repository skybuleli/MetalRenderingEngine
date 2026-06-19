# ICommandRecorder 当前边界

本文档记录 `MetalRenderingEngine.Core/Rendering/ICommandRecorder` 与 `MetalCommandRecorder` 的当前真实执行边界。

## 目标

- 提供统一的渲染命令入口
- 保留 `MetalCommandList` 的批量回放收益
- 明确哪些命令已经走批量路径，哪些仍是直通 encoder 的过渡实现

## 当前批量回放命令

这些命令会先录入 `MetalCommandList`，并在 `EndRenderPass()` 时统一回放：

- `SetPipelineState`
- `SetViewport`
- `SetCullMode`
- `SetFrontFacing`
- `SetDepthBias`
- `SetDepthClipMode`
- `SetTriangleFillMode`
- `SetDepthStencilState`
- `SetStencilReference`
- `SetVertexBytes<T>`
- `SetFragmentBytes<T>`
- `UseResource`
- `Draw`
- `DrawIndexed`
- `DrawIndirect`
- `DrawIndexedIndirect`
- `WaitForFence`
- `UpdateFence`

## 当前直通 encoder 的命令

这些命令仍直接调用 `MetalRenderEncoder`：

- `SetScissor`
- `SetVertexBuffer`
- `SetFragmentBuffer`
- `SetFragmentTexture`

## 为什么允许这部分直通

- 这些命令目前调用频率较低，不是批量回放收益的主要来源
- 现有测试已经覆盖混合路径，不必为了“纯粹”而先重写整条链
- 这是一种显式折中，不是隐式漂移

## 维护约定

- 新增命令时，优先判断它是否属于高频状态绑定或 draw/dispatch 热路径
- 高频命令优先进入 `MetalCommandList`
- 低频命令可以暂时直通，但必须同步更新本文档和代码注释
- Demo 或测试如果特意绕过 `ICommandRecorder`，需要在代码里说明原因
