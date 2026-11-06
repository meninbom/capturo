// Gravação de GIF: captura a área ~10 vezes por segundo e codifica em segundo plano.
// Uma moldura vermelha marca a área e uma barra mostra o tempo, com Parar/Cancelar.
// As duas janelas são excluídas da captura (Windows 10 2004+) e ficam fora da área.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    class GifRecorder
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public const int Fps = 10, MaxSeconds = 300;
        public static GifRecorder Current;

        readonly TrayApp app;
        readonly Rectangle area;
        readonly RecFrame frame;
        readonly RecBar bar;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly Stopwatch clock = new Stopwatch();
        readonly BlockingCollection<Tuple<Bitmap, long>> queue = new BlockingCollection<Tuple<Bitmap, long>>(12);
        readonly string path;
        long endMs;
        volatile bool cancelled;
        bool stopping;
        int frames;
        Exception error;

        public static void ExcludeFromCapture(IntPtr hwnd)
        {
            try { SetWindowDisplayAffinity(hwnd, 0x11); } catch { } // WDA_EXCLUDEFROMCAPTURE
        }

        public static void Start(TrayApp app, Rectangle area)
        {
            if (Current != null) return;
            Current = new GifRecorder(app, area);
        }

        GifRecorder(TrayApp app, Rectangle area)
        {
            this.app = app; this.area = area;
            path = Services.UniquePath(TrayApp.S.SaveFolder, "gif");
            frame = new RecFrame(area);
            bar = new RecBar(area);
            bar.StopClicked += () => Stop(false);
            bar.CancelClicked += () => Stop(true);
            frame.Show(); bar.Show();
            ExcludeFromCapture(frame.Handle); ExcludeFromCapture(bar.Handle);

            new Thread(Encode) { IsBackground = true, Name = "gif" }.Start();
            tick.Interval = 1000 / Fps;
            tick.Tick += (s, e) => Grab();
            clock.Start();
            tick.Start();
            Grab();
        }

        void Grab()
        {
            if (stopping) return;
            long ms = clock.ElapsedMilliseconds;
            bar.SetTime(ms);
            if (ms >= MaxSeconds * 1000L) { Stop(false); return; }
            if (queue.Count >= queue.BoundedCapacity - 1) return; // codificador atrasado: pula o quadro
            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                    DrawCursor(g);
                }
                queue.Add(Tuple.Create(bmp, ms));
            }
            catch { bmp.Dispose(); }
        }

        void DrawCursor(Graphics g)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref ci) || ci.flags != 1 || ci.hCursor == IntPtr.Zero) return;
            int hx = 0, hy = 0;
            ICONINFO ii;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            int x = ci.pt.X - area.X - hx, y = ci.pt.Y - area.Y - hy;
            if (x > area.Width || y > area.Height || x < -64 || y < -64) return;
            IntPtr hdc = g.GetHdc();
            try { DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(hdc); }
        }

        void Encode()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
                using (var gw = new GifWriter(fs, area.Width, area.Height))
                {
                    foreach (var item in queue.GetConsumingEnumerable())
                    {
                        if (!cancelled) gw.AddFrame(item.Item1, item.Item2);
                        item.Item1.Dispose();
                    }
                    if (!cancelled) gw.Finish(endMs);
                    frames = gw.Frames;
                }
            }
            catch (Exception ex) { error = ex; }
            app.RunOnUi(Finished);
        }

        public void Stop(bool cancel)
        {
            if (stopping) return;
            stopping = true;
            cancelled = cancel;
            tick.Stop();
            endMs = clock.ElapsedMilliseconds;
            frame.Close(); bar.Close();
            queue.CompleteAdding();
            if (!cancel) app.Notify("Gravação encerrada", "Gerando o GIF...", null);
        }

        void Finished()
        {
            Current = null;
            tick.Dispose();
            if (cancelled || error != null || frames == 0)
            {
                try { File.Delete(path); } catch { }
                if (error != null) app.Notify("Erro ao gravar GIF", error.Message, null);
                else if (cancelled) app.Notify("Gravação cancelada", "Nenhum arquivo foi salvo.", null);
                return;
            }
            app.GifSaved(path, endMs);
        }
    }

    // Moldura vermelha em volta da área gravada (o clique atravessa).
    class RecFrame : Form
    {
        const int B = 3;
        bool paused;
        public bool Paused { get { return paused; } set { paused = value; Invalidate(); } }   // amarela enquanto pausado
        public bool Armed;   // azul: área escolhida, ainda não está gravando
        public RecFrame(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            var r = area; r.Inflate(B, B);
            Bounds = r;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Magenta);
            using (var br = new SolidBrush(Armed ? Art.PickBlue : paused ? Color.FromArgb(250, 200, 60) : Color.FromArgb(235, 30, 50)))
            {
                g.FillRectangle(br, 0, 0, Width, B);
                g.FillRectangle(br, 0, Height - B, Width, B);
                g.FillRectangle(br, 0, 0, B, Height);
                g.FillRectangle(br, Width - B, 0, B, Height);
            }
        }
    }

    // Barra com o tempo de gravação e os botões Parar / Cancelar.
    class RecBar : Form
    {
        public event Action StopClicked, CancelClicked, ToggleChanged, PauseClicked;
        readonly float k;
        readonly Rectangle stopR, cancelR, micR, sysR, pauseR, camR;
        bool paused;
        public bool Paused { get { return paused; } set { paused = value; Invalidate(); } }
        readonly bool video, camera;
        int hot = -1;
        string time = "00:00";
        bool blink = true;
        readonly Font font, fontB;
        public bool MicOn, SysOn, CamOn, HasMic = true, HasSys = true, HasCam = true;
        float micLevel, sysLevel;

        int D(float v) { return (int)Math.Round(v * k); }

        public RecBar(Rectangle area) : this(area, false, false) { }
        public RecBar(Rectangle area, bool video) : this(area, video, false) { }

        // video: mostra também os botões de microfone e de som do sistema (e da webcam, se houver)
        public RecBar(Rectangle area, bool video, bool camera)
        {
            this.video = video; this.camera = video && camera;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            font = new Font("Segoe UI", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fontB = new Font("Segoe UI Semibold", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            int extra = video ? D(this.camera ? 148 : 112) : 0;
            int w = D(300) + extra, h = D(44);
            var mon = Screen.FromRectangle(area).WorkingArea;
            int x = Math.Max(mon.Left, Math.Min(area.Left + (area.Width - w) / 2, mon.Right - w));
            int y = area.Bottom + D(10);
            if (y + h > mon.Bottom) y = area.Top - h - D(10);
            if (y < mon.Top) y = area.Bottom - h - D(12);   // área ocupa a tela toda: a barra fica por dentro (e fora da gravação)
            Bounds = new Rectangle(x, y, w, h);
            using (var path = Art.RoundRect(new RectangleF(0, 0, w, h), h / 2f)) Region = new Region(path);
            micR = new Rectangle(D(124), D(7), D(32), D(30));
            sysR = new Rectangle(D(160), D(7), D(32), D(30));
            camR = new Rectangle(D(196), D(7), D(32), D(30));
            pauseR = new Rectangle(D(this.camera ? 232 : 196), D(7), D(32), D(30));
            stopR = new Rectangle(D(124) + extra, D(7), D(88), D(30));
            cancelR = new Rectangle(D(218) + extra, D(7), D(74), D(30));
            Cursor = Cursors.Hand;
        }

        public void SetLevels(float mic, float sys)
        {
            if (Math.Abs(mic - micLevel) > 0.02f || Math.Abs(sys - sysLevel) > 0.02f) { micLevel = mic; sysLevel = sys; Invalidate(); }
        }

        int HitAt(Point p)
        {
            if (stopR.Contains(p)) return 0;
            if (cancelR.Contains(p)) return 1;
            if (video && micR.Contains(p) && HasMic) return 2;
            if (video && sysR.Contains(p) && HasSys) return 3;
            if (video && pauseR.Contains(p)) return 4;
            if (camera && camR.Contains(p) && HasCam) return 5;
            return -1;
        }

        // kind: 0 microfone, 1 som do computador, 2 webcam
        void DrawToggle(Graphics g, Rectangle r, int kind, bool on, bool avail, bool hover, float level) { DrawToggle(g, r, kind, on, avail, hover, level, k); }

        // compartilhado com o painel de preparação (PreRecBar)
        internal static void DrawToggle(Graphics g, Rectangle r, int kind, bool on, bool avail, bool hover, float level, float k)
        {
            Func<float, int> D = v => (int)Math.Round(v * k);
            if (hover) Art.FillRound(g, Color.FromArgb(60, 60, 72), r, D(8));
            Color fg = !avail ? Color.FromArgb(80, 80, 90) : on ? Color.White : Color.FromArgb(130, 130, 140);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f - D(1);
            using (var pen = new Pen(fg, D(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var br = new SolidBrush(fg))
            {
                if (kind == 2)
                {
                    using (var body = Art.RoundRect(new RectangleF(cx - D(8.5f), cy - D(5), D(12), D(10)), D(2.5f))) g.FillPath(br, body);
                    g.FillPolygon(br, new[] { new PointF(cx + D(4.5f), cy - D(1.5f)), new PointF(cx + D(9), cy - D(4.5f)), new PointF(cx + D(9), cy + D(4.5f)), new PointF(cx + D(4.5f), cy + D(1.5f)) });
                }
                else if (kind == 0)
                {
                    using (var cap = Art.RoundRect(new RectangleF(cx - D(3.5f), cy - D(8), D(7), D(11)), D(3.5f))) g.FillPath(br, cap);
                    g.DrawArc(pen, cx - D(6), cy - D(5), D(12), D(11), 0, 180);
                    g.DrawLine(pen, cx, cy + D(6), cx, cy + D(8.5f));
                }
                else
                {
                    g.FillPolygon(br, new[] { new PointF(cx - D(8), cy - D(3)), new PointF(cx - D(4.5f), cy - D(3)), new PointF(cx + D(0.5f), cy - D(7.5f)),
                                              new PointF(cx + D(0.5f), cy + D(7.5f)), new PointF(cx - D(4.5f), cy + D(3)), new PointF(cx - D(8), cy + D(3)) });
                    g.DrawArc(pen, cx - D(3), cy - D(4), D(8), D(8), -55, 110);
                    g.DrawArc(pen, cx - D(5), cy - D(7.5f), D(14), D(15), -55, 110);
                }
                if (!on || !avail)
                    using (var slash = new Pen(Color.FromArgb(235, 30, 50), D(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(slash, cx - D(8), cy - D(8), cx + D(8), cy + D(8));
            }
            if (on && avail && level > 0.01f)
                using (var lb = new SolidBrush(Color.FromArgb(70, 210, 120)))
                    g.FillRectangle(lb, r.X + D(6), r.Bottom - D(3), (r.Width - D(12)) * Math.Min(1f, level * 1.6f), D(2));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } // TOOLWINDOW | NOACTIVATE
        }

        public void SetTime(long ms)
        {
            long sec = ms / 1000;
            string t = (sec / 60).ToString("00") + ":" + (sec % 60).ToString("00");
            bool b = (ms / 500) % 2 == 0;
            if (t != time || b != blink) { time = t; blink = b; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 24, 30));
            float r = D(5.5f);
            Color dc = paused ? Color.FromArgb(250, 200, 60) : blink ? Color.FromArgb(235, 30, 50) : Color.FromArgb(90, 235, 30, 50);
            using (var dot = new SolidBrush(dc))
                g.FillEllipse(dot, D(20) - r, Height / 2f - r, r * 2, r * 2);
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString((paused ? "PAUSA " : "REC  ") + time, fontB, Brushes.White, new RectangleF(D(32), 0, D(92), Height), sf);
            if (video)
            {
                DrawToggle(g, micR, 0, MicOn, HasMic, hot == 2, micLevel);
                DrawToggle(g, sysR, 1, SysOn, HasSys, hot == 3, sysLevel);
                if (camera) DrawToggle(g, camR, 2, CamOn, HasCam, hot == 5, 0);
                if (hot == 4) Art.FillRound(g, Color.FromArgb(60, 60, 72), pauseR, D(8));
                float cx = pauseR.X + pauseR.Width / 2f, cy = pauseR.Y + pauseR.Height / 2f;
                if (paused)
                    g.FillPolygon(Brushes.White, new[] { new PointF(cx - D(4), cy - D(6.5f)), new PointF(cx + D(6), cy), new PointF(cx - D(4), cy + D(6.5f)) });
                else
                {
                    g.FillRectangle(Brushes.White, cx - D(5), cy - D(6), D(3.5f), D(12));
                    g.FillRectangle(Brushes.White, cx + D(1.5f), cy - D(6), D(3.5f), D(12));
                }
            }
            DrawBtn(g, stopR, "■  Parar", hot == 0, Color.FromArgb(235, 30, 50));
            DrawBtn(g, cancelR, "Cancelar", hot == 1, Color.FromArgb(70, 70, 80));
        }

        void DrawBtn(Graphics g, Rectangle r, string text, bool hover, Color bg)
        {
            Art.FillRound(g, hover ? ControlPaint.Light(bg, 0.25f) : bg, r, r.Height / 2f);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, Brushes.White, r, sf);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hot)
            {
                hot = h; Invalidate();
                if (video) tip.SetToolTip(this, h == 2 ? (MicOn ? "Silenciar o microfone" : "Gravar o microfone") : h == 3 ? (SysOn ? "Silenciar o som do computador" : "Gravar o som do computador")
                    : h == 4 ? (paused ? "Continuar a gravação" : "Pausar a gravação") : h == 5 ? (CamOn ? "Desligar a webcam" : "Gravar a webcam") : "");
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h == 0 && StopClicked != null) StopClicked();
            else if (h == 1 && CancelClicked != null) CancelClicked();
            else if (h == 4 && PauseClicked != null) PauseClicked();
            else if (h == 2 || h == 3 || h == 5)
            {
                if (h == 2) MicOn = !MicOn; else if (h == 3) SysOn = !SysOn; else CamOn = !CamOn;
                Invalidate();
                if (ToggleChanged != null) ToggleChanged();
            }
        }

        readonly ToolTip tip = new ToolTip();

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            font.Dispose(); fontB.Dispose(); tip.Dispose();
            base.OnFormClosed(e);
        }
    }

    // Painel que aparece depois de escolher a área do vídeo e antes da contagem:
    // liga/desliga microfone, som do computador e webcam, escolhe a câmera e a contagem, e começa a gravar.
    class PreRecBar : Form
    {
        public event Action StartClicked, CancelClicked;
        readonly float k;
        readonly Font font, fontB, fontS;
        readonly System.Collections.Generic.List<string> cams;
        readonly Rectangle micR, sysR, camR, camMoreR, countR, recR, cancelR;
        int hot = -1;
        bool done;
        readonly ToolTip tip = new ToolTip();

        public bool MicOn, SysOn, CamOn;
        public string CamName;
        public int Countdown;   // 0, 3 ou 5 s
        public bool HasCam { get { return cams.Count > 0; } }

        int D(float v) { return (int)Math.Round(v * k); }

        public PreRecBar(Rectangle area, System.Collections.Generic.List<string> cams)
        {
            this.cams = cams ?? new System.Collections.Generic.List<string>();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            font = new Font("Segoe UI", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fontB = new Font("Segoe UI Semibold", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fontS = new Font("Segoe UI", 12 * k, FontStyle.Regular, GraphicsUnit.Pixel);

            // [mic][som][cam▾] | Contagem 3 s | ● Gravar  Cancelar
            int x = D(10), y = D(9), ih = D(34);
            micR = new Rectangle(x, y, D(36), ih); x += D(38);
            sysR = new Rectangle(x, y, D(36), ih); x += D(38);
            camR = new Rectangle(x, y, D(36), ih); x += D(36);
            camMoreR = this.cams.Count > 1 ? new Rectangle(x, y, D(16), ih) : Rectangle.Empty;
            x += this.cams.Count > 1 ? D(18) : D(2);
            x += D(10);
            countR = new Rectangle(x, y, D(118), ih); x += D(118) + D(12);
            recR = new Rectangle(x, y, D(100), ih); x += D(100) + D(6);
            cancelR = new Rectangle(x, y, D(82), ih); x += D(82) + D(10);
            int w = x, h = D(52);

            var mon = Screen.FromRectangle(area).WorkingArea;
            int bx = Math.Max(mon.Left, Math.Min(area.Left + (area.Width - w) / 2, mon.Right - w));
            int by = area.Bottom + D(12);
            if (by + h > mon.Bottom) by = area.Top - h - D(12);
            if (by < mon.Top) by = area.Bottom - h - D(24);   // área ocupa a tela toda: fica por dentro, embaixo
            Bounds = new Rectangle(bx, by, w, h);
            using (var path = Art.RoundRect(new RectangleF(0, 0, w, h), h / 2f)) Region = new Region(path);
            Cursor = Cursors.Hand;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.ForceForeground(Handle);   // Enter grava, Esc cancela
        }

        int HitAt(Point p)
        {
            if (micR.Contains(p)) return 0;
            if (sysR.Contains(p)) return 1;
            if (camR.Contains(p)) return 2;
            if (!camMoreR.IsEmpty && camMoreR.Contains(p)) return 3;
            if (countR.Contains(p)) return 4;
            if (recR.Contains(p)) return 5;
            if (cancelR.Contains(p)) return 6;
            return -1;
        }

        string Tip(int h)
        {
            switch (h)
            {
                case 0: return MicOn ? "Microfone ligado (clique para não gravar)" : "Microfone desligado (clique para gravar)";
                case 1: return SysOn ? "Som do computador ligado (clique para não gravar)" : "Som do computador desligado (clique para gravar)";
                case 2: return !HasCam ? "Nenhuma webcam encontrada" : CamOn ? "Webcam ligada: " + CamName + " (clique para não gravar)" : "Webcam desligada (clique para gravar)";
                case 3: return "Escolher a webcam";
                case 4: return "Contagem antes de começar (clique para mudar)";
                case 5: return "Começar a gravar (Enter)";
                case 6: return "Cancelar (Esc)";
            }
            return "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 24, 30));

            RecBar.DrawToggle(g, micR, 0, MicOn, true, hot == 0, 0, k);
            RecBar.DrawToggle(g, sysR, 1, SysOn, true, hot == 1, 0, k);
            RecBar.DrawToggle(g, camR, 2, CamOn && HasCam, HasCam, hot == 2 && HasCam, 0, k);
            if (!camMoreR.IsEmpty)
            {
                if (hot == 3) Art.FillRound(g, Color.FromArgb(60, 60, 72), camMoreR, D(6));
                float cx = camMoreR.X + camMoreR.Width / 2f, cy = camMoreR.Y + camMoreR.Height / 2f;
                using (var pen = new Pen(Color.FromArgb(190, 190, 200), D(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[] { new PointF(cx - D(3.5f), cy - D(1.5f)), new PointF(cx, cy + D(2)), new PointF(cx + D(3.5f), cy - D(1.5f)) });
            }

            // separador
            using (var sep = new Pen(Color.FromArgb(60, 60, 70), 1))
                g.DrawLine(sep, countR.X - D(6), D(14), countR.X - D(6), Height - D(14));

            // contagem: ícone de cronômetro + texto
            if (hot == 4) Art.FillRound(g, Color.FromArgb(60, 60, 72), countR, D(8));
            float tx = countR.X + D(16), ty = countR.Y + countR.Height / 2f + D(1);
            using (var pen = new Pen(Color.White, D(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, tx - D(6.5f), ty - D(6.5f), D(13), D(13));
                g.DrawLine(pen, tx, ty, tx, ty - D(4));
                g.DrawLine(pen, tx - D(2.5f), ty - D(9.5f), tx + D(2.5f), ty - D(9.5f));
            }
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(Color.FromArgb(225, 225, 232)))
                g.DrawString(Countdown <= 0 ? "Sem contagem" : "Contagem: " + Countdown + " s", fontS, br,
                    new RectangleF(countR.X + D(28), countR.Y, countR.Width - D(28), countR.Height), sf);

            DrawBtn(g, recR, "●  Gravar", hot == 5, Color.FromArgb(235, 30, 50), fontB);
            DrawBtn(g, cancelR, "Cancelar", hot == 6, Color.FromArgb(70, 70, 80), font);
        }

        void DrawBtn(Graphics g, Rectangle r, string text, bool hover, Color bg, Font f)
        {
            Art.FillRound(g, hover ? ControlPaint.Light(bg, 0.25f) : bg, r, r.Height / 2f);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, Brushes.White, r, sf);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hot) { hot = h; Invalidate(); tip.SetToolTip(this, Tip(h)); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            switch (HitAt(e.Location))
            {
                case 0: MicOn = !MicOn; break;
                case 1: SysOn = !SysOn; break;
                case 2:
                    if (!HasCam) return;
                    CamOn = !CamOn;
                    if (CamOn && (string.IsNullOrEmpty(CamName) || !cams.Contains(CamName))) CamName = cams[0];
                    break;
                case 3: ShowCamMenu(); return;
                case 4: Countdown = Countdown <= 0 ? 3 : Countdown <= 3 ? 5 : 0; break;
                case 5: Finish(true); return;
                case 6: Finish(false); return;
                default: return;
            }
            tip.SetToolTip(this, Tip(hot));
            Invalidate();
        }

        void ShowCamMenu()
        {
            var m = new ContextMenuStrip();
            var off = new ToolStripMenuItem("Não gravar a webcam") { Checked = !CamOn };
            off.Click += (s, e) => { CamOn = false; Invalidate(); };
            m.Items.Add(off);
            m.Items.Add(new ToolStripSeparator());
            foreach (var c in cams)
            {
                string name = c;
                var it = new ToolStripMenuItem(name) { Checked = CamOn && name == CamName };
                it.Click += (s, e) => { CamOn = true; CamName = name; Invalidate(); };
                m.Items.Add(it);
            }
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, new Point(camR.X, Height));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter) { e.Handled = true; Finish(true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; Finish(false); }
        }

        void Finish(bool start)
        {
            if (done) return;
            done = true;
            Close();
            var h = start ? StartClicked : CancelClicked;
            if (h != null) h();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // fechou sem escolher (Alt+F4 etc.): conta como cancelar
            if (!done) { done = true; if (CancelClicked != null) CancelClicked(); }
            font.Dispose(); fontB.Dispose(); fontS.Dispose(); tip.Dispose();
            base.OnFormClosed(e);
        }
    }

    // Contagem regressiva da captura com atraso. Some da tela antes da captura acontecer.
    class CountdownForm : Form
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public event Action Done;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly float k;
        readonly Font big, small;
        int left, ticks;
        readonly string label;

        int D(float v) { return (int)Math.Round(v * k); }

        public CountdownForm(int seconds) : this(seconds, "Capturando em", Rectangle.Empty) { }

        // area: aparece no meio dela (gravação); vazia: embaixo da tela do mouse
        public CountdownForm(int seconds, string text, Rectangle area)
        {
            left = seconds;
            label = text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            big = new Font("Segoe UI Semibold", 30 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            small = new Font("Segoe UI", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            int w = D(230), h = D(64);
            var mon = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = area.IsEmpty || area.Width < w || area.Height < h
                ? new Rectangle(mon.Left + (mon.Width - w) / 2, mon.Bottom - h - D(28), w, h)
                : new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            using (var path = Art.RoundRect(new RectangleF(0, 0, w, h), D(14))) Region = new Region(path);
            // a janela não recebe o teclado (não rouba o foco do que vai ser capturado):
            // o Esc é lido direto do teclado, a cada 100 ms
            timer.Interval = 100;
            timer.Tick += (s, e) =>
            {
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { timer.Stop(); Close(); return; }   // Esc: cancela
                if (++ticks % 10 != 0) return;
                left--;
                if (left > 0) { Invalidate(); return; }
                timer.Stop();
                Close();
                // dá tempo da janela sumir antes de fotografar a tela
                var t = new System.Windows.Forms.Timer { Interval = 120 };
                t.Tick += (s2, e2) => { t.Dispose(); if (Done != null) Done(); };
                t.Start();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000; return cp; } // TOOLWINDOW | NOACTIVATE | TRANSPARENT | LAYERED
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            GifRecorder.ExcludeFromCapture(Handle);
            Opacity = 0.94;
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 24, 30));
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                g.DrawString(label, small, Brushes.Gainsboro, new RectangleF(D(22), 0, D(130), Height * 0.62f), sf);
                using (var hint = new Font(small.FontFamily, small.Size * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var hb = new SolidBrush(Color.FromArgb(150, 150, 165)))
                    g.DrawString("Esc cancela", hint, hb, new RectangleF(D(22), Height * 0.5f, D(130), Height * 0.42f), sf);
                using (var acc = new SolidBrush(Color.FromArgb(160, 130, 255)))
                    g.DrawString(left.ToString(), big, acc, new RectangleF(D(150), 0, D(70), Height), sf);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose(); big.Dispose(); small.Dispose();
            base.OnFormClosed(e);
        }
    }
}
