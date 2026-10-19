using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Lumen.Audio
{
    internal sealed class PlaybackErrorEventArgs : EventArgs
    {
        public readonly Exception Error;

        public PlaybackErrorEventArgs(Exception error)
        {
            Error = error;
        }
    }

    /// <summary>
    /// Small Media Foundation playback wrapper based on the built-in MFPlay COM API.
    /// It avoids the legacy Windows Media Player dependency used by WPF MediaPlayer.
    /// </summary>
    internal sealed class MfPlayAudioEngine : IDisposable
    {
        private static readonly Guid PositionType100Ns = Guid.Empty;
        private const int MfVersion = 0x00020070;

        private readonly Dispatcher _dispatcher;
        private readonly MfPlayCallback _callback;
        private object _currentItem;
        private IMFPMediaPlayer _player;
        private bool _disposed;
        private bool _mfStarted;
        private float _volume = 0.7f;
        private bool _hasDurationCache;
        private TimeSpan _durationCache;

        public event EventHandler MediaOpened;
        public event EventHandler MediaEnded;
        public event EventHandler<PlaybackErrorEventArgs> Failed;

        public MfPlayAudioEngine()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _callback = new MfPlayCallback(this);
            try
            {
                MFStartup(MfVersion, 0);
                _mfStarted = true;
            }
            catch { }

            IMFPMediaPlayer player;
            int hr = MFPCreateMediaPlayer(null, false, 0, _callback, IntPtr.Zero, out player);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            _player = player;
            _player.SetVolume(_volume);
            _player.SetMute(false);
        }

        public float Volume
        {
            get { return _volume; }
            set
            {
                _volume = Math.Max(0f, Math.Min(1f, value));
                if (_player != null)
                {
                    try { _player.SetVolume(_volume); }
                    catch { }
                }
            }
        }

        public bool HasMedia
        {
            get { return _currentItem != null; }
        }

        public bool HasCachedDuration
        {
            get { return _hasDurationCache; }
        }

        public TimeSpan CachedDuration
        {
            get { return _durationCache; }
        }

        public bool IsStopped
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get
            {
                if (_player == null || _currentItem == null) return false;
                try
                {
                    MFP_MEDIAPLAYER_STATE state;
                    int hr = _player.GetState(out state);
                    return hr >= 0 && state == MFP_MEDIAPLAYER_STATE.Stopped;
                }
                catch
                {
                    return false;
                }
            }
        }

        public TimeSpan Position
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get
            {
                if (_player == null || _currentItem == null) return TimeSpan.Zero;
                try
                {
                    Guid positionType = PositionType100Ns;
                    PROPVARIANT value;
                    int hr = _player.GetPosition(ref positionType, out value);
                    if (hr < 0) return TimeSpan.Zero;
                    long ticks = value.hVal;
                    if (ticks < 0) ticks = 0;
                    return TimeSpan.FromTicks(ticks);
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }

        public void Open(string path)
        {
            if (_disposed) throw new ObjectDisposedException("MfPlayAudioEngine");
            if (_player == null) throw new InvalidOperationException("Media player is not initialized.");

            CloseItem();
            _hasDurationCache = false;
            _durationCache = TimeSpan.Zero;
            string url = path;
            object item;
            int hr = _player.CreateMediaItemFromURL(url, true, IntPtr.Zero, out item);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            _currentItem = item;
            hr = _player.SetMediaItem(item);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            _player.SetVolume(_volume);
            TimeSpan itemDuration;
            if (TryReadItemDuration(item, out itemDuration))
            {
                _durationCache = itemDuration;
                _hasDurationCache = true;
            }
            RaiseMediaOpened();
        }

        public void Play()
        {
            if (_player == null) return;
            int hr = _player.Play();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public void Pause()
        {
            if (_player == null) return;
            int hr = _player.Pause();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public void Stop()
        {
            if (_player == null) return;
            try { _player.Stop(); }
            catch { }
        }

        public void Seek(TimeSpan position)
        {
            if (_player == null || _currentItem == null) return;
            PROPVARIANT value = new PROPVARIANT();
            value.vt = 20; // VT_I8
            value.hVal = Math.Max(0, position.Ticks);
            try
            {
                Guid positionType = PositionType100Ns;
                _player.SetPosition(ref positionType, ref value);
            }
            catch (Exception ex) { Log.Write("MFPlay seek failed", ex); }
        }

        public void Close()
        {
            CloseItem();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CloseItem(); }
            catch { }
            if (_player != null)
            {
                try { _player.Shutdown(); }
                catch { }
                try { Marshal.ReleaseComObject(_player); }
                catch { }
                _player = null;
            }
            _currentItem = null;
            if (_mfStarted)
            {
                try { MFShutdown(); }
                catch { }
                _mfStarted = false;
            }
        }

        private void CloseItem()
        {
            if (_player == null) return;
            try { _player.Stop(); }
            catch { }
            try { _player.ClearMediaItem(); }
            catch { }
            if (_currentItem != null)
            {
                try
                {
                    if (Marshal.IsComObject(_currentItem)) Marshal.ReleaseComObject(_currentItem);
                }
                catch { }
                _currentItem = null;
            }
        }

        private static bool TryReadItemDuration(object item, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            IMFPMediaItem mediaItem = item as IMFPMediaItem;
            if (mediaItem == null) return false;
            try
            {
                Guid positionType = PositionType100Ns;
                PROPVARIANT value;
                int hr = mediaItem.GetDuration(ref positionType, out value);
                if (hr < 0) return false;
                if (value.vt != 20 && value.vt != 21) return false;
                long ticks = value.hVal;
                if (ticks <= 0) return false;
                duration = TimeSpan.FromTicks(ticks);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("MFPlay item duration failed", ex);
                return false;
            }
        }

        private void RaiseMediaOpened()
        {
            EventHandler handler = MediaOpened;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        internal void OnPlayerEvent(int eventType, int hrEvent)
        {
            if (_disposed) return;
            if (eventType == 11) // MFP_EVENT_TYPE_PLAYBACK_ENDED
            {
                _dispatcher.BeginInvoke(new Action(delegate
                {
                    EventHandler handler = MediaEnded;
                    if (handler != null) handler(this, EventArgs.Empty);
                }));
            }
            else if (eventType == 10) // MFP_EVENT_TYPE_ERROR
            {
                Exception error;
                try { error = Marshal.GetExceptionForHR(hrEvent); }
                catch { error = new Exception("MFPlay error 0x" + hrEvent.ToString("X8")); }
                if (error == null) error = new Exception("MFPlay error 0x" + hrEvent.ToString("X8"));
                Log.Write("MFPlay error event", error);
                _dispatcher.BeginInvoke(new Action(delegate
                {
                    EventHandler<PlaybackErrorEventArgs> handler = Failed;
                    if (handler != null) handler(this, new PlaybackErrorEventArgs(error));
                }));
            }
        }

        // ============================ COM INTEROP ============================

        [ComVisible(true)]
        [Guid("766C8FFB-5FDB-4FEA-A28D-B912996F51BD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFPMediaPlayerCallback
        {
            void OnMediaPlayerEvent(IntPtr eventHeader);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class MfPlayCallback : IMFPMediaPlayerCallback
        {
            private readonly MfPlayAudioEngine _engine;

            public MfPlayCallback(MfPlayAudioEngine engine)
            {
                _engine = engine;
            }

            public void OnMediaPlayerEvent(IntPtr eventHeader)
            {
                try
                {
                    if (eventHeader == IntPtr.Zero) return;
                    MFP_EVENT_HEADER header = (MFP_EVENT_HEADER)Marshal.PtrToStructure(
                        eventHeader, typeof(MFP_EVENT_HEADER));
                    _engine.OnPlayerEvent(header.eEventType, header.hrEvent);
                }
                catch (Exception ex)
                {
                    Log.Write("MFPlay callback failed", ex);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MFP_EVENT_HEADER
        {
            public int eEventType;
            public int hrEvent;
            public IntPtr pMediaPlayer;
            public int eState;
            public IntPtr pPropertyStore;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public long hVal;
            [FieldOffset(8)] public double dblVal;
            [FieldOffset(8)] public IntPtr ptr;
        }

        private enum MFP_MEDIAPLAYER_STATE
        {
            Empty = 0,
            Stopped = 1,
            Playing = 2,
            Paused = 3,
            Shutdown = 4
        }

        [ComImport]
        [Guid("A714590A-58AF-430A-85BF-44F5EC838D85")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFPMediaPlayer
        {
            [PreserveSig] int Play();
            [PreserveSig] int Pause();
            [PreserveSig] int Stop();
            [PreserveSig] int FrameStep();
            [PreserveSig] int SetPosition([In] ref Guid positionType, [In] ref PROPVARIANT value);
            [PreserveSig] int GetPosition([In] ref Guid positionType, out PROPVARIANT value);
            [PreserveSig] int GetDuration([In] ref Guid positionType, out PROPVARIANT value);
            [PreserveSig] int SetRate(float rate);
            [PreserveSig] int GetRate(out float rate);
            [PreserveSig] int GetSupportedRates([MarshalAs(UnmanagedType.Bool)] bool forward,
                out float slowest, out float fastest);
            [PreserveSig] int GetState(out MFP_MEDIAPLAYER_STATE state);
            [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url,
                [MarshalAs(UnmanagedType.Bool)] bool synchronous, IntPtr userData,
                [MarshalAs(UnmanagedType.Interface)] out object item);
            [PreserveSig] int CreateMediaItemFromObject([MarshalAs(UnmanagedType.IUnknown)] object source,
                [MarshalAs(UnmanagedType.Bool)] bool synchronous, IntPtr userData,
                [MarshalAs(UnmanagedType.Interface)] out object item);
            [PreserveSig] int SetMediaItem([MarshalAs(UnmanagedType.Interface)] object item);
            [PreserveSig] int ClearMediaItem();
            [PreserveSig] int GetMediaItem([MarshalAs(UnmanagedType.Interface)] out object item);
            [PreserveSig] int GetVolume(out float volume);
            [PreserveSig] int SetVolume(float volume);
            [PreserveSig] int GetBalance(out float balance);
            [PreserveSig] int SetBalance(float balance);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute);
            [PreserveSig] int GetNativeVideoSize(out SIZE video, out SIZE aspectRatio);
            [PreserveSig] int GetIdealVideoSize(out SIZE minimum, out SIZE maximum);
            [PreserveSig] int SetVideoSourceRect(IntPtr sourceRect);
            [PreserveSig] int GetVideoSourceRect(IntPtr sourceRect);
            [PreserveSig] int SetAspectRatioMode(uint mode);
            [PreserveSig] int GetAspectRatioMode(out uint mode);
            [PreserveSig] int GetVideoWindow(out IntPtr window);
            [PreserveSig] int UpdateVideo();
            [PreserveSig] int SetBorderColor(uint color);
            [PreserveSig] int GetBorderColor(out uint color);
            [PreserveSig] int InsertEffect([MarshalAs(UnmanagedType.IUnknown)] object effect,
                [MarshalAs(UnmanagedType.Bool)] bool optional);
            [PreserveSig] int RemoveEffect([MarshalAs(UnmanagedType.IUnknown)] object effect);
            [PreserveSig] int RemoveAllEffects();
            [PreserveSig] int Shutdown();
        }

        [ComImport]
        [Guid("90EB3E6B-ECBF-45CC-B1DA-C6FE3EA70D57")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFPMediaItem
        {
            [PreserveSig] int GetMediaPlayer(out IntPtr player);
            [PreserveSig] int GetURL(out IntPtr url);
            [PreserveSig] int GetObject(out IntPtr source);
            [PreserveSig] int GetUserData(out IntPtr userData);
            [PreserveSig] int SetUserData(IntPtr userData);
            [PreserveSig] int GetStartStopPosition(out Guid startType, out PROPVARIANT startValue,
                out Guid stopType, out PROPVARIANT stopValue);
            [PreserveSig] int SetStartStopPosition(ref Guid startType, ref PROPVARIANT startValue,
                ref Guid stopType, ref PROPVARIANT stopValue);
            [PreserveSig] int HasVideo([MarshalAs(UnmanagedType.Bool)] out bool hasVideo,
                [MarshalAs(UnmanagedType.Bool)] out bool selected);
            [PreserveSig] int HasAudio([MarshalAs(UnmanagedType.Bool)] out bool hasAudio,
                [MarshalAs(UnmanagedType.Bool)] out bool selected);
            [PreserveSig] int IsProtected([MarshalAs(UnmanagedType.Bool)] out bool isProtected);
            [PreserveSig] int GetDuration(ref Guid positionType, out PROPVARIANT duration);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [DllImport("mfplay.dll", CharSet = CharSet.Unicode, PreserveSig = true,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int MFPCreateMediaPlayer(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
            uint creationOptions,
            [MarshalAs(UnmanagedType.Interface)] IMFPMediaPlayerCallback callback,
            IntPtr window,
            out IMFPMediaPlayer player);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();
    }
}
