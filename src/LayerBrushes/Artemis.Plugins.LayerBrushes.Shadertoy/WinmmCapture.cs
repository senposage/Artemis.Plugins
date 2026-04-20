#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// WinMM (waveIn) capture backend. Used only when the user explicitly selects
/// Stereo Mix / WinMM mode.
///
/// This backend must bind a "Stereo Mix" / "What U Hear" recording device.
/// If none is found, capture fails closed instead of opening the default microphone
/// or any other default recording device.
/// </summary>
internal sealed unsafe class WinmmCapture : IDisposable
{
    public int SampleRate => SAMPLE_RATE;

    // Invoked on the capture thread with left/right float32 samples.
    public Action<float[], float[], int>? DataAvailable;

    private IntPtr   _hWaveIn = IntPtr.Zero;
    private Thread?  _thread;
    private volatile bool _stopping;

    // Two ping-pong buffers (each ~50 ms of stereo 16-bit PCM at 44100 Hz)
    private const int CHANNELS     = 2;
    private const int SAMPLE_RATE  = 44100;
    private const int BITS         = 16;
    private const int BUF_MS       = 50;
    private static readonly int BUF_FRAMES = SAMPLE_RATE * BUF_MS / 1000;              // 2205 frames
    private static readonly int BUF_BYTES  = BUF_FRAMES * CHANNELS * (BITS / 8);       // 8820 bytes

    private const int WHDR_DONE    = 0x00000001;
    private const int WHDR_INQUEUE = 0x00000010;
    private const int CALLBACK_NULL = 0;

    // WinMM WAVEHDR on x64: 48 bytes.
    private const int WAVEHDR_SIZE = 48;

    private readonly IntPtr[] _hdrs  = new IntPtr[2];
    private readonly IntPtr[] _datas = new IntPtr[2];

    // Conversion scratch: up to BUF_FRAMES stereo float samples.
    private readonly float[] _left = new float[BUF_FRAMES];
    private readonly float[] _right = new float[BUF_FRAMES];

    // ------------------------------------------------------------------ public

    public void Start()
    {
        if (_hWaveIn != IntPtr.Zero) return;

        uint deviceId = FindStereoMixDevice();
        ShaderLogger.Log($"WinmmCapture: opening Stereo Mix device #{deviceId} '{GetDeviceName(deviceId)}'");

        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,                          // PCM
            nChannels       = CHANNELS,
            nSamplesPerSec  = (uint)SAMPLE_RATE,
            wBitsPerSample  = BITS,
            nBlockAlign     = (ushort)(CHANNELS * BITS / 8),
            nAvgBytesPerSec = (uint)(SAMPLE_RATE * CHANNELS * BITS / 8),
            cbSize          = 0
        };

        int hr = waveInOpen(out _hWaveIn, deviceId, ref wfx,
                            IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (hr != 0) throw new Exception($"waveInOpen failed: MMRESULT=0x{hr:X}");

        // Allocate fixed-address buffers so the driver can write to them.
        for (int i = 0; i < 2; i++)
        {
            _datas[i] = Marshal.AllocHGlobal(BUF_BYTES);
            _hdrs[i]  = Marshal.AllocHGlobal(WAVEHDR_SIZE);
            PrepareAndQueue(i);
        }

        hr = waveInStart(_hWaveIn);
        if (hr != 0) throw new Exception($"waveInStart failed: MMRESULT=0x{hr:X}");

        _stopping = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WinMM-Capture" };
        _thread.Start();

        ShaderLogger.Log("WinmmCapture: started");
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(500);
        _thread = null;

        if (_hWaveIn != IntPtr.Zero)
        {
            waveInStop(_hWaveIn);
            waveInReset(_hWaveIn);  // un-queues all buffers

            for (int i = 0; i < 2; i++)
            {
                if (_hdrs[i]  != IntPtr.Zero) { waveInUnprepareHeader(_hWaveIn, _hdrs[i],  WAVEHDR_SIZE); Marshal.FreeHGlobal(_hdrs[i]);  _hdrs[i]  = IntPtr.Zero; }
                if (_datas[i] != IntPtr.Zero) {                                                            Marshal.FreeHGlobal(_datas[i]); _datas[i] = IntPtr.Zero; }
            }

            waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }

        ShaderLogger.Log("WinmmCapture: stopped");
    }

    // ------------------------------------------------------------------ capture loop

    private void CaptureLoop()
    {
        int emptyPolls = 0;
        while (!_stopping)
        {
            Thread.Sleep(5);
            bool anyDone = false;

            for (int i = 0; i < 2; i++)
            {
                if (_hdrs[i] == IntPtr.Zero) continue;
                int flags = Marshal.ReadInt32(_hdrs[i] + 24); // dwFlags at offset 24
                if ((flags & WHDR_DONE) == 0) continue;

                anyDone = true;
                int recorded = Marshal.ReadInt32(_hdrs[i] + 12); // dwBytesRecorded at offset 12

                if (recorded > 0)
                    Deliver(_datas[i], recorded);

                // Re-queue the buffer.
                PrepareAndQueue(i);
            }

            if (!anyDone)
            {
                emptyPolls++;
                if (emptyPolls == 400) // ~2 s
                    ShaderLogger.Log("WinmmCapture: 400 empty polls — no audio frames delivered");
            }
            else
            {
                emptyPolls = 0;
            }
        }
    }

    private void PrepareAndQueue(int i)
    {
        IntPtr hdr  = _hdrs[i];
        IntPtr data = _datas[i];

        // Unprepare before re-using a completed buffer (required by WinMM).
        waveInUnprepareHeader(_hWaveIn, hdr, WAVEHDR_SIZE);

        // Zero the header then fill required fields.
        for (int b = 0; b < WAVEHDR_SIZE; b++) Marshal.WriteByte(hdr, b, 0);
        Marshal.WriteIntPtr(hdr, 0,  data);          // lpData
        Marshal.WriteInt32 (hdr, 8,  BUF_BYTES);     // dwBufferLength

        waveInPrepareHeader(_hWaveIn, hdr, WAVEHDR_SIZE);
        waveInAddBuffer    (_hWaveIn, hdr, WAVEHDR_SIZE);
    }

    private void Deliver(IntPtr data, int byteCount)
    {
        // Stereo 16-bit PCM to left/right float32.
        int frames = byteCount / (CHANNELS * (BITS / 8));
        if (frames > _left.Length) frames = _left.Length;

        var src = (short*)data;
        for (int i = 0; i < frames; i++)
        {
            _left[i] = src[i * CHANNELS] / 32768f;
            _right[i] = src[i * CHANNELS + 1] / 32768f;
        }

        DataAvailable?.Invoke(_left, _right, frames);
    }

    // ------------------------------------------------------------------ device selection

    private static uint FindStereoMixDevice()
    {
        uint n = waveInGetNumDevs();
        ShaderLogger.Log($"WinmmCapture: {n} recording device(s) found");

        for (uint id = 0; id < n; id++)
        {
            string name = GetDeviceName(id);
            ShaderLogger.Log($"WinmmCapture: device #{id} = '{name}'");

            if (name.IndexOf("Stereo Mix",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("What U Hear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Wave Out",    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ShaderLogger.Log($"WinmmCapture: selected device #{id} '{name}'");
                return id;
            }
        }

        throw new InvalidOperationException("Stereo Mix / WinMM capture was selected, but no Stereo Mix / What U Hear device was found");
    }

    private static string GetDeviceName(uint id)
    {
        // WAVEINCAPSA: wMid(2) + wPid(2) + vDriverVersion(4) + szPname(32) + dwFormats(4) + wChannels(2) + wReserved1(2) = 48 bytes
        IntPtr caps = Marshal.AllocHGlobal(48);
        try
        {
            waveInGetDevCapsA(id, caps, 48u);
            return Marshal.PtrToStringAnsi(caps + 8) ?? string.Empty; // szPname at offset 8
        }
        finally { Marshal.FreeHGlobal(caps); }
    }

    // ------------------------------------------------------------------ WAVEHDR layout helpers
    // On x64: lpData(8) + dwBufferLength(4) + dwBytesRecorded(4) + dwUser(8) + dwFlags(4) + ...
    // Offsets:  0            8                  12                  16           24

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint   nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [DllImport("winmm.dll")] static extern int    waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX lpwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
    [DllImport("winmm.dll")] static extern int    waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int    waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int    waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int    waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int    waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int    waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] static extern int    waveInClose(IntPtr hwi);
    [DllImport("winmm.dll")] static extern uint   waveInGetNumDevs();
    [DllImport("winmm.dll")] static extern int    waveInGetDevCapsA(uint uDeviceID, IntPtr pwic, uint cbwic);
}
#endif
