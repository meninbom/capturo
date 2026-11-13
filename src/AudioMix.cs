// Som do vídeo final lido do disco aos poucos: as trilhas gravadas (.raw) são misturadas,
// cortadas e aceleradas/desaceleradas por partes. Antes o editor carregava as trilhas inteiras
// (220 MB em float para 10 min) e misturava tudo de uma vez a cada Play (~0,4 s com a janela
// parada e mais de 500 MB alocados). O resultado é o mesmo, amostra por amostra.
using System;
using System.IO;

namespace Capturo
{
    // Um .raw (cabeçalho + float intercalado) visto como 48 kHz estéreo.
    sealed class RawReader : IDisposable
    {
        const int Rate = 48000;
        readonly FileStream fs;
        readonly int ch;
        readonly long n;          // quadros no arquivo
        readonly double step;     // quadros do arquivo por quadro de saída
        readonly bool same;
        public readonly long Length;   // quadros de saída
        byte[] bytes = new byte[0];
        float[] src = new float[0], st = new float[0];

        RawReader(FileStream fs, int srcRate, int ch, long n)
        {
            this.fs = fs; this.ch = ch; this.n = n;
            same = srcRate == Rate;
            step = (double)srcRate / Rate;
            Length = same ? n : n * Rate / srcRate;
        }

        // null se o arquivo não existir ou não tiver áudio
        public static RawReader Open(string path)
        {
            if (path == null || !File.Exists(path) || new FileInfo(path).Length <= 12) return null;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try
            {
                var h = new byte[12];
                if (fs.Read(h, 0, 12) != 12 || BitConverter.ToInt32(h, 0) != 0x44554143) { fs.Dispose(); return null; }
                int srcRate = BitConverter.ToInt32(h, 4), ch = BitConverter.ToInt32(h, 8);
                if (ch <= 0 || srcRate <= 0) { fs.Dispose(); return null; }
                long n = (fs.Length - 12) / 4 / ch;
                if (n <= 0) { fs.Dispose(); return null; }
                return new RawReader(fs, srcRate, ch, n);
            }
            catch { fs.Dispose(); throw; }
        }

        // Lê os quadros [f0, f1] do arquivo, em estéreo, para st.
        void Load(long f0, long f1)
        {
            int k = (int)(f1 - f0 + 1);
            int nb = k * ch * 4;
            if (bytes.Length < nb) bytes = new byte[nb];
            if (src.Length < k * ch) src = new float[k * ch];
            if (st.Length < k * 2) st = new float[k * 2];
            fs.Position = 12 + f0 * ch * 4;
            int got = 0;
            while (got < nb) { int r = fs.Read(bytes, got, nb - got); if (r <= 0) break; got += r; }
            if (got < nb) Array.Clear(bytes, got, nb - got);
            Buffer.BlockCopy(bytes, 0, src, 0, nb);
            for (int i = 0; i < k; i++)
            {
                float l = src[i * ch], r = ch > 1 ? src[i * ch + 1] : l;
                st[i * 2] = l; st[i * 2 + 1] = r;
            }
        }

        // Soma em dst (a partir do quadro dstOff) os quadros de saída [start, start+count).
        // Além do fim da trilha não soma nada.
        public void AddTo(float[] dst, int dstOff, long start, int count)
        {
            long end = Math.Min(start + count, Length);
            if (start >= end) return;
            if (same)
            {
                Load(start, end - 1);
                int k = (int)(end - start), o = dstOff * 2;
                for (int i = 0; i < k * 2; i++) dst[o + i] += st[i];
                return;
            }
            // reamostragem linear, como a leitura antiga do arquivo inteiro
            long a0 = (long)(start * step), a1 = Math.Min((long)((end - 1) * step) + 1, n - 1);
            Load(a0, a1);
            for (long i = start; i < end; i++)
            {
                double pos = i * step;
                long a = (long)pos; double f = pos - a;
                long b = Math.Min(a + 1, n - 1);
                int o = (int)(dstOff + i - start) * 2;
                dst[o] += (float)(st[(a - a0) * 2] * (1 - f) + st[(b - a0) * 2] * f);
                dst[o + 1] += (float)(st[(a - a0) * 2 + 1] * (1 - f) + st[(b - a0) * 2 + 1] * f);
            }
        }

        public void Dispose() { fs.Dispose(); }
    }

    // A mistura do vídeo final (48 kHz estéreo), pedaço por pedaço.
    sealed class MixStream : IDisposable
    {
        public struct Part { public long S0, S1; public float Rate; }   // quadros da gravação a 48 kHz

        // mudança de velocidade sem mudar o tom (WSOLA: pedaços sobrepostos com janela de Hann,
        // cada um escolhido onde melhor continua o anterior, para não "picotar" a voz)
        const int N = 1536, Hop = N / 2, Tol = 360;
        static float[] window;

        readonly RawReader a, b;
        readonly Part[] parts;
        public readonly long Length;   // quadros no total

        int pi = -1, mode;   // parte atual; 0 normal, 1 silêncio (acima de 4×), 2 WSOLA, 3 trecho curto
        long cur, left;
        // WSOLA
        int n, outN, o, prev;
        double rate;
        readonly float[] acc = new float[N * 2];
        // janela da parte atual (índices locais) com o estéreo e o mono reduzido para comparar trechos
        float[] wst = new float[1 << 17], wmono = new float[1 << 16];
        int wStart, wLen;
        // saída pronta
        float[] pend = new float[4800 * 2];
        int pendPos, pendLen;

        MixStream(RawReader a, RawReader b, Part[] parts)
        {
            this.a = a; this.b = b; this.parts = parts;
            foreach (var p in parts) Length += OutFrames(p);
        }

        // null se nenhuma das trilhas puder ser lida
        public static MixStream Open(string sysPath, string micPath, Part[] parts)
        {
            RawReader a = RawReader.Open(sysPath), b = null;
            try { b = RawReader.Open(micPath); }
            catch { if (a != null) a.Dispose(); throw; }
            if (a == null && b == null) return null;
            return new MixStream(a, b, parts);
        }

        static long OutFrames(Part p)
        {
            long len = p.S1 - p.S0;
            if (Math.Abs(p.Rate - 1) < 0.001f) return len;
            if (p.Rate > 4.01f) return (long)(len / p.Rate);
            int outN = (int)((int)len / (double)p.Rate);
            return Math.Max(0, outN);
        }

        // Mistura das trilhas nos quadros [start, start+count) da gravação.
        void Raw(long start, int count, float[] dst, int dstOff)
        {
            Array.Clear(dst, dstOff * 2, count * 2);
            if (a != null) a.AddTo(dst, dstOff, start, count);
            if (b != null) b.AddTo(dst, dstOff, start, count);
        }

        // Copia até 'frames' quadros para dst; menos só no fim.
        public int Read(float[] dst, int frames)
        {
            int done = 0;
            while (done < frames)
            {
                if (pendPos == pendLen && !Fill()) break;
                int k = Math.Min(frames - done, pendLen - pendPos);
                Array.Copy(pend, pendPos * 2, dst, done * 2, k * 2);
                pendPos += k; done += k;
            }
            return done;
        }

        bool Fill()
        {
            pendPos = pendLen = 0;
            while (pi < parts.Length)
            {
                if (pi >= 0)
                {
                    switch (mode)
                    {
                        case 0:
                            if (cur < parts[pi].S1)
                            {
                                int k = (int)Math.Min(4800, parts[pi].S1 - cur);
                                Raw(cur, k, pend, 0);
                                cur += k; pendLen = k;
                                return true;
                            }
                            break;
                        case 1:
                            if (left > 0)
                            {
                                int k = (int)Math.Min(4800, left);
                                Array.Clear(pend, 0, k * 2);
                                left -= k; pendLen = k;
                                return true;
                            }
                            break;
                        case 2:
                            if ((long)o * Hop < outN) { WsolaStep(); return true; }
                            break;
                        case 3:
                            if (left > 0) { left = 0; Short(); return true; }
                            break;
                    }
                }
                if (++pi >= parts.Length) return false;
                Begin(parts[pi]);
            }
            return false;
        }

        void Begin(Part p)
        {
            long len = p.S1 - p.S0;
            if (Math.Abs(p.Rate - 1) < 0.001f) { mode = 0; cur = p.S0; return; }
            if (p.Rate > 4.01f) { mode = 1; left = (long)(len / p.Rate); return; }   // acima de 4× fica mudo
            n = (int)len; rate = p.Rate;
            outN = (int)(n / rate);
            if (outN <= 0) { mode = 1; left = 0; return; }
            if (n < N * 2) { mode = 3; left = 1; return; }
            mode = 2; o = 0; prev = 0; wStart = 0; wLen = 0;
            Array.Clear(acc, 0, acc.Length);
            if (window == null)
            {
                var w = new float[N];
                for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
                window = w;
            }
        }

        // muito curto: reamostra simples
        void Short()
        {
            var raw = new float[n * 2];
            Raw(parts[pi].S0, n, raw, 0);
            if (pend.Length < outN * 2) pend = new float[outN * 2];
            for (int i = 0; i < outN; i++) { int j = Math.Min(n - 1, (int)(i * rate)); pend[i * 2] = raw[j * 2]; pend[i * 2 + 1] = raw[j * 2 + 1]; }
            pendLen = outN;
        }

        // Garante na janela os quadros locais [lo, hi). Andando para frente, descarta o que ficou para trás
        // e lê adiante de uma vez.
        void Ensure(int lo, int hi)
        {
            if (lo >= wStart && hi <= wStart + wLen) return;
            if (lo < wStart || lo > wStart + wLen) { wStart = lo; wLen = 0; }
            else if (lo > wStart)
            {
                int drop = lo - wStart;
                Array.Copy(wst, drop * 2, wst, 0, (wLen - drop) * 2);
                Array.Copy(wmono, drop, wmono, 0, wLen - drop);
                wStart = lo; wLen -= drop;
            }
            int cap = wmono.Length;
            if (hi - wStart > cap)
            {
                cap = hi - wStart;
                Array.Resize(ref wst, cap * 2); Array.Resize(ref wmono, cap);
            }
            int upTo = Math.Min(n, wStart + cap);
            int k = upTo - (wStart + wLen);
            if (k <= 0) return;
            Raw(parts[pi].S0 + wStart + wLen, k, wst, wLen);
            for (int i = wLen; i < wLen + k; i++) wmono[i] = wst[i * 2] + wst[i * 2 + 1];
            wLen += k;
        }

        void WsolaStep()
        {
            int ideal = (int)(o * (double)Hop * rate);
            int best = ideal, nat = prev + Hop;
            int lo0 = Math.Max(0, Math.Min(Math.Min(o > 0 ? nat : ideal, ideal - Tol), n - N));
            int hi0 = Math.Min(n, Math.Max(o > 0 ? nat + Hop : 0, ideal + Tol + N + 1));
            Ensure(lo0, hi0);
            int ws = wStart;
            if (o > 0)
            {
                // o trecho que naturalmente viria depois do anterior
                double bestScore = double.MinValue;
                int lo = Math.Max(0, ideal - Tol), hi = Math.Min(n - N - 1, ideal + Tol);
                if (nat + Hop < n)
                    for (int c = lo; c <= hi; c += 2)
                    {
                        double s = 0;
                        for (int k = 0; k < Hop; k += 4) s += wmono[nat + k - ws] * wmono[c + k - ws];
                        if (s > bestScore) { bestScore = s; best = c; }
                    }
            }
            best = Math.Max(0, Math.Min(n - N, best));
            int sb = (best - ws) * 2;
            for (int k = 0; k < N; k++)
            {
                float w = window[k];
                acc[k * 2] += wst[sb + k * 2] * w; acc[k * 2 + 1] += wst[sb + k * 2 + 1] * w;
            }
            prev = best;
            // a primeira metade já recebeu os dois pedaços que a cobrem: está pronta
            int emit = (int)Math.Min(Hop, outN - (long)o * Hop);
            Array.Copy(acc, 0, pend, 0, emit * 2);
            pendLen = emit;
            Array.Copy(acc, Hop * 2, acc, 0, Hop * 2);
            Array.Clear(acc, Hop * 2, Hop * 2);
            o++;
        }

        public void Dispose()
        {
            if (a != null) a.Dispose();
            if (b != null) b.Dispose();
        }
    }
}
