# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A C# rendering engine targeting Apple Silicon (M1+) via Metal.framework. The architecture uses a thin Objective-C bridge (`native/bridge.m`) exposed as C ABI, consumed by C# through P/Invoke (`LibraryImport`). All GPU resource management, command encoding, and rendering logic lives in C#. No third-party Metal binding libraries (Veldrid, Metal.NET, SharpMetal, etc.) are used.

Shader compilation path: **Slang → DXIL → `metal-shaderconverter` (MSC) → `.metallib`** (precompiled, no runtime shader compilation).

## Build Commands

```bash
# Compile the ObjC bridge dylib (must be done before running anything)
./build/build_bridge.sh

# Compile all .slang shaders → .metallib
./build/compile_shaders.sh

# Build the full solution
# Note: MetalShaders.targets auto-compiles .slang files during BeforeBuild
# so shaders are rebuilt incrementally on dotnet build
dotnet build MetalRenderingEngine.sln

# Run specific demo modes
dotnet run --project src/MetalRenderingEngine.Demo -- compute
dotnet run --project src/MetalRenderingEngine.Demo -- triangle
dotnet run --project src/MetalRenderingEngine.Demo -- textured

# Run tests (requires .metallib files in out/shaders/ and dylib in out/)
dotnet test MetalRenderingEngine.sln

# Run a specific test class
dotnet test --filter "FullyQualifiedName~MetalDeviceTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~Multiply_DoublesAllElements"
```

## Architecture: C# ↔ Metal Binding Model

### Three-Layer Stack

```
C# SafeHandle wrappers (MetalDevice, MetalBuffer, MetalRenderEncoder, …)
    ↓ P/Invoke [LibraryImport("libmetal_bridge")]
native/bridge.m — C ABI thin wrappers (~20 lines each)
    ↓ [(id<MTLXXX>)handle method]
Metal.framework
```

- **C# layer**: `MetalObject : SafeHandle` is the base for all Metal objects. `MetalBridge.cs` centralizes all DllImport declarations.
- **bridge.m layer**: Functions use `__bridge_retained` to transfer ownership to C# and `CFRetain`/`CFRelease` for reference counting. No ARC — manual retain/release.
- **Handle type**: `mtl_handle_t` = `uintptr_t`. C# uses `nuint`. `0` = `MTL_NULL_HANDLE`.

### MSC 4.0 Argument Buffer Binding (Critical)

MSC 4.0 does **not** map HLSL `register(bN)` 1:1 to Metal `[[buffer(N)]]`. Instead, it uses a **top-level argument buffer at `[[buffer(2)]]`** that contains GPU-address descriptors:

| Resource Type | Descriptor Size | Layout |
|---------------|-----------------|--------|
| StructuredBuffer / RWStructuredBuffer (UAV/SRV buffer) | 24 bytes | `{u64 gpuAddress, u64 length, u64 stride}` |
| ConstantBuffer | 8 bytes (unverified) or 24 bytes | `{u64 gpuAddress}` (guess) |
| Texture2D / SamplerState | **unverified** | Unknown — may go through argument buffer or direct `[[texture(N)]]`/`[[sampler(N)]]` |

**Binding code pattern** (verified for compute and render):
```csharp
// 1. Declare GPU residency
encoder.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);

// 2. Build 24-byte descriptor
var desc = new UavDescriptor { GpuAddress = buffer.GpuAddress, Length = buffer.Length, Stride = sizeof(float) };

// 3. Set as inline bytes at argument buffer index (MSC uses buffer(2))
encoder.SetBytes(desc, index: 2);  // or SetVertexBytes / SetFragmentBytes for render
```

**Reflection JSON**: Build with `--output-reflection-file` to get `.reflect.json` showing `TopLevelArgumentBuffer` offsets. The `compile_one_shader.sh` script does this. Read `EltOffset`/`Size`/`Slot` from the JSON to know descriptor layout — **do not guess**.

### Shader Compilation Pipeline

```
.slang source
  ↓ slangc -target dxil -entry main -stage <compute|vertex|fragment> -profile sm_6_0
.dxil
  ↓ metal-shaderconverter
.metallib
```

- **Build script**: `build/compile_shaders.sh` compiles all `.slang` files under `src/MetalRenderingEngine.Shaders/`.
- **MSBuild integration**: `build/targets/MetalShaders.targets` auto-discovers `*.slang`, compiles incrementally via `Inputs`/`Outputs`, and copies `.metallib` to output. It is imported by `MetalRenderingEngine.Demo.csproj`.
- **Error remapping**: `compile_one_shader.sh` pipes slangc stderr through Perl to convert errors to MSBuild format (`file(line,col): error CODE: message`) for IDE click-through.
- **Runtime loading**: `MetalShaderLoader.cs` loads `.metallib` from `AppContext.BaseDirectory/shaders/` with caching.

## Key Project Constraints (from AGENTS.md)

- **No third-party Metal bindings** — Veldrid, Metal.NET, SharpMetal, Silk.NET.Metal are forbidden.
- **No `objc_msgSend` from C#** — all Metal calls must go through `bridge.m`.
- **No ObjC outside bridge.m** — SDL3 window creation is done via SDL's own ObjC code inside `libSDL3.dylib`.
- **No runtime shader compilation** — shaders must be precompiled to `.metallib`.
- **NuGet additions require AGENTS.md §7.3 registration**.
- **.NET 10+**, `AllowUnsafeBlocks=true`, `LangVersion=latest`.

## Code Patterns

### Adding a New Metal API to the Bridge

When you need a Metal API not yet exposed:

1. Add function declaration to `native/bridge.h`
2. Add implementation to `native/bridge.m` (≤20 lines, `__bridge_retained` for new objects)
3. Add `[LibraryImport]` to `Metal/Interop/MetalBridge.cs`
4. Add public method to the corresponding `MetalXxx` SafeHandle wrapper class
5. Rebuild bridge: `./build/build_bridge.sh`
6. Rebuild C# project

### SafeHandle Lifecycle

```csharp
// Ownership follows SafeHandle; using statement for determinism
using var buffer = device.NewBuffer(1024, MTLResourceOptions.StorageModeShared);

// Cross-scope sharing: Retain manually
public void Share(MetalBuffer buffer)
{
    buffer.Retain();  // +1 refcount
    _shared = buffer;
}
```

### Buffer CPU Access (UMA)

M1 has unified memory. `StorageModeShared` buffers are CPU/GPU coherent:
```csharp
Span<float> data = buffer.AsSpan<float>();
data[0] = 1.0f;  // Direct CPU write, no explicit upload needed
```

## Windowing

Two window backends exist:
- **SDL3** (`Platform/SDL3.cs`, `Platform/SDL3Window.cs`): Hand-written P/Invoke to `libSDL3.dylib`. Creates window via `SDL_CreateWindow` + `SDL_Metal_GetLayer`. Used by demo apps.
- **Cocoa fallback** (`Platform/CocoaWindow.cs`): Uses `Cocoa_CreateMetalWindow` in bridge.m to create NSWindow + CAMetalLayer directly. Minimal, no event loop.

## Testing

Tests are xUnit under `tests/MetalRenderingEngine.Tests/`:
- `MetalDeviceTests.cs`: Device lifecycle, buffer creation/read/write, SafeHandle idempotency
- `MultiplyKernelTests.cs`: End-to-end compute pipeline (load metallib → dispatch → validate)

Tests require the bridge dylib and `.metallib` files to be present. The test csproj copies `.metallib` from `out/shaders/` to bin via `<Content>` items.
