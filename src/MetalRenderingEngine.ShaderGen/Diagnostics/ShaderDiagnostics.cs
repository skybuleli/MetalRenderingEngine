using Microsoft.CodeAnalysis;

namespace MetalRenderingEngine.ShaderGen.Diagnostics;

/// <summary>
/// 着色器源生成器所有 diagnostic 定义（MSGEN001~099）。
/// </summary>
internal static class ShaderDiagnostics
{
    private const string Category = "MetalRenderingEngine.ShaderGen";

    // ---- 结构体声明错误（001-009）----

    public static readonly DiagnosticDescriptor ShaderMustBePartialStruct = new(
        id: "MSGEN001",
        title: "[Shader] 必须标注在 partial struct 上",
        messageFormat: "类型 '{0}' 必须是 partial struct 才能使用 [Shader] 属性",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ShaderMustImplementInterface = new(
        id: "MSGEN002",
        title: "[Shader] struct 必须实现 IComputeShader/IVertexShader/IFragmentShader",
        messageFormat: "类型 '{0}' 必须实现 IComputeShader、IVertexShader<TIn,TOut> 或 IFragmentShader<TIn,TOut> 之一",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ComputeShaderMissingThreadGroupSize = new(
        id: "MSGEN003",
        title: "Compute shader 缺少 [ThreadGroupSize] 属性",
        messageFormat: "Compute shader '{0}' 必须标注 [ThreadGroupSize(x, y, z)]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ---- 表达式翻译错误（010-019）----

    public static readonly DiagnosticDescriptor UnsupportedExpression = new(
        id: "MSGEN010",
        title: "不支持的表达式类型",
        messageFormat: "着色器方法体中不支持 {0} 表达式（GPU 代码无法表达此语义）",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedStatement = new(
        id: "MSGEN011",
        title: "不支持的语句类型",
        messageFormat: "着色器方法体中不支持 {0} 语句",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnresolvableType = new(
        id: "MSGEN012",
        title: "无法解析的类型",
        messageFormat: "类型 '{0}' 未在 Slang 类型映射表中注册",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMethodCall = new(
        id: "MSGEN013",
        title: "不支持的方法调用",
        messageFormat: "方法 '{0}' 没有对应的 Slang 内建函数映射",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ---- Execute 方法签名错误（020-029）----

    public static readonly DiagnosticDescriptor InvalidExecuteSignature = new(
        id: "MSGEN020",
        title: "Execute 方法签名不正确",
        messageFormat: "着色器 '{0}' 的 Execute 方法签名与接口定义不匹配：{1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VertexOutputMissingPosition = new(
        id: "MSGEN021",
        title: "顶点着色器输出缺少 Position 字段",
        messageFormat: "顶点着色器输出结构体 '{0}' 必须包含名为 Position 的 float4 字段（映射到 SV_Position）",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ---- 资源字段错误（030-039）----

    public static readonly DiagnosticDescriptor InvalidResourceFieldType = new(
        id: "MSGEN030",
        title: "资源字段类型不合法",
        messageFormat: "字段 '{0}' 的类型 '{1}' 不是合法的着色器资源类型",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
