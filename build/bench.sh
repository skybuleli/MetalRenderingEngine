#!/usr/bin/env bash
# Phase 6: 性能基准脚本。跑 mandelbrot 和 instanced demo 各 8 秒，
# 采集帧时间/P/Invoke 计数到 stdout，供 docs/perf.md 记录基线。
# 用法: ./build/bench.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DURATION=8  # 每个 demo 运行秒数

echo "=== Phase 6 Performance Baseline ==="
echo "Device: $(sysctl -n machdep.cpu.brand_string 2>/dev/null || echo 'unknown')"
echo "Date: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo ".NET: $(dotnet --version)"
echo ""

# 确保产物最新
echo "[1/3] Building bridge + shaders..."
./build/build_bridge.sh > /dev/null 2>&1
./build/compile_shaders.sh > /dev/null 2>&1

echo "[2/3] Building solution..."
dotnet build src/MetalRenderingEngine.Demo/MetalRenderingEngine.Demo.csproj -c Release > /dev/null 2>&1

echo "[3/3] Running benchmarks (each ${DURATION}s)..."
echo ""
echo "--- mandelbrot (compute + render, single draw) ---"
timeout $((DURATION + 4)) dotnet run --project src/MetalRenderingEngine.Demo -c Release --no-build -- mandelbrot 2>&1 | head -5 || true
echo ""
echo "--- instanced (1000 draws, batched vs direct) ---"
echo "Note: demo prints FPS/frame-time/P-Invoke count via ImGui overlay (visible in GUI session)."
echo "In headless bench, the demo runs but ImGui output is not captured here."
timeout $((DURATION + 4)) dotnet run --project src/MetalRenderingEngine.Demo -c Release --no-build -- instanced 2>&1 | head -5 || true
echo ""
echo "--- fence-bench (MTLFence blocking vs MTLSharedEvent async) ---"
echo "Outputs per-second: mode/fps/cpuFrame/cpuWait/gpuBusy"
timeout $((DURATION + 4)) dotnet run --project src/MetalRenderingEngine.Demo -c Release --no-build -- fence-bench 2>&1 | grep "\[fence-bench\]" | head -5 || true

echo ""
echo "=== Bench complete ==="
echo "GUI metrics (FPS, frame time, P/Invoke count) are shown in the ImGui overlay"
echo "during interactive runs. Record them into docs/perf.md."
