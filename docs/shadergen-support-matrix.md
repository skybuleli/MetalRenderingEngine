# ShaderGen 支持矩阵

本文档描述 `MetalRenderingEngine.ShaderGen` 当前支持的 C# 着色器子集。

目标不是“支持所有 C#”，而是稳定支持一小块可验证、可诊断、可维护的语法。

## 入口约束

- 仅支持带 `[Shader]` 的 `partial struct`
- 必须实现以下接口之一：
  - `IComputeShader`
  - `IVertexShader`
  - `IFragmentShader`
- Compute shader 必须带 `ThreadGroupSizeAttribute`

## 当前支持的 shader 类型

- Compute shader
- Vertex shader
- Fragment shader

## 当前支持的字段类型

- 标量：`float`、`int`、`uint`、`bool`、`double`
- 向量/矩阵：`float2`、`float3`、`float4`、`int2`、`int3`、`int4`、`uint3`、`float4x4`
- 资源：
  - `ReadWriteBuffer<T>`
  - `ReadOnlyBuffer<T>`
  - `Texture2D<T>`
  - `SamplerState`
  - `ConstantBuffer<T>`

## 当前支持的表达式

- 字面量：数值、`true`、`false`、`null`
- 标识符
- 成员访问
- 方法调用
- 二元运算：`+ - * / % == != < <= > >= && || & | ^ << >>`
- 一元运算：`+ - ! ++ --`
- 类型转换
- `new` 向量/结构体构造
- 索引访问
- 条件表达式 `?:`
- 赋值表达式

## 当前支持的语句

- 局部变量声明
- 表达式语句
- `if / else`
- `for`
- `while`
- `break`
- `continue`
- `return`
- 代码块

## 当前限制

- 不支持完整 C# 语义，只做受限子集翻译
- 不支持：
  - lambda / 匿名函数
  - `await`
  - 元组
  - `switch` 表达式
  - `stackalloc`
  - `typeof` / `sizeof`
  - 模式匹配
  - `throw` 表达式
  - `with` 表达式
- 标量字段当前仍依赖隐式常量缓冲绑定约定；生成器会保留说明，但不会自动泛化所有布局场景

## 维护原则

- 新语法支持前，先补诊断，再补测试，最后补翻译
- 不能稳定支持的语法，宁可明确报错，不要静默生成错误 Slang
- 每次扩展都应同步更新本矩阵
