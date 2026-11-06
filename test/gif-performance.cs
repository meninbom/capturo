using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using Capturo;

static class GifPerformance
{
    static int Main(string[] args)
    {
        AppDomain.MonitoringIsEnabled = true;
        using (var warm = new MemoryStream()) using (var b = new Bitmap(16, 16))
        using (var w = new GifWriter(warm, 16, 16)) { w.AddFrame(b, 0); w.Finish(100); }
        GC.Collect();
        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var clock = Stopwatch.StartNew();
        using (var output = new MemoryStream())
        {
            using (var b = new Bitmap(640, 360))
            using (var g = Graphics.FromImage(b))
            {
                var writer = new GifWriter(output, b.Width, b.Height);
                for (int i = 0; i < 120; i++)
                {
                    g.Clear(Color.White);
                    // Includes identical frames and moving content, exercising delta transparency.
                    g.FillRectangle(Brushes.Navy, (i / 3 * 11) % 500, 40, 100, 90);
                    writer.AddFrame(b, i * 100);
                }
                writer.Finish(12000);
            }
            clock.Stop();
            byte[] bytes = output.ToArray();
            File.WriteAllBytes(args[0], bytes);
            output.Position = 0;
            using (var image = Image.FromStream(output))
                if (image.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time) != 40)
                    throw new Exception("GIF frame count changed");
            using (var hash = SHA256.Create())
                Console.WriteLine("ms={0}; allocated={1}; bytes={2}; sha256={3}", clock.ElapsedMilliseconds,
                    AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated, bytes.Length,
                    BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""));
        }
        return 0;
    }
}
