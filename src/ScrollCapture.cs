// Print com rolagem: o Capturo rola sozinho o conteúdo sob a área escolhida, fotografa a cada
// passo e emenda as partes numa imagem longa. Cabeçalhos e rodapés fixos (que não rolam junto)
// são detectados e entram uma vez só.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Capturo
{
    class ScrollCapture
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        const int MOUSEEVENTF_WHEEL = 0x0800;

        public static ScrollCapture Current;

        readonly TrayApp app;
        readonly Rectangle area;
        readonly RecFrame frame;
        readonly ScrollPanel panel;
        volatile bool stop, cancel;
        const int MaxPixels = 40000000;   // ~160 MB: a imagem final fica com no máximo isso

        ScrollCapture(TrayApp app, Rectangle area)
        {
            this.app = app; this.area = area;
            frame = new RecFrame(area) { Armed = true };
            panel = new ScrollPanel(area);
            panel.StopClicked += () => stop = true;
            panel.CancelClicked += () => { cancel = true; stop = true; };
        }

        public static void Start(TrayApp app, Rectangle area)
        {
            if (Current != null) return;
            if (area.Width < 40 || area.Height < 40) { app.Notify("Área pequena demais", "Escolha uma área maior para o print com rolagem.", null); return; }
            Current = new ScrollCapture(app, area);
            Current.Begin();
        }

        public void Stop() { stop = true; }

        void Begin()
        {
            frame.Show(); panel.Show();
            GifRecorder.ExcludeFromCapture(frame.Handle); GifRecorder.ExcludeFromCapture(panel.Handle);
            new Thread(Run) { IsBackground = true, Name = "rolagem" }.Start();
        }

        // ---- captura
        Bitmap grabBmp;

        int[] Grab()
        {
            if (grabBmp == null) grabBmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(grabBmp))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
            var px = new int[area.Width * area.Height];
            var d = grabBmp.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < area.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * area.Width, area.Width);
            grabBmp.UnlockBits(d);
            return px;
        }

        // Espera a página parar de se mexer (rolagem suave, imagens carregando).
        // before: a imagem de antes de rolar; primeiro espera ela mudar (a rolagem do navegador
        // é animada e pode demorar a começar), depois espera parar.
        int[] GrabStable()
        {
            var px = Stitcher.GrabStable(Grab, area.Width, area.Height, lastPx, () => stop);
            lastPx = px;
            return px;
        }

        int[] lastPx;   // a última imagem parada (antes de rolar de novo)

        void Run()
        {
            Point old; GetCursorPos(out old);
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            Stitcher st = null;
            string why = null;
            try
            {
                Thread.Sleep(200);   // a sobreposição da captura acabou de fechar
                SetCursorPos(center.X, center.Y);
                st = new Stitcher(area.Width, area.Height, GrabStable());
                TrayApp.Log("rolagem: área " + area);
                int maxRows = Math.Max(area.Height, MaxPixels / area.Width);
                while (!stop)
                {
                    if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) break;   // Esc: para e salva o que já tem
                    Point now; GetCursorPos(out now);
                    if (Math.Abs(now.X - center.X) > 3 || Math.Abs(now.Y - center.Y) > 3)
                    {
                        // mexeu no mouse (talvez para clicar em Concluir/Cancelar): para de rolar e espera a escolha
                        TrayApp.Log("rolagem: mouse movido para " + now + " (centro " + center + ")");
                        panel.SetPaused();
                        while (!stop && (GetAsyncKeyState(0x1B) & 0x8000) == 0) Thread.Sleep(50);
                        break;
                    }
                    if (st.Rows.Count >= maxRows) { why = "Parei no limite de tamanho da imagem."; break; }

                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -120 * st.Notches, IntPtr.Zero);
                    var cur = GrabStable();
                    if (stop) break;
                    int d = st.Add(cur);
                    TrayApp.Log("rolagem: parte " + (st.Parts + (d > 0 ? 0 : 1)) + ": " + st.LastInfo);
                    if (d == 0) break;   // não andou: chegou ao fim
                    if (d < 0) { why = st.Parts == 1 ? "Não consegui emendar: a área não rolou ou o conteúdo muda sozinho." : "Parei: não consegui emendar a próxima parte (conteúdo mudando ou rolagem grande demais)."; break; }
                    panel.SetStatus(st.Parts, st.Rows.Count);
                }
                st.Finish();
            }
            catch (Exception ex) { why = "Erro: " + ex.Message; TrayApp.Log("rolagem: " + ex); }
            finally
            {
                Point now; GetCursorPos(out now);
                if (Math.Abs(now.X - center.X) <= 3 && Math.Abs(now.Y - center.Y) <= 3) SetCursorPos(old.X, old.Y);
                if (grabBmp != null) { grabBmp.Dispose(); grabBmp = null; }
            }
            var rows = st != null ? st.Rows : new List<int[]>();
            int parts = st != null ? st.Parts : 0;
            app.RunOnUi(() => Finish(rows, parts, why));
        }

        void Finish(List<int[]> rows, int parts, string why)
        {
            Current = null;
            frame.Close(); panel.Close();
            if (cancel || rows.Count == 0) { app.Notify("Print com rolagem cancelado", "Nenhuma imagem foi salva.", null); return; }
            int w = rows[0].Length, h = rows.Count;
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var line = new int[w];
            for (int y = 0; y < h; y++)
            {
                var r = rows[y];
                for (int x = 0; x < w; x++) line[x] = r[x] | unchecked((int)0xFF000000);
                Marshal.Copy(line, 0, d.Scan0 + y * d.Stride, w);
            }
            b.UnlockBits(d);
            rows.Clear();
            app.FinishScroll(b, parts, why, area);
        }
    }

    // Emenda as partes: acha quanto o conteúdo andou entre uma imagem e a próxima comparando uma
    // "assinatura" de cada linha, e guarda só as linhas novas. Cabeçalho e rodapé fixos entram uma vez.
    // Só as colunas que de fato rolam entram na comparação (e na imagem final): menus laterais e
    // painéis fixos ficam de fora, senão as linhas nunca batem.
    // Assinatura de uma imagem: uma por linha, se a linha é lisa e a da faixa da direita (barra de rolagem).
    class Sig { public ulong[] Rows; public bool[] Flat; public ulong Strip; }

    class Stitcher
    {
        public readonly int W, H;
        public readonly List<int[]> Rows = new List<int[]>();
        public int Notches = 2, Parts = 1;   // rolagem do próximo passo (cliques da roda) e partes emendadas
        public int CropX0 = -1, CropX1 = -1; // colunas que rolam (a imagem final é recortada nelas)
        public string LastInfo = "";         // diagnóstico do último passo (vai para o log)
        int[] last;
        int botNow, expected;
        double perNotch;

        public Stitcher(int w, int h, int[] first)
        {
            W = w; H = h;
            for (int y = 0; y < h; y++) Rows.Add(Line(first, y));
            last = first;
        }

        int[] Line(int[] px, int y) { var r = new int[W]; Array.Copy(px, y * W, r, 0, W); return r; }

        public static Sig Hash(int[] px, int w, int h) { return Hash(px, w, h, 0, w); }

        // Assinatura por linha nas colunas [x0, x1) (sem a faixa da direita, onde a barra de rolagem
        // muda a cada passo) e se a linha é de uma cor só (não serve para achar o encaixe).
        public static Sig Hash(int[] px, int w, int h, int x0, int x1)
        {
            int span = x1 - x0;
            int cut = span > 300 ? Math.Min(40, span / 12) : 0, used = x1 - cut;
            var hs = new ulong[h]; var fl = new bool[h];
            ulong strip = 14695981039346656037UL;
            for (int y = 0; y < h; y++)
                for (int x = used; x < x1; x++) strip = (strip ^ (uint)(px[y * w + x] & 0xFFFFFF)) * 1099511628211UL;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                ulong v = 14695981039346656037UL;
                int o = y * w, first = px[o + x0] & 0xFFFFFF;
                bool same = true;
                for (int x = x0; x < used; x++)
                {
                    int c = px[o + x] & 0xFFFFFF;
                    v = (v ^ (uint)c) * 1099511628211UL;
                    if (c != first) same = false;
                }
                hs[y] = v; fl[y] = same;
            });
            return new Sig { Rows = hs, Flat = fl, Strip = strip };
        }

        public static bool Same(Sig a, Sig b)
        {
            if (a.Strip != b.Strip) return false;
            for (int i = 0; i < a.Rows.Length; i++) if (a.Rows[i] != b.Rows[i]) return false;
            return true;
        }

        // Linhas com desenho de n que batem com as de p deslocadas d linhas para cima (n[i] == p[i + d]).
        static int Matches(Sig p, Sig n, int d)
        {
            int c = 0, h = p.Rows.Length;
            for (int i = 0; i + d < h; i++) if (n.Rows[i] == p.Rows[i + d] && !n.Flat[i]) c++;
            return c;
        }

        // b não rolou em relação a a: nas colunas que mudaram, nenhum deslocamento bate melhor que a
        // mesma posição. Uma animação na página (banner, vídeo, imagem aparecendo) muda algumas linhas sem
        // parar; isto não impede de ver que a rolagem acabou, como a exigência de imagens idênticas impedia.
        // (Menus e painéis fixos ficam fora das colunas comparadas, senão escondem o deslocamento.)
        public static bool Settled(int[] a, int[] b, int w, int h)
        {
            int x0, x1;
            Moving(a, b, w, h, out x0, out x1);
            if (x1 - x0 < 8) return true;   // só mudanças miúdas (cursor piscando)
            Sig sa = Hash(a, w, h, x0, x1), sb = Hash(b, w, h, x0, x1);
            int s0 = Matches(sa, sb, 0), best = 0;
            for (int d = 1; d < h * 3 / 4; d++) best = Math.Max(best, Matches(sa, sb, d));
            if (s0 < 8 && best < 8) return false;   // quase tudo liso: não dá para afirmar
            return best <= s0;
        }

        // Espera a página parar de se mexer (rolagem suave, imagens carregando).
        // before: a imagem de antes de rolar; primeiro espera o conteúdo andar (a rolagem do navegador
        // é animada e pode demorar a começar), depois espera parar. Parado = igual à anterior ou, com algo
        // animando na página, sem deslocamento por alguns quadros seguidos.
        public static int[] GrabStable(Func<int[]> grab, int w, int h, int[] before, Func<bool> stop)
        {
            Thread.Sleep(60);
            var a = grab();
            var ha = Hash(a, w, h);
            Sig hb0 = before != null ? Hash(before, w, h) : null;
            for (int waited = 0; before != null && (Same(ha, hb0) || Settled(before, a, w, h)) && waited < 1200 && !stop(); waited += 60)
            {
                Thread.Sleep(60);
                a = grab(); ha = Hash(a, w, h);
            }
            int still = 0;
            for (int i = 0; i < 20 && !stop(); i++)
            {
                Thread.Sleep(70);
                var b = grab();
                var hb = Hash(b, w, h);
                if (Same(ha, hb)) break;
                bool settled = Settled(a, b, w, h);
                a = b; ha = hb;
                if (settled && ++still >= 3) break;   // ~0,2 s sem rolar: o resto é animação
                if (!settled) still = 0;
            }
            return a;
        }

        // Colunas que mudaram de verdade entre duas imagens (uma parte boa das linhas mudou):
        // um cursor piscando ou um destaque num menu fixo não contam.
        public static void Moving(int[] a, int[] b, int w, int h, out int x0, out int x1) { Moving(a, b, w, h, 0, out x0, out x1); }

        // minFrac > 0: só conta a coluna que mudou em pelo menos essa fração das linhas da que mais mudou
        // (um cabeçalho que muda de cor ao rolar faz até um menu lateral fixo parecer "mexer").
        public static void Moving(int[] a, int[] b, int w, int h, double minFrac, out int x0, out int x1)
        {
            var cnt = new int[w];
            System.Threading.Tasks.Parallel.For(0, h, () => new int[w], (y, s, local) =>
            {
                int o = y * w;
                for (int x = 0; x < w; x++) if (((a[o + x] ^ b[o + x]) & 0xFFFFFF) != 0) local[x]++;
                return local;
            }, local => { lock (cnt) for (int x = 0; x < w; x++) cnt[x] += local[x]; });
            int need = Math.Max(3, h / 25);
            if (minFrac > 0) { int max = 0; foreach (int c in cnt) max = Math.Max(max, c); need = Math.Max(need, (int)(max * minFrac)); }
            x0 = 0; while (x0 < w && cnt[x0] < need) x0++;
            x1 = w; while (x1 > x0 && cnt[x1 - 1] < need) x1--;
        }

        // Quanto o conteúdo andou entre a imagem anterior (p) e a nova (n). top/bot: faixas fixas
        // (cabeçalho e rodapé). -1: não achou o encaixe; 0: nada mudou (fim da página).
        public static int Offset(Sig ps, Sig ns, int expected, out int top, out int bot)
        {
            ulong[] p = ps.Rows, n = ns.Rows; bool[] nFlat = ns.Flat;
            int h = p.Length;
            // faixas fixas: linhas iguais no topo e no fim; as lisas na borda delas não contam
            // (um espaço em branco no conteúdo também fica "igual" e não é cabeçalho)
            top = 0; while (top < h / 2 && p[top] == n[top]) top++;
            while (top > 0 && nFlat[top - 1]) top--;
            bot = 0; while (bot < h / 2 && p[h - 1 - bot] == n[h - 1 - bot]) bot++;
            while (bot > 0 && nFlat[h - bot]) bot--;
            bool same = true;
            for (int i = 0; i < h && same; i++) if (p[i] != n[i]) same = false;
            if (same)
            {
                // tudo igual, mas a barra de rolagem andou: passou por um trecho em branco
                if (ps.Strip != ns.Strip && expected > 0) return Math.Min(expected, h - top - bot - 1);
                return 0;
            }
            int L = h - top - bot;
            int bestD = -1, bestInfo = -1, bestDist = int.MaxValue;
            for (int d = 1; d < L; d++)
            {
                int overlap = L - d;
                if (overlap < Math.Min(24, L / 4)) break;
                int info = 0, miss = 0, allowMiss = overlap / 4;
                for (int i = 0; i < overlap; i++)
                {
                    if (n[top + i] == p[top + i + d]) { if (!nFlat[top + i]) info++; }
                    else if (++miss > allowMiss) break;
                }
                if (miss > allowMiss) continue;
                int dist = expected > 0 ? Math.Abs(d - expected) : 0;
                if (info > bestInfo || (info == bestInfo && dist < bestDist)) { bestD = d; bestInfo = info; bestDist = dist; }
            }
            // só linhas lisas batendo (um trecho em branco): sem pista do quanto andou, usa o esperado
            if (bestD > 0 && bestInfo < 6 && expected <= 0) return -1;
            if (bestD > 0) return bestD;
            return Loose(p, n, nFlat, top, L, expected);
        }

        // Sem encaixe com no máximo 1/4 das linhas diferentes (algo na página mudou sozinho: banner
        // animado, cabeçalho que encolhe, imagem aparecendo, botão "voltar ao topo"): aceita o
        // deslocamento com mais linhas de conteúdo batendo, se ele se destaca com folga dos outros.
        static int Loose(ulong[] p, ulong[] n, bool[] nFlat, int top, int L, int expected)
        {
            int bestD = -1, best = 0, second = 0;
            var score = new int[L];
            for (int d = 1; d < L; d++)
            {
                int overlap = L - d;
                if (overlap < Math.Min(24, L / 4)) break;
                int info = 0;
                for (int i = 0; i < overlap; i++) if (!nFlat[top + i] && n[top + i] == p[top + i + d]) info++;
                score[d] = info;
                if (info > best || (info == best && expected > 0 && Math.Abs(d - expected) < Math.Abs(bestD - expected))) { bestD = d; best = info; }
            }
            for (int d = 1; d < L; d++) if (Math.Abs(d - bestD) > 3 && score[d] > second) second = score[d];
            // nada andou (só a animação mudou): é o fim da página
            int still = 0;
            for (int i = 0; i < L; i++) if (!nFlat[top + i] && n[top + i] == p[top + i]) still++;
            if (still >= 8 && still >= best) return 0;
            if (bestD <= 0 || best < Math.Max(20, (L - bestD) / 10) || best < second * 2) return -1;
            return bestD;
        }

        // Próxima imagem: devolve quanto andou (linhas novas guardadas), 0 no fim ou -1 se não encaixou.
        public int Add(int[] cur)
        {
            int x0, x1;
            Moving(last, cur, W, H, out x0, out x1);
            if (x1 - x0 < 8) { LastInfo = "nada mudou"; return 0; }
            // a faixa que rola não encolhe de um passo para o outro (as partes ficam alinhadas)
            if (CropX0 >= 0) { x0 = Math.Min(x0, CropX0); x1 = Math.Max(x1, CropX1); }
            int top, bot;
            var ps = Hash(last, W, H, x0, x1);
            var ns = Hash(cur, W, H, x0, x1);
            int d = Offset(ps, ns, expected, out top, out bot);
            if (d < 0)
            {
                // de novo só com as colunas que mudaram muito (o conteúdo que rola), sem as que mudaram só
                // numa faixa (menu fixo sob um cabeçalho que mudou)
                int rx0, rx1;
                Moving(last, cur, W, H, 0.2, out rx0, out rx1);
                if (CropX0 >= 0) { rx0 = Math.Min(rx0, CropX0); rx1 = Math.Max(rx1, CropX1); }
                if (rx1 - rx0 >= 8 && (rx0 != x0 || rx1 != x1))
                {
                    int t2, b2;
                    int d2 = Offset(Hash(last, W, H, rx0, rx1), Hash(cur, W, H, rx0, rx1), expected, out t2, out b2);
                    if (d2 >= 0) { d = d2; x0 = rx0; x1 = rx1; top = t2; bot = b2; }
                }
            }
            LastInfo = "colunas " + x0 + "-" + x1 + ", topo " + top + ", rodapé " + bot + ", andou " + d + ", cliques " + Notches;
            if (d <= 0) return d;
            CropX0 = x0; CropX1 = x1;
            // rodapé fixo: sai do fim do que já foi emendado (ele entra uma vez, no final)
            if (bot > botNow)
            {
                int extra = Math.Min(bot - botNow, Rows.Count);
                Rows.RemoveRange(Rows.Count - extra, extra);
                botNow = bot;
            }
            for (int y = Math.Max(0, H - botNow - d); y < H - botNow; y++) Rows.Add(Line(cur, y));
            Parts++;
            // ajusta a rolagem para andar ~metade da área por passo (sobra encaixe de sobra)
            double pn = d / (double)Notches;
            perNotch = perNotch <= 0 ? pn : perNotch * 0.5 + pn * 0.5;
            int L = H - top - bot;
            Notches = Math.Max(1, Math.Min(15, (int)Math.Round(L * 0.5 / Math.Max(1, perNotch))));
            expected = (int)Math.Round(perNotch * Notches);
            last = cur;
            return d;
        }

        // Rodapé fixo da última imagem e recorte nas colunas que rolam.
        public void Finish()
        {
            for (int y = H - botNow; y < H; y++) Rows.Add(Line(last, y));
            botNow = 0;
            if (CropX0 > 0 || (CropX1 > 0 && CropX1 < W))
            {
                // devolve a margem lisa da página (até 40 px de cada lado); um menu fixo, com desenho, fica de fora
                Func<int, bool> plain = x =>
                {
                    int c = last[(H / 4) * W + x] & 0xFFFFFF;
                    for (int y = H / 4; y < H * 3 / 4; y++) if ((last[y * W + x] & 0xFFFFFF) != c) return false;
                    return true;
                };
                for (int i = 0; i < 40 && CropX0 > 0 && plain(CropX0 - 1); i++) CropX0--;
                for (int i = 0; i < 40 && CropX1 < W && plain(CropX1); i++) CropX1++;
                int w = CropX1 - CropX0;
                for (int i = 0; i < Rows.Count; i++)
                {
                    var r = new int[w]; Array.Copy(Rows[i], CropX0, r, 0, w); Rows[i] = r;
                }
            }
        }

        public int Width { get { return Rows.Count > 0 ? Rows[0].Length : W; } }
    }

    // Barrinha embaixo da área durante o print com rolagem: andamento e os botões Concluir / Cancelar.
    class ScrollPanel : Form
    {
        public event Action StopClicked, CancelClicked;
        readonly float k;
        readonly Font font, fontB;
        readonly Rectangle doneR, cancelR;
        int hot = -1, parts = 1, height;
        bool paused;
        string Status { get { return paused ? "Pausado: o mouse foi movido" : parts <= 1 ? "Rolando e capturando…" : parts + " partes · " + height + " px de altura"; } }
        string Hint { get { return paused ? "Concluir salva · Cancelar descarta" : "Não mexa o mouse · Esc conclui"; } }

        int D(float v) { return (int)Math.Round(v * k); }

        public ScrollPanel(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false; TopMost = true;
            DoubleBuffered = true;
            using (var g = CreateGraphics()) k = Math.Max(1f, g.DpiX / 96f);
            font = new Font("Segoe UI", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fontB = new Font("Segoe UI Semibold", 13 * k, FontStyle.Regular, GraphicsUnit.Pixel);
            int w = D(470), h = D(52);
            doneR = new Rectangle(w - D(10) - D(82) - D(6) - D(100), D(9), D(100), D(34));
            cancelR = new Rectangle(w - D(10) - D(82), D(9), D(82), D(34));
            var mon = Screen.FromRectangle(area).WorkingArea;
            int bx = Math.Max(mon.Left, Math.Min(area.Left + (area.Width - w) / 2, mon.Right - w));
            int by = area.Bottom + D(12);
            if (by + h > mon.Bottom) by = area.Top - h - D(12);
            if (by < mon.Top) by = mon.Top + D(12);   // a área ocupa a altura toda: fica em cima (fora dela, se der)
            Bounds = new Rectangle(bx, by, w, h);
            using (var path = Art.RoundRect(new RectangleF(0, 0, w, h), h / 2f)) Region = new Region(path);
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // TOOLWINDOW | NOACTIVATE
        }

        public void SetStatus(int parts, int height)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { this.parts = parts; this.height = height; Invalidate(); })); } catch { }
        }

        public void SetPaused()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { paused = true; Invalidate(); })); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(24, 24, 30));
            // seta para baixo
            float cx = D(26), cy = Height / 2f;
            using (var pen = new Pen(Art.PickBlue, D(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cx, cy - D(8), cx, cy + D(7));
                g.DrawLines(pen, new[] { new PointF(cx - D(5), cy + D(2)), new PointF(cx, cy + D(7)), new PointF(cx + D(5), cy + D(2)) });
            }
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(Status, fontB, Brushes.White, new RectangleF(D(44), 0, doneR.X - D(48), Height * 0.6f), sf);
                using (var dim = new SolidBrush(Color.FromArgb(160, 160, 175)))
                    g.DrawString(Hint, font, dim, new RectangleF(D(44), Height * 0.45f, doneR.X - D(48), Height * 0.5f), sf);
            }
            Btn(g, doneR, "Concluir", hot == 0, Art.PickBlue, fontB);
            Btn(g, cancelR, "Cancelar", hot == 1, Color.FromArgb(70, 70, 80), font);
        }

        void Btn(Graphics g, Rectangle r, string text, bool hover, Color bg, Font f)
        {
            Art.FillRound(g, hover ? ControlPaint.Light(bg, 0.25f) : bg, r, r.Height / 2f);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, Brushes.White, r, sf);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = doneR.Contains(e.Location) ? 0 : cancelR.Contains(e.Location) ? 1 : -1;
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (doneR.Contains(e.Location)) { if (StopClicked != null) StopClicked(); }
            else if (cancelR.Contains(e.Location)) { if (CancelClicked != null) CancelClicked(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            font.Dispose(); fontB.Dispose();
            base.OnFormClosed(e);
        }
    }
}
