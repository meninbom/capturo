// Gravação de vídeo no estilo "demo": grava a área sem o cursor, num MP4 intermediário,
// e guarda à parte a posição do mouse, os cliques e o desenho do cursor. O editor usa isso
// para montar o vídeo final com zoom automático nos cliques, cursor maior e suavizado,
// destaque de clique e fundo com degradê e sombra.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    struct CursorSample { public int T; public short X, Y, Shape; }   // Shape -1: cursor escondido
    struct ClickMark { public int T; public short X, Y; public byte Button; }

    class CursorShape { public Bitmap Image; public int HotX, HotY; }

    // ------------------------------------------------------------------ gravação
    class VideoRecorder
    {
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, int usage, out IntPtr bits, IntPtr section, int offset);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr mod, int thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        delegate IntPtr MouseProc(int code, IntPtr w, IntPtr l);

        readonly int Fps, MaxSeconds;     // das configurações (30 ou 60 fps; limite em minutos)
        volatile bool paused;
        Rectangle barBounds, frameBounds;  // cliques na nossa própria barra não viram zoom
        public static VideoRecorder Current;

        readonly TrayApp app;
        readonly Rectangle area;
        readonly string dir;
        readonly RecFrame frame;
        readonly RecBar bar;
        readonly Stopwatch clock = new Stopwatch();
        readonly System.Windows.Forms.Timer ui = new System.Windows.Forms.Timer();
        readonly BlockingCollection<Tuple<IntPtr, long>> queue = new BlockingCollection<Tuple<IntPtr, long>>(8);
        readonly ConcurrentBag<IntPtr> pool = new ConcurrentBag<IntPtr>();
        readonly List<CursorSample> cursor = new List<CursorSample>();
        readonly List<ClickMark> clicks = new List<ClickMark>();
        readonly Dictionary<IntPtr, int> shapeIds = new Dictionary<IntPtr, int>();
        readonly List<CursorShape> shapes = new List<CursorShape>();
        readonly MouseProc hookProc;
        readonly AudioCapture sys, mic;
        IntPtr hook;
        bool micUsed, sysUsed;      // ficaram ligados em algum momento (senão o arquivo é só silêncio)
        Thread grabber, encoder;
        volatile bool stopping, cancelled;
        Exception error;
        int frames, dropped, skipped;
        public string Method = "";   // como a tela está sendo copiada (DXGI, BitBlt ou janela)
        long endMs;
        // gravação de uma janela: a captura acompanha a janela e a origem (para o mouse) anda junto
        readonly IntPtr hwnd;
        volatile int ox, oy;
        bool windowGone;
        // webcam
        WebcamCapture cam;
        CamBubble bubble;
        Rectangle bubbleBounds;
        readonly bool camAvailable;
        PointF? camPos;

        public static void Start(TrayApp app, Rectangle area) { Start(app, area, IntPtr.Zero); }

        public static void Start(TrayApp app, Rectangle area, IntPtr window)
        {
            if (Current != null) return;
            try { Current = new VideoRecorder(app, area, window); }
            catch (Exception ex) { Current = null; app.Notify("Não foi possível gravar", ex.Message, null); }
        }

        public static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capturo", "gravacoes"); }
        }

        // Gravações que ficaram para trás (o app fechou com o editor aberto).
        public static void CleanOld()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var d in Directory.GetDirectories(Root))
                    if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-3)) Directory.Delete(d, true);
            }
            catch { }
        }

        VideoRecorder(TrayApp app, Rectangle area, IntPtr window)
        {
            this.app = app;
            Fps = TrayApp.S.VidFps >= 60 ? 60 : 30;
            MaxSeconds = Math.Max(1, TrayApp.S.VidMaxMin) * 60;
            if (window != IntPtr.Zero && WinGrab.Supported && WinGrab.IsWindow(window))
            {
                var wb = WinGrab.Bounds(window);
                if (wb.Width >= 32 && wb.Height >= 32) { hwnd = window; area = wb; }
            }
            // H.264 exige largura e altura pares
            area.Width &= ~1; area.Height &= ~1;
            this.area = area;
            ox = area.X; oy = area.Y;
            try { camAvailable = Webcam.Devices().Count > 0; } catch { camAvailable = false; }
            dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);

            // abre o codificador antes de mostrar qualquer coisa: se ele recusar, nada aparece.
            // Ele é criado e usado sempre na mesma thread (objetos COM do Media Foundation).
            var opened = new ManualResetEvent(false);
            encoder = new Thread(() => Encode(opened)) { IsBackground = true, Name = "video-codificador" };
            encoder.SetApartmentState(ApartmentState.MTA);
            encoder.Start();
            opened.WaitOne();
            if (writer == null)
            {
                try { Directory.Delete(dir, true); } catch { }
                throw new Exception(error != null ? error.Message : "O codificador de vídeo do Windows não respondeu.");
            }

            frame = new RecFrame(area);
            bar = new RecBar(area, true, camAvailable);
            bar.MicOn = TrayApp.S.RecMic; bar.SysOn = TrayApp.S.RecSys;
            bar.CamOn = camAvailable && TrayApp.S.VidCam;
            bar.StopClicked += () => Stop(false);
            bar.CancelClicked += () => Stop(true);
            bar.PauseClicked += TogglePause;
            micUsed = bar.MicOn; sysUsed = bar.SysOn;
            bar.ToggleChanged += () =>
            {
                TrayApp.S.RecMic = bar.MicOn; TrayApp.S.RecSys = bar.SysOn;
                micUsed |= bar.MicOn; sysUsed |= bar.SysOn;
                if (sys != null) sys.Muted = !bar.SysOn;
                if (mic != null) mic.Muted = !bar.MicOn;
                if (camAvailable)
                {
                    TrayApp.S.VidCam = bar.CamOn;
                    if (bar.CamOn) StartCam();
                    if (cam != null) cam.On = bar.CamOn;
                    if (bubble != null) bubble.Off = !bar.CamOn;
                    bubbleBounds = bubble != null && bubble.Visible ? bubble.Bounds : Rectangle.Empty;
                }
            };
            frame.Show(); bar.Show();
            GifRecorder.ExcludeFromCapture(frame.Handle); GifRecorder.ExcludeFromCapture(bar.Handle);
            barBounds = bar.Bounds; frameBounds = frame.Bounds;

            hookProc = MouseHook;
            hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookProc, GetModuleHandle(null), 0);

            for (int i = 0; i < 6; i++) pool.Add(Marshal.AllocHGlobal(area.Width * area.Height * 4));
            clock.Start();
            grabber = new Thread(Grab) { IsBackground = true, Name = "video-captura", Priority = ThreadPriority.AboveNormal };
            grabber.Start();
            // o áudio sempre é gravado; o botão só silencia (dá para ligar no meio da gravação)
            sys = new AudioCapture(true, Path.Combine(dir, "sys.raw"), clock) { Muted = !bar.SysOn };
            mic = new AudioCapture(false, Path.Combine(dir, "mic.raw"), clock) { Muted = !bar.MicOn };
            sys.Start(); mic.Start();
            bar.HasMic = mic.Started; bar.HasSys = sys.Started;
            if (bar.CamOn) StartCam();

            ui.Interval = 100;
            ui.Tick += (s, e) =>
            {
                long ms = clock.ElapsedMilliseconds;
                bar.SetTime(ms);
                barBounds = bar.Bounds;
                if (bubble != null) bubbleBounds = bubble.Visible ? bubble.Bounds : Rectangle.Empty;
                bar.SetLevels(mic.Started && bar.MicOn ? mic.Level : 0, sys.Started && bar.SysOn ? sys.Level : 0);
                if (cam != null && cam.Failed && bar.CamOn)
                {
                    bar.CamOn = false; bar.HasCam = false; bar.Invalidate();
                    if (bubble != null) bubble.Off = true;
                    app.Notify("A webcam não abriu", cam.Error ?? "", null);
                }
                if (hwnd != IntPtr.Zero)
                {
                    // a janela fechou: termina a gravação; andou: a moldura vai junto
                    if (!WinGrab.IsWindow(hwnd)) { windowGone = true; Stop(false); return; }
                    var r = new Rectangle(ox, oy, area.Width, area.Height);
                    r.Inflate(3, 3);
                    if (frame.Bounds != r) { frame.Bounds = r; frameBounds = r; }
                }
                if (ms >= MaxSeconds * 1000L) Stop(false);
            };
            ui.Start();
        }

        // Liga a webcam (na primeira vez que for pedida) e mostra a bolha de prévia.
        void StartCam()
        {
            if (cam != null || stopping) return;
            cam = new WebcamCapture(TrayApp.S.VidCamName, Path.Combine(dir, "camera.bin"), clock) { Paused = paused };
            cam.Start();
            float k;
            using (var g = bar.CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            bubble = new CamBubble(cam, area, k);
            bubble.Show();
            bubbleBounds = bubble.Bounds;
        }

        Mp4Writer writer;

        Mp4Writer OpenWriter()
        {
            string path = Path.Combine(dir, "tela.mp4");
            // qualidade alta: este arquivo é só intermediário
            int bitrate = (int)Math.Max(4e6, Math.Min(40e6, area.Width * (double)area.Height * Fps * 0.25));
            try { return new Mp4Writer(path, area.Width, area.Height, Fps, bitrate, 0, 0, true, true); }
            catch
            {
                try { File.Delete(path); } catch { }
                return new Mp4Writer(path, area.Width, area.Height, Fps, bitrate, 0, 0, false, true);
            }
        }

        IntPtr MouseHook(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = w.ToInt32();
                int sx = Marshal.ReadInt32(l, 0), sy = Marshal.ReadInt32(l, 4);
                var now = new Rectangle(ox, oy, area.Width, area.Height);
                bool ours = barBounds.Contains(sx, sy) || bubbleBounds.Contains(sx, sy) || (frameBounds.Contains(sx, sy) && !now.Contains(sx, sy));
                if ((msg == 0x201 || msg == 0x204 || msg == 0x207) && !paused && !ours)
                {
                    int x = sx - now.X, y = sy - now.Y;
                    lock (clicks)
                        clicks.Add(new ClickMark { T = (int)clock.ElapsedMilliseconds, X = Clamp16(x), Y = Clamp16(y), Button = (byte)(msg == 0x201 ? 0 : msg == 0x204 ? 1 : 2) });
                }
            }
            return CallNextHookEx(hook, code, w, l);
        }

        static short Clamp16(int v) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v)); }

        // Pausar congela o relógio da gravação: vídeo, áudio e mouse param juntos e o arquivo
        // continua sem buraco quando retoma.
        public void TogglePause()
        {
            if (stopping) return;
            if (!paused)
            {
                paused = true;
                sys.Paused = mic.Paused = true;
                if (cam != null) cam.Paused = true;
                clock.Stop();
            }
            else
            {
                clock.Start();
                sys.Paused = mic.Paused = false;
                if (cam != null) cam.Paused = false;
                paused = false;
            }
            bar.Paused = paused;
            frame.Paused = paused;
        }

        void Grab()
        {
            // Janela: Windows Graphics Capture (acompanha a janela). Área: Desktop Duplication quando dá
            // (só quadros que mudaram, cópia na GPU); senão, BitBlt.
            IScreenGrab src = null;
            if (hwnd != IntPtr.Zero)
            {
                src = WinGrab.TryCreate(hwnd, area.Width, area.Height);
                if (src != null) Method = "janela (WGC)";
            }
            if (src == null)
            {
                src = DupGrab.TryCreate(area);
                Method = src != null ? "DXGI" : "BitBlt";
                if (src == null) src = new BltGrab(area);
            }
            bool followWindow = src is WinGrab;
            timeBeginPeriod(1);
            try
            {
                long period = 10000000L / Fps;      // 100 ns
                long lastSlot = -1, tick = 0;
                // o mouse é lido a 120 Hz (pelo menos duas vezes por quadro): o cursor suavizado do editor
                // fica mais fiel ao movimento real, sem "cantos" entre uma leitura e outra
                double half = Math.Min(1000.0 / Fps / 2, 1000.0 / 120);
                while (!stopping)
                {
                    if (paused) { Thread.Sleep(10); continue; }
                    double due = tick * half;
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (now < due) { Thread.Sleep(Math.Max(1, (int)(due - now))); continue; }
                    tick = (long)(now / half) + 1;
                    if (followWindow)
                    {
                        var wb = WinGrab.Bounds(hwnd);
                        if (wb.Width > 0) { ox = wb.X; oy = wb.Y; }
                    }
                    SampleCursor((int)now);
                    long slot = (long)(now * Fps / 1000.0);
                    if (slot <= lastSlot) continue;
                    lastSlot = slot;
                    IntPtr buf;
                    if (!pool.TryTake(out buf)) { dropped++; continue; }   // codificador atrasado: repete o quadro anterior
                    bool changed;
                    try { changed = src.Grab(buf); }
                    catch
                    {
                        // a duplicação caiu (troca de resolução, UAC, tela bloqueada...): segue com o BitBlt
                        // (na janela: grava o retângulo onde ela está agora)
                        src.Dispose();
                        src = new BltGrab(new Rectangle(ox, oy, area.Width, area.Height));
                        Method = followWindow ? "BitBlt (após falha da captura de janela)" : "BitBlt (após falha da DXGI)";
                        followWindow = false;
                        changed = src.Grab(buf);
                    }
                    if (!changed) { pool.Add(buf); skipped++; continue; }   // tela parada: nada a gravar
                    queue.Add(Tuple.Create(buf, slot * period));
                }
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                timeEndPeriod(1);
                src.Dispose();
                queue.CompleteAdding();
            }
        }

        void SampleCursor(int t)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            short shape = -1;
            if (GetCursorInfo(ref ci) && ci.flags == 1 && ci.hCursor != IntPtr.Zero)
            {
                int id;
                if (!shapeIds.TryGetValue(ci.hCursor, out id))
                {
                    id = shapes.Count;
                    shapes.Add(ExtractCursor(ci.hCursor));
                    shapeIds[ci.hCursor] = id;
                }
                shape = (short)id;
            }
            lock (cursor) cursor.Add(new CursorSample { T = t, X = Clamp16(ci.pt.X - ox), Y = Clamp16(ci.pt.Y - oy), Shape = shape });
        }

        // Desenha o cursor sobre preto e sobre branco para descobrir a transparência de cada pixel.
        // Pixels que invertem a tela (cursor de texto) viram preto com contorno branco.
        static CursorShape ExtractCursor(IntPtr h)
        {
            var cs = new CursorShape();
            ICONINFO ii;
            int w = 32, hgt = 32;
            if (GetIconInfo(h, out ii))
            {
                cs.HotX = ii.xHotspot; cs.HotY = ii.yHotspot;
                BITMAP bm;
                if (ii.hbmColor != IntPtr.Zero && GetObject(ii.hbmColor, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; hgt = bm.bmHeight; }
                else if (ii.hbmMask != IntPtr.Zero && GetObject(ii.hbmMask, Marshal.SizeOf(typeof(BITMAP)), out bm) != 0) { w = bm.bmWidth; hgt = bm.bmHeight / 2; }
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            w = Math.Max(1, Math.Min(256, w)); hgt = Math.Max(1, Math.Min(256, hgt));
            var onBlack = Paint(h, w, hgt, Color.Black);
            var onWhite = Paint(h, w, hgt, Color.White);
            var res = new Bitmap(w, hgt, PixelFormat.Format32bppArgb);
            var inv = new bool[w * hgt];
            var px = new int[w * hgt];
            for (int y = 0; y < hgt; y++)
                for (int x = 0; x < w; x++)
                {
                    int b = onBlack[y * w + x], wh = onWhite[y * w + x];
                    int bG = (b >> 8) & 255, wG = (wh >> 8) & 255;
                    int a = 255 - (wG - bG);
                    if (wG < bG) { inv[y * w + x] = true; px[y * w + x] = unchecked((int)0xFF000000); continue; }
                    if (a <= 0) continue;
                    a = Math.Min(255, a);
                    int r = Math.Min(255, ((b >> 16) & 255) * 255 / a), g = Math.Min(255, bG * 255 / a), bb = Math.Min(255, (b & 255) * 255 / a);
                    px[y * w + x] = (a << 24) | (r << 16) | (g << 8) | bb;
                }
            for (int y = 0; y < hgt; y++)
                for (int x = 0; x < w; x++)
                {
                    if (px[y * w + x] != 0) continue;
                    bool near = false;
                    for (int dy = -1; dy <= 1 && !near; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx >= 0 && yy >= 0 && xx < w && yy < hgt && inv[yy * w + xx]) { near = true; break; }
                        }
                    if (near) px[y * w + x] = unchecked((int)0xFFFFFFFF);
                }
            var d = res.LockBits(new Rectangle(0, 0, w, hgt), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < hgt; y++) Marshal.Copy(px, y * w, d.Scan0 + y * d.Stride, w);
            res.UnlockBits(d);
            cs.Image = res;
            return cs;
        }

        static int[] Paint(IntPtr h, int w, int hgt, Color bg)
        {
            using (var b = new Bitmap(w, hgt, PixelFormat.Format32bppRgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.Clear(bg);
                    IntPtr hdc = g.GetHdc();
                    try { DrawIconEx(hdc, 0, 0, h, w, hgt, 0, IntPtr.Zero, 3); }
                    finally { g.ReleaseHdc(hdc); }
                }
                var d = b.LockBits(new Rectangle(0, 0, w, hgt), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                var px = new int[w * hgt];
                for (int y = 0; y < hgt; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w);
                b.UnlockBits(d);
                return px;
            }
        }

        void Encode(ManualResetEvent opened)
        {
            long period = 10000000L / Fps;
            try { writer = OpenWriter(); }
            catch (Exception ex) { error = ex; }
            opened.Set();
            if (writer == null) return;
            try
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    try { if (!cancelled && error == null) { writer.WriteVideo(item.Item1, area.Width * 4, item.Item2, period); frames++; } }
                    catch (Exception ex) { error = ex; }
                    pool.Add(item.Item1);
                }
                if (!cancelled && error == null && frames > 0) writer.Finish();
            }
            catch (Exception ex) { error = ex; }
            finally { writer.Dispose(); }
        }

        public void Stop(bool cancel)
        {
            if (stopping) return;
            stopping = true;
            cancelled = cancel;
            endMs = clock.ElapsedMilliseconds;
            ui.Stop(); ui.Dispose();
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (bubble != null)
            {
                if (bubble.Moved && bubble.Visible) camPos = bubble.CenterIn(new Rectangle(ox, oy, area.Width, area.Height));
                bubble.Close();
            }
            frame.Close(); bar.Close();
            if (!cancel) app.Notify(windowGone ? "A janela foi fechada" : "Gravação encerrada", "Abrindo o editor de vídeo...", null);
            new Thread(() =>
            {
                grabber.Join();
                sys.Stop(); mic.Stop();
                if (cam != null) cam.Stop();
                encoder.Join();
                IntPtr p; while (pool.TryTake(out p)) Marshal.FreeHGlobal(p);
                if (!cancel && error == null && frames > 0)
                {
                    try { SaveSession(); }
                    catch (Exception ex) { error = ex; }
                }
                app.RunOnUi(Finished);
            }) { IsBackground = true }.Start();
        }

        void SaveSession()
        {
            var sb = new StringBuilder();
            sb.AppendLine("w=" + area.Width); sb.AppendLine("h=" + area.Height);
            sb.AppendLine("fps=" + Fps); sb.AppendLine("dur=" + endMs);
            sb.AppendLine("frames=" + frames); sb.AppendLine("dropped=" + dropped); sb.AppendLine("skipped=" + skipped); sb.AppendLine("captura=" + Method);
            sb.AppendLine("sys=" + (sys.Started && sysUsed ? 1 : 0)); sb.AppendLine("mic=" + (mic.Started && micUsed ? 1 : 0));
            sb.AppendLine("janela=" + (hwnd != IntPtr.Zero ? 1 : 0));
            if (cam != null)
            {
                sb.AppendLine("cam=" + cam.Frames);
                if (camPos.HasValue) sb.AppendLine("campos=" + camPos.Value.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + camPos.Value.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }
            for (int i = 0; i < shapes.Count; i++)
            {
                shapes[i].Image.Save(Path.Combine(dir, "cursor" + i + ".png"), ImageFormat.Png);
                sb.AppendLine("shape" + i + "=" + shapes[i].HotX + "," + shapes[i].HotY);
            }
            File.WriteAllText(Path.Combine(dir, "sessao.txt"), sb.ToString());
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "mouse.bin"))))
            {
                lock (cursor)
                {
                    w.Write(cursor.Count);
                    foreach (var c in cursor) { w.Write(c.T); w.Write(c.X); w.Write(c.Y); w.Write(c.Shape); }
                }
                lock (clicks)
                {
                    w.Write(clicks.Count);
                    foreach (var c in clicks) { w.Write(c.T); w.Write(c.X); w.Write(c.Y); w.Write(c.Button); }
                }
            }
        }

        void Finished()
        {
            Current = null;
            foreach (var s in shapes) s.Image.Dispose();
            if (cancelled || error != null || frames == 0)
            {
                try { Directory.Delete(dir, true); } catch { }
                if (error != null) app.Notify("Erro ao gravar o vídeo", error.Message, null);
                else if (cancelled) app.Notify("Gravação cancelada", "Nenhum arquivo foi salvo.", null);
                return;
            }
            if (TrayApp.S.VidOpenEditor) app.OpenVideoEditor(dir);
            else app.QuickExportVideo(dir);
        }
    }

}
