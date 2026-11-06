// Editor da gravação de vídeo: prévia, linha do tempo (corte e trechos de zoom editáveis),
// estilo (zoom, cursor, fundo) e exportação para MP4 ou GIF.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    // Prévia: JPEGs com orçamento de memória; gravações grandes usam arquivo temporário.
    class PreviewCache
    {
        public readonly int Fps;
        readonly PreviewFrames frames = new PreviewFrames();
        public volatile int LoadedMs;
        public volatile bool Done;
        public string Error;
        volatile bool stop;
        public readonly int Width, Height;
        readonly string path;
        readonly int srcW, srcH;

        public PreviewCache(string path, int w, int h, int durationMs)
        {
            this.path = path; srcW = w; srcH = h;
            Fps = durationMs <= 120000 ? 30 : 15;     // gravações curtas: prévia fluida
            float k = Math.Min(1f, 1280f / Math.Max(w, h));
            Width = Math.Max(2, (int)(w * k)); Height = Math.Max(2, (int)(h * k));
            var t = new Thread(Run) { IsBackground = true, Name = "previa" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Stop()
        {
            stop = true;
            frames.Dispose();
            if (last != null) { last.Dispose(); last = null; }
        }

        void Run()
        {
            ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 84L);
            try
            {
                using (var r = new Mp4Reader(path, true, false))
                using (var full = new Bitmap(srcW, srcH, PixelFormat.Format32bppRgb))
                using (var small = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
                {
                    int next = 0;
                    while (!stop)
                    {
                        long time = 0;
                        bool ok = r.ReadFrame((p, pitch, t) =>
                        {
                            time = t;
                            if (t / 10000 + 2 < next) return;
                            var d = full.LockBits(new Rectangle(0, 0, srcW, srcH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                            int rows = Math.Min(srcH, r.Height), bytes = Math.Min(srcW, r.Width) * 4;
                            for (int y = 0; y < rows; y++) MF.Copy(d.Scan0 + y * d.Stride, p + y * pitch, (IntPtr)bytes);
                            full.UnlockBits(d);
                            using (var g = Graphics.FromImage(small))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.DrawImage(full, new Rectangle(0, 0, Width, Height));
                            }
                            using (var ms = new MemoryStream())
                            {
                                small.Save(ms, jpg, ep);
                                frames.Add((int)(t / 10000), ms.ToArray());
                            }
                            next = (int)(t / 10000) + 1000 / Fps;
                        });
                        if (!ok) break;
                        LoadedMs = (int)(time / 10000);
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { ep.Dispose(); Done = true; }
        }

        int lastIndex = -1;
        Bitmap last;

        public Bitmap At(int t)
        {
            int idx;
            byte[] data = frames.At(t, lastIndex, false, out idx);
            if (data == null) return idx == lastIndex ? last : null;
            using (var stream = new MemoryStream(data, false))
            using (var bmp = new Bitmap(stream))
            {
                var conv = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(conv)) g.DrawImageUnscaled(bmp, 0, 0);
                if (last != null) last.Dispose();
                last = conv; lastIndex = idx;
                return last;
            }
        }

        public Image Thumb(int t)
        {
            int idx;
            byte[] data = frames.At(t, -1, !Done, out idx);
            if (data == null) return null;
            using (var stream = new MemoryStream(data, false))
            using (var image = Image.FromStream(stream))
            {
                int w = Math.Max(1, 100 * Width / Height);
                var thumb = new Bitmap(w, 100, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(image, new Rectangle(0, 0, w, 100));
                }
                return thumb;
            }
        }
    }

    // Som da prévia: toca o áudio misturado a partir da posição atual (waveOut, já vem no Windows).
    sealed class AudioPreview : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        volatile float[] sys, mic;
        IntPtr h, hdr, data;

        public AudioPreview(VideoProject p)
        {
            string dir = p.Dir;
            bool hs = p.HasSys, hm = p.HasMic;
            if (!hs && !hm) return;
            new Thread(() =>
            {
                try
                {
                    if (hs) sys = RawAudio.LoadStereo(Path.Combine(dir, "sys.raw"), 48000);
                    if (hm) mic = RawAudio.LoadStereo(Path.Combine(dir, "mic.raw"), 48000);
                }
                catch { }
            }) { IsBackground = true, Name = "audio-previa" }.Start();
        }

        // Toca o som do vídeo final a partir do instante fromMs da gravação (com as velocidades).
        public void Play(VideoProject p, int fromMs)
        {
            Stop();
            float[] a = p.UseSys ? sys : null, b = p.UseMic ? mic : null;
            if (a == null && b == null) return;
            float[] mix;
            try { mix = p.MixAudio(fromMs, a, b); } catch { return; }
            if (mix == null || mix.Length < 960) return;
            var pcm = new short[mix.Length];
            for (int i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Max(-32767, Math.Min(32767, mix[i] * 32767));
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000, nAvgBytesPerSec = 48000 * 4, nBlockAlign = 4, wBitsPerSample = 16 };
            if (waveOutOpen(out h, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { h = IntPtr.Zero; return; }
            data = Marshal.AllocHGlobal(pcm.Length * 2);
            Marshal.Copy(pcm, 0, data, pcm.Length);
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            hdr = Marshal.AllocHGlobal(hs);
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = pcm.Length * 2 }, hdr, false);
            waveOutPrepareHeader(h, hdr, hs);
            waveOutWrite(h, hdr, hs);
        }

        public void Stop()
        {
            if (h == IntPtr.Zero) return;
            waveOutReset(h);
            waveOutUnprepareHeader(h, hdr, Marshal.SizeOf(typeof(WAVEHDR)));
            waveOutClose(h);
            Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
            h = hdr = data = IntPtr.Zero;
        }

        public void Dispose() { Stop(); }
    }

    class VideoEditorForm : Form
    {
        readonly TrayApp app;
        readonly VideoProject p;
        readonly PreviewCache cache;
        readonly AudioPreview sound;
        readonly DoubleBufferedPanel preview;
        readonly Timeline timeline;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly Stopwatch playClock = new Stopwatch();
        readonly Label timeLabel;
        readonly UiButton playBtn, exportBtn, cancelBtn, openBtn, folderBtn, discardBtn, undoBtn, redoBtn;
        readonly UiProgress bar;
        readonly UiNote status, exportInfo;
        readonly UiTabs tabs;
        readonly List<UiColumn> pages = new List<UiColumn>();
        readonly List<Action> refreshers = new List<Action>();
        int position;
        double playOutFrom;     // instante do vídeo final onde a reprodução começou
        bool playing, exported, suppress;
        Thread exportThread;
        volatile bool exportCancel;
        Bitmap frameBmp;
        int frameAt = -1;
        string lastSaved;
        public bool Quiet;                     // fechando junto com o app: guarda a gravação sem perguntar
        ZoomRegion selected;
        SpeedRegion selectedSpeed;

        // ---- desfazer
        class EditState { public List<ZoomRegion> Zooms; public List<SpeedRegion> Speeds; public int TrimStart, TrimEnd; public VideoStyle Style; public bool UseSys, UseMic; }
        readonly Stack<EditState> undo = new Stack<EditState>(), redo = new Stack<EditState>();
        EditState committed;

        EditState Snapshot()
        {
            return new EditState { Zooms = p.Zooms.Select(z => z.Clone()).ToList(), Speeds = p.Speeds.Select(z => z.Clone()).ToList(), TrimStart = p.TrimStart, TrimEnd = p.TrimEnd, Style = p.Style.Clone(), UseSys = p.UseSys, UseMic = p.UseMic };
        }

        public VideoProject Project { get { return p; } }
        public PreviewCache Cache { get { return cache; } }
        public int Position { get { return position; } }
        public ZoomRegion Selected { get { return selected; } }
        public SpeedRegion SelectedSpeed { get { return selectedSpeed; } }

        public VideoEditorForm(TrayApp app, string dir)
        {
            this.app = app;
            p = VideoProject.Load(dir);
            cache = new PreviewCache(p.RawVideo, p.W, p.H, p.Duration);
            sound = new AudioPreview(p);

            Text = "Editor de vídeo · Capturo";
            Icon = Art.AppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) Ui.K = Math.Max(1f, g.DpiX / 96f);
            Font = Ui.Font;
            BackColor = Ui.Bg; ForeColor = Ui.Text;
            KeyPreview = true;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(wa.Width - D(40), D(1280)), Math.Min(wa.Height - D(40), D(820)));
            MinimumSize = new Size(D(980), D(640));

            // ---------------- painel lateral
            var side = new Panel { Dock = DockStyle.Right, Width = D(340), BackColor = Ui.Panel };
            tabs = new UiTabs(new[] { "Edição", "Cursor", "Câmera", "Fundo", "Exportar" }) { Dock = DockStyle.Top };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Panel, Padding = new Padding(D(18), D(10), D(18), D(14)), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            int fw = D(340) - D(36);
            exportBtn = new UiButton("Exportar MP4", fw, D(42)) { Fill = Ui.Accent, Fore = Color.White, Margin = new Padding(0, 0, 0, D(6)) };
            cancelBtn = new UiButton("Cancelar exportação", fw, D(34)) { Visible = false };
            bar = new UiProgress(fw) { Visible = false };
            status = new UiNote("", fw) { Visible = false };
            var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Ui.Panel, Margin = new Padding(0), Visible = false };
            openBtn = new UiButton("Abrir", D(96), D(30)) { Margin = new Padding(0, 0, D(6), D(4)) };
            folderBtn = new UiButton("Mostrar na pasta", D(140), D(30)) { Margin = new Padding(0, 0, 0, D(4)) };
            actions.Controls.Add(openBtn); actions.Controls.Add(folderBtn);
            discardBtn = new UiButton("Descartar gravação", fw, D(30)) { Fill = Ui.Panel, Fore = Ui.Danger, Margin = new Padding(0, D(2), 0, 0) };
            footer.Controls.Add(exportBtn); footer.Controls.Add(cancelBtn); footer.Controls.Add(bar); footer.Controls.Add(status); footer.Controls.Add(actions); footer.Controls.Add(discardBtn);
            var sep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line };
            var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Panel };
            for (int i = 0; i < 5; i++) { var c = new UiColumn { Visible = i == 0 }; pages.Add(c); pageHost.Controls.Add(c); }
            side.Controls.Add(pageHost); side.Controls.Add(sep); side.Controls.Add(footer); side.Controls.Add(tabs);
            var sideLine = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Ui.Line };

            int cw = D(340) - D(32) - SystemInformation.VerticalScrollBarWidth - 4;
            BuildZoomPage(pages[0], cw);
            BuildCursorPage(pages[1], cw);
            BuildCameraPage(pages[2], cw);
            BuildFramePage(pages[3], cw);
            exportInfo = BuildExportPage(pages[4], cw);

            // ---------------- transporte e linha do tempo
            timeline = new Timeline(this) { Dock = DockStyle.Bottom, Height = D(186) };
            var transport = new Panel { Dock = DockStyle.Bottom, Height = D(52), BackColor = Ui.Bg };
            playBtn = new UiButton("▶", D(44), D(36)) { Location = new Point(D(16), D(8)), Fill = Ui.Panel2, BackColor = Ui.Bg, Font = new Font("Segoe UI Symbol", 11f) };
            timeLabel = new Label { AutoSize = false, Location = new Point(D(70), D(8)), Size = new Size(D(170), D(36)), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text, BackColor = Ui.Bg, Font = new Font("Segoe UI Semibold", 10.5f) };
            var addZoom = new UiButton("+ Zoom aqui", D(112), D(34)) { Location = new Point(D(244), D(9)), BackColor = Ui.Bg };
            var addSpeed = new UiButton("+ Velocidade", D(116), D(34)) { Location = new Point(D(362), D(9)), BackColor = Ui.Bg };
            undoBtn = new UiButton("↶", D(38), D(34)) { Location = new Point(D(486), D(9)), BackColor = Ui.Bg, Font = new Font("Segoe UI Symbol", 11f), Enabled = false };
            redoBtn = new UiButton("↷", D(38), D(34)) { Location = new Point(D(528), D(9)), BackColor = Ui.Bg, Font = new Font("Segoe UI Symbol", 11f), Enabled = false };
            var hint = new Label { AutoSize = false, Dock = DockStyle.Right, Width = D(330), TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.Text3, BackColor = Ui.Bg, Padding = new Padding(0, 0, D(16), 0),
                Text = "Espaço: tocar · Z: zoom · V: velocidade · Del: excluir" };
            var tip = new ToolTip();
            tip.SetToolTip(undoBtn, "Desfazer (Ctrl+Z)"); tip.SetToolTip(redoBtn, "Refazer (Ctrl+Y)");
            tip.SetToolTip(addZoom, "Cria um trecho de zoom na posição atual (Z)");
            tip.SetToolTip(addSpeed, "Cria um trecho acelerado na posição atual (V)");
            transport.Controls.Add(playBtn); transport.Controls.Add(timeLabel); transport.Controls.Add(addZoom); transport.Controls.Add(addSpeed);
            transport.Controls.Add(undoBtn); transport.Controls.Add(redoBtn); transport.Controls.Add(hint);
            addSpeed.Click += (s, e) => AddSpeedAt(position);

            preview = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(14, 14, 18) };
            preview.Paint += (s, e) => PaintPreview(e.Graphics);
            preview.Resize += (s, e) => { frameAt = -1; preview.Invalidate(); };
            preview.MouseDown += PreviewMouseDown;
            preview.MouseMove += PreviewMouseMove;
            preview.MouseUp += PreviewMouseUp;

            Controls.Add(preview);
            Controls.Add(transport);
            Controls.Add(timeline);
            Controls.Add(sideLine);
            Controls.Add(side);

            // ---------------- eventos
            tabs.SelectedChanged += () => { for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == tabs.Selected; };
            playBtn.Click += (s, e) => TogglePlay();
            addZoom.Click += (s, e) => AddZoomAt(position);
            undoBtn.Click += (s, e) => Undo();
            redoBtn.Click += (s, e) => Redo();
            exportBtn.Click += (s, e) => Export();
            cancelBtn.Click += (s, e) => exportCancel = true;
            discardBtn.Click += (s, e) =>
            {
                if (!exported && MessageBox.Show(this, "Descartar esta gravação? Não dá para desfazer.", "Capturo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                discard = true; Close();
            };
            openBtn.Click += (s, e) => { if (lastSaved != null) Services.Open(lastSaved); };
            folderBtn.Click += (s, e) => { if (lastSaved != null) try { Process.Start("explorer.exe", "/select,\"" + lastSaved + "\""); } catch { } };
            timeline.Seek += t => { Pause(); SetPosition(t); };
            timeline.Changed += () => { p.Invalidate(); Redraw(); };
            timeline.Commit += () => Commit();
            timeline.SelectionChanged += r => Select(r);
            timeline.SpeedSelectionChanged += r => SelectSpeed(r);

            tick.Interval = 15;
            tick.Tick += (s, e) => OnTick();
            tick.Start();
            committed = Snapshot();
            RefreshAll();
        }

        bool discard;
        int D(float v) { return Ui.D(v); }

        // ======================================================== páginas do painel
        UiSegmented zoomDepth, regionDepth;
        UiToggle regionFollow;
        UiNote regionInfo, zoomSummary;
        UiButton regionDelete;
        UiSection regionHeader;

        static readonly float[] Depths = { 1.25f, 1.5f, 1.8f, 2.2f, 3f };
        static readonly string[] DepthNames = { "1,25×", "1,5×", "1,8×", "2,2×", "3×" };
        static int DepthIndex(float d)
        {
            int best = 0;
            for (int i = 0; i < Depths.Length; i++) if (Math.Abs(Depths[i] - d) < Math.Abs(Depths[best] - d)) best = i;
            return best;
        }

        UiToggle Toggle(UiColumn col, string text, Func<bool> get, Action<bool> set, int w)
        {
            var t = new UiToggle(text, get(), w);
            t.CheckedChanged += () => { if (suppress) return; set(t.Checked); StyleEdited(true); };
            refreshers.Add(() => t.Checked = get());
            col.Controls.Add(t);
            return t;
        }

        UiSlider Slider(UiColumn col, string text, float min, float max, float step, Func<float> get, Action<float> set, Func<float, string> fmt, int w)
        {
            var s = new UiSlider(text, min, max, get(), step, w) { Format = fmt };
            s.ValueChanged += () => { if (suppress) return; set(s.Value); StyleEdited(false); };
            s.Committed += () => Commit();
            refreshers.Add(() => s.Value = get());
            col.Controls.Add(s);
            return s;
        }

        UiSegmented Segmented(UiColumn col, string label, string[] items, Func<int> get, Action<int> set, int w)
        {
            var s = new UiSegmented(label, items, get(), w);
            s.SelectedChanged += () => { if (suppress) return; set(s.Selected); StyleEdited(true); };
            refreshers.Add(() => s.Selected = get());
            col.Controls.Add(s);
            return s;
        }

        static string Pct(float v) { return (int)Math.Round(v * 100) + "%"; }
        static string Secs(float v) { return v.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + " s"; }

        void BuildZoomPage(UiColumn c, int w)
        {
            // bloco do trecho selecionado: fica no topo e só aparece com um trecho escolhido
            regionBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Panel, Margin = new Padding(0), Padding = new Padding(0), Visible = false };
            c.Controls.Add(regionBox);
            speedBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.Panel, Margin = new Padding(0), Padding = new Padding(0), Visible = false };
            c.Controls.Add(speedBox);
            speedBox.Controls.Add(new UiSection("Trecho de velocidade", w));
            speedInfo = new UiNote("", w);
            speedBox.Controls.Add(speedInfo);
            speedRate = new UiSegmented("Velocidade (vezes)", SpeedRegion.Rates.Select(r => SpeedRegion.Name(r).TrimEnd('×')).ToArray(), 3, w);
            speedRate.SelectedChanged += () =>
            {
                if (suppress || selectedSpeed == null) return;
                selectedSpeed.Rate = SpeedRegion.Rates[speedRate.Selected];
                p.Invalidate(); Commit(); SelectSpeed(selectedSpeed);
            };
            speedBox.Controls.Add(speedRate);
            var speedDel = new UiButton("Excluir trecho", w, D(32)) { Fore = Ui.Danger, Margin = new Padding(0, D(2), 0, D(8)) };
            speedDel.Click += (s, e) => DeleteSelected();
            speedBox.Controls.Add(speedDel);
            speedBox.Controls.Add(new Panel { Width = w, Height = 1, BackColor = Ui.Line, Margin = new Padding(0, D(2), 0, D(4)) });

            c.Controls.Add(new UiSection("Zoom automático", w));
            Toggle(c, "Aplicar os zooms", () => p.Style.Zoom, v => p.Style.Zoom = v, w);
            var motion = new UiToggle("Sugerir zoom onde o cursor para", p.Style.ZoomMotion, w);
            motion.CheckedChanged += () =>
            {
                if (suppress) return;
                p.Style.ZoomMotion = motion.Checked;
                p.BuildAutoZooms(); Select(null); StyleEdited(true);
            };
            refreshers.Add(() => motion.Checked = p.Style.ZoomMotion);
            c.Controls.Add(motion);
            zoomSummary = new UiNote("", w);
            c.Controls.Add(zoomSummary);
            zoomDepth = Segmented(c, "Profundidade", DepthNames, () => DepthIndex(p.Style.Depth), i =>
            {
                p.Style.Depth = Depths[i];
                foreach (var z in p.Zooms) if (z.Auto) z.Depth = z.Fit > 0 ? Math.Min(Depths[i], z.Fit) : Depths[i];   // os automáticos acompanham (sem passar do que cabe)
            }, w);
            // duração da animação: à direita fica mais lenta
            Slider(c, "Duração do zoom (entrar e sair)", 0.4f, 4f, 0.1f, () => p.Style.ZoomTime, v => p.Style.ZoomTime = v,
                v => (v < 0.9f ? "Rápida" : v < 2f ? "Suave" : v < 3f ? "Lenta" : "Bem lenta") + " · " + Secs(v), w);
            Slider(c, "Tempo em foco depois do clique", 0.3f, 8f, 0.1f, () => p.Style.ZoomHold,
                v => { p.Style.ZoomHold = v; p.BuildAutoZooms(); Select(null); }, Secs, w);
            Toggle(c, "Câmera segue o cursor", () => p.Style.Follow, v => p.Style.Follow = v, w);
            Toggle(c, "Deslizar entre zooms próximos", () => p.Style.Connect, v => p.Style.Connect = v, w);
            Slider(c, "Desfoque de movimento", 0, 1, 0.05f, () => p.Style.ZoomBlur, v => p.Style.ZoomBlur = v, Pct, w);
            var rebuild = new UiButton("Refazer as sugestões de zoom", w, D(34)) { Margin = new Padding(0, D(6), 0, D(4)) };
            rebuild.Click += (s, e) => { p.BuildAutoZooms(); Select(null); Commit(); Redraw(); };
            c.Controls.Add(rebuild);
            c.Controls.Add(new UiSection("Velocidade", w));
            c.Controls.Add(new UiNote("Acelere esperas (um carregamento, uma digitação longa) ou desacelere um detalhe. A voz mantém o tom.", w));
            var addSp = new UiButton("+ Trecho de velocidade aqui", w, D(34)) { Margin = new Padding(0, D(4), 0, D(4)) };
            addSp.Click += (s, e) => AddSpeedAt(position);
            c.Controls.Add(addSp);

            var c0 = c;
            c = null;
            Action<Control> add = ctl => regionBox.Controls.Add(ctl);
            regionHeader = new UiSection("Trecho selecionado", w);
            add(regionHeader);
            regionInfo = new UiNote("", w);
            add(regionInfo);
            regionDepth = new UiSegmented("Profundidade deste trecho", DepthNames, 2, w);
            regionDepth.SelectedChanged += () => { if (suppress || selected == null) return; selected.Depth = Depths[regionDepth.Selected]; selected.Auto = false; p.Invalidate(); Commit(); Redraw(); };
            add(regionDepth);
            regionFollow = new UiToggle("Seguir o cursor neste trecho", true, w);
            regionFollow.CheckedChanged += () => { if (suppress || selected == null) return; selected.Manual = !regionFollow.Checked; p.Invalidate(); Commit(); Redraw(); };
            add(regionFollow);
            add(new UiNote("Arraste o retângulo na prévia para escolher o ponto do zoom.", w));
            regionDelete = new UiButton("Excluir trecho", w, D(32)) { Fore = Ui.Danger, Margin = new Padding(0, D(2), 0, D(8)) };
            regionDelete.Click += (s, e) => DeleteSelected();
            add(regionDelete);
            add(new Panel { Width = w, Height = 1, BackColor = Ui.Line, Margin = new Padding(0, D(2), 0, D(4)) });
            c0.Controls.Add(new UiNote("Clique num trecho da linha do tempo para editá-lo. Arraste o trecho para mover e as bordas para mudar a duração; dê um duplo clique num espaço vazio da faixa (ou tecle Z ou V) para criar outro.", w));
        }

        FlowLayoutPanel regionBox, speedBox;
        UiNote speedInfo;
        UiSegmented speedRate;

        // ---------------- câmera
        void BuildCameraPage(UiColumn c, int w)
        {
            c.Controls.Add(new UiSection("Webcam", w));
            if (!p.HasCam)
            {
                c.Controls.Add(new UiNote("Esta gravação não tem câmera. Para gravar a webcam junto com a tela, ligue o botão da câmera na barra de gravação ou escolha a câmera em Configurações › Vídeo.", w));
                return;
            }
            Toggle(c, "Mostrar a câmera", () => p.Style.Cam, v => p.Style.Cam = v, w);
            Segmented(c, "Posição", new[] { "Inf. esq.", "Inf. dir.", "Sup. esq.", "Sup. dir." }, () => p.Style.CamPos < 4 ? p.Style.CamPos : -1, i => p.Style.CamPos = i, w);
            c.Controls.Add(new UiNote("Ou arraste a câmera na prévia para colocá-la em qualquer lugar.", w));
            Slider(c, "Tamanho", 0.12f, 0.45f, 0.01f, () => p.Style.CamSize, v => p.Style.CamSize = v, Pct, w);
            Slider(c, "Formato", 0, 1, 0.05f, () => p.Style.CamRound, v => p.Style.CamRound = v, v => v >= 0.99f ? "Círculo" : v <= 0.01f ? "Quadrado" : "Arredondado " + Pct(v), w);
            Slider(c, "Sombra", 0, 1, 0.05f, () => p.Style.CamShadow, v => p.Style.CamShadow = v, Pct, w);
            Toggle(c, "Espelhar (como num espelho)", () => p.Style.CamMirror, v => p.Style.CamMirror = v, w);
            Toggle(c, "Diminuir durante o zoom", () => p.Style.CamShrink, v => p.Style.CamShrink = v, w);
            c.Controls.Add(new UiNote("Onde a câmera foi desligada durante a gravação, ela some do vídeo.", w));

            c.Controls.Add(new UiSection("Efeitos", w));
            Segmented(c, "Fundo", new[] { "Normal", "Desfocar", "Cor", "Sem fundo" }, () => p.Style.CamBg, i => p.Style.CamBg = i, w);
            Slider(c, "Intensidade do desfoque", 0, 1, 0.05f, () => p.Style.CamBlur, v => p.Style.CamBlur = v, Pct, w);
            var bgColors = new ColorRow(this, new[] { 0xFF1E293B, 0xFFF1F5F9, 0xFF7B5CFA, 0xFF2563EB, 0xFF10B981, 0xFFF59E0B }, w, "Cor do fundo", () => p.Style.CamBgColor);
            bgColors.Picked += v => { p.Style.CamBgColor = v; if (p.Style.CamBg != 2) { p.Style.CamBg = 2; RefreshAll(); } StyleEdited(true); };
            refreshers.Add(() => bgColors.Invalidate());
            c.Controls.Add(bgColors);
            if (!Segmenter.Available)
                c.Controls.Add(new UiNote("Desfocar ou trocar o fundo precisa do Windows ML (Windows 10 de 2018 em diante)" + (Segmenter.Error != null ? ": " + Segmenter.Error : "."), w));
            else
                c.Controls.Add(new UiNote("A pessoa é separada do fundo por um modelo de IA que roda no seu computador. Nada é enviado para a internet.", w));
            // filtros em duas linhas (seis não cabem numa só); a escolhida numa linha apaga a da outra
            Segmented(c, "Filtro", new[] { "Nenhum", "Vívido", "P&B" }, () => p.Style.CamFilter <= 2 ? p.Style.CamFilter : -1, i => { p.Style.CamFilter = i; RefreshAll(); }, w);
            Segmented(c, null, new[] { "Sépia", "Quente", "Frio" }, () => p.Style.CamFilter >= 3 ? p.Style.CamFilter - 3 : -1, i => { p.Style.CamFilter = i + 3; RefreshAll(); }, w);
            Slider(c, "Brilho", -0.5f, 0.5f, 0.05f, () => p.Style.CamBright, v => p.Style.CamBright = v, v => (v > 0.001f ? "+" : "") + Pct(v), w);
            Slider(c, "Contorno", 0, 1, 0.05f, () => p.Style.CamBorder, v => p.Style.CamBorder = v, v => v <= 0.01f ? "Só um fio" : Pct(v), w);
            var borderColors = new ColorRow(this, new[] { 0xFFFFFFFF, 0xFF111827, 0xFF7B5CFA, 0xFF2563EB, 0xFFEC4899, 0xFFF59E0B }, w, "Cor do contorno", () => p.Style.CamBorderColor);
            borderColors.Picked += v => { p.Style.CamBorderColor = v; if (p.Style.CamBorder <= 0.01f) { p.Style.CamBorder = 0.4f; RefreshAll(); } StyleEdited(true); };
            refreshers.Add(() => borderColors.Invalidate());
            c.Controls.Add(borderColors);
        }

        void BuildCursorPage(UiColumn c, int w)
        {
            c.Controls.Add(new UiSection("Cursor", w));
            Toggle(c, "Mostrar o cursor", () => p.Style.Cursor, v => p.Style.Cursor = v, w);
            Slider(c, "Tamanho", 0.6f, 4f, 0.1f, () => p.Style.CursorSize, v => p.Style.CursorSize = v, Pct, w);
            Slider(c, "Suavidade do movimento", 0, 2, 0.05f, () => p.Style.Smoothing, v => p.Style.Smoothing = v, v => Pct(v / 2), w);
            Slider(c, "Inclinação ao mover", 0, 1, 0.05f, () => p.Style.Sway, v => p.Style.Sway = v, Pct, w);
            Slider(c, "Quique no clique", 0, 2, 0.1f, () => p.Style.Bounce, v => p.Style.Bounce = v, v => Pct(v / 2), w);
            Slider(c, "Rastro de movimento", 0, 1, 0.05f, () => p.Style.CursorBlur, v => p.Style.CursorBlur = v, Pct, w);
            Toggle(c, "Sombra no cursor", () => p.Style.CursorShadow, v => p.Style.CursorShadow = v, w);
            c.Controls.Add(new UiSection("Cliques", w));
            Segmented(c, "Efeito do clique", new[] { "Nenhum", "Anel", "Onda" }, () => p.Style.ClickFx, i => p.Style.ClickFx = i, w);
            var colors = new ColorRow(this, new[] { 0xFF2563EB, 0xFF7B5CFA, 0xFFEC4899, 0xFFF59E0B, 0xFF10B981, 0xFFFFFFFF }, w);
            colors.Picked += v => { p.Style.ClickColor = v; StyleEdited(true); };
            refreshers.Add(() => colors.Invalidate());
            c.Controls.Add(colors);
            var reset = new UiButton("Restaurar o padrão do cursor", w, D(32)) { Margin = new Padding(0, D(14), 0, 0) };
            reset.Click += (s, e) =>
            {
                var d = new VideoStyle();
                p.Style.Cursor = d.Cursor; p.Style.CursorSize = d.CursorSize; p.Style.Smoothing = d.Smoothing; p.Style.Sway = d.Sway;
                p.Style.Bounce = d.Bounce; p.Style.CursorBlur = d.CursorBlur; p.Style.CursorShadow = d.CursorShadow;
                p.Style.ClickFx = d.ClickFx; p.Style.ClickColor = d.ClickColor;
                StyleEdited(true); RefreshAll();
            };
            c.Controls.Add(reset);
        }

        UiSlider blurSlider;

        void BuildFramePage(UiColumn c, int w)
        {
            c.Controls.Add(new UiSection("Fundo", w));
            var sw = new Swatches(this, w);
            sw.Picked += i =>
            {
                if (i == VideoProject.BgImageIndex)
                {
                    using (var d = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos|*.*", Title = "Escolha a imagem de fundo" })
                    {
                        if (File.Exists(p.Style.BgImage)) d.FileName = p.Style.BgImage;
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        p.Style.BgImage = d.FileName;
                    }
                }
                // papel de parede pronto: nítido por padrão (dá para desfocar no controle abaixo)
                if (VideoProject.IsWallpaper(i) && !VideoProject.IsWallpaper(p.Style.Background)) p.Style.BgBlur = 0;
                p.Style.Background = i;
                StyleEdited(true); RefreshAll();
            };
            refreshers.Add(() => sw.Invalidate());
            c.Controls.Add(sw);
            blurSlider = Slider(c, "Desfoque da imagem", 0, 1, 0.05f, () => p.Style.BgBlur, v => p.Style.BgBlur = v, Pct, w);
            refreshers.Add(() => blurSlider.Visible = p.Style.Background == VideoProject.BgImageIndex || VideoProject.IsWallpaper(p.Style.Background));
            Slider(c, "Margem", 0, 0.25f, 0.01f, () => p.Style.Padding, v => p.Style.Padding = v, Pct, w);
            Slider(c, "Cantos arredondados", 0, 1, 0.05f, () => p.Style.Radius, v => p.Style.Radius = v, Pct, w);
            Slider(c, "Sombra", 0, 1, 0.05f, () => p.Style.Shadow, v => p.Style.Shadow = v, Pct, w);
            c.Controls.Add(new UiSection("Formato da tela", w));
            Segmented(c, null, VideoStyle.AspectNames, () => p.Style.Aspect, i => p.Style.Aspect = i, w);
            c.Controls.Add(new UiNote("16:9 serve para YouTube e apresentações; 9:16 para Reels, Shorts e TikTok; 1:1 para feeds.", w));
        }

        UiNote BuildExportPage(UiColumn c, int w)
        {
            c.Controls.Add(new UiSection("Formato", w));
            Segmented(c, null, new[] { "MP4 (vídeo)", "GIF" }, () => p.Style.Format, i => p.Style.Format = i, w);
            var mp4Res = Segmented(c, "Resolução", new[] { "Original", "1080p", "720p" }, () => p.Style.Res, i => p.Style.Res = i, w);
            var mp4Fps = Segmented(c, "Quadros por segundo", new[] { "30", "60" }, () => p.Style.OutFps >= 60 ? 1 : 0, i => p.Style.OutFps = i == 1 ? 60 : 30, w);
            var mp4Q = Segmented(c, "Qualidade", new[] { "Alta", "Média", "Menor" }, () => p.Style.Quality, i => p.Style.Quality = i, w);
            int[] gw = { 480, 640, 800, 960 }, gf = { 10, 15, 20 };
            var gifW = Segmented(c, "Largura do GIF", gw.Select(v => v + " px").ToArray(), () => Math.Max(0, Array.IndexOf(gw, p.Style.GifWidth)), i => p.Style.GifWidth = gw[i], w);
            var gifF = Segmented(c, "Quadros por segundo", gf.Select(v => v.ToString()).ToArray(), () => Math.Max(0, Array.IndexOf(gf, p.Style.GifFps)), i => p.Style.GifFps = gf[i], w);
            refreshers.Add(() =>
            {
                bool mp4 = p.Style.Format == 0;
                mp4Res.Visible = mp4Fps.Visible = mp4Q.Visible = mp4;
                gifW.Visible = gifF.Visible = !mp4;
            });
            var info = new UiNote("", w);
            c.Controls.Add(info);
            c.Controls.Add(new UiSection("Áudio", w));
            var sys = new UiToggle(p.HasSys ? "Som do computador" : "Som do computador (não gravado)", p.UseSys, w) { Enabled = p.HasSys };
            var mic = new UiToggle(p.HasMic ? "Microfone" : "Microfone (não gravado)", p.UseMic, w) { Enabled = p.HasMic };
            sys.CheckedChanged += () => { if (suppress) return; p.UseSys = sys.Checked; Commit(); if (playing) { Pause(); Play(); } };
            mic.CheckedChanged += () => { if (suppress) return; p.UseMic = mic.Checked; Commit(); if (playing) { Pause(); Play(); } };
            refreshers.Add(() => { sys.Checked = p.UseSys; mic.Checked = p.UseMic; });
            c.Controls.Add(sys); c.Controls.Add(mic);
            c.Controls.Add(new UiNote("O GIF não tem som. O vídeo e o GIF vão para a pasta de capturas e ficam copiados, prontos para colar.", w));
            return info;
        }

        // ======================================================== estado e atualização
        void StyleEdited(bool commit)
        {
            p.Style.Clamp();
            p.Invalidate();
            if (commit) Commit();
            RefreshLabels();
            Redraw();
        }

        void Commit()
        {
            var now = Snapshot();
            undo.Push(committed);
            committed = now;
            redo.Clear();
            p.RememberStyle();
            undoBtn.Enabled = true; redoBtn.Enabled = false;
            RefreshLabels();
        }

        void Restore(EditState s)
        {
            p.Zooms = s.Zooms.Select(z => z.Clone()).ToList();
            p.Speeds = s.Speeds.Select(z => z.Clone()).ToList();
            p.TrimStart = s.TrimStart; p.TrimEnd = s.TrimEnd;
            p.Style = s.Style.Clone();
            p.UseSys = s.UseSys; p.UseMic = s.UseMic;
            p.Invalidate();
            selected = null; selectedSpeed = null;
            RefreshAll();
            Redraw();
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Push(Snapshot());
            var s = undo.Pop();
            committed = s;
            Restore(s);
            undoBtn.Enabled = undo.Count > 0; redoBtn.Enabled = true;
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Push(Snapshot());
            var s = redo.Pop();
            committed = s;
            Restore(s);
            undoBtn.Enabled = true; redoBtn.Enabled = redo.Count > 0;
        }

        void RefreshAll()
        {
            suppress = true;
            try { foreach (var r in refreshers) r(); }
            finally { suppress = false; }
            if (selectedSpeed != null) SelectSpeed(selectedSpeed); else Select(selected);
            RefreshLabels();
        }

        void RefreshLabels()
        {
            int autos = p.Zooms.Count(z => z.Auto), manual = p.Zooms.Count - autos, byMotion = p.Zooms.Count(z => z.Auto && z.Motion);
            zoomSummary.SetText(p.Zooms.Count == 0
                ? (p.Clicks.Length == 0 && !p.Style.ZoomMotion ? "Nenhum clique na gravação. Use “+ Zoom aqui” ou dê um duplo clique na faixa de zoom da linha do tempo." : "Nenhum trecho de zoom.")
                : p.Zooms.Count + (p.Zooms.Count == 1 ? " trecho" : " trechos") + " na linha do tempo"
                    + (byMotion > 0 ? ": " + (autos - byMotion) + " pelos cliques, " + byMotion + " pelo movimento do cursor" : "")
                    + (manual > 0 ? (byMotion > 0 ? " e " : " (") + manual + " feito" + (manual == 1 ? "" : "s") + " à mão" + (byMotion > 0 ? "" : ")") : "") + ".");
            timeLabel.Text = Fmt((int)p.OutAt(position)) + " / " + Fmt((int)p.OutDuration);
            if (p.Style.Format == 0)
            {
                var sz = VideoExport.Mp4Size(p);
                int fps = p.Style.OutFps;
                double bpp = p.Style.Quality == 0 ? 0.16 : p.Style.Quality == 1 ? 0.10 : 0.06;
                double mbps = Math.Max(1.5e6, Math.Min(40e6, sz.Width * (double)sz.Height * fps * bpp));
                double mb = mbps * p.OutDuration / 1000.0 / 8 / 1048576 * 0.6;   // o H.264 costuma ficar abaixo do teto
                exportInfo.SetText("Saída: " + sz.Width + "×" + sz.Height + " · " + fps + " fps · cerca de " + (mb < 1 ? "<1" : mb.ToString("0")) + " MB");
                exportBtn.Text = "Exportar MP4";
            }
            else
            {
                var sz = VideoExport.GifSize(p);
                exportInfo.SetText("Saída: " + sz.Width + "×" + sz.Height + " · " + p.Style.GifFps + " fps · repete sem parar");
                exportBtn.Text = "Exportar GIF";
            }
        }

        void Redraw() { frameAt = -1; preview.Invalidate(); timeline.Invalidate(); }

        public static string Fmt(int ms)
        {
            ms = Math.Max(0, ms);
            return (ms / 60000).ToString("0") + ":" + (ms / 1000 % 60).ToString("00") + "," + (ms / 100 % 10);
        }

        // ======================================================== trechos de zoom
        public void Select(ZoomRegion r)
        {
            selected = r != null && p.Zooms.Contains(r) ? r : null;
            bool on = selected != null;
            if (on) { selectedSpeed = null; speedBox.Visible = false; }
            regionBox.Visible = on;
            if (on) pages[0].ScrollControlIntoView(regionBox);
            if (on)
            {
                suppress = true;
                regionDepth.Selected = DepthIndex(selected.Depth);
                regionFollow.Checked = !selected.Manual;
                suppress = false;
                regionInfo.SetText(Fmt(selected.Start) + " até " + Fmt(selected.End) + " · " + (!selected.Auto ? "editado à mão" : selected.Motion ? "sugerido pelo movimento do cursor" : "criado pelos cliques"));
                if (tabs.Selected != 0) tabs.Selected = 0;
            }
            Redraw();
        }

        public void SelectSpeed(SpeedRegion r)
        {
            selectedSpeed = r != null && p.Speeds.Contains(r) ? r : null;
            bool on = selectedSpeed != null;
            if (on) { selected = null; regionBox.Visible = false; }
            speedBox.Visible = on;
            if (on)
            {
                pages[0].ScrollControlIntoView(speedBox);
                suppress = true;
                int best = 0;
                for (int i = 0; i < SpeedRegion.Rates.Length; i++) if (Math.Abs(SpeedRegion.Rates[i] - r.Rate) < Math.Abs(SpeedRegion.Rates[best] - r.Rate)) best = i;
                speedRate.Selected = best;
                suppress = false;
                int len = r.End - r.Start;
                speedInfo.SetText(Fmt(r.Start) + " até " + Fmt(r.End) + " · " + SpeedRegion.Name(r.Rate) + ": " + Fmt(len) + " viram " + Fmt((int)(len / r.Rate)) + (r.Rate > 4.01f ? " (sem som)" : ""));
                if (tabs.Selected != 0) tabs.Selected = 0;
            }
            RefreshLabels();
            Redraw();
        }

        public void AddSpeedAt(int t)
        {
            int start = Math.Max(p.TrimStart, t), end = Math.Min(p.TrimEnd, start + 3000);
            foreach (var z in p.Speeds.OrderBy(z => z.Start))
            {
                if (z.Start <= t && t < z.End) { SelectSpeed(z); return; }
                if (z.Start > start && z.Start < end) end = z.Start;
            }
            if (end - start < 400) return;
            var r = new SpeedRegion { Start = start, End = end, Rate = 2f };
            p.Speeds.Add(r);
            p.SortSpeeds();
            p.Invalidate();
            Commit();
            RefreshAll();
            SelectSpeed(r);
        }

        public void AddZoomAt(int t)
        {
            int start = Math.Max(0, t - 300), end = Math.Min(p.Duration, start + 2500);
            // não sobrepõe: encaixa no espaço livre
            foreach (var z in p.Zooms.OrderBy(z => z.Start))
            {
                if (z.Start <= t && t < z.End) { Select(z); return; }
                if (z.Start > start && z.Start < end) end = z.Start;
                if (z.End > start && z.End <= t) start = z.End;
            }
            if (end - start < 400) return;
            int sh;
            var m = p.MouseAt(t, out sh);
            if (!p.InFrame(m.X, m.Y)) m = new PointF(p.W / 2f, p.H / 2f);
            var r = new ZoomRegion { Start = start, End = end, Depth = p.Style.Depth, X = m.X, Y = m.Y };
            p.Zooms.Add(r);
            p.Style.Zoom = true;
            p.SortZooms();
            Commit();
            RefreshAll();
            Select(r);
        }

        public void DeleteSelected()
        {
            if (selectedSpeed != null)
            {
                p.Speeds.Remove(selectedSpeed);
                p.Invalidate();
                selectedSpeed = null;
                Commit();
                SelectSpeed(null);
                return;
            }
            if (selected == null) return;
            p.Zooms.Remove(selected);
            p.Invalidate();
            selected = null;
            Commit();
            Select(null);
        }

        // ======================================================== reprodução
        void TogglePlay() { if (playing) Pause(); else Play(); }

        void Play()
        {
            if (position >= p.TrimEnd - 50) position = p.TrimStart;
            position = Math.Max(position, p.TrimStart);
            playOutFrom = p.OutAt(position);
            sound.Play(p, position);
            playClock.Restart();
            playing = true;
            playBtn.Text = "❚❚";
            Redraw();
        }

        void Pause()
        {
            if (!playing) return;
            playing = false;
            sound.Stop();
            playClock.Stop();
            playBtn.Text = "▶";
            Redraw();
        }

        public void SetPosition(int t)
        {
            position = Math.Max(0, Math.Min(p.Duration, t));
            frameAt = -1;
            preview.Invalidate(); timeline.Invalidate(); RefreshLabels();
        }

        int cacheShown = -1;

        void OnTick()
        {
            if (playing)
            {
                // o relógio anda no tempo do vídeo final; os trechos de velocidade mudam o da gravação
                double u = playOutFrom + playClock.ElapsedMilliseconds;
                int t = (int)p.SourceAt(u);
                if (u >= p.OutDuration || t >= p.TrimEnd) { t = p.TrimEnd; Pause(); }
                position = t;
                preview.Invalidate(); timeline.Invalidate();
                timeLabel.Text = Fmt((int)Math.Min(u, p.OutDuration)) + " / " + Fmt((int)p.OutDuration);
            }
            if (!cache.Done) { timeline.Invalidate(); if (frameAt == -1 || cacheShown < cache.LoadedMs) preview.Invalidate(); }
        }

        // ======================================================== prévia
        Rectangle previewRect;          // onde a tela de saída aparece no painel
        float previewScale;
        bool FocusMode { get { return selected != null && !playing; } }

        void PaintPreview(Graphics g)
        {
            g.Clear(preview.BackColor);
            var outSize = p.OutputSize;
            float s = Math.Min((preview.Width - D(48)) / (float)outSize.Width, (preview.Height - D(36)) / (float)outSize.Height);
            if (s <= 0) return;
            int w = Math.Max(2, (int)(outSize.Width * s)), h = Math.Max(2, (int)(outSize.Height * s));
            var dst = new Rectangle((preview.Width - w) / 2, (preview.Height - h) / 2, w, h);
            previewRect = dst; previewScale = s;
            var src = cache.At(position);
            if (src == null)
            {
                Ui.Draw(g, cache.Error != null ? "Não foi possível abrir a prévia: " + cache.Error : "Preparando a prévia...", Ui.Font, Ui.Text2, preview.ClientRectangle, ContentAlignment.MiddleCenter);
                return;
            }
            bool focus = FocusMode;
            int key = position * 2 + (focus ? 1 : 0);
            if (frameBmp == null || frameBmp.Width != w || frameBmp.Height != h || frameAt != key)
            {
                if (frameBmp == null || frameBmp.Width != w || frameBmp.Height != h)
                {
                    if (frameBmp != null) frameBmp.Dispose();
                    frameBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                }
                using (var fg = Graphics.FromImage(frameBmp))
                {
                    fg.ScaleTransform(w / (float)outSize.Width, h / (float)outSize.Height);
                    p.NoCamera = focus;
                    try { p.Render(fg, src, position, playing); }
                    finally { p.NoCamera = false; }
                }
                frameAt = key;
                cacheShown = cache.LoadedMs;
            }
            g.DrawImageUnscaled(frameBmp, dst.X, dst.Y);
            if (focus) DrawFocusBox(g);
            if (!cache.Done && position > cache.LoadedMs)
            {
                var r = new Rectangle(dst.X + D(12), dst.Y + D(12), D(190), D(28));
                Art.FillRound(g, Color.FromArgb(210, 20, 20, 26), r, D(6));
                Ui.Draw(g, "Preparando a prévia...", Ui.Font, Ui.Text, r, ContentAlignment.MiddleCenter);
            }
        }

        // Retângulo do enquadramento do trecho selecionado, em coordenadas do painel.
        RectangleF FocusBox()
        {
            var z = selected;
            var outSize = p.OutputSize;
            var content = p.ContentRect(outSize);
            float vw = p.W / z.Depth, vh = p.H / z.Depth;
            float fx = Math.Max(vw / 2, Math.Min(p.W - vw / 2, z.X)), fy = Math.Max(vh / 2, Math.Min(p.H - vh / 2, z.Y));
            return new RectangleF(previewRect.X + (content.X + fx - vw / 2) * previewScale, previewRect.Y + (content.Y + fy - vh / 2) * previewScale, vw * previewScale, vh * previewScale);
        }

        void DrawFocusBox(Graphics g)
        {
            var box = FocusBox();
            var content = p.ContentRect(p.OutputSize);
            var cr = new RectangleF(previewRect.X + content.X * previewScale, previewRect.Y + content.Y * previewScale, content.Width * previewScale, content.Height * previewScale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            using (var region = new Region(cr))
            {
                region.Exclude(box);
                g.FillRegion(dim, region);
            }
            using (var pen = new Pen(Color.White, D(2)))
            using (var path = Art.RoundRect(box, D(6)))
                g.DrawPath(pen, path);
            float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), D(1.5f)))
            {
                g.DrawLine(pen, cx - D(8), cy, cx + D(8), cy);
                g.DrawLine(pen, cx, cy - D(8), cx, cy + D(8));
            }
            var tag = new RectangleF(box.X + D(8), box.Y + D(8), D(150), D(24));
            Art.FillRound(g, Color.FromArgb(220, Ui.Accent), tag, D(6));
            Ui.Draw(g, "Zoom " + selected.Depth.ToString("0.##") + "× · arraste", Ui.FontS, Color.White, Rectangle.Round(tag), ContentAlignment.MiddleCenter);
        }

        bool draggingFocus, draggingCam;
        PointF focusGrab;

        // bolha da câmera na prévia (coordenadas do painel)
        RectangleF CamBoxOnPreview()
        {
            if (!p.CamVisible(position) || previewScale <= 0) return RectangleF.Empty;
            var r = p.CamRect(p.OutputSize, FocusMode ? 1 : p.ZoomAt(position));
            return new RectangleF(previewRect.X + r.X * previewScale, previewRect.Y + r.Y * previewScale, r.Width * previewScale, r.Height * previewScale);
        }

        void PreviewMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var cb = CamBoxOnPreview();
            if (!cb.IsEmpty && cb.Contains(e.Location))
            {
                Pause();
                draggingCam = true;
                focusGrab = new PointF(e.X - (cb.X + cb.Width / 2), e.Y - (cb.Y + cb.Height / 2));
                return;
            }
            if (FocusMode)
            {
                var box = FocusBox();
                if (box.Contains(e.Location))
                {
                    draggingFocus = true;
                    focusGrab = new PointF(e.X - (box.X + box.Width / 2), e.Y - (box.Y + box.Height / 2));
                    return;
                }
                // clicar fora: centraliza o enquadramento ali
                var content = p.ContentRect(p.OutputSize);
                float vx = (e.X - previewRect.X) / previewScale - content.X, vy = (e.Y - previewRect.Y) / previewScale - content.Y;
                if (p.InFrame(vx, vy)) { selected.X = vx; selected.Y = vy; selected.Auto = false; p.Invalidate(); draggingFocus = true; focusGrab = PointF.Empty; Redraw(); return; }
                Select(null);
                return;
            }
            TogglePlay();
        }

        void PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (draggingCam)
            {
                var outSize = p.OutputSize;
                p.Style.CamPos = 4;
                p.Style.CamX = (e.X - focusGrab.X - previewRect.X) / previewScale / outSize.Width;
                p.Style.CamY = (e.Y - focusGrab.Y - previewRect.Y) / previewScale / outSize.Height;
                p.Style.Clamp();
                Redraw();
                return;
            }
            var cb = CamBoxOnPreview();
            if (!cb.IsEmpty && cb.Contains(e.Location)) preview.Cursor = Cursors.SizeAll;
            else if (FocusMode) preview.Cursor = FocusBox().Contains(e.Location) || draggingFocus ? Cursors.SizeAll : Cursors.Cross;
            else preview.Cursor = Cursors.Hand;
            if (!draggingFocus || selected == null) return;
            var content = p.ContentRect(p.OutputSize);
            float vx = (e.X - focusGrab.X - previewRect.X) / previewScale - content.X, vy = (e.Y - focusGrab.Y - previewRect.Y) / previewScale - content.Y;
            float vw = p.W / selected.Depth, vh = p.H / selected.Depth;
            selected.X = Math.Max(vw / 2, Math.Min(p.W - vw / 2, vx));
            selected.Y = Math.Max(vh / 2, Math.Min(p.H - vh / 2, vy));
            selected.Auto = false;
            p.Invalidate();
            Redraw();
        }

        void PreviewMouseUp(object sender, MouseEventArgs e)
        {
            if (draggingCam)
            {
                draggingCam = false;
                // perto de um canto: encaixa na posição pronta
                var outSize = p.OutputSize;
                var r = p.CamRect(outSize, 1);
                float m = Math.Min(outSize.Width, outSize.Height) * 0.08f;
                bool left = r.Left < m, right = r.Right > outSize.Width - m, top = r.Top < m, bottom = r.Bottom > outSize.Height - m;
                if ((left || right) && (top || bottom)) p.Style.CamPos = bottom ? (left ? 0 : 1) : (left ? 2 : 3);
                StyleEdited(true); RefreshAll();
                return;
            }
            if (draggingFocus) { draggingFocus = false; Commit(); Select(selected); }
        }

        // ======================================================== exportação
        void Export()
        {
            if (exportThread != null) return;
            Pause();
            bool gif = p.Style.Format == 1;
            var job = p.CloneForExport();
            string path = Services.UniquePath(TrayApp.S.SaveFolder, gif ? "gif" : "mp4");
            exportCancel = false;
            exportBtn.Enabled = discardBtn.Enabled = false;
            cancelBtn.Visible = true; bar.Visible = true; bar.Value = 0;
            status.Visible = true; status.SetText(gif ? "Gerando o GIF..." : "Gerando o vídeo...");
            openBtn.Parent.Visible = false;
            var watch = Stopwatch.StartNew();
            exportThread = new Thread(() =>
            {
                string err = null;
                try
                {
                    VideoExport.Progress prog = f => app.RunOnUi(() =>
                    {
                        if (IsDisposed) return;
                        bar.Value = f;
                        double el = watch.Elapsed.TotalSeconds;
                        if (f > 0.04 && f < 1) status.SetText((gif ? "Gerando o GIF... " : "Gerando o vídeo... ") + (int)(f * 100) + "% · falta cerca de " + Math.Max(1, (int)(el / f - el)) + " s");
                    });
                    if (gif) VideoExport.Gif(job, path, prog, () => exportCancel);
                    else VideoExport.Mp4(job, path, prog, () => exportCancel);
                }
                catch (Exception ex) { err = ex.Message; TrayApp.Log("exportar vídeo: " + ex); }
                finally { job.Dispose(); }
                app.RunOnUi(() => ExportDone(path, gif, err));
            }) { IsBackground = true, Name = "exportar" };
            exportThread.SetApartmentState(ApartmentState.MTA);
            exportThread.Start();
        }

        void ExportDone(string path, bool gif, string err)
        {
            exportThread = null;
            if (IsDisposed) { if (exportCancel || err != null) TryDelete(path); return; }
            exportBtn.Enabled = discardBtn.Enabled = true;
            cancelBtn.Visible = false; bar.Visible = false;
            if (exportCancel || err != null)
            {
                TryDelete(path);
                status.SetText(err != null ? "Erro ao exportar: " + err : "Exportação cancelada.");
                return;
            }
            exported = true;
            lastSaved = path;
            long kb = 0;
            try { kb = new FileInfo(path).Length / 1024; } catch { }
            string size = kb > 2048 ? (kb / 1024.0).ToString("0.0") + " MB" : kb + " KB";
            status.SetText("Salvo: " + Path.GetFileName(path) + " (" + size + "). Já está copiado: é só colar.");
            openBtn.Parent.Visible = true;
            discardBtn.Text = "Fechar e apagar a gravação";
            try
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(path);
                Clipboard.SetFileDropList(files);
            }
            catch { }
            app.Notify((gif ? "GIF" : "Vídeo") + " salvo · " + size, "O arquivo foi copiado: é só colar no chat ou no e-mail. Clique para abrir.", path);
        }

        static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        // ======================================================== teclado e janela
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var ac = ActiveControl;
            bool inControl = ac is UiSlider || ac is UiToggle || ac is UiButton;
            if (e.KeyCode == Keys.Space && !(ac is UiToggle || ac is UiButton)) { TogglePlay(); e.Handled = e.SuppressKeyPress = true; }
            else if (inControl) return;
            else if (e.KeyCode == Keys.Left) { Pause(); SetPosition(position - (e.Shift ? 5000 : 1000)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Pause(); SetPosition(position + (e.Shift ? 5000 : 1000)); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Pause(); SetPosition(p.TrimStart); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { Pause(); SetPosition(p.TrimEnd); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Z && !e.Control) { AddZoomAt(position); e.Handled = true; }
            else if (e.KeyCode == Keys.V && !e.Control) { AddSpeedAt(position); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && selectedSpeed != null) { SelectSpeed(null); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && selected != null) { Select(null); e.Handled = true; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.ForceForeground(Handle);
            preview.Focus();
        }

        bool keep;   // guardar a gravação para abrir depois

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            if (exportThread != null)
            {
                if (!Quiet && MessageBox.Show(this, "A exportação ainda está em andamento. Cancelar e fechar?", "Capturo", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
                exportCancel = true;
            }
            if (Quiet) { keep = !exported; return; }
            if (!exported && !discard)
            {
                var r = CloseChoice.Ask(this);
                if (r == 0) { e.Cancel = true; return; }
                keep = r == 2;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop(); tick.Dispose();
            sound.Dispose();
            cache.Stop();
            p.RememberStyle();
            TrayApp.S.Save();
            p.Dispose();
            if (frameBmp != null) frameBmp.Dispose();
            string dir = p.Dir;
            if (keep)
            {
                try { File.WriteAllText(Path.Combine(dir, "guardada.txt"), DateTime.Now.ToString("s")); } catch { }
            }
            else
            {
                // apaga a gravação intermediária quando ninguém mais estiver lendo
                new Thread(() =>
                {
                    for (int i = 0; i < 40; i++)
                    {
                        if (cache.Done && exportThread == null) break;
                        Thread.Sleep(250);
                    }
                    Thread.Sleep(300);
                    try { Directory.Delete(dir, true); } catch { }
                }) { IsBackground = true }.Start();
            }
            base.OnFormClosed(e);
        }

        // ======================================================== peças pequenas
        class Swatches : UiControl
        {
            public event Action<int> Picked;
            readonly VideoEditorForm f;
            int hot = -1;
            const int Cols = 6;
            readonly int[] order = VideoProject.BackgroundOrder;   // posição na grade -> índice do fundo
            public Swatches(VideoEditorForm f, int width)
            {
                this.f = f; Width = width; Cursor = Cursors.Hand;
                int rows = (order.Length + Cols - 1) / Cols;
                Height = rows * (Cell(0).Height + Ui.D(8)) + Ui.D(22);
            }
            Rectangle Cell(int pos)
            {
                int gap = Ui.D(8), s = (Width - gap * (Cols - 1)) / Cols;
                return new Rectangle((pos % Cols) * (s + gap), (pos / Cols) * (s + gap), s, s);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Smooth(e);
                for (int pos = 0; pos < order.Length; pos++)
                {
                    int i = order[pos];
                    var r = Cell(pos); r.Inflate(-Ui.D(3), -Ui.D(3));
                    var pair = VideoProject.BackgroundPair(i);
                    Bitmap thumb = VideoProject.IsWallpaper(i) ? VideoProject.WallpaperThumb(i, r.Width) : null;
                    using (var path = Art.RoundRect(r, Ui.D(7)))
                    {
                        if (thumb != null)
                            using (var tb = new TextureBrush(thumb, WrapMode.Clamp))
                            {
                                tb.TranslateTransform(r.X, r.Y);
                                g.FillPath(tb, path);
                            }
                        else if (pair != null) using (var b = new LinearGradientBrush(r, pair[0], pair[1], 35f)) g.FillPath(b, path);
                        else
                        {
                            using (var b = new SolidBrush(Ui.Panel2)) g.FillPath(b, path);
                            using (var pen = new Pen(Ui.Text2, Ui.D(1.5f)))
                            {
                                if (i == 0) g.DrawLine(pen, r.Left + Ui.D(9), r.Bottom - Ui.D(9), r.Right - Ui.D(9), r.Top + Ui.D(9));
                                else
                                {
                                    // ícone de imagem
                                    var ir = new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.28f, r.Width * 0.56f, r.Height * 0.44f);
                                    g.DrawRectangle(pen, ir.X, ir.Y, ir.Width, ir.Height);
                                    g.DrawLines(pen, new[] { new PointF(ir.X, ir.Bottom - 2), new PointF(ir.X + ir.Width * 0.4f, ir.Y + ir.Height * 0.45f), new PointF(ir.X + ir.Width * 0.65f, ir.Bottom - ir.Height * 0.3f), new PointF(ir.Right, ir.Y + ir.Height * 0.4f) });
                                }
                            }
                        }
                    }
                    if (i == f.p.Style.Background || i == hot)
                    {
                        var o = Cell(pos);
                        using (var path = Art.RoundRect(new RectangleF(o.X + 1, o.Y + 1, o.Width - 2, o.Height - 2), Ui.D(9)))
                        using (var pen = new Pen(i == f.p.Style.Background ? Color.White : Ui.Text3, Ui.D(2)))
                            g.DrawPath(pen, path);
                    }
                }
                int idx = hot >= 0 ? hot : f.p.Style.Background;
                string name = VideoProject.BackgroundName(idx);
                if (idx == VideoProject.BgImageIndex && hot < 0 && File.Exists(f.p.Style.BgImage)) name = "Imagem: " + Path.GetFileName(f.p.Style.BgImage);
                Ui.Draw(g, name, Ui.Font, Ui.Text2, new Rectangle(0, Height - Ui.D(20), Width, Ui.D(20)), ContentAlignment.MiddleLeft);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = -1;
                for (int pos = 0; pos < order.Length; pos++) if (Cell(pos).Contains(e.Location)) h = order[pos];
                if (h != hot) { hot = h; Invalidate(); }
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (hot >= 0 && Picked != null) { Picked(hot); Invalidate(); }
            }
        }

        class ColorRow : UiControl
        {
            public event Action<int> Picked;
            readonly VideoEditorForm f;
            readonly int[] colors;
            readonly string label;
            readonly Func<int> current;
            public ColorRow(VideoEditorForm f, uint[] colors, int width) : this(f, colors, width, "Cor do efeito", null) { }
            public ColorRow(VideoEditorForm f, uint[] colors, int width, string label, Func<int> current)
            {
                this.f = f; this.colors = colors.Select(c => unchecked((int)c)).ToArray();
                this.label = label; this.current = current ?? (() => f.p.Style.ClickColor);
                Width = width; Height = Ui.D(52); Cursor = Cursors.Hand;
            }
            Rectangle Cell(int i) { int s = Ui.D(26), gap = Ui.D(10); return new Rectangle(i * (s + gap) + Ui.D(2), Ui.D(22), s, s); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = Smooth(e);
                Ui.Draw(g, label, Ui.Font, Ui.Text, new Rectangle(0, 0, Width, Ui.D(20)), ContentAlignment.MiddleLeft);
                for (int i = 0; i < colors.Length; i++)
                {
                    var r = Cell(i);
                    using (var b = new SolidBrush(Color.FromArgb(colors[i]))) g.FillEllipse(b, r);
                    if (colors[i] == current())
                        using (var pen = new Pen(Color.White, Ui.D(2))) g.DrawEllipse(pen, r.X - Ui.D(3), r.Y - Ui.D(3), r.Width + Ui.D(6), r.Height + Ui.D(6));
                }
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                for (int i = 0; i < colors.Length; i++)
                    if (Cell(i).Contains(e.Location) && Picked != null) { Picked(colors[i]); Invalidate(); }
            }
        }
    }

    // Pergunta ao fechar sem exportar: descartar, guardar para depois ou voltar.
    class CloseChoice : Form
    {
        int result;
        public static int Ask(IWin32Window owner)
        {
            using (var f = new CloseChoice()) { f.ShowDialog(owner); return f.result; }
        }
        CloseChoice()
        {
            Text = "Capturo";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.None;
            BackColor = Ui.Panel; ForeColor = Ui.Text; Font = Ui.Font;
            ClientSize = new Size(Ui.D(440), Ui.D(170));
            var title = new Label { Text = "O vídeo ainda não foi exportado", Font = new Font("Segoe UI Semibold", 11f), AutoSize = false, Location = new Point(Ui.D(20), Ui.D(18)), Size = new Size(Ui.D(400), Ui.D(26)), ForeColor = Ui.Text };
            var body = new Label { Text = "Se guardar, dá para abrir de novo pela bandeja (Gravações não exportadas) durante 3 dias.", AutoSize = false, Location = new Point(Ui.D(20), Ui.D(48)), Size = new Size(Ui.D(400), Ui.D(44)), ForeColor = Ui.Text2 };
            var back = new UiButton("Voltar", Ui.D(100), Ui.D(36)) { Location = new Point(Ui.D(20), Ui.D(114)) };
            var del = new UiButton("Descartar", Ui.D(120), Ui.D(36)) { Location = new Point(Ui.D(176), Ui.D(114)), Fore = Ui.Danger };
            var keep = new UiButton("Guardar", Ui.D(110), Ui.D(36)) { Location = new Point(Ui.D(306), Ui.D(114)), Fill = Ui.Accent, Fore = Color.White };
            back.Click += (s, e) => { result = 0; Close(); };
            del.Click += (s, e) => { result = 1; Close(); };
            keep.Click += (s, e) => { result = 2; Close(); };
            Controls.AddRange(new Control[] { title, body, back, del, keep });
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { result = 0; Close(); } };
        }
    }

    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }
}
