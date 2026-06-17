// Multiply.slang — Phase 1 最小验证 compute shader
// 功能：把 RWStructuredBuffer<float> 每个元素 ×2
//
// 根签名通过 slangc -Xdxc -rootsig-define=... 在 DXIL 编译期注入（见 build/compile_shaders.sh），
// 让 metal-shaderconverter 把 UAV 直接绑到 buffer slot 0，绕过默认 argument buffer 间接寻址。

#define MULTIPLY_RS "RootFlags(0),UAV(u0)"

RWStructuredBuffer<float> Data : register(u0);

[shader("compute")]
[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    Data[id.x] = Data[id.x] * 2.0;
}
