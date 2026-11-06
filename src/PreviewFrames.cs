using System;
using System.Collections.Generic;
using System.IO;

namespace Capturo
{
    // Small recordings stay in RAM. Larger previews spill to a private, auto-deleted file.
    // Access is serialized, including close while the decoder is running.
    sealed class PreviewFrames : IDisposable
    {
        readonly object gate = new object();
        readonly long budget;
        readonly List<int> times = new List<int>(), lengths = new List<int>();
        readonly List<long> offsets = new List<long>();
        readonly List<byte[]> memory = new List<byte[]>();
        FileStream disk;
        long memoryBytes;
        bool disposed;

        public PreviewFrames() : this(16 * 1024 * 1024) { }
        internal PreviewFrames(long budget) { this.budget = budget; }

        public bool Add(int time, byte[] data)
        {
            lock (gate)
            {
                if (disposed) return false;
                if (disk == null && memoryBytes + data.Length > budget)
                {
                    disk = new FileStream(Path.Combine(Path.GetTempPath(), "Capturo-preview-" + Guid.NewGuid().ToString("N") + ".tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                        FileOptions.DeleteOnClose | FileOptions.RandomAccess);
                    foreach (var item in memory) disk.Write(item, 0, item.Length);
                    memory.Clear(); memoryBytes = 0;
                }
                long offset = offsets.Count == 0 ? 0 : offsets[offsets.Count - 1] + lengths[lengths.Count - 1];
                if (disk == null) { memory.Add(data); memoryBytes += data.Length; }
                else { disk.Position = offset; disk.Write(data, 0, data.Length); }
                offsets.Add(offset); lengths.Add(data.Length); times.Add(time);
                return true;
            }
        }

        public byte[] At(int time, int skipIndex, bool requireNear, out int index)
        {
            lock (gate)
            {
                index = -1;
                if (disposed || times.Count == 0) return null;
                index = times.BinarySearch(time);
                if (index < 0) index = Math.Max(0, ~index - 1);
                if (index == skipIndex || (requireNear && times[index] + 1500 < time)) return null;
                if (disk == null) return memory[index];
                var data = new byte[lengths[index]];
                disk.Position = offsets[index];
                int read = 0;
                while (read < data.Length)
                {
                    int n = disk.Read(data, read, data.Length - read);
                    if (n == 0) throw new EndOfStreamException("Prévia incompleta.");
                    read += n;
                }
                return data;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (disk != null) { disk.Dispose(); disk = null; }
                memory.Clear(); memoryBytes = 0;
                times.Clear(); lengths.Clear(); offsets.Clear();
            }
        }
    }
}
