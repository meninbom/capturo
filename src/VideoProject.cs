// Projeto de vídeo: estilo, trechos de zoom, câmera, cursor e a montagem de cada quadro.
// A câmera e o cursor seguem as ideias do Recordly (zoom que "mergulha" no ponto com curva
// de saída rápida, câmera que só anda quando o cursor sai de uma zona segura, cursor suavizado
// sem atraso, zooms próximos encadeados com um deslize), escritas aqui do zero.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Capturo
{
    // ------------------------------------------------------------------ estilo (fica salvo)
    class VideoStyle
    {
        // zoom
        public bool Zoom = true;            // aplicar os trechos de zoom
        public float Depth = 1.8f;          // profundidade dos zooms novos
        public int Speed = 1;               // (antigo: 0 = rápida, 1 = suave; virou ZoomTime)
        public float ZoomTime = 1.5f;       // duração da entrada do zoom, em segundos (0,4 rápida .. 4 bem lenta)
        public float ZoomHold = 0.5f;       // quanto o zoom fica no foco depois do último clique (s)
        public bool Follow = true;          // câmera acompanha o cursor dentro do zoom
        public bool Connect = true;         // zooms próximos deslizam de um ponto ao outro
        public float ZoomBlur = 0.35f;      // desfoque de movimento da câmera (0..1)
        public bool ZoomMotion = true;      // sugere zoom também onde o cursor para depois de andar
        // webcam
        public bool Cam = true;             // mostrar a câmera (se a gravação tiver)
        public float CamSize = 0.24f;       // 0.12..0.45 do lado menor do vídeo
        public int CamPos = 1;              // 0 inf. esq., 1 inf. dir., 2 sup. esq., 3 sup. dir., 4 livre (CamX, CamY)
        public float CamX = 0.85f, CamY = 0.8f;
        public float CamRound = 1f;         // 0 quadrado .. 1 círculo
        public bool CamMirror = true;
        public float CamShadow = 0.6f;
        public bool CamShrink = true;       // fica menor durante o zoom
        public int CamBg = 0;               // fundo: 0 normal, 1 desfocado, 2 uma cor, 3 sem fundo (pessoa recortada)
        public float CamBlur = 0.6f;        // intensidade do desfoque do fundo
        public int CamBgColor = unchecked((int)0xFF1E293B);
        public int CamFilter = 0;           // 0 nenhum, 1 vívido, 2 P&B, 3 sépia, 4 quente, 5 frio
        public float CamBright = 0;         // -0,5..0,5
        public float CamBorder = 0;         // contorno: 0 (só um fio) .. 1 (grosso)
        public int CamBorderColor = unchecked((int)0xFFFFFFFF);
        // cursor
        public bool Cursor = true;
        public float CursorSize = 2f;       // 1..4 (× o tamanho do sistema)
        public float Smoothing = 0.67f;     // 0..2 (quanto o cursor é suavizado)
        public float Sway = 0.4f;           // 0..1 inclinação conforme a velocidade
        public float Bounce = 1f;           // 0..2 quique no clique
        public float CursorBlur = 0.5f;     // 0..1 rastro do cursor
        public bool CursorShadow = true;
        public int ClickFx = 1;             // 0 nenhum, 1 anel, 2 onda
        public int ClickColor = unchecked((int)0xFF2563EB);
        // moldura
        public int Background = 1;          // índice em Backgrounds; BgImageIndex = imagem própria
        public string BgImage = "";
        public float BgBlur = 0.5f;
        public float Padding = 0.08f;       // 0..0.25 do lado maior
        public float Radius = 0.3f;         // 0..1
        public float Shadow = 0.6f;         // 0..1
        public int Aspect = 0;              // 0 original, 1 16:9, 2 9:16, 3 1:1, 4 4:3
        // exportação
        public int Format = 0;              // 0 MP4, 1 GIF
        public int Res = 0;                 // 0 original, 1 1080p, 2 720p
        public int OutFps = 30;
        public int Quality = 0;             // 0 alta, 1 média, 2 arquivo menor
        public int GifWidth = 800;
        public int GifFps = 15;

        public static readonly string[] AspectNames = { "Original", "16:9", "9:16", "1:1", "4:3" };

        public string Serialize()
        {
            var sb = new StringBuilder();
            // ordem fixa: o .NET não garante a ordem de GetFields
            foreach (var f in typeof(VideoStyle).GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                object v = f.GetValue(this);
                string s = v is float ? ((float)v).ToString("R", CultureInfo.InvariantCulture)
                    : v is bool ? ((bool)v ? "1" : "0") : Uri.EscapeDataString(Convert.ToString(v, CultureInfo.InvariantCulture));
                sb.Append(f.Name).Append('=').Append(s).Append(';');
            }
            return sb.ToString();
        }

        public static VideoStyle Parse(string text)
        {
            var st = new VideoStyle();
            if (string.IsNullOrEmpty(text)) return st;
            foreach (var part in text.Split(';'))
            {
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                var f = typeof(VideoStyle).GetField(part.Substring(0, i), BindingFlags.Public | BindingFlags.Instance);
                if (f == null) continue;
                string v = part.Substring(i + 1);
                try
                {
                    if (f.FieldType == typeof(bool)) f.SetValue(st, v == "1");
                    else if (f.FieldType == typeof(int)) f.SetValue(st, int.Parse(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(float)) f.SetValue(st, float.Parse(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(string)) f.SetValue(st, Uri.UnescapeDataString(v));
                }
                catch { }
            }
            // estilos salvos antes da duração ajustável: "Rápida" era 0,6 s
            if (text.IndexOf("ZoomTime=", StringComparison.Ordinal) < 0 && st.Speed == 0) st.ZoomTime = 0.6f;
            st.Clamp();
            return st;
        }

        public void Clamp()
        {
            Depth = Math.Max(1.1f, Math.Min(4f, Depth));
            CursorSize = Math.Max(0.6f, Math.Min(4f, CursorSize));
            Smoothing = Math.Max(0, Math.Min(2, Smoothing));
            Sway = Math.Max(0, Math.Min(1, Sway)); Bounce = Math.Max(0, Math.Min(2, Bounce));
            CursorBlur = Math.Max(0, Math.Min(1, CursorBlur)); ZoomBlur = Math.Max(0, Math.Min(1, ZoomBlur));
            Padding = Math.Max(0, Math.Min(0.25f, Padding)); Radius = Math.Max(0, Math.Min(1, Radius));
            Shadow = Math.Max(0, Math.Min(1, Shadow)); BgBlur = Math.Max(0, Math.Min(1, BgBlur));
            Background = Math.Max(0, Math.Min(VideoProject.BackgroundCount - 1, Background));
            Aspect = Math.Max(0, Math.Min(4, Aspect)); Format = Format == 1 ? 1 : 0; Res = Math.Max(0, Math.Min(2, Res));
            OutFps = OutFps >= 60 ? 60 : 30; Quality = Math.Max(0, Math.Min(2, Quality));
            GifFps = Math.Max(5, Math.Min(30, GifFps)); GifWidth = Math.Max(240, Math.Min(1920, GifWidth));
            ClickFx = Math.Max(0, Math.Min(2, ClickFx)); Speed = Speed == 0 ? 0 : 1;
            ZoomTime = Math.Max(0.4f, Math.Min(4f, ZoomTime)); ZoomHold = Math.Max(0.3f, Math.Min(8f, ZoomHold));
            CamSize = Math.Max(0.12f, Math.Min(0.45f, CamSize)); CamPos = Math.Max(0, Math.Min(4, CamPos));
            CamX = Math.Max(0, Math.Min(1, CamX)); CamY = Math.Max(0, Math.Min(1, CamY));
            CamRound = Math.Max(0, Math.Min(1, CamRound)); CamShadow = Math.Max(0, Math.Min(1, CamShadow));
            CamBg = Math.Max(0, Math.Min(3, CamBg)); CamBlur = Math.Max(0, Math.Min(1, CamBlur)); CamFilter = Math.Max(0, Math.Min(5, CamFilter));
            CamBright = Math.Max(-0.5f, Math.Min(0.5f, CamBright)); CamBorder = Math.Max(0, Math.Min(1, CamBorder));
        }

        public VideoStyle Clone() { return (VideoStyle)MemberwiseClone(); }
    }

    class ZoomRegion
    {
        public int Start, End;      // ms do vídeo gravado
        public float Depth;
        public float X, Y;          // ponto de foco (pixels do vídeo)
        public bool Manual;         // foco fixo (não segue o cursor)
        public bool Auto;           // criado a partir dos cliques
        public bool Motion;         // sugerido pelo movimento do cursor (parou depois de andar)
        public float Fit;           // automáticos: profundidade máxima para os cliques do trecho caberem (0 = sem limite)
        public ZoomRegion Clone() { return (ZoomRegion)MemberwiseClone(); }
    }

    // Trecho com outra velocidade (acelerar uma espera, desacelerar um detalhe).
    class SpeedRegion
    {
        public int Start, End;      // ms do vídeo gravado
        public float Rate = 2f;     // 2 = duas vezes mais rápido
        public SpeedRegion Clone() { return (SpeedRegion)MemberwiseClone(); }
        public static readonly float[] Rates = { 0.5f, 0.75f, 1.5f, 2f, 3f, 4f, 8f };
        public static string Name(float r) { return r.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + "×"; }
    }

    // ------------------------------------------------------------------ projeto
    class VideoProject
    {
        public string Dir;
        public int W, H, Fps, Duration;
        public bool HasSys, HasMic;
        public CursorSample[] Cursor;
        public ClickMark[] Clicks;
        public List<CursorShape> Shapes = new List<CursorShape>();
        public List<ZoomRegion> Zooms = new List<ZoomRegion>();
        public VideoStyle Style = new VideoStyle();
        public int TrimStart, TrimEnd;
        public bool UseSys = true, UseMic = true;
        public List<SpeedRegion> Speeds = new List<SpeedRegion>();
        public CamTrack Cam;                 // webcam (null se a gravação não tiver)
        public string Capture = "";          // como a tela foi gravada (para mostrar no editor)
        public bool WindowCapture;           // gravou uma janela (e não uma área da tela)

        public string RawVideo { get { return Path.Combine(Dir, "tela.mp4"); } }

        // ---- fundos: sem fundo, degradês, imagem própria e papéis de parede prontos (embutidos no .exe)
        static readonly string[] BaseNames = { "Sem fundo", "Violeta", "Aurora", "Oceano", "Pôr do sol", "Floresta", "Pêssego", "Meia-noite", "Grafite", "Claro", "Imagem..." };
        static readonly int[][] BackgroundColors = {
            null,
            new[] { 0x7B5CFA, 0x3BA7F5 }, new[] { 0xEC4899, 0x8B5CF6 }, new[] { 0x1B8FB8, 0x65D8E8 }, new[] { 0xFF7A59, 0xF43F86 },
            new[] { 0x1F9D6B, 0x9BD86A }, new[] { 0xFBC2A4, 0xF78CA0 }, new[] { 0x0F172A, 0x334155 }, new[] { 0x26272D, 0x26272D },
            new[] { 0xF2F4F8, 0xD8DDE6 }, null };
        public const int BgImageIndex = 10, WallStart = 11;
        static readonly string[][] WallInfo = {
            new[] { "aurora", "Papel de parede: Aurora" }, new[] { "sunset", "Papel de parede: Entardecer" }, new[] { "ocean", "Papel de parede: Oceano" },
            new[] { "forest", "Papel de parede: Floresta" }, new[] { "graphite", "Papel de parede: Grafite" }, new[] { "candy", "Papel de parede: Doce" },
            new[] { "dune", "Papel de parede: Dunas" }, new[] { "midnight", "Papel de parede: Meia-noite" }, new[] { "citrus", "Papel de parede: Cítrico" },
            new[] { "mono-light", "Papel de parede: Papel claro" } };
        static List<string[]> walls;   // os que estão embutidos: recurso, nome
        static List<string[]> Walls
        {
            get
            {
                if (walls != null) return walls;
                var res = new HashSet<string>(typeof(VideoProject).Assembly.GetManifestResourceNames());
                walls = WallInfo.Where(w => res.Contains("Capturo.wall." + w[0] + ".jpg")).Select(w => new[] { "Capturo.wall." + w[0] + ".jpg", w[1] }).ToList();
                return walls;
            }
        }
        public static int BackgroundCount { get { return WallStart + Walls.Count; } }
        public static string BackgroundName(int i) { return i < WallStart ? BaseNames[i] : Walls[i - WallStart][1]; }
        public static bool IsWallpaper(int i) { return i >= WallStart && i < BackgroundCount; }
        // ordem na grade: degradês, papéis de parede e por último a imagem própria
        public static int[] BackgroundOrder
        {
            get
            {
                var o = new List<int>();
                for (int i = 0; i < BgImageIndex; i++) o.Add(i);
                for (int i = WallStart; i < BackgroundCount; i++) o.Add(i);
                o.Add(BgImageIndex);
                return o.ToArray();
            }
        }

        static readonly Dictionary<int, Image> wallCache = new Dictionary<int, Image>();
        static readonly Dictionary<int, Bitmap> wallThumbs = new Dictionary<int, Bitmap>();
        static readonly object wallLock = new object();

        // Papel de parede decodificado (fica em memória enquanto o app roda; só os usados).
        public static Image Wallpaper(int i)
        {
            if (!IsWallpaper(i)) return null;
            lock (wallLock)
            {
                Image img;
                if (wallCache.TryGetValue(i, out img)) return img;
                using (var s = typeof(VideoProject).Assembly.GetManifestResourceStream(Walls[i - WallStart][0]))
                using (var tmp = Image.FromStream(s))
                    img = new Bitmap(tmp);
                wallCache[i] = img;
                return img;
            }
        }

        // Papel de parede cobrindo dest (cortando o que sobra); usado também pela moldura dos prints.
        public static void DrawWallpaperCover(Graphics g, int i, Rectangle dest)
        {
            var img = Wallpaper(i);
            if (img == null) return;
            lock (wallLock)
            {
                float k = Math.Max(dest.Width / (float)img.Width, dest.Height / (float)img.Height);
                float w = img.Width * k, h = img.Height * k;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var st = g.Save();
                g.SetClip(dest);
                g.DrawImage(img, new RectangleF(dest.X + (dest.Width - w) / 2, dest.Y + (dest.Height - h) / 2, w, h));
                g.Restore(st);
            }
        }

        public static Bitmap WallpaperThumb(int i, int size)
        {
            lock (wallLock)
            {
                Bitmap b;
                if (wallThumbs.TryGetValue(i, out b) && b.Width == size) return b;
            }
            var img = Wallpaper(i);
            if (img == null) return null;
            var t = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            lock (wallLock)
            {
                using (var g = Graphics.FromImage(t))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    int s = Math.Min(img.Width, img.Height);
                    g.DrawImage(img, new Rectangle(0, 0, size, size), new Rectangle((img.Width - s) / 2, (img.Height - s) / 2, s, s), GraphicsUnit.Pixel);
                }
                Bitmap old;
                if (wallThumbs.TryGetValue(i, out old)) old.Dispose();
                wallThumbs[i] = t;
            }
            return t;
        }

        public static Color[] BackgroundPair(int i)
        {
            if (i < 0 || i >= BackgroundColors.Length || BackgroundColors[i] == null) return null;
            var c = BackgroundColors[i];
            return new[] { Color.FromArgb(255, Color.FromArgb(c[0])), Color.FromArgb(255, Color.FromArgb(c[1])) };
        }

        bool HasFrame { get { return Style.Background != 0 || Style.Aspect != 0; } }

        public static VideoProject Load(string dir)
        {
            var p = new VideoProject { Dir = dir };
            var kv = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "sessao.txt")))
            {
                int i = line.IndexOf('=');
                if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1);
            }
            p.W = int.Parse(kv["w"]); p.H = int.Parse(kv["h"]); p.Fps = int.Parse(kv["fps"]); p.Duration = int.Parse(kv["dur"]);
            p.HasSys = kv["sys"] == "1"; p.HasMic = kv["mic"] == "1";
            for (int i = 0; ; i++)
            {
                string v, png = Path.Combine(dir, "cursor" + i + ".png");
                if (!kv.TryGetValue("shape" + i, out v) || !File.Exists(png)) break;
                var hs = v.Split(',');
                Bitmap img;
                using (var tmp = new Bitmap(png)) img = new Bitmap(tmp);
                p.Shapes.Add(new CursorShape { Image = img, HotX = int.Parse(hs[0]), HotY = int.Parse(hs[1]) });
            }
            using (var r = new BinaryReader(File.OpenRead(Path.Combine(dir, "mouse.bin"))))
            {
                p.Cursor = new CursorSample[r.ReadInt32()];
                for (int i = 0; i < p.Cursor.Length; i++) p.Cursor[i] = new CursorSample { T = r.ReadInt32(), X = r.ReadInt16(), Y = r.ReadInt16(), Shape = r.ReadInt16() };
                p.Clicks = new ClickMark[r.ReadInt32()];
                for (int i = 0; i < p.Clicks.Length; i++) p.Clicks[i] = new ClickMark { T = r.ReadInt32(), X = r.ReadInt16(), Y = r.ReadInt16(), Button = r.ReadByte() };
            }
            p.TrimStart = 0; p.TrimEnd = p.Duration;
            p.UseSys = p.HasSys; p.UseMic = p.HasMic;
            p.Style = VideoStyle.Parse(TrayApp.S.VidStyle);
            string v2;
            if (kv.TryGetValue("captura", out v2)) p.Capture = v2;
            p.WindowCapture = kv.TryGetValue("janela", out v2) && v2 == "1";
            try { p.Cam = CamTrack.Open(Path.Combine(dir, "camera.bin")); } catch { p.Cam = null; }
            // a bolha foi arrastada durante a gravação: a câmera começa ali
            if (p.Cam != null && kv.TryGetValue("campos", out v2))
            {
                var xy = v2.Split(',');
                float cx, cy;
                if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out cx) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cy))
                { p.Style.CamPos = 4; p.Style.CamX = cx; p.Style.CamY = cy; p.Style.Clamp(); }
            }
            p.BuildAutoZooms();
            return p;
        }

        public bool HasCam { get { return Cam != null; } }

        public void RememberStyle() { TrayApp.S.VidStyle = Style.Serialize(); }

        public bool InFrame(float x, float y) { return x >= 0 && y >= 0 && x < W && y < H; }

        // ---- zooms automáticos: cliques a menos de 2,5 s viram um trecho (com 0,5 s antes e depois)
        public void BuildAutoZooms()
        {
            Zooms.RemoveAll(z => z.Auto);
            int clusterStart = -1, clusterEnd = -1;
            float x0 = 0, y0 = 0, x1 = 0, y1 = 0;   // retângulo que contém os cliques do grupo
            var found = new List<ZoomRegion>();
            Action flush = () =>
            {
                if (clusterStart < 0) return;
                // foco no meio dos cliques; se eles estão espalhados, o zoom é menor para todos caberem
                // (os cliques ocupam no máximo 60% da área visível)
                float bw = x1 - x0, bh = y1 - y0, fit = 0;
                if (bw > 1 || bh > 1)
                    fit = Math.Max(1.25f, Math.Min(bw > 1 ? W * 0.6f / bw : 99, bh > 1 ? H * 0.6f / bh : 99));
                float depth = fit > 0 ? Math.Min(Style.Depth, fit) : Style.Depth;
                // dura o bastante para o zoom chegar ao foco (~45% da animação) e ficar lá o tempo de foco
                int start = Math.Max(0, clusterStart - (int)LeadMs);
                int end = Math.Max(clusterEnd, start + (int)(InMs * 0.45)) + (int)(Style.ZoomHold * 1000);
                found.Add(new ZoomRegion { Start = start, End = Math.Min(Duration, end), Depth = depth, Fit = fit, X = (x0 + x1) / 2, Y = (y0 + y1) / 2, Auto = true });
            };
            foreach (var c in Clicks.OrderBy(c => c.T))
            {
                if (!InFrame(c.X, c.Y)) continue;
                if (clusterStart >= 0 && c.T - clusterEnd <= 2500)
                {
                    clusterEnd = c.T;
                    x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y);
                    continue;
                }
                flush();
                clusterStart = clusterEnd = c.T; x0 = x1 = c.X; y0 = y1 = c.Y;
            }
            flush();
            // com um tempo de foco longo, um trecho pode alcançar o próximo: ele termina onde o outro
            // começa e a câmera desliza de um foco ao outro (em vez de o segundo sumir)
            for (int i = 0; i + 1 < found.Count; i++)
                if (found[i].End >= found[i + 1].Start) found[i].End = Math.Max(found[i].Start + 1, found[i + 1].Start - 1);
            // não sobrepõe trechos feitos à mão
            foreach (var z in found)
                if (!Zooms.Any(m => z.End > m.Start && z.Start < m.End)) Zooms.Add(z);
            // depois, onde o cursor parou para trabalhar (sem cobrir os outros nem chegar a menos de 0,6 s)
            if (Style.ZoomMotion)
                foreach (var z in MotionZooms())
                    if (!Zooms.Any(m => z.End + 600 > m.Start && z.Start - 600 < m.End)) Zooms.Add(z);
            Zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
            Invalidate();
        }

        // Sugestões pelo movimento: o cursor atravessa uma boa parte da tela e então fica um tempo
        // numa região pequena (lendo, digitando, mexendo num controle). Ali vale um zoom.
        public List<ZoomRegion> MotionZooms()
        {
            var res = new List<ZoomRegion>();
            if (Cursor == null || Cursor.Length < 4 || Duration < 2500) return res;
            const int step = 50;
            int n = Duration / step + 1;
            var px = new float[n]; var py = new float[n]; var ok = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int sh;
                var m = MouseAt(i * step, out sh);
                px[i] = m.X; py[i] = m.Y;
                ok[i] = sh >= 0 && InFrame(m.X, m.Y);
            }
            float small = Math.Min(W, H);
            float stay = small * 0.12f;      // raio da "região de trabalho"
            float travel = small * 0.28f;    // o quanto precisa ter andado antes
            int look = 1500 / step;
            int i0 = look;
            while (i0 < n)
            {
                if (!ok[i0]) { i0++; continue; }
                // de onde veio: maior distância nos últimos 1,5 s
                float far = 0;
                for (int b = Math.Max(0, i0 - look); b < i0; b++)
                    if (ok[b]) far = Math.Max(far, Dist(px[b], py[b], px[i0], py[i0]));
                if (far < travel) { i0++; continue; }
                // quanto tempo fica por perto
                int j = i0;
                double sx = 0, sy = 0; int cnt = 0; float moved = 0;
                while (j + 1 < n && ok[j + 1] && Dist(px[j + 1], py[j + 1], px[i0], py[i0]) <= stay)
                {
                    j++;
                    sx += px[j]; sy += py[j]; cnt++;
                    moved += Dist(px[j], py[j], px[j - 1], py[j - 1]);
                }
                int dwell = (j - i0) * step;
                // precisa de alguma atividade ali: o cursor imóvel é só o mouse tirado do caminho
                if (dwell >= 1300 && cnt > 0 && moved >= small * 0.03f)
                {
                    int start = Math.Max(0, i0 * step - (int)LeadMs + 150);
                    int end = Math.Min(Duration, Math.Min(j * step, i0 * step + 7000) + Math.Max(300, (int)(Style.ZoomHold * 1000) - 200));
                    res.Add(new ZoomRegion { Start = start, End = end, Depth = Style.Depth, X = (float)(sx / cnt), Y = (float)(sy / cnt), Auto = true, Motion = true });
                    i0 = j + look;   // espera andar de novo
                    continue;
                }
                i0++;
            }
            return res;
        }

        static float Dist(float x0, float y0, float x1, float y1) { float dx = x1 - x0, dy = y1 - y0; return (float)Math.Sqrt(dx * dx + dy * dy); }

        // ---- velocidade: mapa entre o tempo do vídeo final e o da gravação (já com o corte)
        struct Seg { public double S0, S1, O0; public float Rate; }
        Seg[] timeMap;
        int mapTs, mapTe;

        public void SortSpeeds() { Speeds.Sort((a, b) => a.Start.CompareTo(b.Start)); timeMap = null; }

        Seg[] Map
        {
            get
            {
                if (timeMap != null && mapTs == TrimStart && mapTe == TrimEnd) return timeMap;
                mapTs = TrimStart; mapTe = TrimEnd;
                var list = new List<Seg>();
                double s = TrimStart, o = 0;
                foreach (var r in Speeds.Where(r => r.End > TrimStart && r.Start < TrimEnd && r.End > r.Start).OrderBy(r => r.Start))
                {
                    double a = Math.Max(s, Math.Max(TrimStart, r.Start)), b = Math.Min(TrimEnd, r.End);
                    if (b <= a) continue;
                    if (a > s) { list.Add(new Seg { S0 = s, S1 = a, O0 = o, Rate = 1 }); o += a - s; }
                    float rate = Math.Max(0.25f, Math.Min(16f, r.Rate));
                    list.Add(new Seg { S0 = a, S1 = b, O0 = o, Rate = rate });
                    o += (b - a) / rate; s = b;
                }
                if (s < TrimEnd || list.Count == 0) list.Add(new Seg { S0 = s, S1 = Math.Max(s, TrimEnd), O0 = o, Rate = 1 });
                timeMap = list.ToArray();
                return timeMap;
            }
        }

        public bool HasSpeedChanges { get { return Map.Any(g => Math.Abs(g.Rate - 1) > 0.001f); } }

        // duração do vídeo final (ms)
        public double OutDuration { get { var m = Map; var l = m[m.Length - 1]; return l.O0 + (l.S1 - l.S0) / l.Rate; } }

        // instante da gravação mostrado no instante u do vídeo final
        public double SourceAt(double u)
        {
            var m = Map;
            foreach (var g in m)
            {
                double len = (g.S1 - g.S0) / g.Rate;
                if (u <= g.O0 + len) return g.S0 + Math.Max(0, u - g.O0) * g.Rate;
            }
            return m[m.Length - 1].S1;
        }

        // instante do vídeo final que mostra o instante s da gravação
        public double OutAt(double s)
        {
            var m = Map;
            if (s <= m[0].S0) return 0;
            foreach (var g in m)
                if (s <= g.S1) return g.O0 + (s - g.S0) / g.Rate;
            return OutDuration;
        }

        public float RateAt(double s)
        {
            foreach (var r in Speeds) if (s >= r.Start && s < r.End) return r.Rate;
            return 1;
        }

        public void SortZooms() { Zooms.Sort((a, b) => a.Start.CompareTo(b.Start)); Invalidate(); }

        // Qualquer mudança de zoom, estilo da câmera ou cursor refaz as trilhas (é rápido).
        public void Invalidate() { track = null; cursorTrack = null; timeMap = null; }

        // ---- mouse
        public PointF MouseAt(double t, out int shape)
        {
            shape = -1;
            if (Cursor.Length == 0) return new PointF(W / 2f, H / 2f);
            int lo = 0, hi = Cursor.Length - 1;
            if (t <= Cursor[0].T) { shape = Cursor[0].Shape; return new PointF(Cursor[0].X, Cursor[0].Y); }
            if (t >= Cursor[hi].T) { shape = Cursor[hi].Shape; return new PointF(Cursor[hi].X, Cursor[hi].Y); }
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (Cursor[m].T <= t) lo = m; else hi = m; }
            var a = Cursor[lo]; var b = Cursor[hi];
            float f = b.T == a.T ? 0 : (float)((t - a.T) / (b.T - a.T));
            shape = a.Shape;
            return new PointF(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }

        // ---- trilha do cursor: suavização sem atraso e inclinação
        // O vídeo já está gravado, então dá para olhar o movimento para frente e para trás: a média
        // gaussiana tira os tremidos e arredonda as curvas sem deixar o cursor "atrasado" (a mola
        // de antes chegava depois do clique). Nos cliques ele fica exatamente onde clicou.
        const double CStep = 4;
        float[] cursorTrack;   // x, y, rotação (graus), velocidade (px/s)

        void BuildCursorTrack()
        {
            int n = (int)(Duration / CStep) + 2;
            var tr = new float[n * 4];
            var rx = new float[n]; var ry = new float[n];
            int sh;
            for (int i = 0; i < n; i++) { var m = MouseAt(i * CStep, out sh); rx[i] = m.X; ry[i] = m.Y; }
            float[] sx = rx, sy = ry;
            double sm = Style.Smoothing;
            if (sm > 0.001)
            {
                double sigma = 16 + sm * 52;                   // ms: 0,67 → ~50 ms; 2 → ~120 ms
                int R = (int)Math.Ceiling(sigma * 3 / CStep);
                var wk = new float[R + 1];
                for (int j = 0; j <= R; j++) { double d = j * CStep / sigma; wk[j] = (float)Math.Exp(-0.5 * d * d); }
                // um salto grande (o cursor foi parar em outro monitor ou a janela andou) não é suavizado
                var seg = new int[n];
                float jump = Math.Max(W, H) * 0.25f;
                for (int i = 1; i < n; i++)
                    seg[i] = seg[i - 1] + (Math.Abs(rx[i] - rx[i - 1]) + Math.Abs(ry[i] - ry[i - 1]) > jump ? 1 : 0);
                var ox = new float[n]; var oy = new float[n];
                System.Threading.Tasks.Parallel.For(0, n, i =>
                {
                    double ax = rx[i] * wk[0], ay = ry[i] * wk[0], aw = wk[0];
                    int sg = seg[i];
                    for (int j = 1; j <= R; j++)
                    {
                        int a = i - j, b = i + j;
                        if (a >= 0 && seg[a] == sg) { ax += rx[a] * wk[j]; ay += ry[a] * wk[j]; aw += wk[j]; }
                        if (b < n && seg[b] == sg) { ax += rx[b] * wk[j]; ay += ry[b] * wk[j]; aw += wk[j]; }
                    }
                    ox[i] = (float)(ax / aw); oy[i] = (float)(ay / aw);
                });
                // perto de cada clique, volta para a posição real (no instante do clique, 100%)
                double ps = sigma * 0.9;
                int PR = (int)Math.Ceiling(ps * 2.5 / CStep);
                foreach (var c in Clicks)
                {
                    int ci = (int)Math.Round(c.T / CStep);
                    for (int j = -PR; j <= PR; j++)
                    {
                        int i = ci + j;
                        if (i < 0 || i >= n) continue;
                        double d = j * CStep / ps;
                        float w = (float)Math.Exp(-0.5 * d * d);
                        ox[i] += (rx[i] - ox[i]) * w; oy[i] += (ry[i] - oy[i]) * w;
                    }
                }
                sx = ox; sy = oy;
            }
            double rot = 0, lx = sx[0], ly = sy[0];
            for (int i = 0; i < n; i++)
            {
                double x = sx[i], y = sy[i];
                double dx = x - lx, dy = y - ly;
                double speed = Math.Sqrt(dx * dx + dy * dy) / (CStep / 1000.0);
                lx = x; ly = y;
                // inclinação: rápida para a direita/baixo pende para um lado, para a esquerda/cima para o outro
                double target = 0;
                if (Style.Sway > 0 && speed > 1)
                {
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double bias = Math.Max(-1, Math.Min(1, (dx + dy * 0.65) / dist));
                    target = bias * Math.Min(1, speed / 1400) * 10 * Style.Sway * 3;   // graus
                    target = Math.Max(-28, Math.Min(28, target));
                }
                rot += (target - rot) * 0.12;   // amortece o balanço
                tr[i * 4] = (float)x; tr[i * 4 + 1] = (float)y; tr[i * 4 + 2] = (float)rot; tr[i * 4 + 3] = (float)speed;
            }
            cursorTrack = tr;
        }

        void CursorAt(double t, out float x, out float y, out float rot, out float speed)
        {
            if (cursorTrack == null) BuildCursorTrack();
            int n = cursorTrack.Length / 4;
            double fi = Math.Max(0, Math.Min(n - 1.001, t / CStep));
            int i = (int)fi; float f = (float)(fi - i);
            x = cursorTrack[i * 4] + (cursorTrack[i * 4 + 4] - cursorTrack[i * 4]) * f;
            y = cursorTrack[i * 4 + 1] + (cursorTrack[i * 4 + 5] - cursorTrack[i * 4 + 1]) * f;
            rot = cursorTrack[i * 4 + 2] + (cursorTrack[i * 4 + 6] - cursorTrack[i * 4 + 2]) * f;
            speed = cursorTrack[i * 4 + 3];
        }

        // ---- câmera: trilha de (escala, deslocamento) a 120 Hz, já suavizada pela mola
        const double TStep = 1000.0 / 120;
        float[] track;

        static double Bezier(double x1, double y1, double x2, double y2, double t)
        {
            // curva cúbica (0,0)-(x1,y1)-(x2,y2)-(1,1): acha o parâmetro pelo x e devolve o y
            t = Math.Max(0, Math.Min(1, t));
            double u = t;
            for (int i = 0; i < 10; i++)
            {
                double om = 1 - u;
                double x = 3 * x1 * om * om * u + 3 * x2 * om * u * u + u * u * u - t;
                double dx = 3 * x1 * om * om + 6 * (x2 - x1) * om * u + 3 * (1 - x2) * u * u;
                if (Math.Abs(x) < 1e-6 || Math.Abs(dx) < 1e-6) break;
                u = Math.Max(0, Math.Min(1, u - x / dx));
            }
            double o = 1 - u;
            return 3 * y1 * o * o * u + 3 * y2 * o * u * u + u * u * u;
        }
        static double EaseZoom(double t) { return Bezier(0.16, 1, 0.3, 1, t); }      // sai rápido, assenta devagar
        static double Glide(double t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }

        // duração da entrada (a saída e o deslize entre zooms acompanham, um pouco mais curtos)
        double InMs { get { return Style.ZoomTime * 1000.0; } }
        double OutMs { get { return InMs * 0.67; } }
        double PanMs { get { return Math.Max(500, InMs * 0.67); } }
        // antecedência: o zoom começa um pouco antes do clique (mais cedo quando a animação é lenta)
        double LeadMs { get { return 300 + InMs * 0.15; } }

        PointF ClampFocus(float x, float y, float depth)
        {
            float hw = W / (2 * depth), hh = H / (2 * depth);
            return new PointF(Math.Max(hw, Math.Min(W - hw, x)), Math.Max(hh, Math.Min(H - hh, y)));
        }

        void BuildTrack()
        {
            int n = (int)(Duration / TStep) + 3;
            var tr = new float[n * 3];
            var regs = Style.Zoom ? Zooms.Where(z => z.End > z.Start).OrderBy(z => z.Start).ToList() : new List<ZoomRegion>();
            // encadeia trechos próximos
            var chains = new List<List<ZoomRegion>>();
            foreach (var z in regs)
            {
                // até 2 s entre um trecho e outro: a câmera desliza em vez de sair e entrar de novo
                // (os cliques já se juntam até 2,5 s, então trechos automáticos ficam a pelo menos 1,5 s)
                if (chains.Count > 0 && Style.Connect && z.Start - chains[chains.Count - 1].Last().End <= 2000 && z.Start >= chains[chains.Count - 1].Last().Start)
                    chains[chains.Count - 1].Add(z);
                else chains.Add(new List<ZoomRegion> { z });
            }
            double inMs = InMs, outMs = OutMs, panMs = PanMs;
            // mola da câmera (≈ amortecimento crítico); a rápida é mais firme
            // mais lenta = mola mais macia (amortecimento quase crítico, sem balançar)
            double k = 100 * Math.Pow(1.5 / Style.ZoomTime, 1.6), c = 2.1 * Math.Sqrt(k);
            double s = 1, tx = 0, ty = 0, vs = 0, vtx = 0, vty = 0;
            // seguir o cursor
            ZoomRegion followFor = null; float cfx = 0, cfy = 0;
            // deslize entre trechos encadeados
            double panStart = -1; float panX = 0, panY = 0, panDepth = 1;
            int ci = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i * TStep;
                while (ci < chains.Count && t > chains[ci].Last().End + outMs) ci++;
                double depth = 1, fx = W / 2.0, fy = H / 2.0, prog = 0;
                if (ci < chains.Count && t >= chains[ci][0].Start)
                {
                    var ch = chains[ci];
                    double st = ch[0].Start, en = ch.Last().End;
                    // a animação sempre tem a duração escolhida: num trecho curto ela só não chega ao fim
                    // (antes era comprimida e ficava rápida de novo)
                    double outStart = en;
                    double peak = EaseZoom(Math.Min(1, (en - st) / inMs));
                    prog = t <= outStart ? EaseZoom(Math.Min(1, (t - st) / inMs)) : peak * (1 - EaseZoom((t - outStart) / outMs));
                    bool zoomingOut = t > outStart;
                    // qual trecho está valendo: o último que começou; terminado um, já vale o próximo
                    int idx = 0;
                    for (int j = 0; j < ch.Count; j++)
                    {
                        if (t >= ch[j].Start) idx = j;
                        if (j < ch.Count - 1 && t > ch[j].End) idx = j + 1;
                    }
                    var r = ch[idx];
                    if (followFor != r)
                    {
                        // trocou de trecho dentro da cadeia: desliza a partir de onde a câmera estava
                        if (followFor != null && ch.Contains(followFor)) { panStart = t; panX = cfx; panY = cfy; panDepth = followFor.Depth; }
                        else panStart = -1;
                        followFor = r;
                        var f0 = ClampFocus(r.X, r.Y, r.Depth);
                        cfx = f0.X; cfy = f0.Y;
                    }
                    bool panning = panStart >= 0 && t < panStart + panMs;
                    // foco do trecho atual (seguindo o cursor, se for o caso)
                    if (Style.Follow && !r.Manual && !zoomingOut && !panning && t >= r.Start + 450)
                    {
                        // usa o cursor já suavizado e só "empurra" a câmera o quanto ele passou da margem
                        // segura (antes ela pulava para centralizar o cursor, um tranco a cada saída)
                        float cx, cy, crot, csp;
                        CursorAt(t, out cx, out cy, out crot, out csp);
                        float hw = W / (2 * r.Depth), hh = H / (2 * r.Depth);
                        float inX = hw * 2 * 0.22f, inY = hh * 2 * 0.22f;
                        float left = cfx - hw + inX, right = cfx + hw - inX, top = cfy - hh + inY, bottom = cfy + hh - inY;
                        if (cx < left) cfx -= left - cx; else if (cx > right) cfx += cx - right;
                        if (cy < top) cfy -= top - cy; else if (cy > bottom) cfy += cy - bottom;
                        var cl = ClampFocus(cfx, cfy, r.Depth);
                        cfx = cl.X; cfy = cl.Y;
                    }
                    depth = r.Depth; fx = cfx; fy = cfy;
                    if (panning)
                    {
                        double g = Glide((t - panStart) / panMs);
                        depth = panDepth + (r.Depth - panDepth) * g;
                        fx = panX + (cfx - panX) * g; fy = panY + (cfy - panY) * g;
                    }
                }
                else { followFor = null; panStart = -1; }

                // alvo no formato do Recordly: escala e deslocamento crescem juntos com o progresso
                double ts = 1 + (depth - 1) * prog;
                double ttx = prog * (W / 2.0 - depth * fx), tty = prog * (H / 2.0 - depth * fy);
                if (i == 0) { s = ts; tx = ttx; ty = tty; }
                else
                {
                    const int sub = 4; double dt = TStep / 1000.0 / sub;
                    for (int q = 0; q < sub; q++)
                    {
                        vs += (k * (ts - s) - c * vs) * dt; s += vs * dt;
                        vtx += (k * (ttx - tx) - c * vtx) * dt; tx += vtx * dt;
                        vty += (k * (tty - ty) - c * vty) * dt; ty += vty * dt;
                    }
                }
                // nunca mostra fora do quadro
                double cs = Math.Max(1, s);
                double ctx = Math.Max(W - cs * W, Math.Min(0, tx)), cty = Math.Max(H - cs * H, Math.Min(0, ty));
                tr[i * 3] = (float)cs; tr[i * 3 + 1] = (float)ctx; tr[i * 3 + 2] = (float)cty;
            }
            track = tr;
        }

        // Região do vídeo visível no instante t (pixels do vídeo).
        public bool NoCamera;   // a prévia mostra o quadro inteiro (para escolher o foco de um zoom)

        public RectangleF View(double t)
        {
            if (NoCamera) return new RectangleF(0, 0, W, H);
            float s, tx, ty;
            Transform(t, out s, out tx, out ty);
            return new RectangleF(-tx / s, -ty / s, W / s, H / s);
        }

        public void Transform(double t, out float s, out float tx, out float ty)
        {
            if (track == null) BuildTrack();
            int n = track.Length / 3;
            double fi = Math.Max(0, Math.Min(n - 1.001, t / TStep));
            int i = (int)fi; float f = (float)(fi - i);
            s = track[i * 3] + (track[i * 3 + 3] - track[i * 3]) * f;
            tx = track[i * 3 + 1] + (track[i * 3 + 4] - track[i * 3 + 1]) * f;
            ty = track[i * 3 + 2] + (track[i * 3 + 5] - track[i * 3 + 2]) * f;
        }

        public float ZoomAt(double t) { float s, a, b; Transform(t, out s, out a, out b); return s; }

        // Quanto a câmera andou num intervalo (em pixels do vídeo): alimenta o desfoque de movimento.
        double CameraSpeed(double t, double dtMs)
        {
            float s0, x0, y0, s1, x1, y1;
            Transform(Math.Max(0, t - dtMs), out s0, out x0, out y0);
            Transform(t, out s1, out x1, out y1);
            return Math.Abs(s1 - s0) * Math.Max(W, H) * 0.5 + Math.Abs(x1 - x0) + Math.Abs(y1 - y0);
        }

        // ---- tela de saída
        public Size OutputSize
        {
            get
            {
                if (!HasFrame) return new Size(W, H);
                int pad = Pad;
                double cw = W + 2.0 * pad, ch = H + 2.0 * pad;
                double ar = AspectRatio;
                if (ar > 0)
                {
                    if (cw / ch < ar) cw = ch * ar; else ch = cw / ar;
                }
                return new Size(((int)Math.Ceiling(cw) + 1) & ~1, ((int)Math.Ceiling(ch) + 1) & ~1);
            }
        }

        double AspectRatio
        {
            get
            {
                switch (Style.Aspect)
                {
                    case 1: return 16 / 9.0;
                    case 2: return 9 / 16.0;
                    case 3: return 1;
                    case 4: return 4 / 3.0;
                }
                return 0;
            }
        }

        public int Pad { get { return !HasFrame ? 0 : (int)(Math.Max(W, H) * (Style.Background == 0 ? 0 : Style.Padding)); } }
        public float Radius { get { return HasFrame ? Style.Radius * Math.Min(W, H) * 0.05f : 0; } }

        public RectangleF ContentRect(Size outSize)
        {
            return new RectangleF((outSize.Width - W) / 2f, (outSize.Height - H) / 2f, W, H);
        }

        // ---- caches de desenho
        Bitmap bgCache; string bgKey; Size bgSize;
        Bitmap[] corners; Rectangle[] cornerRects;
        Bitmap bgDev, scaled;
        Scaler scaler = new Scaler();
        // exportação: zoom ampliado com bicúbico (mais nítido; a prévia fica no bilinear, mais leve)
        public bool SharpZoom { get { return scaler.Sharp; } set { scaler.Sharp = value; } }
        readonly Dictionary<int, Bitmap> shadows = new Dictionary<int, Bitmap>();

        string BgKey(Size s)
        {
            return Style.Background + "|" + Style.BgImage + "|" + Style.BgBlur + "|" + Style.Padding + "|" + Style.Radius + "|" + Style.Shadow + "|" + Style.Aspect + "|" + s;
        }

        // Fundo + sombra da janela (muda só quando o estilo muda) e os cantos recortados em curva.
        Bitmap BackgroundLayer(Size outSize)
        {
            string key = BgKey(outSize);
            if (bgCache != null && bgKey == key) return bgCache;
            if (bgCache != null) bgCache.Dispose();
            if (corners != null) foreach (var c in corners) c.Dispose();
            if (bgDev != null) { bgDev.Dispose(); bgDev = null; }
            bgKey = key; bgSize = outSize;
            bgCache = new Bitmap(outSize.Width, outSize.Height, PixelFormat.Format32bppRgb);
            var r = ContentRect(outSize);
            float rad = Radius;
            using (var g = Graphics.FromImage(bgCache))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawBackdrop(g, outSize);
                // sombra em três camadas (larga e suave, média e curta), como no Recordly
                float unit = Math.Max(W, H) / 1600f * Style.Shadow;
                if (Style.Shadow > 0 && Style.Background != 0)
                {
                    float[][] layers = { new[] { 12f, 0.55f, 48f }, new[] { 4f, 0.4f, 16f }, new[] { 2f, 0.25f, 8f } };
                    foreach (var L in layers)
                    {
                        float off = L[0] * unit * 2, blur = Math.Max(1, L[2] * unit * 2);
                        int steps = Math.Max(4, Math.Min(18, (int)blur));
                        int a = Math.Max(1, (int)(255 * L[1] * Math.Min(1, Style.Shadow * 1.4) / steps));
                        using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, a), 0, 0, 0)))
                            for (int i = steps; i >= 1; i--)
                            {
                                float e = blur * i / steps;
                                var sr = new RectangleF(r.X - e * 0.5f, r.Y - e * 0.5f + off, r.Width + e, r.Height + e);
                                using (var path = Art.RoundRect(sr, rad + e * 0.5f)) g.FillPath(b, path);
                            }
                    }
                }
            }
            int cs = (int)Math.Ceiling(rad) + 2;
            var rr = Rectangle.Round(r);
            cornerRects = new[] {
                new Rectangle(rr.X, rr.Y, cs, cs), new Rectangle(rr.Right - cs, rr.Y, cs, cs),
                new Rectangle(rr.X, rr.Bottom - cs, cs, cs), new Rectangle(rr.Right - cs, rr.Bottom - cs, cs, cs) };
            corners = new Bitmap[4];
            for (int i = 0; i < 4; i++)
            {
                var cr = cornerRects[i];
                corners[i] = new Bitmap(cs, cs, PixelFormat.Format32bppPArgb);
                if (rad < 0.5f) continue;
                using (var g = Graphics.FromImage(corners[i]))
                using (var tb = new TextureBrush(bgCache, WrapMode.Clamp))
                using (var path = new GraphicsPath(FillMode.Alternate))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(-cr.X, -cr.Y);
                    var big = cr; big.Inflate(4, 4);
                    path.AddRectangle(big);
                    using (var round = Art.RoundRect(r, rad)) path.AddPath(round, false);
                    g.SetClip(cr);
                    g.FillPath(tb, path);
                }
            }
            return bgCache;
        }

        void DrawBackdrop(Graphics g, Size outSize)
        {
            var full = new Rectangle(0, 0, outSize.Width, outSize.Height);
            bool wall = IsWallpaper(Style.Background);
            if ((Style.Background == BgImageIndex && File.Exists(Style.BgImage)) || wall)
            {
                try
                {
                    // o papel de parede é compartilhado entre o editor e a exportação: uma thread por vez
                    if (wall)
                    {
                        var shared = Wallpaper(Style.Background);
                        lock (wallLock) DrawCover(g, shared, outSize, full);
                    }
                    else
                        using (var img = Image.FromFile(Style.BgImage)) DrawCover(g, img, outSize, full);
                    return;
                }
                catch { }
            }
            var pair = BackgroundPair(Style.Background) ?? BackgroundPair(7);
            using (var br = new LinearGradientBrush(full, pair[0], pair[1], 35f))
                g.FillRectangle(br, full);
        }

        void DrawCover(Graphics g, Image img, Size outSize, Rectangle full)
        {
                    {
                        // cobre a tela inteira, cortando o que sobra
                        float k = Math.Max(outSize.Width / (float)img.Width, outSize.Height / (float)img.Height);
                        float w = img.Width * k, h = img.Height * k;
                        var dst = new RectangleF((outSize.Width - w) / 2, (outSize.Height - h) / 2, w, h);
                        if (Style.BgBlur > 0.01f)
                        {
                            // desfoque barato: reduz bastante e amplia de volta, duas vezes
                            int d = Math.Max(1, (int)(2 + Style.BgBlur * 30));
                            using (var small = new Bitmap(Math.Max(1, outSize.Width / d), Math.Max(1, outSize.Height / d)))
                            {
                                using (var gs = Graphics.FromImage(small))
                                {
                                    gs.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                    gs.DrawImage(img, new RectangleF(dst.X / d, dst.Y / d, dst.Width / d, dst.Height / d));
                                }
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                using (var ia = new ImageAttributes())
                                {
                                    ia.SetWrapMode(WrapMode.TileFlipXY);
                                    g.DrawImage(small, full, 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, ia);
                                }
                            }
                        }
                        else
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, dst);
                        }
                    }
        }

        // Sombra do cursor: a silhueta em preto, um pouco desfocada.
        Bitmap ShadowOf(int shape)
        {
            Bitmap b;
            if (shadows.TryGetValue(shape, out b)) return b;
            var src = Shapes[shape].Image;
            int pad = 3;
            b = new Bitmap(src.Width + pad * 2, src.Height + pad * 2, PixelFormat.Format32bppArgb);
            var a = new int[src.Width * src.Height];
            var d = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < src.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, a, y * src.Width, src.Width);
            src.UnlockBits(d);
            int bw = b.Width, bh = b.Height;
            var alpha = new float[bw * bh];
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    alpha[(y + pad) * bw + x + pad] = ((a[y * src.Width + x] >> 24) & 255) / 255f;
            // caixa 3x3 duas vezes
            for (int pass = 0; pass < 2; pass++)
            {
                var o = new float[alpha.Length];
                for (int y = 1; y < bh - 1; y++)
                    for (int x = 1; x < bw - 1; x++)
                    {
                        float s = 0;
                        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) s += alpha[(y + dy) * bw + x + dx];
                        o[y * bw + x] = s / 9;
                    }
                alpha = o;
            }
            var px = new int[bw * bh];
            for (int i = 0; i < px.Length; i++) px[i] = ((int)(Math.Min(1, alpha[i]) * 255 * 0.38) << 24);
            var dd = b.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bh; y++) Marshal.Copy(px, y * bw, dd.Scan0 + y * dd.Stride, bw);
            b.UnlockBits(dd);
            shadows[shape] = b;
            return b;
        }

        // Desenha o quadro do tempo t (ms do vídeo gravado). O Graphics já deve estar em coordenadas
        // do tamanho de saída (OutputSize), podendo ter uma escala (prévia e GIF).
        // motion: true quando os quadros vêm em sequência (exportação ou reprodução), para o desfoque.
        public void Render(Graphics g, Bitmap frame, double t, bool motion)
        {
            var outSize = OutputSize;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var view = View(t);
            float zoom = W / view.Width;
            var dst = ContentRect(outSize);
            var src = SourceRect(frame, view);

            var tm = g.Transform;
            float ts = tm.Elements[0], tx = tm.Elements[4], ty = tm.Elements[5];
            tm.Dispose();
            var saved = g.Save();
            g.ResetTransform();
            g.CompositingMode = CompositingMode.SourceCopy;
            if (HasFrame)
            {
                var bg = DeviceBackground(outSize, ts);
                g.DrawImageUnscaled(bg, (int)tx, (int)ty);
            }
            var dev = Rectangle.Round(new RectangleF(dst.X * ts + tx, dst.Y * ts + ty, dst.Width * ts, dst.Height * ts));
            float blend = MotionBlend(t, ts, motion);
            if (blend == 0 && dev.Width == frame.Width && dev.Height == frame.Height && Math.Abs(src.Width - frame.Width) < 0.01f && Math.Abs(src.Height - frame.Height) < 0.01f)
            {
                g.DrawImageUnscaled(frame, dev.X, dev.Y);   // sem zoom e no tamanho original
                scaler.Forget();
            }
            else if (dev.Width > 0 && dev.Height > 0)
            {
                if (scaled == null || scaled.Size != dev.Size)
                {
                    if (scaled != null) scaled.Dispose();
                    scaled = new Bitmap(dev.Width, dev.Height, PixelFormat.Format32bppRgb);
                }
                scaler.Bilinear(frame, src, scaled, blend);
                g.DrawImageUnscaled(scaled, dev.X, dev.Y);
            }
            g.Restore(saved);
            DrawOverlays(g, t, view, dst, outSize, zoom);
        }

        // Exportação: o fundo e o quadro (com o zoom) vão direto para a memória do bitmap de saída
        // (as cópias do GDI+ custavam ~20 ms por quadro 1080p); só cursor, cliques, cantos e câmera
        // passam pelo GDI+. scale: tamanho do bitmap / OutputSize.
        public unsafe void RenderInto(Bitmap target, float scale, Bitmap frame, double t, bool motion)
        {
            var outSize = OutputSize;
            var view = View(t);
            float zoom = W / view.Width;
            var dst = ContentRect(outSize);
            var src = SourceRect(frame, view);
            var bounds = new Rectangle(0, 0, target.Width, target.Height);
            var dev = Rectangle.Round(new RectangleF(dst.X * scale, dst.Y * scale, dst.Width * scale, dst.Height * scale));
            dev.Intersect(bounds);
            float blend = MotionBlend(t, scale, motion);
            var td = target.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
            try
            {
                if (HasFrame)
                {
                    var bg = DeviceBackground(outSize, scale);
                    int w = Math.Min(bg.Width, target.Width), h = Math.Min(bg.Height, target.Height);
                    var bd = bg.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        byte* s0 = (byte*)bd.Scan0, d0 = (byte*)td.Scan0;
                        int ss = bd.Stride, ds = td.Stride;
                        System.Threading.Tasks.Parallel.For(0, h, y =>
                            MF.Copy((IntPtr)(d0 + (long)y * ds), (IntPtr)(s0 + (long)y * ss), (IntPtr)(w * 4)));
                    }
                    finally { bg.UnlockBits(bd); }
                }
                if (dev.Width > 0 && dev.Height > 0)
                    scaler.ScaleInto(frame, src, td, dev, blend);
            }
            finally { target.UnlockBits(td); }
            using (var g = Graphics.FromImage(target))
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (scale < 0.999f || scale > 1.001f) g.ScaleTransform(scale, scale);
                DrawOverlays(g, t, view, dst, outSize, zoom);
            }
        }

        RectangleF SourceRect(Bitmap frame, RectangleF view)
        {
            float sx = frame.Width / (float)W, sy = frame.Height / (float)H;
            return new RectangleF(view.X * sx, view.Y * sy, view.Width * sx, view.Height * sy);
        }

        // desfoque de movimento da câmera: mistura um pouco do quadro anterior quando ela anda rápido
        float MotionBlend(double t, float ts, bool motion)
        {
            if (!motion || Style.ZoomBlur <= 0) return 0;
            double v = CameraSpeed(t, 1000.0 / 30) * ts;
            float blend = (float)Math.Min(0.5, v / 60.0 * Style.ZoomBlur);
            return blend < 0.03f ? 0 : blend;
        }

        // Fundo já no tamanho da tela de destino (refeito só quando o estilo ou o tamanho mudam).
        Bitmap DeviceBackground(Size outSize, float ts)
        {
            var bg = BackgroundLayer(outSize);
            var devSize = new Size((int)Math.Round(outSize.Width * ts), (int)Math.Round(outSize.Height * ts));
            if (devSize == outSize) return bg;
            if (bgDev == null || bgDev.Size != devSize)
            {
                if (bgDev != null) bgDev.Dispose();
                bgDev = new Bitmap(devSize.Width, devSize.Height, PixelFormat.Format32bppRgb);
                using (var gb = Graphics.FromImage(bgDev))
                {
                    gb.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    gb.DrawImage(bg, new Rectangle(0, 0, devSize.Width, devSize.Height));
                }
            }
            return bgDev;
        }

        // Cantos arredondados, destaque dos cliques, cursor e webcam (em coordenadas da tela de saída).
        void DrawOverlays(Graphics g, double t, RectangleF view, RectangleF dst, Size outSize, float zoom)
        {
            g.CompositingMode = CompositingMode.SourceOver;
            if (HasFrame && Radius >= 0.5f)
                for (int i = 0; i < 4; i++)
                    g.DrawImage(corners[i], cornerRects[i].X, cornerRects[i].Y, cornerRects[i].Width, cornerRects[i].Height);

            float k = dst.Width / view.Width;
            Func<float, float, PointF> map = (x, y) => new PointF(dst.X + (x - view.X) * k, dst.Y + (y - view.Y) * k);
            var clip = g.Clip;
            g.SetClip(dst);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawClickFx(g, t, map, zoom);
            if (Style.Cursor) DrawCursor(g, t, map, zoom);
            g.Clip = clip;
            clip.Dispose();
            if (HasCam && Style.Cam) DrawCam(g, t, outSize, zoom);
            g.SmoothingMode = SmoothingMode.None;
        }

        // ---- webcam: bolha sobre o vídeo, em coordenadas da tela de saída
        public RectangleF CamRect(Size outSize, float zoom)
        {
            float small = Math.Min(outSize.Width, outSize.Height);
            float d = small * Style.CamSize;
            if (Style.CamShrink && zoom > 1.01f)
                d *= 1 - 0.3f * Math.Min(1, (zoom - 1) / 0.8f);   // abre espaço para o conteúdo durante o zoom
            float m = small * 0.035f;
            float cx, cy;
            switch (Style.CamPos)
            {
                case 0: cx = m + d / 2; cy = outSize.Height - m - d / 2; break;
                case 2: cx = m + d / 2; cy = m + d / 2; break;
                case 3: cx = outSize.Width - m - d / 2; cy = m + d / 2; break;
                case 4: cx = Style.CamX * outSize.Width; cy = Style.CamY * outSize.Height; break;
                default: cx = outSize.Width - m - d / 2; cy = outSize.Height - m - d / 2; break;
            }
            cx = Math.Max(d / 2, Math.Min(outSize.Width - d / 2, cx));
            cy = Math.Max(d / 2, Math.Min(outSize.Height - d / 2, cy));
            return new RectangleF(cx - d / 2, cy - d / 2, d, d);
        }

        public bool CamVisible(double t) { return HasCam && Style.Cam && Cam.Visible(t); }

        CamFx camFx;   // efeitos da câmera (desfoque do fundo, filtros...), com cache do último quadro

        void DrawCam(Graphics g, double t, Size outSize, float zoom)
        {
            var frame = Cam.At(t);
            if (frame == null) return;
            var r = CamRect(outSize, zoom);
            // com efeitos, a câmera é processada no tamanho em que aparece na tela (prévia leve, exportação nítida)
            Bitmap img = frame;
            int s = Math.Min(frame.Width, frame.Height);
            float sx = (frame.Width - s) / 2f, sy = (frame.Height - s) / 2f;
            bool cutout = false;
            if (CamFx.Active(Style))
            {
                float ts;
                using (var tm = g.Transform) ts = Math.Abs(tm.Elements[0]);
                if (camFx == null) camFx = new CamFx();
                try
                {
                    img = camFx.Process(frame, Cam.LastIndex, Style, (int)Math.Ceiling(r.Width * ts * 1.15f));
                    s = img.Width; sx = sy = 0;
                    cutout = Style.CamBg == 3 && Segmenter.Available;
                }
                catch (Exception ex) { TrayApp.Log("efeito da câmera: " + ex.Message); img = frame; }
            }
            float rad = r.Width / 2 * Style.CamRound;
            float k = r.Width / s;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            if (cutout)
            {
                // sem fundo: só a pessoa sobre o vídeo, sem moldura
                var st = g.Save();
                if (Style.CamMirror) { g.TranslateTransform(r.Right, r.Y); g.ScaleTransform(-k, k); }
                else { g.TranslateTransform(r.X, r.Y); g.ScaleTransform(k, k); }
                g.DrawImage(img, new RectangleF(0, 0, s, s), new RectangleF(0, 0, s, s), GraphicsUnit.Pixel);
                g.Restore(st);
                return;
            }
            // sombra suave em camadas
            if (Style.CamShadow > 0)
            {
                float blur = r.Width * 0.07f * Style.CamShadow + 2, off = r.Width * 0.025f * Style.CamShadow;
                int steps = 8;
                int a = Math.Max(1, (int)(150 * Style.CamShadow / steps));
                using (var b = new SolidBrush(Color.FromArgb(a, 0, 0, 0)))
                    for (int i = steps; i >= 1; i--)
                    {
                        float e = blur * i / steps;
                        var sr = new RectangleF(r.X - e / 2, r.Y - e / 2 + off, r.Width + e, r.Height + e);
                        using (var sp = Art.RoundRect(sr, rad + e / 2)) g.FillPath(b, sp);
                    }
            }
            using (var tb = new TextureBrush(img, WrapMode.Clamp))
            using (var path = Art.RoundRect(r, rad))
            {
                if (Style.CamMirror)
                {
                    tb.TranslateTransform(r.Right, r.Y);
                    tb.ScaleTransform(-k, k);
                }
                else
                {
                    tb.TranslateTransform(r.X, r.Y);
                    tb.ScaleTransform(k, k);
                }
                tb.TranslateTransform(-sx, -sy);
                g.FillPath(tb, path);
                if (Style.CamBorder > 0.01f)
                {
                    // contorno por dentro da borda
                    float bw = r.Width * 0.045f * Style.CamBorder;
                    var ir = RectangleF.Inflate(r, -bw / 2, -bw / 2);
                    using (var ip = Art.RoundRect(ir, Math.Max(0, rad - bw / 2)))
                    using (var pen = new Pen(Color.FromArgb(Style.CamBorderColor), bw)) g.DrawPath(pen, ip);
                }
                else
                    using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, r.Width / 160f))) g.DrawPath(pen, path);
            }
        }

        void DrawClickFx(Graphics g, double t, Func<float, float, PointF> map, float zoom)
        {
            if (Style.ClickFx == 0) return;
            var col = Color.FromArgb(Style.ClickColor);
            // proporcional ao cursor (que já acompanha o zoom)
            float unit = (map(1, 0).X - map(0, 0).X) * Math.Max(1, Style.CursorSize * 0.6f);
            const double dur = 600, delay = 80;
            foreach (var cl in Clicks)
            {
                double age = t - cl.T - delay;
                if (age < 0 || age > dur) continue;
                double f = age / dur;
                var p = map(cl.X, cl.Y);
                double e = 1 - Math.Pow(1 - f, 3);
                if (Style.ClickFx == 1)
                {
                    float rad = (float)(5 + 26 * e) * unit;
                    int a = (int)(235 * (1 - f));
                    using (var pen = new Pen(Color.FromArgb(a, 255, 255, 255), 2.6f * unit))
                        g.DrawEllipse(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                    using (var pen = new Pen(Color.FromArgb(a * 2 / 3, col), 1.4f * unit))
                        g.DrawEllipse(pen, p.X - rad - 1.6f * unit, p.Y - rad - 1.6f * unit, (rad + 1.6f * unit) * 2, (rad + 1.6f * unit) * 2);
                }
                else
                {
                    float rad = (float)(4 + 30 * e) * unit;
                    int a = (int)(150 * (1 - f));
                    using (var b = new SolidBrush(Color.FromArgb(a, col)))
                        g.FillEllipse(b, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                    using (var pen = new Pen(Color.FromArgb(Math.Min(255, a + 60), col), 1.5f * unit))
                        g.DrawEllipse(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                }
            }
        }

        void DrawCursor(Graphics g, double t, Func<float, float, PointF> map, float zoom)
        {
            int shape;
            MouseAt(t, out shape);
            if (shape < 0 || shape >= Shapes.Count) return;
            float cx, cy, rot, speed;
            CursorAt(t, out cx, out cy, out rot, out speed);
            var cs = Shapes[shape];
            // quique no clique: encolhe e volta (350 ms)
            float bounce = 1;
            if (Style.Bounce > 0)
                foreach (var cl in Clicks)
                {
                    double age = t - cl.T;
                    if (age >= 0 && age <= 350)
                        bounce = Math.Min(bounce, (float)(1 - Math.Sin((1 - age / 350) * Math.PI) * 0.08 * Style.Bounce));
                }
            // 1 = o tamanho real do cursor na tela; acompanha o zoom da câmera
            float s = Style.CursorSize * (map(1, 0).X - map(0, 0).X) * bounce;
            float w = cs.Image.Width * s, h = cs.Image.Height * s;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // rastro: cópias fracas nas posições recentes quando o cursor anda rápido
            if (Style.CursorBlur > 0 && speed > 500)
            {
                float amount = Math.Min(1, (speed - 500) / 2500f) * Style.CursorBlur;
                for (int i = 4; i >= 1; i--)
                {
                    float px, py, pr, psp;
                    CursorAt(t - i * 7, out px, out py, out pr, out psp);
                    var pp = map(px, py);
                    float alpha = amount * 0.22f * (1 - i / 5f);
                    if (alpha < 0.02f) continue;
                    using (var ia = new ImageAttributes())
                    {
                        var cm = new ColorMatrix { Matrix33 = alpha };
                        ia.SetColorMatrix(cm);
                        DrawRotated(g, cs.Image, pp, cs.HotX * s, cs.HotY * s, w, h, pr, ia);
                    }
                }
            }
            var p = map(cx, cy);
            if (Style.CursorShadow)
            {
                var sb = ShadowOf(shape);
                float pad = 3 * s;
                DrawRotated(g, sb, new PointF(p.X + 1.2f * s, p.Y + 2.2f * s), cs.HotX * s + pad, cs.HotY * s + pad, sb.Width * s, sb.Height * s, rot, null);
            }
            DrawRotated(g, cs.Image, p, cs.HotX * s, cs.HotY * s, w, h, rot, null);
            g.InterpolationMode = InterpolationMode.Bilinear;
        }

        static void DrawRotated(Graphics g, Image img, PointF at, float hotX, float hotY, float w, float h, float deg, ImageAttributes ia)
        {
            var st = g.Save();
            g.TranslateTransform(at.X, at.Y);
            if (Math.Abs(deg) > 0.05f) g.RotateTransform(deg);
            var r = new RectangleF(-hotX, -hotY, w, h);
            if (ia == null) g.DrawImage(img, r);
            else g.DrawImage(img, new[] { r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) }, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            g.Restore(st);
        }

        public void Dispose()
        {
            foreach (var s in Shapes) s.Image.Dispose();
            foreach (var b in shadows.Values) b.Dispose();
            if (bgCache != null) bgCache.Dispose();
            if (corners != null) foreach (var c in corners) c.Dispose();
            if (bgDev != null) bgDev.Dispose();
            if (scaled != null) scaled.Dispose();
            if (Cam != null) Cam.Dispose();
            if (camFx != null) camFx.Dispose();
        }

        // Cópia independente para exportar em outra thread enquanto o editor continua aberto
        // (o GDI+ não deixa duas threads desenharem a mesma imagem ao mesmo tempo).
        public VideoProject CloneForExport()
        {
            var c = (VideoProject)MemberwiseClone();
            c.Shapes = new List<CursorShape>();
            foreach (var s in Shapes) c.Shapes.Add(new CursorShape { Image = new Bitmap(s.Image), HotX = s.HotX, HotY = s.HotY });
            c.Zooms = Zooms.Select(z => z.Clone()).ToList();
            c.Speeds = Speeds.Select(z => z.Clone()).ToList();
            c.Style = Style.Clone();
            c.Cam = Cam != null ? Cam.Reopen() : null;
            c.camFx = null;   // a exportação tem o próprio processador (e a própria sessão do modelo)
            c.track = null; c.cursorTrack = null; c.timeMap = null;
            c.bgCache = null; c.bgKey = null; c.corners = null; c.cornerRects = null;
            c.bgDev = null; c.scaled = null; c.scaler = new Scaler();
            typeof(VideoProject).GetField("shadows", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, new Dictionary<int, Bitmap>());
            return c;
        }

        // ---- áudio misturado (48 kHz estéreo) do trecho cortado
        public float[] MixAudio()
        {
            float[] a = UseSys && HasSys ? RawAudio.LoadStereo(Path.Combine(Dir, "sys.raw"), 48000) : null;
            float[] b = UseMic && HasMic ? RawAudio.LoadStereo(Path.Combine(Dir, "mic.raw"), 48000) : null;
            return MixAudio(TrimStart, a, b);
        }

        // Áudio do vídeo final a partir de 'from' (ms da gravação) até o fim do corte, com os trechos
        // de velocidade já aplicados (o tom da voz é mantido; acima de 4× fica mudo).
        public float[] MixAudio(int from, float[] a, float[] b)
        {
            if (a == null && b == null) return null;
            from = Math.Max(TrimStart, Math.Min(TrimEnd, from));
            var parts = new List<float[]>();
            long total = 0;
            foreach (var g in Map)
            {
                if (g.S1 <= from) continue;
                long s0 = (long)(Math.Max(g.S0, from) * 48), s1 = (long)(g.S1 * 48);
                if (s1 <= s0) continue;
                var raw = new float[(s1 - s0) * 2];
                if (a != null) for (long i = 0; i < raw.Length; i++) { long j = s0 * 2 + i; if (j < a.Length) raw[i] += a[j]; }
                if (b != null) for (long i = 0; i < raw.Length; i++) { long j = s0 * 2 + i; if (j < b.Length) raw[i] += b[j]; }
                float[] part;
                if (Math.Abs(g.Rate - 1) < 0.001f) part = raw;
                else if (g.Rate > 4.01f) part = new float[(long)(raw.Length / 2 / g.Rate) * 2];
                else part = TimeStretch.Run(raw, g.Rate);
                parts.Add(part); total += part.Length;
            }
            var mix = new float[total];
            long o = 0;
            foreach (var p in parts) { Array.Copy(p, 0, mix, o, p.Length); o += p.Length; }
            for (long i = 0; i < mix.Length; i++) if (Math.Abs(mix[i]) > 0.0005f) return mix;
            return null;
        }
    }

    // Muda a velocidade do áudio sem mudar o tom (WSOLA: pedaços sobrepostos com janela de Hann,
    // cada um escolhido onde melhor continua o anterior, para não "picotar" a voz).
    static class TimeStretch
    {
        const int N = 1536, Hop = N / 2, Tol = 360;
        static float[] window;

        public static float[] Run(float[] stereo, double rate)
        {
            int n = stereo.Length / 2;
            int outN = (int)(n / rate);
            if (outN <= 0) return new float[0];
            if (n < N * 2)
            {
                // muito curto: reamostra simples
                var r = new float[outN * 2];
                for (int i = 0; i < outN; i++) { int j = Math.Min(n - 1, (int)(i * rate)); r[i * 2] = stereo[j * 2]; r[i * 2 + 1] = stereo[j * 2 + 1]; }
                return r;
            }
            if (window == null)
            {
                var w = new float[N];
                for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
                window = w;
            }
            // mono reduzido para comparar os trechos rápido
            var mono = new float[n];
            for (int i = 0; i < n; i++) mono[i] = stereo[i * 2] + stereo[i * 2 + 1];
            var y = new float[(outN + N) * 2];
            int prev = 0;
            for (int o = 0; o * Hop < outN; o++)
            {
                int ideal = (int)(o * (double)Hop * rate);
                int best = ideal;
                if (o > 0)
                {
                    // o trecho que naturalmente viria depois do anterior
                    int nat = prev + Hop;
                    double bestScore = double.MinValue;
                    int lo = Math.Max(0, ideal - Tol), hi = Math.Min(n - N - 1, ideal + Tol);
                    if (nat + Hop < n)
                        for (int c = lo; c <= hi; c += 2)
                        {
                            double s = 0;
                            for (int k = 0; k < Hop; k += 4) s += mono[nat + k] * mono[c + k];
                            if (s > bestScore) { bestScore = s; best = c; }
                        }
                }
                best = Math.Max(0, Math.Min(n - N, best));
                int at = o * Hop;
                for (int k = 0; k < N; k++)
                {
                    int dst = (at + k) * 2, src = (best + k) * 2;
                    if (dst + 1 >= y.Length) break;
                    float w = window[k];
                    y[dst] += stereo[src] * w; y[dst + 1] += stereo[src + 1] * w;
                }
                prev = best;
            }
            var res = new float[outN * 2];
            Array.Copy(y, res, res.Length);
            return res;
        }
    }

    // Redimensionamento direto na memória dos bitmaps (o do GDI+ é várias vezes mais lento para quadros
    // grandes), dividido entre os núcleos do processador, com mistura opcional com o quadro anterior
    // (desfoque de movimento). Sharp: bicúbico (Catmull-Rom) quando amplia, para o zoom não ficar borrado.
    class Scaler
    {
        int[] prev;
        int prevW, prevH;
        bool havePrev;
        int[] x0, fx;            // bilinear: coluna de origem e fração (0..256)
        int[] cx, cwx;           // bicúbico: 4 colunas e 4 pesos (x256) por coluna de destino
        public bool Sharp;

        public void Forget() { havePrev = false; }

        public void Bilinear(Bitmap src, RectangleF s, Bitmap dst, float blend)
        {
            var dd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { ScaleInto(src, s, dd, new Rectangle(0, 0, dst.Width, dst.Height), blend); }
            finally { dst.UnlockBits(dd); }
        }

        // Escreve o recorte s de src no retângulo r de um bitmap já travado (32 bpp).
        public unsafe void ScaleInto(Bitmap src, RectangleF s, BitmapData dd, Rectangle r, float blend)
        {
            int sw = src.Width, sh = src.Height, dw = r.Width, dh = r.Height;
            if (prev == null || prevW != dw || prevH != dh) { prev = new int[dw * dh]; prevW = dw; prevH = dh; havePrev = false; }
            float kx = s.Width / dw, ky = s.Height / dh;
            bool cubic = Sharp && kx < 0.98f && ky < 0.98f;   // só quando amplia
            if (cubic) PrepCubic(s, sw, dw, kx);
            else PrepLinear(s, sw, dw, kx);
            bool mix = blend > 0 && havePrev;
            uint wb = (uint)(blend * 256), wa = 256 - wb;
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte* sBase = (byte*)sd.Scan0, dBase = (byte*)dd.Scan0 + (long)r.Y * dd.Stride + r.X * 4;
                int sStride = sd.Stride, dStride = dd.Stride;
                int[] pv = prev, X0 = x0, FX = fx, CX = cx, CW = cwx;
                float sy0 = s.Y;
                System.Threading.Tasks.Parallel.For(0, dh, y =>
                {
                    uint* o = (uint*)(dBase + (long)y * dStride);
                    float v = sy0 + (y + 0.5f) * ky - 0.5f;
                    if (cubic) CubicRow(sBase, sStride, sw, sh, v, o, dw, CX, CW);
                    else
                    {
                        v = Math.Max(0, Math.Min(sh - 1.001f, v));
                        int yi = (int)v; uint fy = (uint)((v - yi) * 256), gy = 256 - fy;
                        uint* r0 = (uint*)(sBase + (long)yi * sStride), r1 = (uint*)(sBase + (long)Math.Min(yi + 1, sh - 1) * sStride);
                        for (int x = 0; x < dw; x++)
                        {
                            int xi = X0[x];
                            int xj = xi + 1 < sw ? xi + 1 : xi;
                            uint f = (uint)FX[x], gx = 256 - f;
                            uint a = r0[xi], b = r0[xj], c = r1[xi], d = r1[xj];
                            uint rbT = (((a & 0xFF00FF) * gx + (b & 0xFF00FF) * f) >> 8) & 0xFF00FF;
                            uint gT = (((a & 0xFF00) * gx + (b & 0xFF00) * f) >> 8) & 0xFF00;
                            uint rbB = (((c & 0xFF00FF) * gx + (d & 0xFF00FF) * f) >> 8) & 0xFF00FF;
                            uint gB = (((c & 0xFF00) * gx + (d & 0xFF00) * f) >> 8) & 0xFF00;
                            uint rb = ((rbT * gy + rbB * fy) >> 8) & 0xFF00FF;
                            uint gg = ((gT * gy + gB * fy) >> 8) & 0xFF00;
                            o[x] = 0xFF000000 | rb | gg;
                        }
                    }
                    // guarda a linha para o próximo quadro (e mistura com a anterior, se for o caso)
                    int po = y * dw;
                    if (mix)
                        for (int x = 0; x < dw; x++)
                        {
                            uint a = o[x], b = (uint)pv[po + x];
                            uint rb = (((a & 0xFF00FF) * wa + (b & 0xFF00FF) * wb) >> 8) & 0xFF00FF;
                            uint gg = (((a & 0xFF00) * wa + (b & 0xFF00) * wb) >> 8) & 0xFF00;
                            o[x] = 0xFF000000 | rb | gg;
                            pv[po + x] = (int)o[x];
                        }
                    else Marshal.Copy((IntPtr)o, pv, po, dw);
                });
            }
            finally { src.UnlockBits(sd); }
            havePrev = true;
        }

        void PrepLinear(RectangleF s, int sw, int dw, float kx)
        {
            if (x0 == null || x0.Length != dw) { x0 = new int[dw]; fx = new int[dw]; }
            for (int x = 0; x < dw; x++)
            {
                float u = s.X + (x + 0.5f) * kx - 0.5f;
                u = Math.Max(0, Math.Min(sw - 1.001f, u));
                x0[x] = (int)u; fx[x] = (int)((u - (int)u) * 256);
            }
        }

        // Pesos do Catmull-Rom para a fração t (x256, somam 256).
        static void CubicW(float t, int[] w, int at)
        {
            float t2 = t * t, t3 = t2 * t;
            float w0 = (-t3 + 2 * t2 - t) * 0.5f, w1 = (3 * t3 - 5 * t2 + 2) * 0.5f, w2 = (-3 * t3 + 4 * t2 + t) * 0.5f;
            int a = (int)Math.Round(w0 * 256), b = (int)Math.Round(w1 * 256), c = (int)Math.Round(w2 * 256);
            w[at] = a; w[at + 1] = b; w[at + 2] = c; w[at + 3] = 256 - a - b - c;
        }

        void PrepCubic(RectangleF s, int sw, int dw, float kx)
        {
            if (cx == null || cx.Length != dw * 4) { cx = new int[dw * 4]; cwx = new int[dw * 4]; }
            for (int x = 0; x < dw; x++)
            {
                float u = s.X + (x + 0.5f) * kx - 0.5f;
                u = Math.Max(0, Math.Min(sw - 1.001f, u));
                int ui = (int)Math.Floor(u);
                for (int k = 0; k < 4; k++) cx[x * 4 + k] = Math.Max(0, Math.Min(sw - 1, ui - 1 + k));
                CubicW(u - ui, cwx, x * 4);
            }
        }

        static unsafe void CubicRow(byte* sBase, int sStride, int sw, int sh, float v, uint* o, int dw, int[] CX, int[] CW)
        {
            v = Math.Max(0, Math.Min(sh - 1.001f, v));
            int vi = (int)Math.Floor(v);
            var wy = new int[4];
            CubicW(v - vi, wy, 0);
            uint* r0 = (uint*)(sBase + (long)Math.Max(0, vi - 1) * sStride);
            uint* r1 = (uint*)(sBase + (long)vi * sStride);
            uint* r2 = (uint*)(sBase + (long)Math.Min(sh - 1, vi + 1) * sStride);
            uint* r3 = (uint*)(sBase + (long)Math.Min(sh - 1, vi + 2) * sStride);
            int y0 = wy[0], y1 = wy[1], y2 = wy[2], y3 = wy[3];
            for (int x = 0; x < dw; x++)
            {
                int i = x * 4;
                int c0 = CX[i], c1 = CX[i + 1], c2 = CX[i + 2], c3 = CX[i + 3];
                int w0 = CW[i], w1 = CW[i + 1], w2 = CW[i + 2], w3 = CW[i + 3];
                // primeiro na horizontal (4 linhas), depois na vertical
                uint p0 = r0[c0], p1 = r0[c1], p2 = r0[c2], p3 = r0[c3];
                int ra = (int)((p0 >> 16) & 255) * w0 + (int)((p1 >> 16) & 255) * w1 + (int)((p2 >> 16) & 255) * w2 + (int)((p3 >> 16) & 255) * w3;
                int ga = (int)((p0 >> 8) & 255) * w0 + (int)((p1 >> 8) & 255) * w1 + (int)((p2 >> 8) & 255) * w2 + (int)((p3 >> 8) & 255) * w3;
                int ba = (int)(p0 & 255) * w0 + (int)(p1 & 255) * w1 + (int)(p2 & 255) * w2 + (int)(p3 & 255) * w3;
                p0 = r1[c0]; p1 = r1[c1]; p2 = r1[c2]; p3 = r1[c3];
                int rb = (int)((p0 >> 16) & 255) * w0 + (int)((p1 >> 16) & 255) * w1 + (int)((p2 >> 16) & 255) * w2 + (int)((p3 >> 16) & 255) * w3;
                int gb = (int)((p0 >> 8) & 255) * w0 + (int)((p1 >> 8) & 255) * w1 + (int)((p2 >> 8) & 255) * w2 + (int)((p3 >> 8) & 255) * w3;
                int bb = (int)(p0 & 255) * w0 + (int)(p1 & 255) * w1 + (int)(p2 & 255) * w2 + (int)(p3 & 255) * w3;
                p0 = r2[c0]; p1 = r2[c1]; p2 = r2[c2]; p3 = r2[c3];
                int rc = (int)((p0 >> 16) & 255) * w0 + (int)((p1 >> 16) & 255) * w1 + (int)((p2 >> 16) & 255) * w2 + (int)((p3 >> 16) & 255) * w3;
                int gc = (int)((p0 >> 8) & 255) * w0 + (int)((p1 >> 8) & 255) * w1 + (int)((p2 >> 8) & 255) * w2 + (int)((p3 >> 8) & 255) * w3;
                int bc = (int)(p0 & 255) * w0 + (int)(p1 & 255) * w1 + (int)(p2 & 255) * w2 + (int)(p3 & 255) * w3;
                p0 = r3[c0]; p1 = r3[c1]; p2 = r3[c2]; p3 = r3[c3];
                int rd = (int)((p0 >> 16) & 255) * w0 + (int)((p1 >> 16) & 255) * w1 + (int)((p2 >> 16) & 255) * w2 + (int)((p3 >> 16) & 255) * w3;
                int gd = (int)((p0 >> 8) & 255) * w0 + (int)((p1 >> 8) & 255) * w1 + (int)((p2 >> 8) & 255) * w2 + (int)((p3 >> 8) & 255) * w3;
                int bd = (int)(p0 & 255) * w0 + (int)(p1 & 255) * w1 + (int)(p2 & 255) * w2 + (int)(p3 & 255) * w3;
                int r = (ra * y0 + rb * y1 + rc * y2 + rd * y3 + 32768) >> 16;
                int g = (ga * y0 + gb * y1 + gc * y2 + gd * y3 + 32768) >> 16;
                int b = (ba * y0 + bb * y1 + bc * y2 + bd * y3 + 32768) >> 16;
                r = r < 0 ? 0 : r > 255 ? 255 : r; g = g < 0 ? 0 : g > 255 ? 255 : g; b = b < 0 ? 0 : b > 255 ? 255 : b;
                o[x] = 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }
    }
}
