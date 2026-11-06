// Gravação de GIF de uma área da tela: codificador GIF próprio (sem dependências) + janelas de controle.
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
    // ------------------------------------------------------------ Codificador
    // Escreve um GIF animado quadro a quadro. Cada quadro só grava o retângulo que mudou
    // em relação ao anterior (pixels iguais viram transparentes), com paleta local de até 255 cores.
    class GifWriter : IDisposable
    {
        readonly Stream s;
        readonly int W, H;
        int[] prev;                 // pixels do último quadro (ARGB)
        int[] pixels;               // dois buffers alternados, sem alocar uma tela por quadro
        readonly int[] count = new int[32768], map = new int[32768];
        readonly long[] sr = new long[32768], sg = new long[32768], sb = new long[32768];
        Pending pending;            // quadro esperando saber sua duração
        readonly int[] lzwCode = new int[1 << 20], lzwStamp = new int[1 << 20];
        int stamp = 1;

        class Pending { public Rectangle R; public byte[] Pal; public byte[] Idx; public bool Transp; public long Ms; }

        public int Frames { get; private set; }

        public GifWriter(Stream stream, int width, int height)
        {
            s = stream; W = width; H = height;
            WriteAscii("GIF89a");
            WriteShort(W); WriteShort(H);
            s.WriteByte(0x70); s.WriteByte(0); s.WriteByte(0);         // sem tabela global
            // NETSCAPE2.0: repetir para sempre
            s.Write(new byte[] { 0x21, 0xFF, 0x0B }, 0, 3);
            WriteAscii("NETSCAPE2.0");
            s.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 }, 0, 5);
        }

        void WriteAscii(string t) { foreach (char c in t) s.WriteByte((byte)c); }
        void WriteShort(int v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); }

        // ms = momento do quadro desde o início da gravação
        public void AddFrame(Bitmap frame, long ms)
        {
            var data = frame.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = pixels ?? new int[W * H];
            try { for (int y = 0; y < H; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * W, W); }
            finally { frame.UnlockBits(data); }
            for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000);

            Rectangle r;
            if (prev == null) r = new Rectangle(0, 0, W, H);
            else
            {
                int l = W, t = H, rr = -1, b = -1;
                for (int y = 0; y < H; y++)
                {
                    int o = y * W;
                    for (int x = 0; x < W; x++)
                        if (px[o + x] != prev[o + x])
                        {
                            if (x < l) l = x; if (x > rr) rr = x;
                            if (y < t) t = y; if (y > b) b = y;
                        }
                }
                if (rr < 0) { pixels = px; return; }   // nada mudou: mantém os buffers e a duração
                r = Rectangle.FromLTRB(l, t, rr + 1, b + 1);
            }
            Flush(ms);
            pending = Quantize(px, r, prev);
            pending.Ms = ms;
            pixels = prev;
            prev = px;
            Frames++;
        }

        void Flush(long nowMs)
        {
            if (pending == null) return;
            int delay = (int)Math.Max(2, Math.Min(65535, (nowMs - pending.Ms + 5) / 10));
            var p = pending; pending = null;
            // Graphic Control Extension: não descartar (o próximo quadro desenha por cima), transparência opcional
            s.Write(new byte[] { 0x21, 0xF9, 0x04, (byte)(0x04 | (p.Transp ? 1 : 0)) }, 0, 4);
            WriteShort(delay);
            s.WriteByte(255); s.WriteByte(0);
            // Image Descriptor + tabela local de 256 cores
            s.WriteByte(0x2C);
            WriteShort(p.R.X); WriteShort(p.R.Y); WriteShort(p.R.Width); WriteShort(p.R.Height);
            s.WriteByte(0x87);
            s.Write(p.Pal, 0, p.Pal.Length);
            Lzw(p.Idx);
        }

        public void Finish(long endMs)
        {
            Flush(endMs);
            s.WriteByte(0x3B);
            s.Flush();
        }

        public void Dispose() { s.Dispose(); }

        // ---- paleta por popularidade em 15 bits (telas costumam ter poucas cores)
        Pending Quantize(int[] px, Rectangle r, int[] prev)
        {
            int fw = W;
            Array.Clear(count, 0, count.Length);
            Array.Clear(sr, 0, sr.Length); Array.Clear(sg, 0, sg.Length); Array.Clear(sb, 0, sb.Length);
            bool transp = false;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int o = y * fw;
                for (int x = r.Left; x < r.Right; x++)
                {
                    int c = px[o + x];
                    if (prev != null && prev[o + x] == c) { transp = true; continue; }
                    int k = ((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F);
                    count[k]++; sr[k] += (c >> 16) & 0xFF; sg[k] += (c >> 8) & 0xFF; sb[k] += c & 0xFF;
                }
            }
            int maxColors = 255;
            var keys = new System.Collections.Generic.List<int>();
            for (int k = 0; k < 32768; k++) if (count[k] > 0) keys.Add(k);
            if (keys.Count > maxColors) { keys.Sort((a, b) => count[b].CompareTo(count[a])); keys.RemoveRange(maxColors, keys.Count - maxColors); }

            var pal = new byte[256 * 3];
            var pr = new int[keys.Count]; var pg = new int[keys.Count]; var pb = new int[keys.Count];
            for (int k = 0; k < 32768; k++) map[k] = -1;
            for (int i = 0; i < keys.Count; i++)
            {
                int k = keys[i];
                pr[i] = (int)(sr[k] / count[k]); pg[i] = (int)(sg[k] / count[k]); pb[i] = (int)(sb[k] / count[k]);
                pal[i * 3] = (byte)pr[i]; pal[i * 3 + 1] = (byte)pg[i]; pal[i * 3 + 2] = (byte)pb[i];
                map[k] = i;
            }

            var idx = new byte[r.Width * r.Height];
            int n = 0;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int o = y * fw;
                for (int x = r.Left; x < r.Right; x++, n++)
                {
                    int c = px[o + x];
                    if (prev != null && prev[o + x] == c) { idx[n] = 255; continue; }
                    int k = ((c >> 9) & 0x7C00) | ((c >> 6) & 0x3E0) | ((c >> 3) & 0x1F);
                    int m = map[k];
                    if (m < 0)
                    {
                        // cor fora da paleta: a mais próxima
                        int cr = (c >> 16) & 0xFF, cg = (c >> 8) & 0xFF, cb = c & 0xFF, best = 0, bd = int.MaxValue;
                        for (int i = 0; i < keys.Count; i++)
                        {
                            int dr = cr - pr[i], dg = cg - pg[i], db = cb - pb[i];
                            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                            if (d < bd) { bd = d; best = i; }
                        }
                        map[k] = m = best;
                    }
                    idx[n] = (byte)m;
                }
            }
            return new Pending { R = r, Pal = pal, Idx = idx, Transp = transp };
        }

        // ---- LZW do GIF (código mínimo de 8 bits), em sub-blocos de 255 bytes
        int bitBuf, bitCount, blockLen;
        readonly byte[] block = new byte[255];

        void PutCode(int code, int size)
        {
            bitBuf |= code << bitCount; bitCount += size;
            while (bitCount >= 8)
            {
                block[blockLen++] = (byte)(bitBuf & 0xFF);
                if (blockLen == 255) { s.WriteByte(255); s.Write(block, 0, 255); blockLen = 0; }
                bitBuf >>= 8; bitCount -= 8;
            }
        }

        void Lzw(byte[] idx)
        {
            const int Clear = 256, Eoi = 257;
            s.WriteByte(8);
            bitBuf = 0; bitCount = 0; blockLen = 0;
            int size = 9, next = 258;
            if (++stamp == int.MaxValue) { Array.Clear(lzwStamp, 0, lzwStamp.Length); stamp = 1; }
            PutCode(Clear, size);
            int prefix = idx[0];
            for (int i = 1; i < idx.Length; i++)
            {
                int k = idx[i];
                int key = (prefix << 8) | k;
                if (lzwStamp[key] == stamp) { prefix = lzwCode[key]; continue; }
                PutCode(prefix, size);
                if (next >= (1 << size) && size < 12) size++;
                if (next < 4096) { lzwCode[key] = next++; lzwStamp[key] = stamp; }
                else
                {
                    PutCode(Clear, size);
                    if (++stamp == int.MaxValue) { Array.Clear(lzwStamp, 0, lzwStamp.Length); stamp = 1; }
                    size = 9; next = 258;
                }
                prefix = k;
            }
            PutCode(prefix, size);
            if (next >= (1 << size) && size < 12) size++;
            PutCode(Eoi, size);
            if (bitCount > 0) { block[blockLen++] = (byte)(bitBuf & 0xFF); bitBuf = 0; bitCount = 0; if (blockLen == 255) { s.WriteByte(255); s.Write(block, 0, 255); blockLen = 0; } }
            if (blockLen > 0) { s.WriteByte((byte)blockLen); s.Write(block, 0, blockLen); }
            s.WriteByte(0);
        }
    }
}
