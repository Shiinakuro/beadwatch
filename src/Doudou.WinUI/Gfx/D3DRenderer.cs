using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Doudou.WinUI.Gfx;

/// <summary>
/// 体素视口的 Direct3D 11 渲染器：交换链挂在 WinUI 的 SwapChainPanel 上（合成交换链）。
/// 两条管线：体素面（光照 + 豆格描边 + 豆孔 + 选中/高亮/任意色花纹）与线框（地面网格、画布边框、坐标轴）。
/// </summary>
public sealed class D3DRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 VP;
        public Vector4 Params;   // x = 豆孔, y = 描边, z = 透明度, w = 曝光
        public Vector4 Params2;  // x = 柔光
    }

    private const string Hlsl = @"
cbuffer C : register(b0) { row_major float4x4 VP; float4 P; float4 P2; };
struct VI { float3 pos:POSITION; float3 nrm:NORMAL; float3 col:COLOR; float2 uv:TEXCOORD0; float flags:TEXCOORD1; };
struct VO { float4 pos:SV_Position; float3 nrm:NORMAL; float3 col:COLOR; float2 uv:TEXCOORD0; nointerpolation int flags:TEXCOORD1; };
VO VS(VI i) { VO o; o.pos = mul(float4(i.pos,1), VP); o.nrm = i.nrm; o.col = i.col; o.uv = i.uv; o.flags = (int)(i.flags + 0.5); return o; }
float4 PS(VO i) : SV_Target {
  float3 L = normalize(float3(-0.45,0.85,0.55));
  float3 L2 = normalize(float3(0.6,0.25,-0.7));
  float s = P2.x;
  float amb = lerp(0.60, 0.82, s);
  float k1 = lerp(0.34, 0.16, s);
  float k2 = lerp(0.12, 0.06, s);
  float3 c = i.col * (amb + k1*max(dot(i.nrm,L),0) + k2*max(dot(i.nrm,L2),0));
  float e = min(min(i.uv.x,1-i.uv.x),min(i.uv.y,1-i.uv.y));
  if (P.y > 0.5) c *= lerp(0.70, 1.0, smoothstep(0.0, 0.07, e));
  if (P.x > 0.5) { float r = length(i.uv-0.5); if (r < 0.15) c *= 0.52; else if (r < 0.19) c *= 0.78; }
  if (i.flags & 4) { float s2 = step(0.5, frac((i.uv.x+i.uv.y)*3.0)); c = lerp(c, c*0.55, s2); }
  if (i.flags & 8) { float s3 = step(0.5, frac((i.uv.x-i.uv.y)*2.5)); c = lerp(c, float3(0.95,0.25,0.25), 0.35*s3); }
  if (i.flags & 1) c = lerp(c, float3(0.25,0.6,1.0), 0.45);
  if (i.flags & 2) c = lerp(c, float3(1.0,0.12,0.08), 0.7);
  return float4(c * P.w, P.z);
}
struct LI { float3 pos:POSITION; float4 col:COLOR; };
struct LO { float4 pos:SV_Position; float4 col:COLOR; };
LO LVS(LI i) { LO o; o.pos = mul(float4(i.pos,1), VP); o.col = i.col; return o; }
float4 LPS(LO i) : SV_Target { return i.col; }
";

    private ID3D11Device? _dev;
    private ID3D11DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11VertexShader? _vs, _lvs;
    private ID3D11PixelShader? _ps, _lps;
    private ID3D11InputLayout? _layout, _lineLayout;
    private ID3D11Buffer? _cb;
    private ID3D11RasterizerState? _raster;
    private ID3D11DepthStencilState? _depth, _depthNoWrite;
    private ID3D11BlendState? _blend, _opaque;
    private readonly GpuBuffers _vox = new(), _preview = new(), _lines = new();
    private int _w = 1, _h = 1;

    public bool Ready => _swap != null;
    public int Width => _w;
    public int Height => _h;

    /// <summary>创建设备与交换链，返回交换链原生指针（交给 SwapChainPanel）。</summary>
    public IntPtr Initialize(int width, int height)
    {
        _w = Math.Max(1, width); _h = Math.Max(1, height);
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _dev, out _ctx);
        if (hr.Failure)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _dev, out _ctx).CheckError();

        using var dxgiDevice = _dev!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)_w, Height = (uint)_h,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
        };
        _swap = factory.CreateSwapChainForComposition(_dev, desc);

        var vsCode = Compiler.Compile(Hlsl, "VS", "voxel", "vs_5_0");
        var psCode = Compiler.Compile(Hlsl, "PS", "voxel", "ps_5_0");
        var lvsCode = Compiler.Compile(Hlsl, "LVS", "line", "vs_5_0");
        var lpsCode = Compiler.Compile(Hlsl, "LPS", "line", "ps_5_0");
        _vs = _dev.CreateVertexShader(vsCode.Span);
        _ps = _dev.CreatePixelShader(psCode.Span);
        _lvs = _dev.CreateVertexShader(lvsCode.Span);
        _lps = _dev.CreatePixelShader(lpsCode.Span);
        _layout = _dev.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 24, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32_Float, 44, 0),
        }, vsCode.Span);
        _lineLayout = _dev.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        }, lvsCode.Span);
        _cb = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer));
        _raster = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = true, MultisampleEnable = false });
        _depth = _dev.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthNoWrite = _dev.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _blend = _dev.CreateBlendState(BlendDescription.NonPremultiplied);
        _opaque = _dev.CreateBlendState(BlendDescription.Opaque);
        CreateTargets();
        return _swap.NativePointer;
    }

    private void CreateTargets()
    {
        _rtv?.Dispose(); _dsv?.Dispose();
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _dev!.CreateRenderTargetView(back);
        using var depth = _dev.CreateTexture2D(new Texture2DDescription(Format.D24_UNorm_S8_UInt, (uint)_w, (uint)_h, 1, 1, BindFlags.DepthStencil));
        _dsv = _dev.CreateDepthStencilView(depth);
    }

    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        if (_swap == null) return;
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == _w && height == _h) { SetScale(scaleX, scaleY); return; }
        _w = width; _h = height;
        _ctx!.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
        _rtv?.Dispose(); _rtv = null;
        _dsv?.Dispose(); _dsv = null;
        _ctx.Flush();
        _swap.ResizeBuffers(2, (uint)_w, (uint)_h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateTargets();
        SetScale(scaleX, scaleY);
    }

    /// <summary>交换链按物理像素创建，需要告诉合成器按 DPI 缩回逻辑尺寸。</summary>
    private void SetScale(float sx, float sy)
    {
        using var s2 = _swap!.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (s2 != null) s2.MatrixTransform = Matrix3x2.CreateScale(1f / Math.Max(0.1f, sx), 1f / Math.Max(0.1f, sy));
    }

    public void UploadVoxels(float[] verts, uint[] idx) => _vox.Upload(_dev!, verts, idx);
    public void UploadPreview(float[] verts, uint[] idx) => _preview.Upload(_dev!, verts, idx);
    public void UploadLines(float[] verts) => _lines.Upload(_dev!, verts, null);

    public void Render(Matrix4x4 vp, bool bead, bool grid, Vector3 clear, float exposure = 1f, bool softLight = false, bool drawLines = true)
    {
        if (_swap == null || _rtv == null) return;
        var ctx = _ctx!;
        ctx.OMSetRenderTargets(_rtv, _dsv);
        ctx.RSSetViewport(new Viewport(_w, _h));
        ctx.RSSetState(_raster);
        ctx.ClearRenderTargetView(_rtv, new Color4(clear.X, clear.Y, clear.Z, 1));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1, 0);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetConstantBuffer(0, _cb);

        // 线框
        if (drawLines)
        {
            SetConstants(vp, new Vector4(0, 0, 1, 1), new Vector4(0, 0, 0, 0));
            ctx.OMSetBlendState(_blend);
            ctx.OMSetDepthStencilState(_depth);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            ctx.IASetInputLayout(_lineLayout);
            ctx.VSSetShader(_lvs); ctx.PSSetShader(_lps);
            _lines.DrawArrays(ctx, 7 * 4);
        }

        // 体素
        SetConstants(vp, new Vector4(bead ? 1 : 0, grid ? 1 : 0, 1, exposure), new Vector4(softLight ? 1 : 0, 0, 0, 0));
        ctx.OMSetBlendState(_opaque);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs); ctx.PSSetShader(_ps);
        _vox.DrawIndexed(ctx, 12 * 4);

        // 笔刷预览（半透明、不写深度）
        if (_preview.IndexCount > 0)
        {
            SetConstants(vp, new Vector4(0, grid ? 1 : 0, 0.55f, 1), new Vector4(0, 0, 0, 0));
            ctx.OMSetBlendState(_blend);
            ctx.OMSetDepthStencilState(_depthNoWrite);
            _preview.DrawIndexed(ctx, 12 * 4);
        }
        _swap.Present(1, PresentFlags.None);
    }

    private void SetConstants(Matrix4x4 vp, Vector4 p, Vector4 p2)
    {
        var c = new Constants { VP = vp, Params = p, Params2 = p2 };
        _ctx!.UpdateSubresource(in c, _cb!);
    }

    public void Dispose()
    {
        _vox.Dispose(); _preview.Dispose(); _lines.Dispose();
        _rtv?.Dispose(); _dsv?.Dispose(); _cb?.Dispose();
        _vs?.Dispose(); _ps?.Dispose(); _lvs?.Dispose(); _lps?.Dispose();
        _layout?.Dispose(); _lineLayout?.Dispose();
        _raster?.Dispose(); _depth?.Dispose(); _depthNoWrite?.Dispose(); _blend?.Dispose(); _opaque?.Dispose();
        _swap?.Dispose(); _ctx?.Dispose(); _dev?.Dispose();
        _swap = null;
    }

    /// <summary>可增长的顶点/索引缓冲。</summary>
    private sealed class GpuBuffers : IDisposable
    {
        private ID3D11Buffer? _vb, _ib;
        private int _vbCap, _ibCap;
        public int IndexCount { get; private set; }
        public int VertexFloats { get; private set; }

        public void Upload(ID3D11Device dev, float[] verts, uint[]? idx)
        {
            VertexFloats = verts.Length;
            if (verts.Length == 0) { IndexCount = 0; return; }
            if (verts.Length > _vbCap)
            {
                _vb?.Dispose();
                _vbCap = Math.Max(verts.Length, _vbCap * 2);
                _vb = dev.CreateBuffer(new BufferDescription((uint)(_vbCap * 4), BindFlags.VertexBuffer));
            }
            dev.ImmediateContext.UpdateSubresource<float>(verts, _vb!, 0, 0, 0, new Box(0, 0, 0, verts.Length * 4, 1, 1));
            if (idx != null)
            {
                IndexCount = idx.Length;
                if (idx.Length == 0) return;
                if (idx.Length > _ibCap)
                {
                    _ib?.Dispose();
                    _ibCap = Math.Max(idx.Length, _ibCap * 2);
                    _ib = dev.CreateBuffer(new BufferDescription((uint)(_ibCap * 4), BindFlags.IndexBuffer));
                }
                dev.ImmediateContext.UpdateSubresource<uint>(idx, _ib!, 0, 0, 0, new Box(0, 0, 0, idx.Length * 4, 1, 1));
            }
        }

        public void DrawIndexed(ID3D11DeviceContext ctx, int stride)
        {
            if (IndexCount == 0 || _vb == null || _ib == null) return;
            ctx.IASetVertexBuffer(0, _vb, (uint)stride);
            ctx.IASetIndexBuffer(_ib, Format.R32_UInt, 0);
            ctx.DrawIndexed((uint)IndexCount, 0, 0);
        }

        public void DrawArrays(ID3D11DeviceContext ctx, int stride)
        {
            int n = VertexFloats * 4 / stride;
            if (n == 0 || _vb == null) return;
            ctx.IASetVertexBuffer(0, _vb, (uint)stride);
            ctx.Draw((uint)n, 0);
        }

        public void Dispose() { _vb?.Dispose(); _ib?.Dispose(); _vb = _ib = null; _vbCap = _ibCap = 0; }
    }
}
