using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using Capturo;

static class PreviewPerformance
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("OK: " + name);
    }

    static object Field(object o, string name)
    {
        return o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    }

    static int Main()
    {
        int idx;
        var store = new PreviewFrames(10);
        Check(store.At(0, -1, false, out idx) == null, "empty preview");
        store.Add(100, new byte[] { 1, 2, 3, 4 });
        store.Add(200, new byte[] { 5, 6, 7, 8 });
        Check(store.At(150, -1, false, out idx)[0] == 1 && idx == 0, "preceding frame in RAM");
        store.Add(300, new byte[] { 9, 10, 11, 12 });
        string tmp = ((FileStream)Field(store, "disk")).Name;
        Check((long)Field(store, "memoryBytes") == 0, "spill releases compressed frames from RAM");
        Check(store.At(50, -1, false, out idx)[3] == 4, "first frame survives spill");
        Check(store.At(250, -1, false, out idx)[0] == 5, "seek backward after spill");
        Check(store.At(999, -1, false, out idx)[3] == 12, "last frame after spill");
        Check(store.At(300, 2, false, out idx) == null && idx == 2, "unchanged frame avoids reading/decoding");
        Check(store.At(5000, -1, true, out idx) == null, "unloaded timeline range stays empty");
        Exception failure = null;
        var worker = new Thread(() => {
            try { for (int i = 0; i < 1000; i++) if (!store.Add(400 + i, new byte[1000])) break; }
            catch (Exception e) { failure = e; }
        });
        worker.Start(); store.Dispose(); worker.Join(); store.Dispose();
        Check(failure == null && !File.Exists(tmp), "concurrent close releases and deletes temporary file");
        Check(!store.Add(2000, new byte[2]) && store.At(0, -1, false, out idx) == null, "closed cache rejects late decoder writes");

        string video = Path.Combine(Path.GetTempPath(), "Capturo-test-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            using (var b = new Bitmap(320, 180, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(b))
            using (var writer = new Mp4Writer(video, 320, 180, 30, 1000000, 0, 0, false))
            {
                for (int i = 0; i < 60; i++)
                {
                    g.Clear(i < 30 ? Color.Red : Color.Blue);
                    var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try { writer.WriteVideo(data.Scan0, data.Stride, i * 10000000L / 30, 10000000L / 30); }
                    finally { b.UnlockBits(data); }
                }
                writer.Finish();
            }
            var cache = new PreviewCache(video, 320, 180, 2000);
            try
            {
                Check(SpinWait.SpinUntil(() => cache.Done, 15000), "real MP4 decoding completes");
                Check(cache.Error == null, "real MP4 decoding without errors: " + cache.Error);
                Check(cache.At(0).GetPixel(100, 100).R > 200, "first frame renders red");
                Check(cache.At(1600).GetPixel(100, 100).B > 200, "seek renders blue");
                using (var thumb = cache.Thumb(1600)) Check(thumb.Height == 100, "timeline uses small detached thumbnails");
            }
            finally { cache.Stop(); }
            Check(cache.At(0) == null, "closing releases the decoded frame");
            var closing = new PreviewCache(video, 320, 180, 2000);
            closing.Stop();
            Check(SpinWait.SpinUntil(() => closing.Done, 15000), "closing during decoding terminates worker");
        }
        finally { File.Delete(video); }
        return 0;
    }
}
