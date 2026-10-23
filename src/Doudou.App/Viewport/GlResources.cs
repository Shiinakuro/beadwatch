using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

using Doudou.Editor;
namespace Doudou.App.Viewport;

/// <summary>着色器与缓冲的薄封装。</summary>
public sealed class Shader : IDisposable
{
    public int Handle { get; }
    private readonly Dictionary<string, int> _loc = new();

    public Shader(string vs, string fs)
    {
        int v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, v); GL.AttachShader(Handle, f);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("着色器链接失败：" + GL.GetProgramInfoLog(Handle));
        GL.DeleteShader(v); GL.DeleteShader(f);
    }

    private static int Compile(ShaderType t, string src)
    {
        int s = GL.CreateShader(t);
        GL.ShaderSource(s, src);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{t} 编译失败：" + GL.GetShaderInfoLog(s));
        return s;
    }

    public void Use() => GL.UseProgram(Handle);
    private int Loc(string n) => _loc.TryGetValue(n, out var l) ? l : _loc[n] = GL.GetUniformLocation(Handle, n);
    public void Set(string n, Matrix4 m) => GL.UniformMatrix4(Loc(n), false, ref m);
    public void Set(string n, int v) => GL.Uniform1(Loc(n), v);
    public void Set(string n, float v) => GL.Uniform1(Loc(n), v);
    public void Set(string n, Vector3 v) => GL.Uniform3(Loc(n), v);
    public void Dispose() => GL.DeleteProgram(Handle);
}

/// <summary>一组 VAO/VBO/EBO，属性布局由构造参数给出（每项为分量数）。</summary>
public sealed class GpuMesh : IDisposable
{
    private readonly int _vao, _vbo, _ebo;
    private readonly int _stride;
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }

    public GpuMesh(params int[] layout)
    {
        _vao = GL.GenVertexArray(); _vbo = GL.GenBuffer(); _ebo = GL.GenBuffer();
        _stride = layout.Sum();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        int off = 0;
        for (int i = 0; i < layout.Length; i++)
        {
            GL.EnableVertexAttribArray(i);
            GL.VertexAttribPointer(i, layout[i], VertexAttribPointerType.Float, false, _stride * 4, off * 4);
            off += layout[i];
        }
        GL.BindVertexArray(0);
    }

    public void Upload(float[] verts, uint[]? idx)
    {
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * 4, verts, BufferUsageHint.DynamicDraw);
        VertexCount = verts.Length / _stride;
        if (idx != null)
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, idx.Length * 4, idx, BufferUsageHint.DynamicDraw);
            IndexCount = idx.Length;
        }
        GL.BindVertexArray(0);
    }

    public void DrawElements()
    {
        if (IndexCount == 0) return;
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, 0);
    }

    public void DrawArrays(PrimitiveType t)
    {
        if (VertexCount == 0) return;
        GL.BindVertexArray(_vao);
        GL.DrawArrays(t, 0, VertexCount);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); GL.DeleteBuffer(_ebo);
    }
}

public static class Shaders
{
    public const string VoxelVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec3 aCol;
layout(location=3) in vec2 aUv;
layout(location=4) in float aFlags;
uniform mat4 uVP;
out vec3 vNrm; out vec3 vCol; out vec2 vUv; flat out int vFlags;
void main(){ vNrm=aNrm; vCol=aCol; vUv=aUv; vFlags=int(aFlags+0.5); gl_Position=vec4(aPos,1.0)*uVP; }";

    public const string VoxelFS = @"#version 330 core
in vec3 vNrm; in vec3 vCol; in vec2 vUv; flat in int vFlags;
uniform int uBead; uniform int uGrid; uniform float uAlpha;
out vec4 frag;
void main(){
  vec3 L = normalize(vec3(-0.45,0.85,0.55));
  vec3 L2 = normalize(vec3(0.6,0.25,-0.7));
  vec3 c = vCol * (0.60 + 0.34*max(dot(vNrm,L),0.0) + 0.12*max(dot(vNrm,L2),0.0));
  float e = min(min(vUv.x,1.0-vUv.x),min(vUv.y,1.0-vUv.y));
  if(uGrid==1) c *= mix(0.70,1.0,smoothstep(0.0,0.07,e));
  if(uBead==1){ float r=length(vUv-0.5); if(r<0.15) c*=0.52; else if(r<0.19) c*=0.78; }
  if((vFlags&4)!=0){ float s=step(0.5,fract((vUv.x+vUv.y)*3.0)); c=mix(c,c*0.55,s); }
  if((vFlags&8)!=0){ float s=step(0.5,fract((vUv.x-vUv.y)*2.5)); c=mix(c,vec3(0.95,0.25,0.25),0.35*s); }
  if((vFlags&1)!=0) c=mix(c,vec3(0.25,0.6,1.0),0.45);
  if((vFlags&2)!=0) c=mix(c,vec3(1.0,0.12,0.08),0.7);
  frag=vec4(c,uAlpha);
}";

    public const string LineVS = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec4 aCol;
uniform mat4 uVP;
out vec4 vCol;
void main(){ vCol=aCol; gl_Position=vec4(aPos,1.0)*uVP; }";

    public const string LineFS = @"#version 330 core
in vec4 vCol; out vec4 frag;
void main(){ frag=vCol; }";
}
