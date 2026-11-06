// Gravação de uma janela de verdade (Windows Graphics Capture, Windows 10 1903+).
//
// Diferente de gravar um retângulo da tela, a captura acompanha a janela: se ela for movida,
// ficar atrás de outra ou parcialmente fora da tela, o vídeo continua mostrando só ela.
// O Windows entrega um quadro novo só quando a janela muda; a cópia acontece na GPU.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Capturo
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    sealed class WinGrab : IScreenGrab
    {
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out Native.RECT r, int size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QI(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Rel(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, ref TEXDESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref BOX box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        [StructLayout(LayoutKind.Sequential)] struct TEXDESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
        [StructLayout(LayoutKind.Sequential)] struct BOX { public uint left, top, front, right, bottom, back; }
        [StructLayout(LayoutKind.Sequential)] struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

        static readonly Guid IID_CaptureItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        static readonly Guid IID_DXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        static readonly Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        static T Fn<T>(IntPtr com, int index) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, index * IntPtr.Size), typeof(T));
        }
        static void Release(IntPtr p) { if (p != IntPtr.Zero) Fn<Rel>(p, 2)(p); }

        readonly int width, height;      // tamanho do vídeo (fixo: o da janela quando a gravação começou)
        IntPtr device, context, staging;
        IDirect3DDevice d3d;
        GraphicsCaptureItem item;
        Direct3D11CaptureFramePool pool;
        GraphicsCaptureSession session;
        SizeInt32 poolSize;
        CopyRegionFn copy; MapFn map; UnmapFn unmap;
        bool haveFrame;

        public static bool Supported
        {
            get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
        }

        // Retângulo que a captura da janela cobre, em coordenadas de tela (sem a borda invisível).
        public static Rectangle Bounds(IntPtr hwnd)
        {
            Native.RECT r;
            if (DwmGetWindowAttribute(hwnd, 9 /*EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (Native.GetWindowRect(hwnd, out r)) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return Rectangle.Empty;
        }

        // null se a captura de janela não estiver disponível (o gravador usa o retângulo da tela).
        public static WinGrab TryCreate(IntPtr hwnd, int width, int height)
        {
            if (!Supported || !IsWindow(hwnd)) return null;
            WinGrab g = null;
            try { g = new WinGrab(hwnd, width, height); return g; }
            catch (Exception ex) { TrayApp.Log("captura de janela: " + ex.Message); if (g != null) g.Dispose(); }
            return null;
        }

        WinGrab(IntPtr hwnd, int width, int height)
        {
            this.width = width; this.height = height;
            int level;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA*/, IntPtr.Zero, 0, 7, out device, out level, out context);
            if (hr != 0) throw new Exception("D3D11CreateDevice 0x" + hr.ToString("X8"));
            IntPtr dxgi, inspectable;
            var iid = IID_DXGIDevice;
            if (Fn<QI>(device, 0)(device, ref iid, out dxgi) != 0) throw new Exception("IDXGIDevice indisponível");
            try { hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable); }
            finally { Release(dxgi); }
            if (hr != 0) throw new Exception("CreateDirect3D11DeviceFromDXGIDevice 0x" + hr.ToString("X8"));
            try { d3d = (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable); }
            finally { Marshal.Release(inspectable); }

            var factory = (IGraphicsCaptureItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            var iidItem = IID_CaptureItem;
            IntPtr itemPtr = factory.CreateForWindow(hwnd, ref iidItem);
            try { item = (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr); }
            finally { Marshal.Release(itemPtr); }

            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { }   // o cursor é desenhado pelo editor
            try { session.IsBorderRequired = false; } catch { }         // sem a borda amarela (Windows 11)

            var td = new TEXDESC { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3 /*STAGING*/, CPUAccessFlags = 0x20000 /*READ*/ };
            if (Fn<CreateTexFn>(device, 5)(device, ref td, IntPtr.Zero, out staging) != 0) throw new Exception("textura de leitura");
            copy = Fn<CopyRegionFn>(context, 46); map = Fn<MapFn>(context, 14); unmap = Fn<UnmapFn>(context, 15);
            session.StartCapture();
        }

        public SizeInt32 ItemSize { get { return item.Size; } }

        public bool Grab(IntPtr dst)
        {
            Direct3D11CaptureFrame f = pool.TryGetNextFrame();
            if (f == null && !haveFrame)
            {
                // o primeiro quadro leva alguns milissegundos para chegar
                for (int i = 0; i < 60 && f == null; i++) { Thread.Sleep(10); f = pool.TryGetNextFrame(); }
                if (f == null)
                {
                    Fill(dst, 0, height);
                    haveFrame = true;
                    return true;
                }
            }
            if (f == null) return false;   // a janela não mudou
            int cw, ch;
            try
            {
                // pega o quadro mais recente (descarta os que ficaram na fila)
                Direct3D11CaptureFrame newer;
                while ((newer = pool.TryGetNextFrame()) != null) { f.Dispose(); f = newer; }
                var cs = f.ContentSize;
                cw = Math.Min(width, cs.Width); ch = Math.Min(height, cs.Height);
                var access = (IDirect3DDxgiInterfaceAccess)(object)f.Surface;
                var iid = IID_Texture2D;
                IntPtr tex = access.GetInterface(ref iid);
                try
                {
                    var box = new BOX { left = 0, top = 0, front = 0, right = (uint)cw, bottom = (uint)ch, back = 1 };
                    if (cw > 0 && ch > 0) copy(context, staging, 0, 0, 0, 0, tex, 0, ref box);
                }
                finally { Release(tex); }
                // a janela mudou de tamanho: o Windows precisa de uma fila com o tamanho novo
                if (cs.Width != poolSize.Width || cs.Height != poolSize.Height)
                {
                    poolSize = cs;
                    pool.Recreate(d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, cs);
                }
            }
            finally { f.Dispose(); }
            MAPPED m;
            if (map(context, staging, 0, 1 /*READ*/, 0, out m) != 0) throw new Exception("não foi possível ler a textura");
            try
            {
                int row = width * 4;
                for (int y = 0; y < ch; y++)
                {
                    MF.Copy(dst + y * row, m.pData + (int)(y * m.RowPitch), (IntPtr)(cw * 4));
                    if (cw < width) Zero(dst + y * row + cw * 4, (width - cw) * 4);
                }
            }
            finally { unmap(context, staging, 0); }
            if (ch < height) Fill(dst, ch, height);
            haveFrame = true;
            return true;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")] static extern void RtlZero(IntPtr dst, IntPtr len);
        static void Zero(IntPtr p, int bytes) { if (bytes > 0) RtlZero(p, (IntPtr)bytes); }
        void Fill(IntPtr dst, int fromRow, int toRow) { Zero(dst + fromRow * width * 4, (toRow - fromRow) * width * 4); }

        public void Dispose()
        {
            try { if (session != null) session.Dispose(); } catch { }
            try { if (pool != null) pool.Dispose(); } catch { }
            session = null; pool = null;
            if (item != null) { try { Marshal.ReleaseComObject(item); } catch { } item = null; }
            if (d3d != null) { try { d3d.Dispose(); } catch { } d3d = null; }
            Release(staging); Release(context); Release(device);
            staging = context = device = IntPtr.Zero;
        }
    }
}
