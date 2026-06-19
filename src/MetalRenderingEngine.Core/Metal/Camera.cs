using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// 简单轨道相机：封装右手系透视投影 + LookAt 视图矩阵。
/// 用法：
/// <code>
/// var cam = new Camera(MathF.PI / 4f, (float)W / H, 0.1f, 100f);
/// cam.LookFrom(eye, target, up);
/// var viewProj = cam.ViewProj;  // 传给 shader
/// </code>
/// </summary>
public sealed class Camera
{
    private readonly float _fov, _aspect, _near, _far;
    private SysVector3 _eye, _target, _up;

    public Camera(float fovY, float aspect, float near, float far)
    {
        _fov = fovY; _aspect = aspect; _near = near; _far = far;
        _up = SysVector3.UnitY;
    }

    /// <summary>设置相机位置和朝向。</summary>
    public void LookFrom(SysVector3 eye, SysVector3 target, SysVector3? up = null)
    {
        _eye = eye; _target = target;
        if (up.HasValue) _up = up.Value;
    }

    /// <summary>相机位置。</summary>
    public SysVector3 Position => _eye;

    /// <summary>View × Proj 矩阵（行主序，直接传给 Slang/HLSL shader）。</summary>
    public SysMatrix ViewProj
    {
        get
        {
            var view = SysMatrix.CreateLookAt(_eye, _target, _up);
            var proj = PerspectiveFovRH(_fov, _aspect, _near, _far);
            return view * proj;
        }
    }

    /// <summary>右手系透视投影矩阵（System.Numerics 未提供 RH 透视，手写）。</summary>
    public static SysMatrix PerspectiveFovRH(float fovY, float aspect, float near, float far)
    {
        float yScale = 1f / MathF.Tan(fovY / 2f);
        float xScale = yScale / aspect;
        return new SysMatrix(
            xScale, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, far / (near - far), -1,
            0, 0, near * far / (near - far), 0);
    }
}
