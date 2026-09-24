#if WINDOWS
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Minimal WASAPI loopback capture via raw COM P/Invoke.
/// Captures the selected render output as left/right float samples.
///
/// Endpoint channel volume is applied manually because some drivers expose loopback
/// samples before the Windows per-channel balance scalar is mixed in.
/// </summary>
internal sealed unsafe class WasapiLoopback : IDisposable
{
    public Action<float[], float[], int>? DataAvailable;
    public int SampleRate => _sampleRate;

    private readonly string? _deviceId;
    private Thread? _thread;
    private volatile bool _stopping;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IAudioEndpointVolume? _endpointVolume;
    private Exception? _startupException;
    private int _channels;
    private uint _channelMask;
    private bool _isFloat;
    private int _bitsPerSample;
    private int _bytesPerSample;
    private int _sampleRate;
    private int _blockAlign;
    private readonly float[] _channelVolumes = new float[32];
    private long _lastVolumeRefreshTick;

    private const uint LOOPBACK = 0x00020000u;
    private const uint DEVICE_STATE_ACTIVE = 1u;
    private const uint STGM_READ = 0u;
    private const ushort VT_LPWSTR = 31;
    private const int COINIT_MULTITHREADED = 0x0;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private const uint SPEAKER_FRONT_LEFT = 0x1;
    private const uint SPEAKER_FRONT_RIGHT = 0x2;
    private const uint SPEAKER_FRONT_CENTER = 0x4;
    private const uint SPEAKER_LOW_FREQUENCY = 0x8;
    private const uint SPEAKER_BACK_LEFT = 0x10;
    private const uint SPEAKER_BACK_RIGHT = 0x20;
    private const uint SPEAKER_FRONT_LEFT_OF_CENTER = 0x40;
    private const uint SPEAKER_FRONT_RIGHT_OF_CENTER = 0x80;
    private const uint SPEAKER_BACK_CENTER = 0x100;
    private const uint SPEAKER_SIDE_LEFT = 0x200;
    private const uint SPEAKER_SIDE_RIGHT = 0x400;

    public WasapiLoopback(string? deviceId = null)
    {
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public void Start()
    {
        if (_thread != null) return;

        _stopping = false;
        _startupException = null;
        using var started = new ManualResetEventSlim(false);
        _thread = new Thread(() => CaptureLoop(started))
        {
            IsBackground = true,
            Name = "WASAPI-Loopback"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("WASAPI capture thread did not finish initialization");

        if (_startupException == null)
            return;

        _thread.Join(500);
        _thread = null;
        throw new InvalidOperationException("WASAPI capture failed to initialize", _startupException);
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(500);
        _thread = null;
    }

    private readonly float[] _left = new float[48000 * 8];
    private readonly float[] _right = new float[48000 * 8];

    private void CaptureLoop(ManualResetEventSlim started)
    {
        int pollCount = 0;
        int errorCount = 0;
        bool comInitialized = false;

        try
        {
            int initHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (initHr < 0 && initHr != RPC_E_CHANGED_MODE)
                throw new COMException("CoInitializeEx", initHr);
            comInitialized = initHr >= 0;

            InitializeCapture();
            started.Set();
        }
        catch (Exception ex)
        {
            _startupException = ex;
            started.Set();
            ShaderLogger.Log($"WasapiLoopback: startup failed: {ex}");
            TeardownCapture();
            if (comInitialized)
                CoUninitialize();
            return;
        }

        try
        {
            while (!_stopping)
            {
                Thread.Sleep(10);
                if (_captureClient == null) break;

                try
                {
                    while (true)
                    {
                        int packetHr = _captureClient.GetNextPacketSize(out uint packet);
                        if (packetHr < 0)
                        {
                            if (errorCount++ < 5)
                                ShaderLogger.Log($"CaptureLoop: GetNextPacketSize hr=0x{packetHr:X8}");
                            break;
                        }

                        if (packet == 0)
                        {
                            if (++pollCount == 200)
                                ShaderLogger.Log("CaptureLoop: 200 empty polls - no audio frames delivered");
                            break;
                        }

                        int bufHr = _captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        if (bufHr < 0)
                        {
                            if (errorCount++ < 5)
                                ShaderLogger.Log($"CaptureLoop: GetBuffer hr=0x{bufHr:X8}");
                            break;
                        }

                        try
                        {
                            bool silent = (flags & 2u) != 0;
                            if (frames > 0)
                            {
                                if (silent)
                                    DeliverSilence(frames);
                                else
                                    Deliver(data, frames);
                            }
                        }
                        finally
                        {
                            _captureClient.ReleaseBuffer(frames);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (errorCount++ < 5)
                        ShaderLogger.Log($"CaptureLoop: exception: {ex.Message}");
                }
            }
        }
        finally
        {
            ShaderLogger.Log($"CaptureLoop: exited (polls={pollCount}, errors={errorCount})");
            TeardownCapture();
            if (comInitialized)
                CoUninitialize();
        }
    }

    private void InitializeCapture()
    {
        var enumClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var enumIid = typeof(IMMDeviceEnumerator).GUID;
        int hr = CoCreateInstance(ref enumClsid, null, 1, ref enumIid, out object enumObj);
        if (hr < 0) throw new COMException("CoCreateInstance(IMMDeviceEnumerator)", hr);

        var enumerator = (IMMDeviceEnumerator)enumObj;
        IMMDevice? device = null;

        try
        {
            if (_deviceId != null)
            {
                hr = enumerator.GetDevice(_deviceId, out device);
                if (hr < 0) throw new COMException("GetDevice(selected render endpoint)", hr);
            }
            else
            {
                // Match the working NAudio plugin: bind the multimedia render default,
                // not the console role, so we listen to the same active playback target.
                hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (hr < 0) throw new COMException("GetDefaultAudioEndpoint", hr);
            }

            uint streamFlags = LOOPBACK;
            string renderName = GetFriendlyName(device);
            ShaderLogger.Log($"WasapiLoopback: render endpoint = '{renderName}', explicitDevice={_deviceId != null}, id='{_deviceId ?? "<default>"}'");

            var acGuid = typeof(IAudioClient).GUID;
            hr = device.Activate(ref acGuid, 1, IntPtr.Zero, out object acObj);
            if (hr < 0) throw new COMException("IMMDevice.Activate(IAudioClient)", hr);
            _audioClient = (IAudioClient)acObj;

            var evGuid = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref evGuid, 1, IntPtr.Zero, out object evObj);
            if (hr >= 0)
            {
                _endpointVolume = (IAudioEndpointVolume)evObj;
                RefreshChannelVolumes(force: true);
            }
            else
            {
                ShaderLogger.Log($"WasapiLoopback: endpoint volume unavailable hr=0x{hr:X8}; Windows balance will not be applied manually");
                FillChannelVolumes(1f);
            }

            hr = _audioClient.GetMixFormat(out IntPtr fmtPtr);
            if (hr < 0) throw new COMException("GetMixFormat", hr);

            try
            {
                var fmt = (WAVEFORMATEX*)fmtPtr;
                _channels = Math.Max(1, (int)fmt->nChannels);
                _channelMask = 0;
                _sampleRate = (int)fmt->nSamplesPerSec;
                _blockAlign = fmt->nBlockAlign;
                _bitsPerSample = fmt->wBitsPerSample;
                _bytesPerSample = Math.Max(1, _bitsPerSample / 8);
                _isFloat = false;
                ushort formatTag = fmt->wFormatTag;
                Guid subFormat = Guid.Empty;

                if (fmt->wFormatTag == 3)
                    _isFloat = true;
                else if (fmt->wFormatTag == 0xFFFE)
                {
                    var extensible = (WAVEFORMATEXTENSIBLE*)fmt;
                    _channelMask = extensible->dwChannelMask;
                    subFormat = extensible->SubFormat;
                    _isFloat = subFormat == IeeeFloat;
                    if (!_isFloat && subFormat != Pcm)
                        ShaderLogger.Log($"WasapiLoopback: extensible subformat {subFormat} will be treated as PCM");
                }

                ShaderLogger.Log($"WasapiLoopback: mix format tag=0x{formatTag:X4}, sub={subFormat}, rate={_sampleRate}, channels={_channels}, mask=0x{_channelMask:X}, bits={_bitsPerSample}, bytesPerSample={_bytesPerSample}, blockAlign={_blockAlign}, float={_isFloat}");

                long bufDuration = (streamFlags & LOOPBACK) != 0 ? 0L : 2_000_000L;
                hr = _audioClient.Initialize(0, streamFlags, bufDuration, 0, fmtPtr, IntPtr.Zero);
                if (hr < 0) throw new COMException($"IAudioClient.Initialize (flags=0x{streamFlags:X})", hr);
            }
            finally
            {
                CoTaskMemFree(fmtPtr);
            }

            var ccGuid = typeof(IAudioCaptureClient).GUID;
            hr = _audioClient.GetService(ref ccGuid, out object ccObj);
            if (hr < 0) throw new COMException("GetService(IAudioCaptureClient)", hr);
            _captureClient = (IAudioCaptureClient)ccObj;

            hr = _audioClient.Start();
            if (hr < 0) throw new COMException("IAudioClient.Start", hr);
        }
        finally
        {
            if (device != null)
                Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private void TeardownCapture()
    {
        try { _audioClient?.Stop(); } catch { }
        if (_captureClient != null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_audioClient != null) { Marshal.ReleaseComObject(_audioClient); _audioClient = null; }
        if (_endpointVolume != null) { Marshal.ReleaseComObject(_endpointVolume); _endpointVolume = null; }
    }

    private void DeliverSilence(uint frames)
    {
        int count = (int)frames;
        if (count > _left.Length) return;
        Array.Clear(_left, 0, count);
        Array.Clear(_right, 0, count);
        DataAvailable?.Invoke(_left, _right, count);
    }

    private void Deliver(IntPtr data, uint frames)
    {
        int total = (int)frames * _channels;
        if (total > _left.Length * Math.Max(1, _channels)) return;

        int frameCount = (int)frames;
        if (!IsSupportedFormat())
        {
            ShaderLogger.Log($"WasapiLoopback: unsupported mix format bits={_bitsPerSample} channels={_channels} float={_isFloat} bytes={_bytesPerSample}");
            return;
        }

        RefreshChannelVolumes(force: false);

        for (int i = 0; i < frameCount; i++)
            DownmixFrame(data, i, out _left[i], out _right[i]);

        DataAvailable?.Invoke(_left, _right, frameCount);
    }

    private bool IsSupportedFormat()
    {
        return (_isFloat && _bitsPerSample == 32) ||
               (!_isFloat && (_bitsPerSample == 16 || _bitsPerSample == 24 || _bitsPerSample == 32));
    }

    private void DownmixFrame(IntPtr data, int frame, out float left, out float right)
    {
        if (_channels == 1)
        {
            float mono = ReadSample(data, frame, 0) * GetChannelVolume(0);
            left = mono;
            right = mono;
            return;
        }

        if (_channelMask != 0)
        {
            DownmixFrameByMask(data, frame, out left, out right);
            return;
        }

        DownmixFrameByIndex(data, frame, out left, out right);
    }

    private void DownmixFrameByIndex(IntPtr data, int frame, out float left, out float right)
    {
        float Channel(int c) => ReadSample(data, frame, c) * GetChannelVolume(c);

        switch (_channels)
        {
            case 4:
                left = (Channel(0) + Channel(2)) * 0.5f;
                right = (Channel(1) + Channel(3)) * 0.5f;
                break;
            case 6:
                left = (Channel(0) + Channel(2) + Channel(3) + Channel(4)) * 0.25f;
                right = (Channel(1) + Channel(2) + Channel(3) + Channel(5)) * 0.25f;
                break;
            case 8:
                left = (Channel(0) + Channel(2) + Channel(3) + Channel(4) + Channel(6)) * 0.2f;
                right = (Channel(1) + Channel(2) + Channel(3) + Channel(5) + Channel(7)) * 0.2f;
                break;
            default:
                left = Channel(0);
                right = Channel(1);
                break;
        }
    }

    private void DownmixFrameByMask(IntPtr data, int frame, out float left, out float right)
    {
        left = 0f;
        right = 0f;
        int leftCount = 0;
        int rightCount = 0;

        for (int c = 0; c < _channels; c++)
        {
            uint speaker = SpeakerForChannel(c);
            float sample = ReadSample(data, frame, c) * GetChannelVolume(c);

            if (IsLeftSpeaker(speaker))
            {
                left += sample;
                leftCount++;
            }
            else if (IsRightSpeaker(speaker))
            {
                right += sample;
                rightCount++;
            }
            else
            {
                left += sample;
                right += sample;
                leftCount++;
                rightCount++;
            }
        }

        if (leftCount > 0) left /= leftCount;
        if (rightCount > 0) right /= rightCount;
    }

    private float ReadSample(IntPtr data, int frame, int channel)
    {
        if (_isFloat)
        {
            var src = (float*)data;
            return src[frame * _channels + channel];
        }

        if (_bitsPerSample == 16)
        {
            var src = (short*)data;
            return src[frame * _channels + channel] / 32768f;
        }

        if (_bitsPerSample == 24)
        {
            byte* src = (byte*)data;
            return ReadPcm24(src, frame, channel) / 8388608f;
        }

        var src32 = (int*)data;
        return src32[frame * _channels + channel] / 2147483648f;
    }

    private uint SpeakerForChannel(int channel)
    {
        uint mask = _channelMask;
        for (int i = 0; i < channel; i++)
            mask &= mask - 1;
        return mask & ~(mask - 1);
    }

    private static bool IsLeftSpeaker(uint speaker) =>
        speaker is SPEAKER_FRONT_LEFT or SPEAKER_BACK_LEFT or SPEAKER_SIDE_LEFT or SPEAKER_FRONT_LEFT_OF_CENTER;

    private static bool IsRightSpeaker(uint speaker) =>
        speaker is SPEAKER_FRONT_RIGHT or SPEAKER_BACK_RIGHT or SPEAKER_SIDE_RIGHT or SPEAKER_FRONT_RIGHT_OF_CENTER;

    private void RefreshChannelVolumes(bool force)
    {
        if (_endpointVolume == null)
        {
            FillChannelVolumes(1f);
            return;
        }

        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastVolumeRefreshTick) < 100)
            return;

        Interlocked.Exchange(ref _lastVolumeRefreshTick, now);

        int hr = _endpointVolume.GetChannelCount(out uint count);
        if (hr < 0)
        {
            FillChannelVolumes(1f);
            return;
        }

        int usable = Math.Min(_channelVolumes.Length, (int)count);
        for (uint i = 0; i < usable; i++)
        {
            hr = _endpointVolume.GetChannelVolumeLevelScalar(i, out float scalar);
            _channelVolumes[i] = hr < 0 ? 1f : scalar;
        }

        for (int i = usable; i < _channelVolumes.Length; i++)
            _channelVolumes[i] = 1f;
    }

    private void FillChannelVolumes(float value)
    {
        for (int i = 0; i < _channelVolumes.Length; i++)
            _channelVolumes[i] = value;
    }

    private float GetChannelVolume(int channel) =>
        channel >= 0 && channel < _channelVolumes.Length ? _channelVolumes[channel] : 1f;

    private int ReadPcm24(byte* src, int frame, int channel)
    {
        int offset = (frame * _channels + channel) * _bytesPerSample;
        int sample = src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16);
        if ((sample & 0x800000) != 0)
            sample |= unchecked((int)0xFF000000);
        return sample;
    }

    public static AudioDeviceOption[] EnumerateRenderDevices()
    {
        int initHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        bool comInitialized = initHr >= 0;
        if (initHr < 0 && initHr != RPC_E_CHANGED_MODE)
        {
            ShaderLogger.Log($"WasapiLoopback.EnumerateRenderDevices: CoInitializeEx hr=0x{initHr:X8}");
            return [];
        }

        try
        {
            var enumClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var enumIid = typeof(IMMDeviceEnumerator).GUID;
            int hr = CoCreateInstance(ref enumClsid, null, 1, ref enumIid, out object enumObj);
            if (hr < 0)
            {
                ShaderLogger.Log($"WasapiLoopback.EnumerateRenderDevices: CoCreateInstance hr=0x{hr:X8}");
                return [];
            }

            var enumerator = (IMMDeviceEnumerator)enumObj;
            IMMDeviceCollection? collection = null;
            try
            {
                hr = enumerator.EnumAudioEndpoints(0, DEVICE_STATE_ACTIVE, out collection);
                if (hr < 0)
                {
                    ShaderLogger.Log($"WasapiLoopback.EnumerateRenderDevices: EnumAudioEndpoints hr=0x{hr:X8}");
                    return [];
                }

                hr = collection.GetCount(out uint count);
                if (hr < 0) return [];

                var devices = new AudioDeviceOption[count];
                for (uint i = 0; i < count; i++)
                {
                    hr = collection.Item(i, out IMMDevice device);
                    if (hr < 0)
                    {
                        devices[i] = new AudioDeviceOption("", $"Unreadable render device #{i}");
                        continue;
                    }

                    try
                    {
                        string name = GetFriendlyName(device);
                        device.GetId(out string id);
                        devices[i] = new AudioDeviceOption(id, string.IsNullOrWhiteSpace(name) ? id : name);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                ShaderLogger.Log($"WasapiLoopback.EnumerateRenderDevices: found {devices.Length} active render endpoint(s)");
                return devices.Where(d => !string.IsNullOrWhiteSpace(d.Id)).ToArray();
            }
            finally
            {
                if (collection != null)
                    Marshal.ReleaseComObject(collection);
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"WasapiLoopback.EnumerateRenderDevices: failed: {ex}");
            return [];
        }
        finally
        {
            if (comInitialized)
                CoUninitialize();
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        int hr = device.OpenPropertyStore(STGM_READ, out IPropertyStore store);
        if (hr < 0) return string.Empty;
        try
        {
            var key = new PROPERTYKEY
            {
                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                pid = 14
            };
            hr = store.GetValue(ref key, out PROPVARIANT pv);
            if (hr < 0 || pv.vt != VT_LPWSTR) return string.Empty;
            try { return Marshal.PtrToStringUni(pv.p) ?? string.Empty; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort Samples;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask,
            [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufDuration, long periodicity,
            IntPtr pFormat, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefault, out long phnsMinimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesAvailable,
            out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, IntPtr pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, IntPtr pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        [PreserveSig] int VolumeStepUp(IntPtr pguidEventContext);
        [PreserveSig] int VolumeStepDown(IntPtr pguidEventContext);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }
}
#endif
