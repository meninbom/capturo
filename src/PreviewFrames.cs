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

        public bool Add(int time, byte[] data) { return Add(time, data, data.Length, false); }

        // count: bytes válidos de data. copy: data é um buffer reaproveitado pelo chamador (na RAM
        // guarda uma cópia; no disco grava direto, sem alocar um array grande por quadro).
        public bool Add(int time, byte[] data, int count, bool copy)
        {
            lock (gate)
            {
                if (disposed) return false;
                if (disk == null && memoryBytes + count > budget)
                {
                    disk = new FileStream(Path.Combine(Path.GetTempPath(), "Capturo-preview-" + Guid.NewGuid().ToString("N") + ".tmp"),
                        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                        FileOptions.DeleteOnClose | FileOptions.RandomAccess);
                    for (int i = 0; i < memory.Count; i++) disk.Write(memory[i], 0, lengths[i]);
                    memory.Clear(); memoryBytes = 0;
                }
                long offset = offsets.Count == 0 ? 0 : offsets[offsets.Count - 1] + lengths[lengths.Count - 1];
                if (disk == null)
                {
                    byte[] keep = data;
                    if (copy || count != data.Length) { keep = new byte[count]; Buffer.BlockCopy(data, 0, keep, 0, count); }
                    memory.Add(keep); memoryBytes += count;
                }
                else { disk.Position = offset; disk.Write(data, 0, count); }
                offsets.Add(offset); lengths.Add(count); times.Add(time);
                return true;
            }
        }

        public byte[] At(int time, int skipIndex, bool requireNear, out int index)
        {
            byte[] buffer = null;
            int count;
            byte[] data = Read(time, skipIndex, requireNear, ref buffer, out count, out index);
            return data;   // sem buffer próprio, o array tem exatamente o tamanho do quadro
        }

        // Como At, mas os quadros em disco são lidos para 'buffer' (reaproveitado entre chamadas).
        // Devolve o array com os dados (o próprio buffer ou o guardado na RAM) e o tamanho válido.
        public byte[] Read(int time, int skipIndex, bool requireNear, ref byte[] buffer, out int count, out int index)
        {
            lock (gate)
            {
                index = -1; count = 0;
                if (disposed || times.Count == 0) return null;
                index = times.BinarySearch(time);
                if (index < 0) index = Math.Max(0, ~index - 1);
                if (index == skipIndex || (requireNear && times[index] + 1500 < time)) return null;
                count = lengths[index];
                if (disk == null) return memory[index];
                if (buffer == null) buffer = new byte[count];
                else if (buffer.Length < count) buffer = new byte[Math.Max(count, buffer.Length * 3 / 2)];
                disk.Position = offsets[index];
                int read = 0;
                while (read < count)
                {
                    int n = disk.Read(buffer, read, count - read);
                    if (n == 0) throw new EndOfStreamException("Prévia incompleta.");
                    read += n;
                }
                return buffer;
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
