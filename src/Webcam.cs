// Webcam: gravação junto com a tela (Media Foundation), bolha de prévia durante a gravação
// e leitura dos quadros no editor.
//
// Os quadros da câmera vão para "camera.bin" como JPEGs com o instante de cada um
// ([ms][tamanho][bytes]...). Assim o editor acha qualquer quadro na hora (prévia, arrastar a
// linha do tempo, exportar) sem precisar de um segundo decodificador de vídeo; quando a câmera
// é desligada no meio da gravação, simplesmente não há quadros naquele trecho e a bolha some.
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
    [ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFActivate
    {
        // IMFAttributes (30 métodos); só GetAllocatedString é usado
        void a0(); void a1(); void a2(); void a3(); void a4(); void a5(); void a6(); void a7(); void a8(); void a9();
        [PreserveSig] int GetAllocatedString([MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr value, out int length);
        void b1(); void b2(); void b3(); void b4(); void b5(); void b6(); void b7(); void b8(); void b9();
        void c0(); void c1(); void c2(); void c3(); void c4(); void c5(); void c6(); void c7(); void c8(); void c9();
        [PreserveSig] int ActivateObject([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr obj);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    static class Webcam
    {
        [DllImport("mf.dll", ExactSpelling = true)] static extern int MFEnumDeviceSources(IMFAttributes attr, out IntPtr devices, out int count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void MFCreateSourceReaderFromMediaSource(IntPtr source, IMFAttributes attr, out IMFSourceReader reader);

        static readonly Guid SOURCE_TYPE = new Guid("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
        static readonly Guid SOURCE_TYPE_VIDCAP = new Guid("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
        static readonly Guid FRIENDLY_NAME = new Guid("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
        public static readonly Guid IID_MediaSource = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");

        // Câmeras instaladas (nome amigável). Vazia se não houver nenhuma.
        public static List<string> Devices()
        {
            var names = new List<string>();
            foreach (var a in Enumerate())
            {
                names.Add(NameOf(a));
                MF.Release(a);
            }
            return names;
        }

        static string NameOf(IMFActivate a)
        {
            IntPtr s; int len;
            if (a.GetAllocatedString(FRIENDLY_NAME, out s, out len) != 0) return "Câmera";
            try { return Marshal.PtrToStringUni(s, len); }
            finally { Marshal.FreeCoTaskMem(s); }
        }

        static List<IMFActivate> Enumerate()
        {
            var list = new List<IMFActivate>();
            MF.Start();
            try
            {
                IMFAttributes attr; MF.MFCreateAttributes(out attr, 1);
                attr.SetGUID(SOURCE_TYPE, SOURCE_TYPE_VIDCAP);
                IntPtr arr; int n;
                int hr = MFEnumDeviceSources(attr, out arr, out n);
                MF.Release(attr);
                if (hr != 0 || arr == IntPtr.Zero) return list;
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                    list.Add((IMFActivate)Marshal.GetObjectForIUnknown(p));
                    Marshal.Release(p);
                }
                Marshal.FreeCoTaskMem(arr);
            }
            catch { }
            finally { MF.Stop(); }
            return list;
        }

        // A câmera pelo nome (ou a primeira, se o nome não existir mais).
        public static IMFActivate Find(string name)
        {
            var all = Enumerate();
            IMFActivate pick = null;
            foreach (var a in all) if (pick == null && NameOf(a) == name) pick = a;
            if (pick == null && all.Count > 0) pick = all[0];
            foreach (var a in all) if (a != pick) MF.Release(a);
            return pick;
        }
    }

    // ------------------------------------------------------------------ gravação
    sealed class WebcamCapture
    {
        public const int MaxWidth = 720;           // o suficiente para a bolha em 1080p, com arquivos leves
        readonly string name, path;
        readonly Stopwatch clock;
        Thread thread;
        volatile bool stop;
        public volatile bool On = true, Paused;
        public volatile bool Started, Failed;
        public string Error;
        public int Frames, Width, Height;           // tamanho dos quadros guardados
        public string Format = "";                  // formato pedido à câmera
        readonly object latestLock = new object();
        Bitmap latest; int latestSeq;               // último quadro (reduzido) para a bolha de prévia
        public event Action Ready;                  // a câmera abriu (chamado na thread da câmera)

        public WebcamCapture(string device, string path, Stopwatch clock)
        {
            name = device; this.path = path; this.clock = clock;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "webcam" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            if (thread != null) thread.Join(4000);
            lock (latestLock) { if (latest != null) latest.Dispose(); latest = null; }
        }

        // Cópia do último quadro para desenhar na bolha (null se ainda não chegou nenhum).
        public Bitmap Latest(ref int seq)
        {
            lock (latestLock)
            {
                if (latest == null || seq == latestSeq) return null;
                seq = latestSeq;
                return new Bitmap(latest);
            }
        }

        void Run()
        {
            IMFActivate dev = null;
            IntPtr source = IntPtr.Zero;
            IMFSourceReader r = null;
            MF.Start();
            try
            {
                dev = Webcam.Find(name);
                if (dev == null) throw new Exception("Nenhuma câmera encontrada.");
                int hr = dev.ActivateObject(Webcam.IID_MediaSource, out source);
                if (hr != 0) throw new Exception("A câmera não abriu (talvez esteja em uso por outro programa).");
                IMFAttributes attr; MF.MFCreateAttributes(out attr, 1);
                attr.SetUINT32(MF.SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                Webcam.MFCreateSourceReaderFromMediaSource(source, attr, out r);
                MF.Release(attr);
                int srcW, srcH, stride;
                ChooseFormat(r, out srcW, out srcH, out stride);
                Format = srcW + "x" + srcH;
                float k = Math.Min(1f, MaxWidth / (float)srcW);
                Width = Math.Max(2, (int)(srcW * k)) & ~1; Height = Math.Max(2, (int)(srcH * k)) & ~1;
                Started = true;
                if (Ready != null) Ready();
                Record(r, srcW, srcH, stride);
            }
            catch (Exception ex) { Failed = true; Error = ex.Message; TrayApp.Log("webcam: " + ex); }
            finally
            {
                if (r != null) MF.Release(r);
                if (dev != null) { try { dev.ShutdownObject(); } catch { } MF.Release(dev); }
                if (source != IntPtr.Zero) Marshal.Release(source);
                MF.Stop();
            }
        }

        // Escolhe o formato nativo mais próximo de 1280x720 a 30 fps e pede quadros em RGB32.
        static void ChooseFormat(IMFSourceReader r, out int w, out int h, out int stride)
        {
            var cands = new List<Tuple<int, int, IMFAttributes>>();   // pontuação, índice, tipo
            for (int i = 0; ; i++)
            {
                IMFAttributes t;
                if (r.GetNativeMediaType(MF.FIRST_VIDEO_STREAM, i, out t) != 0) break;
                ulong fs, fr;
                if (t.GetUINT64(MF.MT_FRAME_SIZE, out fs) != 0) { MF.Release(t); continue; }
                int tw = (int)(fs >> 32), th = (int)(fs & 0xffffffff);
                double fps = t.GetUINT64(MF.MT_FRAME_RATE, out fr) == 0 && (fr & 0xffffffff) != 0 ? (fr >> 32) / (double)(fr & 0xffffffff) : 30;
                // perto de 800 px de largura: nítido na bolha sem custar CPU para reduzir
                int score = Math.Abs(tw - 800) + (tw < 600 ? 400 : 0) + (tw > 1280 ? 1500 : 0) + (fps < 24 ? 4000 : 0) + (fps > 31 ? 300 : 0);
                cands.Add(Tuple.Create(score, i, t));
            }
            foreach (var c in cands.OrderBy(c => c.Item1).ThenBy(c => c.Item2))
            {
                try
                {
                    r.SetCurrentMediaType(MF.FIRST_VIDEO_STREAM, IntPtr.Zero, c.Item3);
                    IMFAttributes o; MF.MFCreateMediaType(out o);
                    o.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
                    o.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
                    try { r.SetCurrentMediaType(MF.FIRST_VIDEO_STREAM, IntPtr.Zero, o); }
                    finally { MF.Release(o); }
                    IMFAttributes cur;
                    if (r.GetCurrentMediaType(MF.FIRST_VIDEO_STREAM, out cur) != 0) continue;
                    ulong fs; cur.GetUINT64(MF.MT_FRAME_SIZE, out fs);
                    w = (int)(fs >> 32); h = (int)(fs & 0xffffffff);
                    int st; stride = cur.GetUINT32(MF.MT_DEFAULT_STRIDE, out st) == 0 ? st : w * 4;
                    MF.Release(cur);
                    foreach (var x in cands) MF.Release(x.Item3);
                    r.SetStreamSelection(MF.ALL_STREAMS, false);
                    r.SetStreamSelection(MF.FIRST_VIDEO_STREAM, true);
                    return;
                }
                catch { }
            }
            foreach (var x in cands) MF.Release(x.Item3);
            throw new Exception("A câmera não oferece um formato de imagem compatível.");
        }

        void Record(IMFSourceReader r, int srcW, int srcH, int stride)
        {
            ImageCodecInfo jpg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
            using (var bw = new BinaryWriter(fs))
            using (var small = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            using (var ms = new MemoryStream())
            {
                int lastT = -1000;
                while (!stop)
                {
                    int idx, flags; long ts; IMFSample s;
                    r.ReadSample(MF.FIRST_VIDEO_STREAM, 0, out idx, out flags, out ts, out s);
                    if ((flags & MF.SRF_ENDOFSTREAM) != 0) { MF.Release(s); break; }
                    if (s == null) continue;
                    int t = (int)clock.ElapsedMilliseconds;
                    try
                    {
                        // desligada ou em pausa: não guarda (a bolha some nesse trecho)
                        if (!On || Paused || !clock.IsRunning) continue;
                        if (t - lastT < 25) continue;     // no máximo ~40 por segundo
                        IMFMediaBuffer buf; s.ConvertToContiguousBuffer(out buf);
                        try
                        {
                            IntPtr p; int max, len;
                            buf.Lock(out p, out max, out len);
                            try
                            {
                                IntPtr top = stride < 0 ? p + (srcH - 1) * -stride : p;
                                using (var src = new Bitmap(srcW, srcH, stride, PixelFormat.Format32bppRgb, top))
                                using (var g = Graphics.FromImage(small))
                                {
                                    // bilinear simples: o de alta qualidade custa 2× mais e na bolha não se nota
                                    g.InterpolationMode = srcW == Width ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    g.CompositingMode = CompositingMode.SourceCopy;
                                    g.DrawImage(src, new Rectangle(0, 0, Width, Height));
                                }
                            }
                            finally { buf.Unlock(); }
                        }
                        finally { MF.Release(buf); }
                        ms.SetLength(0);
                        small.Save(ms, jpg, ep);
                        bw.Write(t); bw.Write((int)ms.Length); bw.Write(ms.GetBuffer(), 0, (int)ms.Length);
                        Frames++;
                        lastT = t;
                        lock (latestLock)
                        {
                            if (latest == null) latest = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
                            using (var g = Graphics.FromImage(latest)) g.DrawImageUnscaled(small, 0, 0);
                            latestSeq++;
                        }
                    }
                    finally { MF.Release(s); }
                }
            }
        }
    }

    // ------------------------------------------------------------------ bolha durante a gravação
    // Mostra o que a câmera está vendo, num círculo que dá para arrastar. Fica fora da gravação
    // (a câmera entra no vídeo pelo editor, onde dá para mudar posição, tamanho e formato).
    class CamBubble : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        readonly WebcamCapture cam;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
        Bitmap frame; int seq;
        bool off;
        public bool Off { get { return off; } set { off = value; Visible = !value; } }

        public CamBubble(WebcamCapture cam, Rectangle area, float k)
        {
            this.cam = cam;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(30, 30, 36);
            int d = (int)Math.Max(96, Math.Min(Math.Min(area.Width, area.Height) * 0.26f, 200 * k));
            int m = (int)(18 * k);
            // canto inferior direito da área (como o padrão do editor)
            var mon = Screen.FromRectangle(area).Bounds;
            int x = Math.Min(area.Right, mon.Right) - d - m, y = Math.Min(area.Bottom, mon.Bottom) - d - m;
            Bounds = new Rectangle(x, y, d, d);
            using (var path = new GraphicsPath()) { path.AddEllipse(0, 0, d, d); Region = new Region(path); }
            Cursor = Cursors.SizeAll;
            timer.Tick += (s, e) =>
            {
                var b = cam.Latest(ref seq);
                if (b == null) return;
                if (frame != null) frame.Dispose();
                frame = b;
                Invalidate();
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // TOOLWINDOW | NOACTIVATE
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            GifRecorder.ExcludeFromCapture(Handle);
            timer.Start();
        }

        // centro da bolha em relação à área gravada (0..1): vira a posição inicial no editor
        public PointF CenterIn(Rectangle area)
        {
            var b = Bounds;
            return new PointF((b.X + b.Width / 2f - area.X) / Math.Max(1, area.Width), (b.Y + b.Height / 2f - area.Y) / Math.Max(1, area.Height));
        }
        public bool Moved;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // arrasta como se fosse a barra de título
            Moved = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (frame == null)
            {
                Ui.Draw(g, cam.Failed ? "Câmera indisponível" : "Abrindo a câmera...", Ui.FontS, Color.Gainsboro, ClientRectangle, ContentAlignment.MiddleCenter);
                return;
            }
            // recorte quadrado no meio, espelhado (como um espelho)
            int s = Math.Min(frame.Width, frame.Height);
            var src = new Rectangle((frame.Width - s) / 2, (frame.Height - s) / 2, s, s);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.TranslateTransform(Width, 0); g.ScaleTransform(-1, 1);
            g.DrawImage(frame, new Rectangle(0, 0, Width, Height), src, GraphicsUnit.Pixel);
            g.ResetTransform();
            using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 2)) g.DrawEllipse(pen, 1, 1, Width - 3, Height - 3);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            if (frame != null) frame.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ leitura no editor
    sealed class CamTrack : IDisposable
    {
        readonly string path;
        int[] times; long[] offsets; int[] lengths;
        FileStream fs;
        int lastIndex = -1;
        Bitmap last;
        public int Width, Height;
        public int Count { get { return times.Length; } }

        CamTrack(string path) { this.path = path; }

        public static CamTrack Open(string path)
        {
            if (!File.Exists(path)) return null;
            var c = new CamTrack(path);
            var t = new List<int>(); var o = new List<long>(); var l = new List<int>();
            try
            {
                c.fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var br = new BinaryReader(c.fs);
                long len = c.fs.Length;
                while (c.fs.Position + 8 <= len)
                {
                    int ms = br.ReadInt32(), n = br.ReadInt32();
                    if (n <= 0 || c.fs.Position + n > len) break;
                    t.Add(ms); o.Add(c.fs.Position); l.Add(n);
                    c.fs.Seek(n, SeekOrigin.Current);
                }
            }
            catch { }
            if (t.Count == 0) { c.Dispose(); return null; }
            c.times = t.ToArray(); c.offsets = o.ToArray(); c.lengths = l.ToArray();
            using (var b = c.Decode(0)) { c.Width = b.Width; c.Height = b.Height; }
            return c;
        }

        // Outra leitura do mesmo arquivo (para exportar numa thread separada).
        public CamTrack Reopen() { return Open(path); }

        public int FirstTime { get { return times[0]; } }
        public int LastIndex { get { return lastIndex; } }   // quadro devolvido pelo último At

        Bitmap Decode(int i)
        {
            var data = new byte[lengths[i]];
            fs.Seek(offsets[i], SeekOrigin.Begin);
            int got = 0;
            while (got < data.Length) { int n = fs.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
            using (var img = new Bitmap(new MemoryStream(data)))
            {
                var b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(b)) g.DrawImageUnscaled(img, 0, 0);
                return b;
            }
        }

        // Quadro da câmera no instante t (ms da gravação). null onde a câmera estava desligada.
        public Bitmap At(double t)
        {
            int lo = 0, hi = times.Length - 1, idx = -1;
            if (t < times[0] - 250) return null;
            while (lo <= hi) { int m = (lo + hi) / 2; if (times[m] <= t) { idx = m; lo = m + 1; } else hi = m - 1; }
            if (idx < 0) idx = 0;
            if (t - times[idx] > 600) return null;   // buraco: a câmera foi desligada aqui
            if (idx == lastIndex && last != null) return last;
            try
            {
                var b = Decode(idx);
                if (last != null) last.Dispose();
                last = b; lastIndex = idx;
            }
            catch { }
            return last;
        }

        // Se a câmera aparece neste instante (para o fade de entrada/saída).
        public bool Visible(double t)
        {
            if (t < times[0] - 250) return false;
            int lo = 0, hi = times.Length - 1, idx = 0;
            while (lo <= hi) { int m = (lo + hi) / 2; if (times[m] <= t) { idx = m; lo = m + 1; } else hi = m - 1; }
            return t - times[idx] <= 600;
        }

        public void Dispose()
        {
            if (fs != null) fs.Dispose();
            if (last != null) last.Dispose();
            fs = null; last = null;
        }
    }
}
