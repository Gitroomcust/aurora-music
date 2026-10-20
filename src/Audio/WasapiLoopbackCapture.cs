using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lumen.Audio
{
    internal sealed class CaptureFormat
    {
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public bool SourceIsFloat;
        public int SourceBits;
        public int SourceChannels;

        public string Display
        {
            get
            {
                string rate = SampleRate.ToString("N0").Replace(",", " ").Replace(".", " ");
                string channelText = Channels == 1 ? "моно" : (Channels == 2 ? "стерео" : Channels + " кан.");
                return rate + " Гц · " + channelText + " · " + BitsPerSample + " бит";
            }
        }
    }

    internal sealed class WasapiLoopbackCapture : IDisposable
    {
        private const int ClsCtxAll = 23;
        private const int AudClntStreamFlagsLoopback = 0x00020000;
        private const int AudClntBufferFlagsSilent = 0x00000002;
        private const long BufferDuration = 1000000; // 100 ms in 100-ns units

        private readonly object _sync = new object();
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _isRunning;
        private volatile float _lastPeak;
        private ManualResetEventSlim _ready;
        private Exception _startError;
        private Exception _runtimeError;
        private Stopwatch _stopwatch = new Stopwatch();
        private CaptureFormat _format;

        public event Action<byte[], int, float> DataAvailable;
        public event Action<Exception> Failed;

        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public float LastPeak
        {
            get { return _lastPeak; }
        }

        public CaptureFormat Format
        {
            get { return _format; }
        }

        public TimeSpan Elapsed
        {
            get { return _stopwatch.Elapsed; }
        }

        public Exception LastError
        {
            get { return _runtimeError ?? _startError; }
        }

        public CaptureFormat Start()
        {
            lock (_sync)
            {
                if (_thread != null)
                {
                    throw new InvalidOperationException("Capture is already running.");
                }

                _startError = null;
                _runtimeError = null;
                _lastPeak = 0;
                _running = true;
                _stopwatch = new Stopwatch();
                _ready = new ManualResetEventSlim(false);

                _thread = new Thread(CaptureThread);
                _thread.IsBackground = true;
                _thread.Name = "Lumen WASAPI loopback";
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();

                if (!_ready.Wait(4000))
                {
                    _running = false;
                    throw new TimeoutException("WASAPI не ответил за 4 секунды.");
                }

                if (_startError != null)
                {
                    _running = false;
                    // Leave _thread in place: the capture thread clears it in its
                    // finally block, so a new Start cannot overlap with the old one.
                    throw new InvalidOperationException("Не удалось открыть системный звук: " + _startError.Message, _startError);
                }

                return _format;
            }
        }

        public void Stop()
        {
            Thread thread;
            lock (_sync)
            {
                _running = false;
                thread = _thread;
            }

            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try { thread.Join(2500); }
                catch { }
            }

            _stopwatch.Stop();
            lock (_sync)
            {
                _isRunning = false;
                // A slow WASAPI call can outlive the join timeout. Keep _thread
                // non-null in that case so Start cannot open a second capture in
                // parallel; the thread clears it in its own finally block.
                if (_thread != null && !_thread.IsAlive)
                {
                    _thread = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_sync)
            {
                bool threadAlive = _thread != null && _thread.IsAlive;
                if (!threadAlive && _ready != null)
                {
                    _ready.Dispose();
                    _ready = null;
                }
            }
        }

        private void CaptureThread()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient audioClient = null;
            IAudioCaptureClient captureClient = null;
            IntPtr mixFormatPointer = IntPtr.Zero;

            try
            {
                enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device));

                Guid iid = typeof(IAudioClient).GUID;
                object clientObject;
                Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out clientObject));
                audioClient = (IAudioClient)clientObject;

                Check(audioClient.GetMixFormat(out mixFormatPointer));
                WAVEFORMATEX sourceFormat = (WAVEFORMATEX)Marshal.PtrToStructure(
                    mixFormatPointer, typeof(WAVEFORMATEX));

                bool sourceFloat = sourceFormat.wFormatTag == 3;
                if (sourceFormat.wFormatTag == 0xFFFE && sourceFormat.cbSize >= 22)
                {
                    WAVEFORMATEXTENSIBLE extensible = (WAVEFORMATEXTENSIBLE)Marshal.PtrToStructure(
                        mixFormatPointer, typeof(WAVEFORMATEXTENSIBLE));
                    sourceFloat = extensible.SubFormat == AudioSubtypes.IeeeFloat;
                }

                if (!sourceFloat && sourceFormat.wFormatTag != 1 && sourceFormat.wFormatTag != 0xFFFE)
                {
                    throw new NotSupportedException("Неизвестный формат микшера: 0x" +
                        sourceFormat.wFormatTag.ToString("X4"));
                }
                if (sourceFormat.nChannels < 1 || sourceFormat.nChannels > 8)
                {
                    throw new NotSupportedException("Неожиданное число каналов: " + sourceFormat.nChannels);
                }

                int sourceBits = sourceFormat.wBitsPerSample;
                if (sourceFloat && sourceBits != 32)
                {
                    throw new NotSupportedException("Неожиданный формат float: " + sourceBits + " бит.");
                }
                if (!sourceFloat && sourceBits != 16 && sourceBits != 24 && sourceBits != 32)
                {
                    throw new NotSupportedException("Неожиданная битность микшера: " + sourceBits);
                }

                int outputChannels = Math.Min(2, Math.Max(1, (int)sourceFormat.nChannels));
                _format = new CaptureFormat();
                _format.SampleRate = (int)sourceFormat.nSamplesPerSec;
                _format.SourceChannels = sourceFormat.nChannels;
                _format.SourceBits = sourceBits;
                _format.SourceIsFloat = sourceFloat;
                _format.Channels = outputChannels;
                _format.BitsPerSample = 16;

                Check(audioClient.Initialize(AudioClientShareMode.Shared, AudClntStreamFlagsLoopback,
                    BufferDuration, 0, mixFormatPointer, IntPtr.Zero));

                Guid captureIid = typeof(IAudioCaptureClient).GUID;
                object captureObject;
                Check(audioClient.GetService(ref captureIid, out captureObject));
                captureClient = (IAudioCaptureClient)captureObject;

                Check(audioClient.Start());
                _stopwatch.Start();
                _isRunning = true;
                _ready.Set();

                int blockAlign = sourceFormat.nBlockAlign;
                int sleepMilliseconds = 8;

                while (_running)
                {
                    uint packetFrames;
                    int hr = captureClient.GetNextPacketSize(out packetFrames);
                    if (hr < 0)
                    {
                        Check(hr);
                    }

                    if (packetFrames == 0)
                    {
                        Thread.Sleep(sleepMilliseconds);
                        continue;
                    }

                    while (packetFrames > 0 && _running)
                    {
                        IntPtr dataPointer;
                        uint frames;
                        uint flags;
                        ulong devicePosition;
                        ulong qpcPosition;
                        Check(captureClient.GetBuffer(out dataPointer, out frames, out flags,
                            out devicePosition, out qpcPosition));

                        if (frames > 0)
                        {
                            int bytes = (int)frames * blockAlign;
                            byte[] raw = new byte[bytes];
                            if ((flags & AudClntBufferFlagsSilent) == 0 && dataPointer != IntPtr.Zero)
                            {
                                Marshal.Copy(dataPointer, raw, 0, bytes);
                            }

                            float peak;
                            byte[] pcm = ConvertToStereoPcm(raw, (int)frames, sourceFormat, sourceFloat, out peak);
                            _lastPeak = peak;

                            Action<byte[], int, float> handler = DataAvailable;
                            if (handler != null && pcm != null && pcm.Length > 0)
                            {
                                try
                                {
                                    handler(pcm, pcm.Length, peak);
                                }
                                catch (Exception ex)
                                {
                                    Log.Write("Capture data subscriber failed", ex);
                                }
                            }
                        }

                        Check(captureClient.ReleaseBuffer(frames));

                        hr = captureClient.GetNextPacketSize(out packetFrames);
                        if (hr < 0)
                        {
                            Check(hr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _runtimeError = ex;
                    Log.Write("WASAPI capture failed", ex);
                }
                else
                {
                    _startError = ex;
                }

                if (_ready != null && !_ready.IsSet)
                {
                    _startError = ex;
                    _ready.Set();
                }
            }
            finally
            {
                _isRunning = false;
                if (_stopwatch.IsRunning) _stopwatch.Stop();

                if (audioClient != null)
                {
                    try { audioClient.Stop(); }
                    catch { }
                }

                if (mixFormatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(mixFormatPointer);
                }

                Release(captureClient);
                Release(audioClient);
                Release(device);
                Release(enumerator);

                _running = false;

                lock (_sync)
                {
                    if (object.ReferenceEquals(_thread, Thread.CurrentThread))
                    {
                        _thread = null;
                    }
                }

                Exception error = _runtimeError;
                if (error != null)
                {
                    Action<Exception> failed = Failed;
                    if (failed != null)
                    {
                        try { failed(error); }
                        catch { }
                    }
                }
            }
        }

        private static byte[] ConvertToStereoPcm(byte[] raw, int frames, WAVEFORMATEX format,
            bool sourceFloat, out float peak)
        {
            int channels = format.nChannels;
            int outputChannels = Math.Min(2, Math.Max(1, channels));
            byte[] output = new byte[frames * outputChannels * 2];
            int sourceBits = format.wBitsPerSample;
            int blockAlign = format.nBlockAlign;
            float max = 0;

            if (sourceFloat)
            {
                int sampleCount = frames * channels;
                float[] samples = new float[sampleCount];
                Buffer.BlockCopy(raw, 0, samples, 0, Math.Min(raw.Length, sampleCount * 4));

                int outIndex = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * channels;
                    float left = samples[baseIndex];
                    float right = outputChannels > 1 && channels > 1 ? samples[baseIndex + 1] : left;
                    if (Math.Abs(left) > max) max = Math.Abs(left);
                    if (Math.Abs(right) > max) max = Math.Abs(right);
                    outIndex = WriteSample(output, outIndex, left);
                    outIndex = WriteSample(output, outIndex, right);
                }
            }
            else if (sourceBits == 16)
            {
                int sampleCount = frames * channels;
                short[] samples = new short[sampleCount];
                Buffer.BlockCopy(raw, 0, samples, 0, Math.Min(raw.Length, sampleCount * 2));

                int outIndex = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * channels;
                    float left = samples[baseIndex] / 32768f;
                    float right = outputChannels > 1 && channels > 1 ? samples[baseIndex + 1] / 32768f : left;
                    if (Math.Abs(left) > max) max = Math.Abs(left);
                    if (Math.Abs(right) > max) max = Math.Abs(right);
                    outIndex = WriteSample(output, outIndex, left);
                    outIndex = WriteSample(output, outIndex, right);
                }
            }
            else if (sourceBits == 24)
            {
                int outIndex = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * blockAlign;
                    float left = Read24(raw, baseIndex) / 8388608f;
                    float right = outputChannels > 1 && channels > 1
                        ? Read24(raw, baseIndex + 3) / 8388608f
                        : left;
                    if (Math.Abs(left) > max) max = Math.Abs(left);
                    if (Math.Abs(right) > max) max = Math.Abs(right);
                    outIndex = WriteSample(output, outIndex, left);
                    outIndex = WriteSample(output, outIndex, right);
                }
            }
            else // 32-bit integer PCM
            {
                int sampleCount = frames * channels;
                int[] samples = new int[sampleCount];
                Buffer.BlockCopy(raw, 0, samples, 0, Math.Min(raw.Length, sampleCount * 4));

                int outIndex = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * channels;
                    float left = (float)(samples[baseIndex] / 2147483648.0);
                    float right = outputChannels > 1 && channels > 1
                        ? (float)(samples[baseIndex + 1] / 2147483648.0)
                        : left;
                    if (Math.Abs(left) > max) max = Math.Abs(left);
                    if (Math.Abs(right) > max) max = Math.Abs(right);
                    outIndex = WriteSample(output, outIndex, left);
                    outIndex = WriteSample(output, outIndex, right);
                }
            }

            peak = Math.Min(1f, max);
            return output;
        }

        private static int WriteSample(byte[] target, int offset, float value)
        {
            if (value > 1f) value = 1f;
            else if (value < -1f) value = -1f;
            int sample = (int)(value * 32767f);
            if (sample > 32767) sample = 32767;
            else if (sample < -32768) sample = -32768;
            target[offset] = (byte)(sample & 0xFF);
            target[offset + 1] = (byte)((sample >> 8) & 0xFF);
            return offset + 2;
        }

        private static float Read24(byte[] data, int offset)
        {
            int value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            return value;
        }

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        private static void Release(object comObject)
        {
            if (comObject == null) return;
            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch { }
        }

        // ============================ WASAPI INTEROP ============================

        private enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
            eAll = 2
        }

        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        private enum AudioClientShareMode
        {
            Shared = 0,
            Exclusive = 1
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        private static class AudioSubtypes
        {
            public static readonly Guid IeeeFloat =
                new Guid("00000003-0000-0010-8000-00AA00389B71");
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);

            [PreserveSig]
            int OpenPropertyStore(int access, out IntPtr properties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(AudioClientShareMode shareMode, int streamFlags,
                long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint bufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long latency);

            [PreserveSig]
            int GetCurrentPadding(out uint paddingFrames);

            [PreserveSig]
            int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr format);

            [PreserveSig]
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(ref Guid iid,
                [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                out ulong devicePosition, out ulong qpcPosition);

            [PreserveSig]
            int ReleaseBuffer(uint frames);

            [PreserveSig]
            int GetNextPacketSize(out uint frames);
        }
    }
}
