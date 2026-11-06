// Captura de tela para a gravação de vídeo.
//
// DesktopDuplication (DXGI, a mesma via do OBS): a placa de vídeo entrega só os quadros que mudaram
// e a cópia da área acontece na GPU. Medido: o BitBlt leva ~33 ms por quadro 1080p (todo o orçamento
// de 30 fps) mesmo com a tela parada; a duplicação custa só a cópia dos quadros que mudaram.
// Funciona quando a área está dentro de um monitor, sem rotação e em 8 bits por cor;
// nos outros casos (ou se algo falhar no meio) o gravador volta para o BitBlt.
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Capturo
{
    interface IScreenGrab : IDisposable
    {
        // Copia a área para dst (BGRA, de cima para baixo, largura*4 por linha).
        // Retorna false quando nada mudou desde a última vez (não precisa gravar quadro novo).
        bool Grab(IntPtr dst);
    }

    sealed class BltGrab : IScreenGrab
    {
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, int usage, out IntPtr bits, IntPtr section, int offset);
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        readonly Rectangle area;
        readonly IntPtr screen, mem, dib, old, bits;

        public BltGrab(Rectangle area)
        {
            this.area = area;
            screen = GetDC(IntPtr.Zero); mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = area.Width, biHeight = -area.Height, biPlanes = 1, biBitCount = 32 };
            dib = CreateDIBSection(mem, ref bi, 0, out bits, IntPtr.Zero, 0);
            old = SelectObject(mem, dib);
        }

        public bool Grab(IntPtr dst)
        {
            BitBlt(mem, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x00CC0020 /*SRCCOPY*/);
            MF.Copy(dst, bits, (IntPtr)(area.Width * area.Height * 4));
            return true;
        }

        public void Dispose()
        {
            SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    sealed class DupGrab : IScreenGrab
    {
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);

        // assinaturas das funções COM usadas (chamadas pela tabela virtual)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QI(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Rel(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumIdx(IntPtr self, uint i, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescFn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DupOutFn(IntPtr self, IntPtr device, out IntPtr dup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, ref TEXDESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AcquireFn(IntPtr self, uint timeout, IntPtr info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref BOX box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);

        [StructLayout(LayoutKind.Sequential)] struct TEXDESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct BOX { public uint left, top, front, right, bottom, back; }
        [StructLayout(LayoutKind.Sequential)] struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

        static readonly Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_D3D11Resource = new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d");
        const int WAIT_TIMEOUT = unchecked((int)0x887A0027);

        static T Fn<T>(IntPtr com, int index) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, index * IntPtr.Size), typeof(T));
        }
        static void Release(IntPtr p) { if (p != IntPtr.Zero) Fn<Rel>(p, 2)(p); }

        IntPtr device, context, dup, staging;
        readonly Rectangle local;     // área em coordenadas do monitor
        readonly int width, height;
        bool haveFrame;
        AcquireFn acquire; ReleaseFrameFn releaseFrame; CopyRegionFn copy; MapFn map; UnmapFn unmap;
        readonly IntPtr info = Marshal.AllocHGlobal(64);

        // null se a duplicação não servir para esta área (o gravador usa o BitBlt).
        public static DupGrab TryCreate(Rectangle area)
        {
            try
            {
                var g = new DupGrab(area);
                if (g.dup != IntPtr.Zero) return g;
                g.Dispose();
            }
            catch { }
            return null;
        }

        DupGrab(Rectangle area)
        {
            width = area.Width; height = area.Height; screenArea = area;
            IntPtr factory;
            if (CreateDXGIFactory1(IID_Factory1, out factory) != 0) return;
            try
            {
                var desc = Marshal.AllocHGlobal(256);
                try
                {
                    for (uint a = 0; dup == IntPtr.Zero; a++)
                    {
                        IntPtr adapter;
                        if (Fn<EnumIdx>(factory, 12)(factory, a, out adapter) != 0) break;   // IDXGIFactory1::EnumAdapters1
                        try
                        {
                            for (uint o = 0; ; o++)
                            {
                                IntPtr output;
                                if (Fn<EnumIdx>(adapter, 7)(adapter, o, out output) != 0) break;   // IDXGIAdapter::EnumOutputs
                                try
                                {
                                    // DXGI_OUTPUT_DESC: nome (64 bytes), RECT, AttachedToDesktop, Rotation, HMONITOR
                                    if (Fn<GetDescFn>(output, 7)(output, desc) != 0) continue;
                                    var r = Rectangle.FromLTRB(Marshal.ReadInt32(desc, 64), Marshal.ReadInt32(desc, 68), Marshal.ReadInt32(desc, 72), Marshal.ReadInt32(desc, 76));
                                    int rotation = Marshal.ReadInt32(desc, 84);
                                    if (!r.Contains(area)) continue;
                                    if (rotation > 1) return;   // monitor girado: fica com o BitBlt
                                    if (!Open(adapter, output)) return;
                                    local = new Rectangle(area.X - r.X, area.Y - r.Y, area.Width, area.Height);
                                    break;
                                }
                                finally { Release(output); }
                            }
                        }
                        finally { Release(adapter); }
                    }
                }
                finally { Marshal.FreeHGlobal(desc); }
            }
            finally { Release(factory); }
        }

        bool Open(IntPtr adapter, IntPtr output)
        {
            int level;
            if (D3D11CreateDevice(adapter, 0 /*UNKNOWN: usa o adaptador dado*/, IntPtr.Zero, 0x20 /*BGRA*/, IntPtr.Zero, 0, 7, out device, out level, out context) != 0) return false;
            IntPtr out1;
            var iid = IID_Output1;
            if (Fn<QI>(output, 0)(output, ref iid, out out1) != 0) return false;
            try
            {
                IntPtr d;
                if (Fn<DupOutFn>(out1, 22)(out1, device, out d) != 0) return false;   // IDXGIOutput1::DuplicateOutput
                // DXGI_OUTDUPL_DESC: ModeDesc.Format no deslocamento 16; só 8 bits BGRA (87)
                var dd = Marshal.AllocHGlobal(64);
                try
                {
                    Fn<GetDescFn>(d, 7)(d, dd);
                    int format = Marshal.ReadInt32(dd, 16);
                    if (format != 87) { Release(d); return false; }
                }
                finally { Marshal.FreeHGlobal(dd); }
                dup = d;
            }
            finally { Release(out1); }
            var td = new TEXDESC { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/ };
            if (Fn<CreateTexFn>(device, 5)(device, ref td, IntPtr.Zero, out staging) != 0) { Release(dup); dup = IntPtr.Zero; return false; }
            acquire = Fn<AcquireFn>(dup, 8); releaseFrame = Fn<ReleaseFrameFn>(dup, 14);
            copy = Fn<CopyRegionFn>(context, 46); map = Fn<MapFn>(context, 14); unmap = Fn<UnmapFn>(context, 15);
            return true;
        }

        Rectangle screenArea;   // área em coordenadas da tela (para o primeiro quadro pelo BitBlt)

        public bool Grab(IntPtr dst)
        {
            if (!haveFrame)
            {
                // O primeiro quadro precisa de imagem de verdade: a duplicação pode começar entregando só
                // o movimento do mouse (sem imagem). Espera um pouco; com a tela parada, copia uma vez pelo BitBlt.
                for (int i = 0; i < 4; i++) if (TryCopy(dst, 60, true)) { haveFrame = true; return true; }
                using (var b = new BltGrab(screenArea)) b.Grab(dst);
                haveFrame = true;
                return true;
            }
            return TryCopy(dst, 0, false);
        }

        // Copia o quadro novo, se houver. needImage: ignora quadros que só trazem o mouse.
        bool TryCopy(IntPtr dst, uint timeout, bool needImage)
        {
            IntPtr res;
            int hr = acquire(dup, timeout, info, out res);
            if (hr == WAIT_TIMEOUT) return false;   // nada mudou
            if (hr != 0) throw new Exception("Desktop Duplication falhou: 0x" + hr.ToString("X8"));
            try
            {
                long lastPresent = Marshal.ReadInt64(info, 0);
                if (lastPresent == 0) return false;   // só o mouse mexeu: a imagem é a mesma
                IntPtr tex;
                var iid = IID_D3D11Resource;
                if (Fn<QI>(res, 0)(res, ref iid, out tex) != 0) throw new Exception("textura indisponível");
                try
                {
                    var box = new BOX { left = (uint)local.X, top = (uint)local.Y, front = 0, right = (uint)local.Right, bottom = (uint)local.Bottom, back = 1 };
                    copy(context, staging, 0, 0, 0, 0, tex, 0, ref box);
                }
                finally { Release(tex); }
            }
            finally { Release(res); releaseFrame(dup); }
            MAPPED m;
            if (map(context, staging, 0, 1 /*READ*/, 0, out m) != 0) throw new Exception("não foi possível ler a textura");
            try
            {
                int row = width * 4;
                if (m.RowPitch == row) MF.Copy(dst, m.pData, (IntPtr)(row * height));
                else for (int y = 0; y < height; y++) MF.Copy(dst + y * row, m.pData + (int)(y * m.RowPitch), (IntPtr)row);
            }
            finally { unmap(context, staging, 0); }
            return true;
        }

        public void Dispose()
        {
            Release(staging); Release(dup); Release(context); Release(device);
            staging = dup = context = device = IntPtr.Zero;
            Marshal.FreeHGlobal(info);
        }
    }
}
