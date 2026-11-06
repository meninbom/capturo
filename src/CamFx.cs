// Efeitos da webcam no editor: fundo desfocado, fundo de uma cor ou sem fundo (a pessoa recortada),
// filtros de cor e brilho. A pessoa é separada do fundo pelo MediaPipe Selfie Segmentation
// (Apache 2.0), um modelo pequeno que roda no próprio computador pelo Windows ML.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;

namespace Capturo
{
    static class Segmenter
    {
        static LearningModel model;
        static bool tried;
        static readonly object gate = new object();
        public static string Error;
        public const int Size = 256;

        // O modelo vai embutido no .exe e é copiado para a pasta do app na primeira vez.
        public static LearningModel Model
        {
            get
            {
                lock (gate)
                {
                    if (tried) return model;
                    tried = true;
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Capturo", "modelos");
                        string path = Path.Combine(dir, "selfie_segmentation.onnx");
                        using (var s = typeof(Segmenter).Assembly.GetManifestResourceStream("Capturo.selfie_segmentation.onnx"))
                        {
                            if (s == null) throw new Exception("o modelo não veio junto com o app");
                            if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
                            {
                                Directory.CreateDirectory(dir);
                                using (var f = File.Create(path)) s.CopyTo(f);
                            }
                        }
                        model = LearningModel.LoadFromFilePath(path);
                    }
                    catch (Exception ex) { model = null; Error = ex.Message; TrayApp.Log("segmentação indisponível: " + ex); }
                    return model;
                }
            }
        }

        public static bool Available { get { return Model != null; } }
    }

    sealed class CamFx : IDisposable
    {
        LearningModelSession session;
        readonly float[] input = new float[3 * Segmenter.Size * Segmenter.Size];
        float[] prevMask; int prevIndex = -100;
        Bitmap small, square, output;
        string outKey;

        public static bool Active(VideoStyle st)
        {
            return st.CamBg != 0 || st.CamFilter != 0 || Math.Abs(st.CamBright) > 0.01f;
        }

        // Quadro quadrado (o meio da câmera) com os efeitos, com lado 'size'. index: número do quadro
        // da câmera (o mesmo quadro com o mesmo estilo sai do cache).
        public Bitmap Process(Bitmap frame, int index, VideoStyle st, int size)
        {
            int s0 = Math.Min(frame.Width, frame.Height);
            int S = Math.Max(64, Math.Min(s0, size));
            int bgMode = st.CamBg != 0 && Segmenter.Available ? st.CamBg : 0;
            string key = index + "|" + S + "|" + bgMode + "|" + st.CamBlur + "|" + st.CamBgColor + "|" + st.CamFilter + "|" + st.CamBright;
            if (key == outKey && output != null) return output;
            var src = new Rectangle((frame.Width - s0) / 2, (frame.Height - s0) / 2, s0, s0);

            // recorte quadrado no tamanho de trabalho
            if (square == null || square.Width != S) { if (square != null) square.Dispose(); square = new Bitmap(S, S, PixelFormat.Format32bppArgb); }
            using (var g = Graphics.FromImage(square))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(frame, new Rectangle(0, 0, S, S), src, GraphicsUnit.Pixel);
            }
            var px = Read(square);

            float[] mask = bgMode != 0 ? Mask(frame, src, index, S) : null;
            int[] bg = null;
            if (bgMode == 1) bg = Blur(px, S, Math.Max(2, (int)(S * (0.012f + 0.05f * st.CamBlur))));
            var bgc = Color.FromArgb(st.CamBgColor);
            int filter = st.CamFilter; float bright = st.CamBright * 120;
            var outPx = new int[S * S];
            Parallel.For(0, S, y =>
            {
                for (int x = 0; x < S; x++)
                {
                    int i = y * S + x, c = px[i];
                    float r = (c >> 16) & 255, gg = (c >> 8) & 255, b = c & 255;
                    Filter(filter, bright, ref r, ref gg, ref b);
                    float a = 1;
                    if (mask != null)
                    {
                        float m = mask[i];
                        if (bgMode == 3) a = m;
                        else
                        {
                            float br, bgg, bb;
                            if (bgMode == 1) { int q = bg[i]; br = (q >> 16) & 255; bgg = (q >> 8) & 255; bb = q & 255; Filter(filter, bright, ref br, ref bgg, ref bb); }
                            else { br = bgc.R; bgg = bgc.G; bb = bgc.B; }
                            r = r * m + br * (1 - m); gg = gg * m + bgg * (1 - m); b = b * m + bb * (1 - m);
                        }
                    }
                    // PArgb: cor já multiplicada pela opacidade
                    int ai = (int)(a * 255 + 0.5f);
                    int ri = Clamp(r * a), gi = Clamp(gg * a), bi = Clamp(b * a);
                    outPx[i] = (ai << 24) | (ri << 16) | (gi << 8) | bi;
                }
            });
            if (output == null || output.Width != S) { if (output != null) output.Dispose(); output = new Bitmap(S, S, PixelFormat.Format32bppPArgb); }
            var d = output.LockBits(new Rectangle(0, 0, S, S), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < S; y++) Marshal.Copy(outPx, y * S, d.Scan0 + y * d.Stride, S);
            output.UnlockBits(d);
            outKey = key;
            return output;
        }

        static int Clamp(float v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f); }

        static void Filter(int filter, float bright, ref float r, ref float g, ref float b)
        {
            switch (filter)
            {
                case 1:   // vívido: mais saturação e um pouco de contraste
                    {
                        float l = 0.299f * r + 0.587f * g + 0.114f * b;
                        r = l + (r - l) * 1.4f; g = l + (g - l) * 1.4f; b = l + (b - l) * 1.4f;
                        r = (r - 128) * 1.08f + 128; g = (g - 128) * 1.08f + 128; b = (b - 128) * 1.08f + 128;
                        break;
                    }
                case 2:   // preto e branco
                    {
                        float l = (0.299f * r + 0.587f * g + 0.114f * b - 128) * 1.1f + 128;
                        r = g = b = l;
                        break;
                    }
                case 3:   // sépia
                    {
                        float nr = 0.393f * r + 0.769f * g + 0.189f * b, ng = 0.349f * r + 0.686f * g + 0.168f * b, nb = 0.272f * r + 0.534f * g + 0.131f * b;
                        r = nr; g = ng; b = nb;
                        break;
                    }
                case 4: r *= 1.08f; g *= 1.02f; b *= 0.88f; break;   // quente
                case 5: r *= 0.9f; g *= 1.0f; b *= 1.1f; break;      // frio
            }
            if (bright != 0) { r += bright; g += bright; b += bright; }
        }

        static int[] Read(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var px = new int[w * h];
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w);
            b.UnlockBits(d);
            return px;
        }

        // Máscara da pessoa (0 = fundo, 1 = pessoa) no tamanho S x S.
        float[] Mask(Bitmap frame, Rectangle src, int index, int S)
        {
            int N = Segmenter.Size;
            if (session == null) session = new LearningModelSession(Segmenter.Model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
            if (small == null) small = new Bitmap(N, N, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(frame, new Rectangle(0, 0, N, N), src, GraphicsUnit.Pixel);
            }
            var px = Read(small);
            int plane = N * N;
            for (int i = 0; i < plane; i++)
            {
                int c = px[i];
                input[i] = ((c >> 16) & 255) / 255f; input[plane + i] = ((c >> 8) & 255) / 255f; input[2 * plane + i] = (c & 255) / 255f;
            }
            var bind = new LearningModelBinding(session);
            bind.Bind("pixel_values", TensorFloat.CreateFromArray(new long[] { 1, 3, N, N }, input));
            var res = session.Evaluate(bind, "camera");
            var m = ((TensorFloat)res.Outputs["alphas"]).GetAsVectorView().ToArray();
            // quadros seguidos: mistura com a máscara anterior (a borda não fica tremendo)
            if (prevMask != null && (index == prevIndex + 1 || index == prevIndex))
                for (int i = 0; i < plane; i++) m[i] = m[i] * 0.65f + prevMask[i] * 0.35f;
            prevMask = m; prevIndex = index;
            // amplia para S x S (bilinear) e firma a borda (sem halo do fundo em volta da pessoa)
            var o = new float[S * S];
            float k = N / (float)S;
            Parallel.For(0, S, y =>
            {
                float v = Math.Max(0, Math.Min(N - 1.001f, (y + 0.5f) * k - 0.5f));
                int y0 = (int)v; float fy = v - y0; int y1 = Math.Min(N - 1, y0 + 1);
                for (int x = 0; x < S; x++)
                {
                    float u = Math.Max(0, Math.Min(N - 1.001f, (x + 0.5f) * k - 0.5f));
                    int x0 = (int)u; float fx = u - x0; int x1 = Math.Min(N - 1, x0 + 1);
                    float a = m[y0 * N + x0] * (1 - fx) + m[y0 * N + x1] * fx, b = m[y1 * N + x0] * (1 - fx) + m[y1 * N + x1] * fx;
                    float t = a * (1 - fy) + b * fy;
                    t = Math.Max(0, Math.Min(1, (t - 0.3f) / 0.45f));
                    o[y * S + x] = t * t * (3 - 2 * t);
                }
            });
            return o;
        }

        // Desfoque de caixa em 3 passadas (parecido com o gaussiano), horizontal e vertical.
        static int[] Blur(int[] src, int S, int radius)
        {
            var a = (int[])src.Clone();
            var b = new int[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(a, b, S, radius);
                BoxV(b, a, S, radius);
            }
            return a;
        }

        static void BoxH(int[] src, int[] dst, int S, int r)
        {
            Parallel.For(0, S, y =>
            {
                int o = y * S, n = 2 * r + 1;
                int sr = 0, sg = 0, sb = 0;
                for (int x = -r; x <= r; x++) { int c = src[o + Math.Max(0, Math.Min(S - 1, x))]; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; }
                for (int x = 0; x < S; x++)
                {
                    dst[o + x] = (255 << 24) | ((sr / n) << 16) | ((sg / n) << 8) | (sb / n);
                    int ca = src[o + Math.Max(0, x - r)], cb = src[o + Math.Min(S - 1, x + r + 1)];
                    sr += ((cb >> 16) & 255) - ((ca >> 16) & 255); sg += ((cb >> 8) & 255) - ((ca >> 8) & 255); sb += (cb & 255) - (ca & 255);
                }
            });
        }

        static void BoxV(int[] src, int[] dst, int S, int r)
        {
            Parallel.For(0, S, x =>
            {
                int n = 2 * r + 1;
                int sr = 0, sg = 0, sb = 0;
                for (int y = -r; y <= r; y++) { int c = src[Math.Max(0, Math.Min(S - 1, y)) * S + x]; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; }
                for (int y = 0; y < S; y++)
                {
                    dst[y * S + x] = (255 << 24) | ((sr / n) << 16) | ((sg / n) << 8) | (sb / n);
                    int ca = src[Math.Max(0, y - r) * S + x], cb = src[Math.Min(S - 1, y + r + 1) * S + x];
                    sr += ((cb >> 16) & 255) - ((ca >> 16) & 255); sg += ((cb >> 8) & 255) - ((ca >> 8) & 255); sb += (cb & 255) - (ca & 255);
                }
            });
        }

        public void Dispose()
        {
            if (small != null) small.Dispose();
            if (square != null) square.Dispose();
            if (output != null) output.Dispose();
            if (session != null) session.Dispose();
            small = square = output = null; session = null;
        }
    }
}
