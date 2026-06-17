using System.Collections.Generic;

namespace MetalRenderingEngine.ShaderGen.Translation;

/// <summary>
/// C# 方法调用 → Slang 内建函数的映射表。
/// </summary>
internal static class MethodIntrinsics
{
    /// <summary>
    /// 数学函数映射：C# 方法名（含类型前缀）→ Slang 内建函数名。
    /// </summary>
    private static readonly Dictionary<string, string> s_mathMap = new()
    {
        // System.MathF 静态方法
        ["MathF.Sin"] = "sin",
        ["MathF.Cos"] = "cos",
        ["MathF.Tan"] = "tan",
        ["MathF.Asin"] = "asin",
        ["MathF.Acos"] = "acos",
        ["MathF.Atan"] = "atan",
        ["MathF.Atan2"] = "atan2",
        ["MathF.Sqrt"] = "sqrt",
        ["MathF.Abs"] = "abs",
        ["MathF.Floor"] = "floor",
        ["MathF.Ceiling"] = "ceil",
        ["MathF.Round"] = "round",
        ["MathF.Min"] = "min",
        ["MathF.Max"] = "max",
        ["MathF.Pow"] = "pow",
        ["MathF.Log"] = "log",
        ["MathF.Log2"] = "log2",
        ["MathF.Exp"] = "exp",
        ["MathF.Exp2"] = "exp2",
        ["MathF.Sign"] = "sign",
        ["MathF.Clamp"] = "clamp",

        // System.Math 同名
        ["Math.Sin"] = "sin",
        ["Math.Cos"] = "cos",
        ["Math.Sqrt"] = "sqrt",
        ["Math.Abs"] = "abs",
        ["Math.Min"] = "min",
        ["Math.Max"] = "max",
        ["Math.Pow"] = "pow",
        ["Math.Clamp"] = "clamp",

        // 向量内建（GPUShaderMath 静态类，用户定义）
        ["GPUShaderMath.Dot"] = "dot",
        ["GPUShaderMath.Cross"] = "cross",
        ["GPUShaderMath.Normalize"] = "normalize",
        ["GPUShaderMath.Length"] = "length",
        ["GPUShaderMath.Distance"] = "distance",
        ["GPUShaderMath.Reflect"] = "reflect",
        ["GPUShaderMath.Refract"] = "refract",
        ["GPUShaderMath.Lerp"] = "lerp",
        ["GPUShaderMath.Saturate"] = "saturate",
        ["GPUShaderMath.Step"] = "step",
        ["GPUShaderMath.SmoothStep"] = "smoothstep",
        ["GPUShaderMath.Mul"] = "mul",
        ["GPUShaderMath.Transpose"] = "transpose",
        ["GPUShaderMath.Determinant"] = "determinant",

        // 纹理方法
        ["Sample"] = "Sample",
        ["Load"] = "Load",
    };

    /// <summary>
    /// 尝试将 C# 方法调用翻译为 Slang 内建函数。
    /// </summary>
    /// <param name="containingType">方法所在类型的全名或短名。</param>
    /// <param name="methodName">方法名。</param>
    /// <param name="slangFunc">输出 Slang 函数名。</param>
    /// <returns>是否找到映射。</returns>
    public static bool TryGetIntrinsic(string containingType, string methodName, out string slangFunc)
    {
        // 先尝试 "TypeName.MethodName"
        var key = $"{containingType}.{methodName}";
        if (s_mathMap.TryGetValue(key, out slangFunc!))
            return true;

        // 再尝试仅 "MethodName"（如纹理的 Sample/Load）
        if (s_mathMap.TryGetValue(methodName, out slangFunc!))
            return true;

        slangFunc = "";
        return false;
    }
}
