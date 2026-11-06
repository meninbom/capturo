// Exportação do projeto de vídeo para MP4 (H.264 + AAC) ou GIF.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace Capturo
{
    static class VideoExport
    {
        public delegate void Progress(double fraction);

        // Lê o MP4 gravado numa thread própria (em paralelo com a montagem e a codificação)
        // e entrega o quadro vigente em cada instante pedido.
        sealed class FrameSource : IDisposable
        {
            readonly BlockingCollection<Tuple<Bitmap, long>> ready = new BlockingCollection<Tuple<Bitmap, long>>(3);
            readonly ConcurrentBag<Bitmap> free = new ConcurrentBag<Bitmap>();
            readonly List<Bitmap> all = new List<Bitmap>();
            readonly Thread thread;
            volatile bool stop;
            Tuple<Bitmap, long> next;
            Bitmap current;
            public Exception Error;

            public FrameSource(string path, int w, int h, int fromMs)
            {
                for (int i = 0; i < 6; i++) { var b = new Bitmap(w, h, PixelFormat.Format32bppRgb); all.Add(b); free.Add(b); }
                thread = new Thread(() => Run(path, w, h, fromMs)) { IsBackground = true, Name = "decodificar" };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
            }

            void Run(string path, int w, int h, int fromMs)
            {
                try
                {
                    using (var r = new Mp4Reader(path, true, false))
                    {
                        if (fromMs > 0) r.Seek(fromMs * 10000L);
                        while (!stop)
                        {
                            Bitmap b = null;
                            while (!stop && !free.TryTake(out b)) Thread.Sleep(2);
                            if (b == null) break;
                            long time = 0;
                            bool ok = r.ReadFrame((p, pitch, t) =>
                            {
                                var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                                int rows = Math.Min(h, r.Height), bytes = Math.Min(w, r.Width) * 4;
                                for (int y = 0; y < rows; y++) MF.Copy(d.Scan0 + y * d.Stride, p + y * pitch, (IntPtr)bytes);
                                b.UnlockBits(d);
                                time = t;
                            });
                            if (!ok) { free.Add(b); break; }
                            ready.Add(Tuple.Create(b, time));
                        }
                    }
                }
                catch (Exception ex) { Error = ex; }
                finally { ready.CompleteAdding(); }
            }

            Tuple<Bitmap, long> Peek()
            {
                if (next == null) ready.TryTake(out next, Timeout.Infinite);
                return next;
            }

            // O último quadro com tempo <= t (100 ns); antes do primeiro, o primeiro.
            public Bitmap At(long t)
            {
                while (true)
                {
                    var n = Peek();
                    if (n == null) break;
                    if (n.Item2 > t + 1000 && current != null) break;
                    if (current != null) free.Add(current);
                    current = n.Item1;
                    next = null;
                }
                if (Error != null) throw Error;
                return current;
            }

            public void Dispose()
            {
                stop = true;
                while (ready.TryTake(out next)) { }
                thread.Join(3000);
                foreach (var b in all) b.Dispose();
            }
        }

        // Tamanho final do MP4 conforme a resolução escolhida (sempre par).
        public static Size Mp4Size(VideoProject p)
        {
            var o = p.OutputSize;
            bool portrait = o.Height > o.Width;
            int bw, bh;
            switch (p.Style.Res)
            {
                case 1: bw = 1920; bh = 1080; break;
                case 2: bw = 1280; bh = 720; break;
                default: bw = 3840; bh = 2160; break;   // original, até 4K (limite do codificador)
            }
            if (portrait) { int t = bw; bw = bh; bh = t; }
            double k = Math.Min(1, Math.Min(bw / (double)o.Width, bh / (double)o.Height));
            return new Size(Math.Max(2, (int)(o.Width * k)) & ~1, Math.Max(2, (int)(o.Height * k)) & ~1);
        }

        public static void Mp4(VideoProject p, string path, Progress progress, Func<bool> cancelled)
        {
            var full = p.OutputSize;
            var size = Mp4Size(p);
            int fps = p.Style.OutFps >= 60 ? 60 : 30;
            double bpp = p.Style.Quality == 0 ? 0.16 : p.Style.Quality == 1 ? 0.10 : 0.06;
            int bitrate = (int)Math.Max(1.5e6, Math.Min(40e6, size.Width * (double)size.Height * fps * bpp));
            float[] audio = p.MixAudio();
            // três etapas em paralelo: decodificar (FrameSource), montar o quadro (thread "montar")
            // e codificar (esta thread, dona do codificador do Media Foundation)
            var ready = new BlockingCollection<Tuple<Bitmap, int>>(2);
            var free = new BlockingCollection<Bitmap>();
            var outs = new List<Bitmap>();
            var stop = new CancellationTokenSource();
            Exception renderError = null;
            Thread render = null;
            p.SharpZoom = true;
            try
            {
                for (int i = 0; i < 3; i++) { var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb); outs.Add(b); free.Add(b); }
                using (var src = new FrameSource(p.RawVideo, p.W, p.H, p.TrimStart))
                {
                    try
                    {
                        // codificador por software primeiro: na exportação ele costuma ser mais rápido e mais previsível
                        Mp4Writer w;
                        try { w = new Mp4Writer(path, size.Width, size.Height, fps, bitrate, audio != null ? 48000 : 0, 2, false, true); }
                        catch
                        {
                            try { File.Delete(path); } catch { }
                            w = new Mp4Writer(path, size.Width, size.Height, fps, bitrate, audio != null ? 48000 : 0, 2, true, true);
                        }
                        using (w)
                        {
                            double len = p.OutDuration;      // já com os trechos de velocidade
                            int n = Math.Max(1, (int)(len * fps / 1000));
                            long period = 10000000L / fps;
                            float sc = size.Width / (float)full.Width;
                            render = new Thread(() =>
                            {
                                try
                                {
                                    for (int i = 0; i < n && !stop.IsCancellationRequested; i++)
                                    {
                                        double t = p.SourceAt(i * 1000.0 / fps);
                                        var frame = src.At((long)(t * 10000));
                                        if (frame == null) break;
                                        Bitmap outBmp = free.Take(stop.Token);
                                        p.RenderInto(outBmp, sc, frame, t, true);
                                        ready.Add(Tuple.Create(outBmp, i), stop.Token);
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex) { renderError = ex; }
                                finally { ready.CompleteAdding(); }
                            }) { IsBackground = true, Name = "montar" };
                            render.Start();

                            long audioDone = 0;
                            var pcm = new short[4800 * 2];
                            foreach (var item in ready.GetConsumingEnumerable())
                            {
                                var outBmp = item.Item1; int i = item.Item2;
                                if (cancelled()) return;
                                var d = outBmp.LockBits(new Rectangle(0, 0, size.Width, size.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                                try { w.WriteVideo(d.Scan0, d.Stride, i * period, period); }
                                finally { outBmp.UnlockBits(d); free.Add(outBmp); }
                                // áudio acompanha o vídeo, em blocos de 100 ms
                                if (audio != null)
                                {
                                    long want = Math.Min(audio.Length / 2, (long)((i + 1) * 48000L / fps) + 4800);
                                    while (audioDone < want)
                                    {
                                        int k = (int)Math.Min(4800, audio.Length / 2 - audioDone);
                                        if (k <= 0) break;
                                        for (int j = 0; j < k * 2; j++)
                                        {
                                            float v = audio[audioDone * 2 + j];
                                            pcm[j] = (short)Math.Max(-32767, Math.Min(32767, v * 32767));
                                        }
                                        w.WriteAudio(pcm, k, audioDone * 10000000L / 48000);
                                        audioDone += k;
                                    }
                                }
                                if (i % 5 == 0) progress((i + 1) / (double)n);
                            }
                            if (renderError != null) throw renderError;
                            w.Finish();
                            progress(1);
                        }
                    }
                    finally
                    {
                        // a montagem para antes de o decodificador ser fechado
                        stop.Cancel();
                        if (render != null) render.Join();
                    }
                }
            }
            finally
            {
                p.SharpZoom = false;
                foreach (var b in outs) b.Dispose();
                stop.Dispose();
            }
        }

        public static Size GifSize(VideoProject p)
        {
            var size = p.OutputSize;
            float k = Math.Min(1f, p.Style.GifWidth / (float)Math.Max(size.Width, size.Height));
            return new Size(Math.Max(2, (int)(size.Width * k)), Math.Max(2, (int)(size.Height * k)));
        }

        public static void Gif(VideoProject p, string path, Progress progress, Func<bool> cancelled)
        {
            var size = p.OutputSize;
            var gs = GifSize(p);
            int fps = p.Style.GifFps;
            using (var small = new Bitmap(gs.Width, gs.Height, PixelFormat.Format32bppArgb))
            using (var src = new FrameSource(p.RawVideo, p.W, p.H, p.TrimStart))
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
            using (var gif = new GifWriter(fs, gs.Width, gs.Height))
            {
                int len = (int)p.OutDuration;
                int n = Math.Max(1, len * fps / 1000);
                for (int i = 0; i < n; i++)
                {
                    if (cancelled()) return;
                    double t = p.SourceAt(i * 1000.0 / fps);
                    var frame = src.At((long)(t * 10000));
                    if (frame == null) break;
                    using (var g = Graphics.FromImage(small))
                    {
                        g.ScaleTransform(gs.Width / (float)size.Width, gs.Height / (float)size.Height);
                        p.Render(g, frame, t, false);   // GIF sem desfoque: comprime melhor
                    }
                    gif.AddFrame(small, (long)(i * 1000.0 / fps));
                    if (i % 3 == 0) progress((i + 1) / (double)n);
                }
                gif.Finish(len);
                progress(1);
            }
        }
    }
}
