// Ponte mínima com o Media Foundation (já vem no Windows): grava MP4 (H.264 + AAC)
// e lê MP4 de volta como quadros RGB32 e áudio PCM. Sem SDK: as interfaces COM são
// declaradas aqui só com o que o Capturo usa (os demais métodos viram "slots" vazios).
using System;
using System.Runtime.InteropServices;

namespace Capturo
{
    static class MF
    {
        public static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid MT_YUV_MATRIX = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        public static readonly Guid MT_VIDEO_NOMINAL_RANGE = new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11");
        public static readonly Guid AudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid AudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid SINK_WRITER_DISABLE_THROTTLING = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        public static readonly Guid SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        public static readonly Guid PD_DURATION = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        public const int FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC), FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);
        public const int ALL_STREAMS = unchecked((int)0xFFFFFFFE), MEDIASOURCE = unchecked((int)0xFFFFFFFF);
        public const int SRF_ENDOFSTREAM = 0x2, SRF_CURRENTMEDIATYPECHANGED = 0x20;

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateMediaType(out IMFAttributes type);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateAttributes(out IMFAttributes attr, int size);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] public static extern void MFCreateMemoryBuffer(int max, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attr, out IMFSinkWriter writer);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attr, out IMFSourceReader reader);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void Copy(IntPtr dst, IntPtr src, IntPtr len);

        static int users;
        static readonly object gate = new object();
        public static void Start() { lock (gate) { if (users++ == 0) MFStartup(0x00020070, 0); } }
        public static void Stop() { lock (gate) { if (--users == 0) { try { MFShutdown(); } catch { } } } }

        public static ulong Pack(int hi, int lo) { return ((ulong)(uint)hi << 32) | (uint)lo; }

        public static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

        public static IMFAttributes VideoType(Guid subtype, int w, int h, int fps)
        {
            IMFAttributes t; MFCreateMediaType(out t);
            t.SetGUID(MT_MAJOR_TYPE, MediaType_Video);
            t.SetGUID(MT_SUBTYPE, subtype);
            t.SetUINT32(MT_INTERLACE_MODE, 2); // progressivo
            t.SetUINT64(MT_FRAME_SIZE, Pack(w, h));
            t.SetUINT64(MT_FRAME_RATE, Pack(fps, 1));
            t.SetUINT64(MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            return t;
        }

        public static IMFAttributes AudioType(Guid subtype, int rate, int channels)
        {
            IMFAttributes t; MFCreateMediaType(out t);
            t.SetGUID(MT_MAJOR_TYPE, MediaType_Audio);
            t.SetGUID(MT_SUBTYPE, subtype);
            t.SetUINT32(MT_AUDIO_BITS_PER_SAMPLE, 16);
            t.SetUINT32(MT_AUDIO_SAMPLES_PER_SECOND, rate);
            t.SetUINT32(MT_AUDIO_NUM_CHANNELS, channels);
            return t;
        }

        // Amostra com uma cópia dos bytes (tempo e duração em unidades de 100 ns).
        public static IMFSample Sample(IntPtr data, int len, long time, long dur)
        {
            IMFMediaBuffer buf; MFCreateMemoryBuffer(len, out buf);
            IntPtr p; int max, cur;
            buf.Lock(out p, out max, out cur);
            Copy(p, data, (IntPtr)len);
            buf.Unlock();
            buf.SetCurrentLength(len);
            IMFSample s; MFCreateSample(out s);
            s.AddBuffer(buf);
            s.SetSampleTime(time);
            s.SetSampleDuration(dur);
            Release(buf);
            return s;
        }
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
        [PreserveSig] int GetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        void GetDouble();
        [PreserveSig] int GetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob();
        void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        void SetDouble();
        void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore();
        void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        // IMFAttributes (30)
        void a0(); void a1(); void a2(); void a3(); void a4(); void a5(); void a6(); void a7(); void a8(); void a9();
        void b0(); void b1(); void b2(); void b3(); void b4(); void b5(); void b6(); void b7(); void b8(); void b9();
        void c0(); void c1(); void c2(); void c3(); void c4(); void c5(); void c6(); void c7(); void c8(); void c9();
        void GetSampleFlags(); void SetSampleFlags();
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void GetSampleDuration(out long dur);
        void SetSampleDuration(long dur);
        void GetBufferCount(out int count);
        void GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(IMFMediaBuffer buffer);
        void RemoveBufferByIndex(); void RemoveAllBuffers();
        void GetTotalLength(out int len);
        void CopyToBuffer();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr data, out int max, out int cur);
        void Unlock();
        void GetCurrentLength(out int len);
        void SetCurrentLength(int len);
        void GetMaxLength(out int len);
    }

    [ComImport, Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMF2DBuffer
    {
        void Lock2D(out IntPtr scan0, out int pitch);
        void Unlock2D();
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        void AddStream([MarshalAs(UnmanagedType.IUnknown)] object type, out int index);
        void SetInputMediaType(int index, [MarshalAs(UnmanagedType.IUnknown)] object type, IMFAttributes encParams);
        void BeginWriting();
        void WriteSample(int index, IMFSample sample);
        void SendStreamTick(int index, long time);
        void PlaceMarker(); void NotifyEndOfSegment(); void Flush();
        void Finalize_();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(int index, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(int index, int typeIndex, out IMFAttributes type);
        [PreserveSig] int GetCurrentMediaType(int index, out IMFAttributes type);
        void SetCurrentMediaType(int index, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] object type);
        void SetCurrentPosition([MarshalAs(UnmanagedType.LPStruct)] Guid format, [In] ref PropVariant pos);
        void ReadSample(int index, int flags, out int actualIndex, out int streamFlags, out long time, out IMFSample sample);
        void Flush(int index);
        void GetServiceForStream();
        [PreserveSig] int GetPresentationAttribute(int index, [MarshalAs(UnmanagedType.LPStruct)] Guid key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public long hVal;
        public static PropVariant Int64(long v) { var p = new PropVariant(); p.vt = 20; p.hVal = v; return p; } // VT_I8
    }

    // Grava MP4: vídeo a partir de quadros BGRA (de cima para baixo) e áudio PCM 16 bits.
    sealed class Mp4Writer : IDisposable
    {
        IMFSinkWriter w;
        readonly int vIndex = -1, aIndex = -1;
        readonly int width, height, stride;
        public readonly int AudioRate, AudioChannels;
        readonly bool nv12;
        bool done;

        public Mp4Writer(string path, int width, int height, int fps, int bitrate, int audioRate, int audioChannels, bool hardware)
            : this(path, width, height, fps, bitrate, audioRate, audioChannels, hardware, false) { }

        // nv12: o próprio Capturo converte os quadros para YUV (bem mais barato que o conversor
        // do Windows, que gasta ~18 ms de CPU por quadro 1080p).
        public Mp4Writer(string path, int width, int height, int fps, int bitrate, int audioRate, int audioChannels, bool hardware, bool nv12)
        {
            this.width = width; this.height = height; stride = width * 4; this.nv12 = nv12;
            AudioRate = audioRate; AudioChannels = audioChannels;
            MF.Start();
            try
            {
                IMFAttributes attr; MF.MFCreateAttributes(out attr, 2);
                attr.SetUINT32(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, hardware ? 1 : 0);
                attr.SetUINT32(MF.SINK_WRITER_DISABLE_THROTTLING, 1);
                MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attr, out w);
                MF.Release(attr);

                var outV = MF.VideoType(MF.VideoFormat_H264, width, height, fps);
                outV.SetUINT32(MF.MT_AVG_BITRATE, bitrate);
                outV.SetUINT32(MF.MT_MPEG2_PROFILE, 100); // High
                if (nv12) { outV.SetUINT32(MF.MT_YUV_MATRIX, 1); outV.SetUINT32(MF.MT_VIDEO_NOMINAL_RANGE, 2); }   // BT.709, 16-235
                w.AddStream(outV, out vIndex);
                var inV = MF.VideoType(nv12 ? MF.VideoFormat_NV12 : MF.VideoFormat_RGB32, width, height, fps);
                inV.SetUINT32(MF.MT_DEFAULT_STRIDE, nv12 ? width : stride);
                if (nv12) { inV.SetUINT32(MF.MT_YUV_MATRIX, 1); inV.SetUINT32(MF.MT_VIDEO_NOMINAL_RANGE, 2); }
                w.SetInputMediaType(vIndex, inV, null);
                MF.Release(outV); MF.Release(inV);

                if (audioRate > 0)
                {
                    var outA = MF.AudioType(MF.AudioFormat_AAC, audioRate, audioChannels);
                    outA.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 24000); // 192 kbps
                    w.AddStream(outA, out aIndex);
                    var inA = MF.AudioType(MF.AudioFormat_PCM, audioRate, audioChannels);
                    inA.SetUINT32(MF.MT_AUDIO_BLOCK_ALIGNMENT, 2 * audioChannels);
                    inA.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 2 * audioChannels * audioRate);
                    w.SetInputMediaType(aIndex, inA, null);
                    MF.Release(outA); MF.Release(inA);
                }
                w.BeginWriting();
            }
            catch
            {
                if (w != null) MF.Release(w);
                w = null;
                MF.Stop();
                throw;
            }
        }

        public int Width { get { return width; } }
        public int Height { get { return height; } }

        // data: BGRA de cima para baixo, com "rowStride" bytes por linha.
        public void WriteVideo(IntPtr data, int rowStride, long time, long dur)
        {
            int len = nv12 ? width * height * 3 / 2 : stride * height;
            IMFMediaBuffer buf; MF.MFCreateMemoryBuffer(len, out buf);
            IntPtr p; int max, cur;
            buf.Lock(out p, out max, out cur);
            if (nv12) Nv12.FromBgra(data, rowStride, p, width, height);
            else if (rowStride == stride) MF.Copy(p, data, (IntPtr)len);
            else for (int y = 0; y < height; y++) MF.Copy(p + y * stride, data + y * rowStride, (IntPtr)stride);
            buf.Unlock();
            buf.SetCurrentLength(len);
            IMFSample s; MF.MFCreateSample(out s);
            s.AddBuffer(buf); s.SetSampleTime(time); s.SetSampleDuration(dur);
            w.WriteSample(vIndex, s);
            MF.Release(s); MF.Release(buf);
        }

        // PCM 16 bits intercalado; time em 100 ns.
        public void WriteAudio(short[] pcm, int frames, long time)
        {
            if (aIndex < 0 || frames <= 0) return;
            var h = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            try
            {
                long dur = frames * 10000000L / AudioRate;
                var s = MF.Sample(h.AddrOfPinnedObject(), frames * AudioChannels * 2, time, dur);
                w.WriteSample(aIndex, s);
                MF.Release(s);
            }
            finally { h.Free(); }
        }

        public void Finish()
        {
            if (done || w == null) return;
            done = true;
            w.Finalize_();
        }

        public void Dispose()
        {
            if (w != null) { MF.Release(w); w = null; MF.Stop(); }
        }
    }

    // Lê um MP4: quadros de vídeo em BGRA e o áudio em PCM 16 bits.
    sealed class Mp4Reader : IDisposable
    {
        IMFSourceReader r;
        public int Width, Height;          // tamanho do quadro decodificado (pode ter linhas extras no fim)
        public long Duration;              // 100 ns
        public bool HasAudio;
        public int AudioRate, AudioChannels;
        int stride;
        bool nv12;
        IntPtr bgra; int bgraBytes;

        public Mp4Reader(string path, bool video, bool audio)
        {
            MF.Start();
            try
            {
                IMFAttributes attr; MF.MFCreateAttributes(out attr, 1);
                attr.SetUINT32(MF.SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
                MF.MFCreateSourceReaderFromURL(path, attr, out r);
                MF.Release(attr);
                r.SetStreamSelection(MF.ALL_STREAMS, false);

                PropVariant pv;
                if (r.GetPresentationAttribute(MF.MEDIASOURCE, MF.PD_DURATION, out pv) == 0) Duration = pv.hVal;

                if (video)
                {
                    r.SetStreamSelection(MF.FIRST_VIDEO_STREAM, true);
                    IMFAttributes t; MF.MFCreateMediaType(out t);
                    t.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
                    // NV12 (o formato do decodificador): a conversão para BGRA é feita aqui, em paralelo.
                    // O conversor do Windows faz isso num núcleo só e era a etapa mais lenta da exportação.
                    t.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_NV12);
                    try { r.SetCurrentMediaType(MF.FIRST_VIDEO_STREAM, IntPtr.Zero, t); nv12 = true; }
                    catch
                    {
                        t.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
                        r.SetCurrentMediaType(MF.FIRST_VIDEO_STREAM, IntPtr.Zero, t);
                    }
                    MF.Release(t);
                    ReadVideoType();
                    VisibleWidth = Width; VisibleHeight = Height;
                }
                if (audio)
                {
                    IMFAttributes nat;
                    if (r.GetNativeMediaType(MF.FIRST_AUDIO_STREAM, 0, out nat) == 0)
                    {
                        MF.Release(nat);
                        r.SetStreamSelection(MF.FIRST_AUDIO_STREAM, true);
                        IMFAttributes t; MF.MFCreateMediaType(out t);
                        t.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
                        t.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_PCM);
                        t.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                        r.SetCurrentMediaType(MF.FIRST_AUDIO_STREAM, IntPtr.Zero, t);
                        MF.Release(t);
                        IMFAttributes cur;
                        if (r.GetCurrentMediaType(MF.FIRST_AUDIO_STREAM, out cur) == 0)
                        {
                            cur.GetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, out AudioRate);
                            cur.GetUINT32(MF.MT_AUDIO_NUM_CHANNELS, out AudioChannels);
                            MF.Release(cur);
                            HasAudio = true;
                        }
                    }
                }
            }
            catch
            {
                if (r != null) MF.Release(r);
                r = null;
                MF.Stop();
                throw;
            }
        }

        public int VisibleWidth, VisibleHeight;   // tamanho do vídeo (sem a margem de alinhamento)

        void ReadVideoType()
        {
            IMFAttributes cur;
            if (r.GetCurrentMediaType(MF.FIRST_VIDEO_STREAM, out cur) != 0) return;
            ulong fs; cur.GetUINT64(MF.MT_FRAME_SIZE, out fs);
            Width = (int)(fs >> 32); Height = (int)(fs & 0xffffffff);
            int st;
            stride = cur.GetUINT32(MF.MT_DEFAULT_STRIDE, out st) == 0 ? st : nv12 ? Width : Width * 4;
            MF.Release(cur);
        }

        public void Seek(long time)
        {
            var pv = PropVariant.Int64(time);
            r.SetCurrentPosition(Guid.Empty, ref pv);
        }

        // Próximo quadro: entrega um ponteiro BGRA de cima para baixo e o passo por linha.
        // Retorna false no fim do vídeo.
        public delegate void FrameAction(IntPtr scan0, int pitch, long time);
        public bool ReadFrame(FrameAction use)
        {
            while (true)
            {
                int idx, flags; long time; IMFSample s;
                r.ReadSample(MF.FIRST_VIDEO_STREAM, 0, out idx, out flags, out time, out s);
                if ((flags & MF.SRF_ENDOFSTREAM) != 0) { MF.Release(s); return false; }
                // o decodificador pode passar a entregar quadros alinhados (ex.: 1424x864 para 1420x860)
                if ((flags & MF.SRF_CURRENTMEDIATYPECHANGED) != 0) ReadVideoType();
                if (s == null) continue;
                try
                {
                    IMFMediaBuffer buf; s.ConvertToContiguousBuffer(out buf);
                    try
                    {
                        var b2 = buf as IMF2DBuffer;
                        if (b2 != null)
                        {
                            IntPtr scan0; int pitch;
                            b2.Lock2D(out scan0, out pitch);
                            try { Deliver(use, scan0, pitch, time); } finally { b2.Unlock2D(); }
                        }
                        else
                        {
                            IntPtr p; int max, cur;
                            buf.Lock(out p, out max, out cur);
                            try
                            {
                                if (nv12) Deliver(use, p, stride, time);
                                else if (stride < 0) use(p + (Height - 1) * -stride, stride, time);
                                else use(p, stride, time);
                            }
                            finally { buf.Unlock(); }
                        }
                    }
                    finally { MF.Release(buf); }
                }
                finally { MF.Release(s); }
                return true;
            }
        }

        void Deliver(FrameAction use, IntPtr scan0, int pitch, long time)
        {
            if (!nv12) { use(scan0, pitch, time); return; }
            int need = Width * Height * 4;
            if (bgra == IntPtr.Zero || bgraBytes < need)
            {
                if (bgra != IntPtr.Zero) Marshal.FreeHGlobal(bgra);
                bgra = Marshal.AllocHGlobal(need); bgraBytes = need;
            }
            Nv12.ToBgra(scan0, pitch, Height, bgra, Width, Height);
            use(bgra, Width * 4, time);
        }

        // Próximo bloco de áudio PCM 16 bits (intercalado). null no fim.
        public short[] ReadAudio(out long time)
        {
            while (true)
            {
                int idx, flags; IMFSample s;
                r.ReadSample(MF.FIRST_AUDIO_STREAM, 0, out idx, out flags, out time, out s);
                if ((flags & MF.SRF_ENDOFSTREAM) != 0) { MF.Release(s); return null; }
                if (s == null) continue;
                try
                {
                    IMFMediaBuffer buf; s.ConvertToContiguousBuffer(out buf);
                    try
                    {
                        IntPtr p; int max, cur;
                        buf.Lock(out p, out max, out cur);
                        var data = new short[cur / 2];
                        Marshal.Copy(p, data, 0, data.Length);
                        buf.Unlock();
                        return data;
                    }
                    finally { MF.Release(buf); }
                }
                finally { MF.Release(s); }
            }
        }

        public void Dispose()
        {
            if (r != null) { MF.Release(r); r = null; MF.Stop(); }
            if (bgra != IntPtr.Zero) { Marshal.FreeHGlobal(bgra); bgra = IntPtr.Zero; }
        }
    }

    // BGRA -> NV12 (BT.709, faixa 16-235: o padrão que os decodificadores usam para vídeo HD), em 4 partes paralelas. ~1 ms de tempo por quadro 1080p.
    static class Nv12
    {
        // NV12 (BT.709, 16-235) -> BGRA, dividido entre os núcleos. planeH: altura do plano Y no buffer.
        public static unsafe void ToBgra(IntPtr src, int pitch, int planeH, IntPtr dst, int w, int h)
        {
            byte* y0 = (byte*)src, uv0 = y0 + (long)pitch * planeH, d0 = (byte*)dst;
            int pairs = h / 2;
            System.Threading.Tasks.Parallel.For(0, pairs, py =>
            {
                byte* uv = uv0 + (long)py * pitch;
                for (int k = 0; k < 2; k++)
                {
                    int yy = py * 2 + k;
                    byte* ys = y0 + (long)yy * pitch;
                    uint* o = (uint*)(d0 + (long)yy * w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        int c = (ys[x] - 16) * 298;
                        int u = uv[x & ~1] - 128, v = uv[(x & ~1) + 1] - 128;
                        int r = (c + 459 * v + 128) >> 8;
                        int g = (c - 55 * u - 136 * v + 128) >> 8;
                        int b = (c + 541 * u + 128) >> 8;
                        r = r < 0 ? 0 : r > 255 ? 255 : r; g = g < 0 ? 0 : g > 255 ? 255 : g; b = b < 0 ? 0 : b > 255 ? 255 : b;
                        o[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                    }
                }
            });
        }

        public static unsafe void FromBgra(IntPtr src, int srcStride, IntPtr dst, int w, int h)
        {
            byte* s0 = (byte*)src, y0 = (byte*)dst, uv0 = y0 + w * h;
            int pairs = h / 2, parts = 4;
            System.Threading.Tasks.Parallel.For(0, parts, part =>
            {
                int from = pairs * part / parts, to = pairs * (part + 1) / parts;
                for (int py = from; py < to; py++)
                {
                    byte* r0 = s0 + (2 * py) * srcStride, r1 = r0 + srcStride;
                    byte* ya = y0 + (2 * py) * w, yb = ya + w, uv = uv0 + py * w;
                    for (int x = 0; x + 1 < w; x += 2)
                    {
                        int b00 = r0[0], g00 = r0[1], q00 = r0[2], b01 = r0[4], g01 = r0[5], q01 = r0[6];
                        int b10 = r1[0], g10 = r1[1], q10 = r1[2], b11 = r1[4], g11 = r1[5], q11 = r1[6];
                        ya[0] = (byte)(((47 * q00 + 157 * g00 + 16 * b00 + 128) >> 8) + 16);
                        ya[1] = (byte)(((47 * q01 + 157 * g01 + 16 * b01 + 128) >> 8) + 16);
                        yb[0] = (byte)(((47 * q10 + 157 * g10 + 16 * b10 + 128) >> 8) + 16);
                        yb[1] = (byte)(((47 * q11 + 157 * g11 + 16 * b11 + 128) >> 8) + 16);
                        int r = q00 + q01 + q10 + q11, g = g00 + g01 + g10 + g11, b = b00 + b01 + b10 + b11;   // média de 4 (x4)
                        uv[0] = (byte)(((-26 * r - 86 * g + 112 * b + 512) >> 10) + 128);   // pesos somam 0: cinza fica cinza
                        uv[1] = (byte)(((112 * r - 102 * g - 10 * b + 512) >> 10) + 128);
                        r0 += 8; r1 += 8; ya += 2; yb += 2; uv += 2;
                    }
                }
            });
        }
    }
}
