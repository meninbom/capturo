// OCR com o leitor de texto embutido no Windows 10/11 (Windows.Media.Ocr), sem dependências extras.
//
// O que aprendemos medindo com prints reais:
//  - ampliar com suavização (bicúbica/bilinear) piora MUITO: o "m" vira "rn", "quando" vira "quuxio";
//  - ampliar sem suavização (vizinho mais próximo) ajuda em letra pequena e acha itens isolados
//    ("1", "}") que a leitura em tamanho original perde;
//  - texto claro demais (cinza sobre branco) melhora esticando o contraste.
// Por isso: lê em mais de uma escala, escolhe a leitura mais plausível e completa com as linhas
// que só as outras encontraram. Depois remonta o layout (linhas, colunas, parágrafos, recuo).
using System;
using System.Collections.Generic;
using System.Drawing;
using Size = System.Drawing.Size;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;

namespace Capturo
{
    class OcrWord
    {
        public string Text;
        public RectangleF Box;          // coordenadas da imagem original
        public int Start, Length;       // posição no texto montado (depende do modo de layout)
    }

    class OcrLine
    {
        public List<OcrWord> Words = new List<OcrWord>();
        public RectangleF Box;
    }

    enum OcrLayout { Lines, Paragraphs, SingleLine }

    class OcrDoc
    {
        public List<OcrLine> Lines = new List<OcrLine>();
        public string Language = "";
        public Size ImageSize;
        public IEnumerable<OcrWord> Words { get { return Lines.SelectMany(l => l.Words); } }
        public bool LooksLikeCode;

        // Monta o texto no modo pedido e anota em cada palavra onde ela ficou no texto.
        public string Build(OcrLayout mode)
        {
            var sb = new StringBuilder();
            var rows = Rows();
            if (rows.Count == 0) return "";
            float lineH = Median(Lines.Select(l => l.Box.Height)), charW = CharWidth();
            float minX = rows.Min(r => r[0].Box.X);
            // distância típica entre linhas: só um salto bem maior que ela vira parágrafo novo
            Func<List<OcrLine>, float> mid = r => r.Average(l => l.Box.Top + l.Box.Height / 2);
            var pitches = new List<float>();
            for (int ri = 1; ri < rows.Count; ri++) pitches.Add(mid(rows[ri]) - mid(rows[ri - 1]));
            // espaçamento "normal" = um dos menores (com poucas linhas a mediana já pega os saltos)
            pitches.Sort();
            float pitch = pitches.Count > 0 ? pitches[(int)(pitches.Count * 0.3)] : lineH * 1.5f;
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var row = rows[ri];
                if (ri > 0)
                {
                    float p = mid(row) - mid(rows[ri - 1]);
                    bool newPara = p > pitch * 1.4f && p > lineH * 1.8f;
                    if (mode == OcrLayout.SingleLine) sb.Append(' ');
                    else if (mode == OcrLayout.Paragraphs)
                    {
                        if (newPara) sb.Append("\r\n\r\n");
                        else if (sb.Length > 0 && sb[sb.Length - 1] == '-' && row[0].Words.Count > 0 && char.IsLower(row[0].Words[0].Text, 0)) sb.Length--;   // palavra hifenizada
                        else sb.Append(' ');
                    }
                    else sb.Append(newPara ? "\r\n\r\n" : "\r\n");
                }
                // recuo (código): espaços proporcionais à distância da margem esquerda
                if (mode == OcrLayout.Lines && LooksLikeCode && charW > 0)
                {
                    int indent = (int)Math.Round((row[0].Box.X - minX) / charW);
                    if (indent >= 2) sb.Append(' ', indent);
                }
                for (int li = 0; li < row.Count; li++)
                {
                    var line = row[li];
                    if (li > 0)
                    {
                        float gap = line.Box.X - row[li - 1].Box.Right;
                        // colunas separadas viram tabulação (cola certo em planilhas); pedaços próximos, espaço
                        sb.Append(mode == OcrLayout.Lines && gap > charW * 2.5f ? '\t' : ' ');
                    }
                    for (int wi = 0; wi < line.Words.Count; wi++)
                    {
                        var w = line.Words[wi];
                        if (wi > 0) sb.Append(' ');
                        w.Start = sb.Length; w.Length = w.Text.Length;
                        sb.Append(w.Text);
                    }
                }
            }
            return sb.ToString();
        }

        // Linhas agrupadas por altura (o OCR devolve coluna por coluna).
        List<List<OcrLine>> Rows()
        {
            var sorted = Lines.Where(l => l.Words.Count > 0).OrderBy(l => l.Box.Top + l.Box.Height / 2).ToList();
            var rows = new List<List<OcrLine>>();
            foreach (var l in sorted)
            {
                float cy = l.Box.Top + l.Box.Height / 2;
                var row = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (row != null)
                {
                    float ry = row.Average(x => x.Box.Top + x.Box.Height / 2), rh = row.Max(x => x.Box.Height);
                    if (Math.Abs(cy - ry) < Math.Max(rh, l.Box.Height) * 0.5f) { row.Add(l); continue; }
                }
                rows.Add(new List<OcrLine> { l });
            }
            foreach (var r in rows) r.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
            return rows;
        }

        float CharWidth()
        {
            var v = new List<float>();
            foreach (var w in Words) if (w.Text.Length >= 2) v.Add(w.Box.Width / w.Text.Length);
            return v.Count > 0 ? Median(v) : 8;
        }

        static float Median(IEnumerable<float> xs)
        {
            var a = xs.OrderBy(x => x).ToList();
            return a.Count == 0 ? 0 : a[a.Count / 2];
        }
    }

    static class Ocr
    {
        // Compatível com o uso antigo: só o texto, no modo de linhas.
        public static string Recognize(Bitmap bmp) { return Read(bmp, null).Build(OcrLayout.Lines); }

        public static string[] Languages()
        {
            return Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
        }

        public static string LanguageName(string tag)
        {
            try { return new Windows.Globalization.Language(tag).NativeName; } catch { return tag; }
        }

        // Lê a imagem. language: etiqueta (ex.: "pt-BR") ou null para o idioma do usuário.
        public static OcrDoc Read(Bitmap bmp, string language)
        {
            Windows.Media.Ocr.OcrEngine engine = null;
            if (!string.IsNullOrEmpty(language))
                try { engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(language)); } catch { }
            if (engine == null) engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new Exception("O Windows não tem nenhum idioma com reconhecimento de texto instalado.");
            int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;

            using (var clean = Normalize(bmp))
            {
                // 1ª leitura no tamanho original; a altura das letras decide as outras escalas
                var passes = new List<Tuple<float, OcrDoc>>();
                var first = Pass(engine, clean, 1f, max);
                passes.Add(Tuple.Create(1f, first));
                var heights = first.Lines.Select(l => l.Box.Height).OrderBy(h => h).ToList();
                float h0 = heights.Count > 0 ? heights[heights.Count / 2] : 0;
                var scales = h0 == 0 ? new[] { 2f, 3f } : h0 < 14 ? new[] { 2f, 3f } : h0 < 26 ? new[] { 1.5f, 2f } : new float[0];
                foreach (var s in scales)
                    if (bmp.Width * s <= max && bmp.Height * s <= max) passes.Add(Tuple.Create(s, Pass(engine, clean, s, max)));

                // a leitura em tamanho original é a base, a não ser que outra seja claramente melhor;
                // as outras só acrescentam linhas que a base não viu
                var best = first; double bestScore = Score(first);
                foreach (var p in passes.Skip(1))
                {
                    double sc = Score(p.Item2);
                    if (sc > bestScore * 1.15 + 3) { best = p.Item2; bestScore = sc; }
                }
                foreach (var p in passes)
                {
                    if (p.Item2 == best) continue;
                    foreach (var l in p.Item2.Lines)
                    {
                        if (best.Lines.Any(b => Overlap(b.Box, l.Box) > 0.25f)) continue;
                        if (LineScore(l) <= 0) continue;
                        best.Lines.Add(l);
                    }
                }
                foreach (var w in best.Words) w.Text = Fix(w.Text);
                best.Language = engine.RecognizerLanguage.LanguageTag;
                best.ImageSize = bmp.Size;
                string all = string.Concat(best.Words.Select(w => w.Text));
                int code = all.Count(ch => "{}();=<>[]".IndexOf(ch) >= 0);
                best.LooksLikeCode = all.Length > 0 && code >= Math.Max(3, all.Length * 0.03);
                return best;
            }
        }

        static OcrDoc Pass(Windows.Media.Ocr.OcrEngine engine, Bitmap src, float scale, int max)
        {
            int w = Math.Min(max, (int)(src.Width * scale)), h = Math.Min(max, (int)(src.Height * scale));
            // margem branca: texto colado na borda costuma ser ignorado
            int pad = (int)(8 * scale);
            using (var b = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.Clear(EdgeColor(src));
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;   // sem suavização: ela piora a leitura
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src, new Rectangle(pad, pad, w, h));
                }
                var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[b.Width * 4 * b.Height];
                for (int y = 0; y < b.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width * 4, b.Width * 4);
                b.UnlockBits(data);
                var writer = new Windows.Storage.Streams.DataWriter();
                writer.WriteBytes(px);
                var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, b.Width, b.Height, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                var result = Wait(engine.RecognizeAsync(sb));
                var doc = new OcrDoc();
                float kx = src.Width / (float)w, ky = src.Height / (float)h;
                foreach (var line in result.Lines)
                {
                    var ol = new OcrLine();
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        ol.Words.Add(new OcrWord { Text = word.Text, Box = new RectangleF((float)(r.X - pad) * kx, (float)(r.Y - pad) * ky, (float)r.Width * kx, (float)r.Height * ky) });
                    }
                    if (ol.Words.Count == 0) continue;
                    float l = ol.Words.Min(x => x.Box.Left), t = ol.Words.Min(x => x.Box.Top), rr = ol.Words.Max(x => x.Box.Right), bb = ol.Words.Max(x => x.Box.Bottom);
                    ol.Box = RectangleF.FromLTRB(l, t, rr, bb);
                    doc.Lines.Add(ol);
                }
                return doc;
            }
        }

        // Cor do fundo (a mais comum nas bordas) para a margem acrescentada.
        static Color EdgeColor(Bitmap b)
        {
            var count = new Dictionary<int, int>();
            Action<int, int> add = (x, y) => { int c = b.GetPixel(x, y).ToArgb() & 0xF0F0F0; int n; count.TryGetValue(c, out n); count[c] = n + 1; };
            for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 40)) { add(x, 0); add(x, b.Height - 1); }
            for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 40)) { add(0, y); add(b.Width - 1, y); }
            int best = count.OrderByDescending(kv => kv.Value).First().Key;
            return Color.FromArgb(255, Color.FromArgb(best | 0x080808));
        }

        // Estica o contraste quando a imagem é "lavada" (texto cinza claro sobre branco, por exemplo).
        static Bitmap Normalize(Bitmap src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.DrawImageUnscaled(src, 0, 0); }
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * b.Width, b.Width);
            var hist = new int[256];
            foreach (int p in px) hist[Lum(p)]++;
            int lo = Pct(hist, px.Length, 0.01), hi = Pct(hist, px.Length, 0.99);
            if (hi - lo < 170 && hi > lo + 8)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    int p = px[i];
                    int r = Stretch((p >> 16) & 255, lo, hi), gg = Stretch((p >> 8) & 255, lo, hi), bb = Stretch(p & 255, lo, hi);
                    px[i] = unchecked((int)0xFF000000) | (r << 16) | (gg << 8) | bb;
                }
                for (int y = 0; y < b.Height; y++) Marshal.Copy(px, y * b.Width, d.Scan0 + y * d.Stride, b.Width);
            }
            b.UnlockBits(d);
            return b;
        }

        static int Lum(int p) { return (((p >> 16) & 255) * 299 + ((p >> 8) & 255) * 587 + (p & 255) * 114) / 1000; }
        static int Stretch(int v, int lo, int hi) { return Math.Max(0, Math.Min(255, (v - lo) * 255 / (hi - lo))); }
        static int Pct(int[] hist, int total, double q)
        {
            long acc = 0, target = (long)(total * q);
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > target) return i; }
            return 255;
        }

        // Quão "texto de verdade" parece uma leitura: letras e números contam, símbolos estranhos descontam.
        static double Score(OcrDoc d) { return d.Lines.Sum(l => LineScore(l)); }

        static double LineScore(OcrLine l)
        {
            double s = 0;
            foreach (var w in l.Words)
            {
                int good = 0, bad = 0;
                foreach (char c in w.Text)
                {
                    if (char.IsLetterOrDigit(c) || "{}()[];".IndexOf(c) >= 0) good++;   // "}" sozinho numa linha de código conta
                    else if ("¿•»«©®~^`|\\¦¬°".IndexOf(c) >= 0) bad += 3;
                }
                // maiúsculas no meio de palavra minúscula ("ParrE") e letras misturadas com dígitos ("1Wúü0")
                bool mixedCase = w.Text.Length > 2 && w.Text.Skip(1).Any(char.IsUpper) && w.Text.Skip(1).Any(char.IsLower) && !w.Text.Skip(1).All(char.IsUpper);
                bool letterDigit = w.Text.Any(char.IsLetter) && w.Text.Any(char.IsDigit) && w.Text.Length > 3;
                s += good - bad - (mixedCase ? 2 : 0) - (letterDigit ? 2 : 0);
            }
            return s;
        }

        static double Overlap(RectangleF a, RectangleF b)
        {
            var i = RectangleF.Intersect(a, b);
            if (i.Width <= 0 || i.Height <= 0) return 0;
            return i.Width * i.Height / Math.Min(a.Width * a.Height, b.Width * b.Height);
        }

        // Correções de enganos comuns do OCR em textos em português.
        static string Fix(string w)
        {
            if (w == "RS" || w == "R$" || w == "R5") return "R$";
            w = Regex.Replace(w, @"^RS(?=\d)", "R$");
            w = w.Replace("¿", "ã");
            w = Regex.Replace(w, @"(?<=\p{L})ã0$", "ão");   // "Versã0" -> "Versão"
            return w;
        }

        // Espera a operação assíncrona do WinRT sem precisar de System.Runtime.WindowsRuntime.
        static T Wait<T>(IAsyncOperation<T> op)
        {
            var until = DateTime.Now.AddSeconds(30);
            while (op.Status == AsyncStatus.Started)
            {
                if (DateTime.Now > until) { op.Cancel(); throw new Exception("O reconhecimento de texto demorou demais."); }
                Thread.Sleep(5);
            }
            if (op.Status == AsyncStatus.Error) throw new Exception("Falha no reconhecimento de texto: " + op.ErrorCode.Message);
            return op.GetResults();
        }
    }
}
