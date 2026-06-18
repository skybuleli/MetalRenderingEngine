# 性能基线 (Phase 6)

> 记录 C# → Metal 渲染引擎在各 demo 下的性能指标，作为后续优化的参照基线。

## 测试环境

| 项 | 值 |
|----|-----|
| 设备 | Apple M1 (8GB UMA) |
| macOS | 26.4.1 |
| .NET | 10.0 |
| 日期 | 2026-06-18 |

## P/Invoke 计数（每帧）

各 demo 主循环每帧的 engine P/Invoke 次数（不含 ImGui 外部后端调用）：

| Demo | P/Invoke/帧 | 说明 |
|------|------------|------|
| compute (单次) | 10 | 一次性 dispatch，非每帧 |
| triangle | 8 | 单 render pass，1 draw |
| textured | 13 | 1 draw + 2 fence + 2 bytes + useResource |
| mandelbrot (动画) | ~16 | compute + render + ImGui overlay |
| mandelbrot (静态缓存) | ~10 | 跳过 compute dispatch |
| imgui | 15 | scene + ImGui overlay |

## 批量命令编码器对比（InstancedTrianglesDemo）

1000 个三角形的渲染，两种模式对比：

| 模式 | P/Invoke/帧 | 说明 |
|------|------------|------|
| 逐次 (DIRECT) | ~1003 | viewport + PSO + 1000× draw + cmdbuf |
| 批量 (BATCHED) | ~5 | viewport + 1× ReplayRender + cmdbuf |

**优化效果**：P/Invoke 从 ~1003 降到 ~5（降 99.5%）。

帧时间对比需在 GUI 会话运行 `dotnet run --project src/MetalRenderingEngine.Demo -- instanced`，
通过 ImGui overlay 观察实时帧时间。预期批量模式帧时间低于逐次模式 0.5-2ms（M1 上单次 P/Invoke ~1-2μs）。

## 运行基准

```bash
./build/bench.sh
```

GUI 指标（FPS、帧时间、P/Invoke 计数）通过 ImGui overlay 在交互式运行中显示，
手动记录到本文件。

## 已知瓶颈与后续优化方向

1. **MTLSharedEvent + CPU fence**：替代当前简单 MTLFence，支持跨 command buffer 的精细同步
   （参考 DXMT `dxmt_command_queue.cpp:31-72`）
2. **资源池**：BufferPool / TexturePool / TransientBufferAllocator（每帧重置 ring buffer）
3. **更多 wmtcmd 命令类型**：当前只覆盖 compute/render 常用子集，可扩展 setTexture、
   drawIndexed、fence 等
4. **ring buffer 扩容**：当前 MetalCommandList 不支持运行时扩容（避免指针重定位），
   大规模场景需预估容量或实现带指针重定位的 Grow
