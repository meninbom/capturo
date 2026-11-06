// Captura de áudio pelo WASAPI (já vem no Windows): o som do sistema (loopback) e o microfone.
// Cada fonte vai para um arquivo próprio em float 32 bits; a exportação mistura e converte.
// Quando o sistema fica em silêncio o loopback para de entregar dados: o silêncio é preenchido
// pelo relógio da gravação, para o áudio continuar alinhado com o vídeo.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Capturo
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int frames);
        void IsFormatSupported();
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr h);
        void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devPos, out long qpcPos);
        void ReleaseBuffer(int frames);
        void GetNextPacketSize(out int frames);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    // Arquivo de áudio cru do Capturo: cabeçalho "CAUD", taxa, canais e amostras float intercaladas.
    static class RawAudio
    {
        public static void WriteHeader(BinaryWriter w, int rate, int channels)
        {
            w.Write(0x44554143); w.Write(rate); w.Write(channels);
        }

        // Lê o arquivo inteiro e converte para "rate" Hz estéreo (float). null se não houver áudio.
        public static float[] LoadStereo(string path, int rate)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 12) return null;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != 0x44554143) return null;
                int srcRate = r.ReadInt32(), ch = r.ReadInt32();
                long n = (r.BaseStream.Length - 12) / 4 / ch;
                if (n <= 0 || ch <= 0 || srcRate <= 0) return null;
                // para estéreo, lendo em blocos
                var st = new float[n * 2];
                var bytes = new byte[4096 * ch * 4];
                var src = new float[4096 * ch];
                long done = 0;
                while (done < n)
                {
                    int k = (int)Math.Min(4096, n - done);
                    int got = r.Read(bytes, 0, k * ch * 4);
                    if (got < k * ch * 4) break;
                    Buffer.BlockCopy(bytes, 0, src, 0, got);
                    for (int i = 0; i < k; i++)
                    {
                        float l = src[i * ch], rr = ch > 1 ? src[i * ch + 1] : l;
                        st[(done + i) * 2] = l; st[(done + i) * 2 + 1] = rr;
                    }
                    done += k;
                }
                if (srcRate == rate) return st;
                long m = n * rate / srcRate;
                var outp = new float[m * 2];
                double step = (double)srcRate / rate;
                for (long i = 0; i < m; i++)
                {
                    double pos = i * step;
                    long a = (long)pos; double f = pos - a;
                    long b = Math.Min(a + 1, n - 1);
                    outp[i * 2] = (float)(st[a * 2] * (1 - f) + st[b * 2] * f);
                    outp[i * 2 + 1] = (float)(st[a * 2 + 1] * (1 - f) + st[b * 2 + 1] * f);
                }
                return outp;
            }
        }
    }

    sealed class AudioCapture
    {
        readonly bool loopback;
        readonly string path;
        readonly Stopwatch clock;
        Thread thread;
        volatile bool stop;
        public volatile bool Muted, Paused;
        public string Error;
        public bool Started;          // o dispositivo abriu
        public float Level;           // pico recente (0..1) para o medidor
        public long Frames;

        public AudioCapture(bool loopback, string path, Stopwatch clock)
        {
            this.loopback = loopback; this.path = path; this.clock = clock;
        }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            thread = new Thread(() => Run(ready)) { IsBackground = true, Name = loopback ? "audio-sistema" : "audio-mic" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            ready.WaitOne(3000);
        }

        public void Stop()
        {
            stop = true;
            if (thread != null) thread.Join(3000);
        }

        void Run(ManualResetEvent ready)
        {
            IAudioClient client = null; IAudioCaptureClient cap = null; IMMDevice dev = null; IMMDeviceEnumerator en = null;
            IntPtr fmt = IntPtr.Zero;
            BinaryWriter w = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                int hr = en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev); // eRender ou eCapture, eConsole
                if (hr != 0 || dev == null) { Error = loopback ? "Nenhuma saída de som encontrada." : "Nenhum microfone encontrado."; return; }
                object o;
                dev.Activate(typeof(IAudioClient).GUID, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
                client = (IAudioClient)o;
                client.GetMixFormat(out fmt);
                short tag = Marshal.ReadInt16(fmt, 0);
                int channels = Marshal.ReadInt16(fmt, 2);
                int rate = Marshal.ReadInt32(fmt, 4);
                int bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3;
                if (tag == unchecked((short)0xFFFE)) // WAVEFORMATEXTENSIBLE: subformato no deslocamento 24
                {
                    var sub = new byte[16];
                    Marshal.Copy(fmt + 24, sub, 0, 16);
                    isFloat = new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                client.Initialize(0 /*shared*/, loopback ? 0x00020000 : 0, 2000000 /*200 ms*/, 0, fmt, IntPtr.Zero);
                client.GetService(typeof(IAudioCaptureClient).GUID, out o);
                cap = (IAudioCaptureClient)o;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
                RawAudio.WriteHeader(w, rate, channels);
                client.Start();
                Started = true;
                ready.Set();

                long startMs = clock.ElapsedMilliseconds;   // o relógio já pode estar andando
                if (startMs > 0) WriteSilence(w, rate * startMs / 1000, channels);
                var buf = new float[0];
                while (!stop)
                {
                    Thread.Sleep(10);
                    int packet;
                    cap.GetNextPacketSize(out packet);
                    bool got = false;
                    while (packet > 0)
                    {
                        IntPtr data; int frames, flags; long dp, qp;
                        if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                        if (Paused)
                        {
                            // gravação pausada: descarta (o relógio também está parado)
                            cap.ReleaseBuffer(frames);
                            cap.GetNextPacketSize(out packet);
                            continue;
                        }
                        int n = frames * channels;
                        if (buf.Length < n) buf = new float[n];
                        bool silent = (flags & 0x2) != 0 || Muted;
                        if (silent) Array.Clear(buf, 0, n);
                        else Convert(data, buf, n, bits, isFloat);
                        cap.ReleaseBuffer(frames);
                        float peak = 0;
                        for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
                        Level = Math.Max(peak, Level * 0.85f);
                        for (int i = 0; i < n; i++) w.Write(buf[i]);
                        Frames += frames;
                        got = true;
                        cap.GetNextPacketSize(out packet);
                    }
                    if (!got) Level *= 0.85f;
                    // sem dados há um tempo (loopback parado): completa com silêncio até o relógio
                    long expected = clock.ElapsedMilliseconds * rate / 1000;
                    if (!got && !Paused && expected - Frames > rate / 10) WriteSilence(w, expected - Frames - rate / 50, channels);
                }
                // fecha com silêncio até o fim do relógio
                long end = clock.ElapsedMilliseconds * rate / 1000;
                if (end > Frames) WriteSilence(w, end - Frames, channels);
                client.Stop();
            }
            catch (Exception ex) { Error = ex.Message; }
            finally
            {
                ready.Set();
                if (w != null) w.Dispose();
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                MF.Release(cap); MF.Release(client); MF.Release(dev); MF.Release(en);
            }
        }

        void WriteSilence(BinaryWriter w, long frames, int channels)
        {
            if (frames <= 0) return;
            var zero = new byte[4096 * 4];
            long left = frames * channels;
            while (left > 0)
            {
                int k = (int)Math.Min(left, 4096);
                w.Write(zero, 0, k * 4);
                left -= k;
            }
            Frames += frames;
        }

        static void Convert(IntPtr data, float[] dst, int n, int bits, bool isFloat)
        {
            if (isFloat && bits == 32) { Marshal.Copy(data, dst, 0, n); return; }
            if (bits == 16)
            {
                var s = new short[n]; Marshal.Copy(data, s, 0, n);
                for (int i = 0; i < n; i++) dst[i] = s[i] / 32768f;
            }
            else if (bits == 32)
            {
                var s = new int[n]; Marshal.Copy(data, s, 0, n);
                for (int i = 0; i < n; i++) dst[i] = s[i] / 2147483648f;
            }
            else if (bits == 24)
            {
                var b = new byte[n * 3]; Marshal.Copy(data, b, 0, n * 3);
                for (int i = 0; i < n; i++) dst[i] = ((b[i * 3] << 8 | b[i * 3 + 1] << 16 | b[i * 3 + 2] << 24) >> 8) / 8388608f;
            }
            else Array.Clear(dst, 0, n);
        }
    }
}
