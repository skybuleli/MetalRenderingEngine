using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Shader;

// ============================================================
// GPU 向量类型（源生成器翻译为 Slang float2/float3/float4/int2/int3/int4/uint3）
// 这些类型在 C# 端可用于 CPU 侧数据准备；在着色器方法体内被源生成器翻译。
// ============================================================

/// <summary>二维浮点向量（Slang float2）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct float2
{
    public readonly float X, Y;
    public float2(float x, float y) { X = x; Y = y; }
    public float2(float s) { X = s; Y = s; }

    public static float2 operator +(float2 a, float2 b) => new(a.X + b.X, a.Y + b.Y);
    public static float2 operator -(float2 a, float2 b) => new(a.X - b.X, a.Y - b.Y);
    public static float2 operator *(float2 a, float s) => new(a.X * s, a.Y * s);
    public static float2 operator *(float s, float2 a) => new(a.X * s, a.Y * s);
    public static float2 operator /(float2 a, float s) => new(a.X / s, a.Y / s);
    public static float2 operator -(float2 a) => new(-a.X, -a.Y);
}

/// <summary>三维浮点向量（Slang float3）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct float3
{
    public readonly float X, Y, Z;
    public float3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float3(float s) { X = s; Y = s; Z = s; }

    public static float3 operator +(float3 a, float3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static float3 operator -(float3 a, float3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static float3 operator *(float3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static float3 operator *(float s, float3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static float3 operator /(float3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
    public static float3 operator -(float3 a) => new(-a.X, -a.Y, -a.Z);
}

/// <summary>四维浮点向量（Slang float4）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct float4
{
    public readonly float X, Y, Z, W;
    public float4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    public float4(float3 xyz, float w) { X = xyz.X; Y = xyz.Y; Z = xyz.Z; W = w; }
    public float4(float s) { X = s; Y = s; Z = s; W = s; }

    public static float4 operator +(float4 a, float4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
    public static float4 operator -(float4 a, float4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    public static float4 operator *(float4 a, float s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);
    public static float4 operator *(float s, float4 a) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);
    public static float4 operator -(float4 a) => new(-a.X, -a.Y, -a.Z, -a.W);
}

/// <summary>二维整数向量（Slang int2）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct int2
{
    public readonly int X, Y;
    public int2(int x, int y) { X = x; Y = y; }
    public int2(int s) { X = s; Y = s; }
}

/// <summary>三维整数向量（Slang int3）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct int3
{
    public readonly int X, Y, Z;
    public int3(int x, int y, int z) { X = x; Y = y; Z = z; }
    public int3(int s) { X = s; Y = s; Z = s; }
}

/// <summary>四维整数向量（Slang int4）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct int4
{
    public readonly int X, Y, Z, W;
    public int4(int x, int y, int z, int w) { X = x; Y = y; Z = z; W = w; }
    public int4(int s) { X = s; Y = s; Z = s; W = s; }
}

/// <summary>三维无符号整数向量（Slang uint3）。用于 ThreadId 等场景。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct uint3
{
    public readonly uint X, Y, Z;
    public uint3(uint x, uint y, uint z) { X = x; Y = y; Z = z; }
    public uint3(uint s) { X = s; Y = s; Z = s; }
}

/// <summary>4×4 浮点矩阵（Slang float4x4）。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct float4x4
{
    public readonly float4 Row0, Row1, Row2, Row3;
    public float4x4(float4 r0, float4 r1, float4 r2, float4 r3)
    {
        Row0 = r0; Row1 = r1; Row2 = r2; Row3 = r3;
    }

    /// <summary>单位矩阵。</summary>
    public static readonly float4x4 Identity = new(
        new float4(1, 0, 0, 0),
        new float4(0, 1, 0, 0),
        new float4(0, 0, 1, 0),
        new float4(0, 0, 0, 1));
}
