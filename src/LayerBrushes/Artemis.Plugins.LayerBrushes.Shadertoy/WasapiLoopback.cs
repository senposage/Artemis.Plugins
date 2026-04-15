using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Minimal WASAPI loopback capture via raw COM P/Invoke — zero external dependencies.
/// Captures the system's default render output as mono float32 samples.
///
/// VoiceMeeter workaround: when the default render endpoint is a VoiceMeeter virtual
/// device, WASAPI loopback never delivers frames (VoiceMeeter's WASAPI implementation
/// does not support it).  We instead enumerate capture endpoints and capture from
/// VoiceMeeter's virtual output device ("VoiceMeeter Output" / "VAIO Out"), which is
/// a normal capture device and does not require the LOOPBACK flag.
/// </summary>
internal sealed unsafe class WasapiLoopback : IDisposable
{
    // Invoked on the capture thread: (monoSamples, count)
    public Action<float[], int>? DataAvailable;

    private Thread?               _thread;
    private volatile bool         _stopping;
    private IAudioClient?         _audioClient;
    private IAudioCaptureClient?  _captureClient;
    private int                   _channels;
    private bool                  _isFloat;
    private int                   _bitsPerSample;

    private const uint LOOPBACK            = 0x00020000u; // AUDCLNT_STREAMFLAGS_LOOPBACK
    private const uint DEVICE_STATE_ACTIVE = 1u;
    private const uint STGM_READ           = 0u;
    private const ushort VT_LPWSTR         = 31;

    // ------------------------------------------------------------------ public

    public void Start()
    {
        if (_thread != null) return;

        var enumClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var enumIid   = typeof(IMMDeviceEnumerator).GUID;
        int hr = CoCreateInstance(ref enumClsid, null, 1 /*INPROC_SERVER*/, ref enumIid, out object enumObj);
        if (hr < 0) throw new COMException("CoCreateInstance(IMMDeviceEnumerator)", hr);
        var enumerator = (IMMDeviceEnumerator)enumObj;

        hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out IMMDevice device);
        if (hr < 0) throw new COMException("GetDefaultAudioEndpoint", hr);

        // Prefer a dedicated capture endpoint over WASAPI loopback to avoid
        // conflicts when Artemis's own audio plugin already holds a loopback client.
        // Priority: Stereo Mix / What U Hear > VoiceMeeter Output > LOOPBACK fallback.
        uint streamFlags = LOOPBACK;
        try
        {
            string renderName = GetFriendlyName(device);
            ShaderLogger.Log($"WasapiLoopback: default render endpoint = '{renderName}'");

            // 1. Try Stereo Mix / What U Hear — available on many Realtek / IDT drivers.
            IMMDevice? stereoMix = FindStereoMixDevice(enumerator);
            if (stereoMix != null)
            {
                string n = GetFriendlyName(stereoMix);
                ShaderLogger.Log($"WasapiLoopback: using Stereo Mix capture device '{n}'");
                device      = stereoMix;
                streamFlags = 0;
            }
            // 2. VoiceMeeter virtual output.
            else if (renderName.IndexOf("VoiceMeeter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                IMMDevice? vmCapture = FindVoiceMeeterCaptureDevice(enumerator);
                if (vmCapture != null)
                {
                    string n = GetFriendlyName(vmCapture);
                    ShaderLogger.Log($"WasapiLoopback: VoiceMeeter detected → using capture device '{n}'");
                    device      = vmCapture;
                    streamFlags = 0;
                }
                else
                    ShaderLogger.Log("WasapiLoopback: VoiceMeeter detected but no capture device found — falling back to loopback");
            }
            else
            {
                ShaderLogger.Log("WasapiLoopback: no Stereo Mix found — using loopback on render endpoint");
            }
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"WasapiLoopback: device selection failed ({ex.Message}) — using loopback");
        }

        var acGuid = typeof(IAudioClient).GUID;
        hr = device.Activate(ref acGuid, 1, IntPtr.Zero, out object acObj);
        if (hr < 0) throw new COMException("IMMDevice.Activate(IAudioClient)", hr);
        _audioClient = (IAudioClient)acObj;

        hr = _audioClient.GetMixFormat(out IntPtr fmtPtr);
        if (hr < 0) throw new COMException("GetMixFormat", hr);

        try
        {
            var fmt = (WAVEFORMATEX*)fmtPtr;
            _channels      = fmt->nChannels;
            _bitsPerSample = fmt->wBitsPerSample;
            if (fmt->wFormatTag == 3) // WAVE_FORMAT_IEEE_FLOAT
                _isFloat = true;
            else if (fmt->wFormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
                _isFloat = ((WAVEFORMATEXTENSIBLE*)fmt)->SubFormat == IeeeFloat;

            // For loopback streams, hnsBufferDuration must be 0 (engine chooses size).
            // For capture streams, use a 200 ms buffer.
            long bufDuration = (streamFlags & LOOPBACK) != 0 ? 0L : 2_000_000L;
            hr = _audioClient.Initialize(0, streamFlags, bufDuration, 0, fmtPtr, IntPtr.Zero);
            if (hr < 0) throw new COMException($"IAudioClient.Initialize (flags=0x{streamFlags:X})", hr);
        }
        finally { CoTaskMemFree(fmtPtr); }

        var ccGuid = typeof(IAudioCaptureClient).GUID;
        hr = _audioClient.GetService(ref ccGuid, out object ccObj);
        if (hr < 0) throw new COMException("GetService(IAudioCaptureClient)", hr);
        _captureClient = (IAudioCaptureClient)ccObj;

        hr = _audioClient.Start();
        if (hr < 0) throw new COMException("IAudioClient.Start", hr);

        _stopping = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WASAPI-Loopback" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(500);
        _thread = null;
        try { _audioClient?.Stop(); } catch { }
        if (_captureClient != null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_audioClient   != null) { Marshal.ReleaseComObject(_audioClient);   _audioClient   = null; }
    }

    // ------------------------------------------------------------------ capture loop

    private readonly float[] _scratch = new float[48000 * 2]; // up to ~1 s stereo at 48 kHz

    private void CaptureLoop()
    {
        int pollCount = 0;
        int errorCount = 0;

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
                        if (++pollCount == 200) // log once after ~2 s of empty polls
                            ShaderLogger.Log("CaptureLoop: 200 empty polls — no audio frames delivered");
                        break;
                    }

                    int bufHr = _captureClient.GetBuffer(
                        out IntPtr data, out uint frames,
                        out uint flags, out _, out _);
                    if (bufHr < 0)
                    {
                        if (errorCount++ < 5)
                            ShaderLogger.Log($"CaptureLoop: GetBuffer hr=0x{bufHr:X8}");
                        break;
                    }

                    bool silent = (flags & 2u) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    if (frames > 0)
                    {
                        if (silent)
                            DeliverSilence(frames);
                        else
                            Deliver(data, frames);
                    }

                    _captureClient.ReleaseBuffer(frames);
                }
            }
            catch (Exception ex)
            {
                if (errorCount++ < 5)
                    ShaderLogger.Log($"CaptureLoop: exception: {ex.Message}");
            }
        }
        ShaderLogger.Log($"CaptureLoop: exited (polls={pollCount}, errors={errorCount})");
    }

    private void DeliverSilence(uint frames)
    {
        // Feed zero-amplitude samples — waveform mapping: (0×0.5+0.5)×255 = 127 (midpoint)
        int count = (int)frames;
        if (count > _scratch.Length) return;
        Array.Clear(_scratch, 0, count);
        DataAvailable?.Invoke(_scratch, count);
    }

    private void Deliver(IntPtr data, uint frames)
    {
        int total = (int)frames * _channels;
        if (total > _scratch.Length) return;

        int monoCount = (int)frames;
        if (_isFloat && _bitsPerSample == 32)
        {
            var src = (float*)data;
            for (int i = 0; i < monoCount; i++)
            {
                float s = 0f;
                for (int ch = 0; ch < _channels; ch++) s += src[i * _channels + ch];
                _scratch[i] = s / _channels;
            }
        }
        else if (!_isFloat && _bitsPerSample == 16)
        {
            var src = (short*)data;
            for (int i = 0; i < monoCount; i++)
            {
                float s = 0f;
                for (int ch = 0; ch < _channels; ch++) s += src[i * _channels + ch] / 32768f;
                _scratch[i] = s / _channels;
            }
        }
        else return;

        DataAvailable?.Invoke(_scratch, monoCount);
    }

    // ------------------------------------------------------------------ VoiceMeeter helpers

    private static string GetFriendlyName(IMMDevice device)
    {
        int hr = device.OpenPropertyStore(STGM_READ, out IPropertyStore store);
        if (hr < 0) return string.Empty;
        try
        {
            var key = new PROPERTYKEY
            {
                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), // PKEY_Device_FriendlyName
                pid   = 14
            };
            hr = store.GetValue(ref key, out PROPVARIANT pv);
            if (hr < 0 || pv.vt != VT_LPWSTR) return string.Empty;
            try   { return Marshal.PtrToStringUni(pv.p) ?? string.Empty; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>
    /// Looks for a "Stereo Mix" / "What U Hear" capture device (available on many
    /// Realtek / IDT drivers).  These devices capture the final mixed render output
    /// without needing the LOOPBACK flag, so they don't conflict with other loopback
    /// clients that may already be active on the render endpoint.
    /// Note: Stereo Mix is often disabled by default — the user may need to enable it
    /// in Sound settings → Recording tab → right-click → Show Disabled Devices.
    /// </summary>
    private static IMMDevice? FindStereoMixDevice(IMMDeviceEnumerator enumerator)
    {
        // Include disabled devices so we can tell the user to enable Stereo Mix.
        const uint ALL_STATES = 0x0Fu;
        int hr = enumerator.EnumAudioEndpoints(1 /*eCapture*/, ALL_STATES,
                                               out IMMDeviceCollection col);
        if (hr < 0) return null;

        IMMDevice? result = null;
        try
        {
            hr = col.GetCount(out uint count);
            if (hr < 0) return null;

            for (uint i = 0; i < count; i++)
            {
                hr = col.Item(i, out IMMDevice dev);
                if (hr < 0) continue;

                bool keep = false;
                try
                {
                    dev.GetState(out uint state);
                    string name = GetFriendlyName(dev);

                    if (state != DEVICE_STATE_ACTIVE)
                    {
                        if (IsWhatUHear(name))
                            ShaderLogger.Log($"WasapiLoopback: '{name}' exists but is disabled — enable it in Sound settings for audio capture");
                    }
                    else if (IsWhatUHear(name))
                    {
                        result = dev;
                        keep   = true;
                    }
                }
                catch { /* skip unreadable devices */ }
                finally { if (!keep) Marshal.ReleaseComObject(dev); }

                if (keep) break;
            }
        }
        finally { Marshal.ReleaseComObject(col); }

        return result;
    }

    private static bool IsWhatUHear(string name) =>
        name.IndexOf("Stereo Mix",  StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("What U Hear", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Wave Out",    StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf("Mix",         StringComparison.OrdinalIgnoreCase) >= 0 && // avoid false positives
        name.IndexOf("Realtek",     StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Enumerates active capture endpoints and returns the first one whose friendly
    /// name contains "VoiceMeeter" and "Out" — i.e. VoiceMeeter's virtual output.
    /// Returns null if no such device is found.
    /// </summary>
    private static IMMDevice? FindVoiceMeeterCaptureDevice(IMMDeviceEnumerator enumerator)
    {
        int hr = enumerator.EnumAudioEndpoints(1 /*eCapture*/, DEVICE_STATE_ACTIVE,
                                               out IMMDeviceCollection col);
        if (hr < 0) return null;

        IMMDevice? result = null;
        try
        {
            hr = col.GetCount(out uint count);
            if (hr < 0) return null;

            for (uint i = 0; i < count; i++)
            {
                hr = col.Item(i, out IMMDevice dev);
                if (hr < 0) continue;

                bool keep = false;
                try
                {
                    string name = GetFriendlyName(dev);
                    // Match "VoiceMeeter Output", "VoiceMeeter VAIO Out", etc.
                    if (name.IndexOf("VoiceMeeter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf("Out", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result = dev;
                        keep   = true;
                        break;
                    }
                }
                catch { /* skip unreadable devices */ }

                if (!keep) Marshal.ReleaseComObject(dev);
            }
        }
        finally { Marshal.ReleaseComObject(col); }

        return result;
    }

    // ------------------------------------------------------------------ COM P/Invoke

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    // ------------------------------------------------------------------ structs / interfaces

    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint   nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort       Samples;
        public uint         dwChannelMask;
        public Guid         SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // PROPVARIANT: always 16 bytes on both x86 and x64.
    // vt (type tag) at offset 0; union data at offset 8 (pointer for VT_LPWSTR).
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p; // e.g. LPWSTR for VT_LPWSTR
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
}
