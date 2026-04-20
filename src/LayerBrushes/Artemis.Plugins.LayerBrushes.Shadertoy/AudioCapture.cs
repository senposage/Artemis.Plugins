using System;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// WASAPI loopback capture (no external dependencies) feeding a 512×2 Shadertoy audio texture.
///
/// Layout (R channel per texel, GBA unused):
///   Row 0 — FFT spectrum  (512 bins, log-scaled dB)
///   Row 1 — PCM waveform  (512 most-recent samples, remapped [−1,1] → [0,255])
/// </summary>
internal static class AudioCapture
{
    private const int FFT_N  = 1024;       // FFT window — must be power-of-2
    private const int OUT_W  = 512;        // output texture width
    private const int RING   = FFT_N * 8;  // ring buffer length in mono float samples
    private const long SILENCE_TIMEOUT_MS = 250;
    private const long RESTART_TIMEOUT_MS = 2000;
    /// <summary>dB floor for FFT normalisation.  Bins below this level map to 0.</summary>
    public static float DbFloor = -50f;

    /// <summary>
    /// EMA alpha for the rising edge (0 = instant attack, 0.99 = very slow rise).
    /// Higher values suppress transient peaks and give a smoother build-up.
    /// </summary>
    public static float Attack = 0.7f;

    /// <summary>
    /// EMA alpha for the falling edge (0 = instant fall, 0.99 = very slow decay).
    /// Lower values mean bars snap back down quickly between beats.
    /// </summary>
    public static float Smoothing = 0.5f;

    // Reference level: N/4 is the theoretical peak for a pure full-scale tone, but real music
    // spreads energy across many bins — typical peak bin magnitudes land around N/32.
    // Using N/32 as the ceiling so the display covers actual content rather than being
    // compressed into the bottom quarter of the scale.
    private static readonly float _dbRef = 20f * MathF.Log10(FFT_N / 32f);

    /// <summary>Lowest frequency shown in the FFT row (Hz).  Default 20 Hz.</summary>
    public static int MinFrequency = 20;
    /// <summary>Highest frequency shown in the FFT row (Hz).  Default 20 000 Hz.</summary>
    public static int MaxFrequency = 20_000;

    /// <summary>
    /// How FFT bins are mapped to the 512 output pixels.
    /// Max/Average use logarithmic frequency spacing; Linear uses 1:1 bin mapping.
    /// </summary>
    public static SpectrumMode FrequencyScale = SpectrumMode.Max;
    public static AudioInputSource InputSource { get; set; } = AudioInputSource.RenderLoopback;
    public static string SelectedDeviceId { get; set; } = "";
    public static int SampleRate { get; private set; } = 44100;
    public static bool StereoTexture { get; set; }

    private static readonly float[] _fftSmoothedLeft = new float[OUT_W];
    private static readonly float[] _fftSmoothedRight = new float[OUT_W];
    private static readonly float[] _fftSmoothedMono = new float[OUT_W];

    private static IDisposable? _capture;
    private static string _captureBackend = "none";
    private static AudioInputSource _activeInputSource = AudioInputSource.RenderLoopback;
    private static string _activeDeviceId = "";
    private static readonly float[]   _ringLeft = new float[RING];
    private static readonly float[]   _ringRight = new float[RING];
    private static readonly float[]   _ringMono = new float[RING];
    private static volatile int        _writePos;

    private static readonly Complex[]  _fftBuf  = new Complex[FFT_N];
    private static readonly float[]    _mags    = new float[FFT_N / 2];
    private static readonly byte[]     _fftSnapLeft = new byte[OUT_W];
    private static readonly byte[]     _fftSnapRight = new byte[OUT_W];
    private static readonly byte[]     _fftSnapMono = new byte[OUT_W];
    // Pre-filled to 127 = silence midpoint (s=0 → (0×0.5+0.5)×255 = 127.5 → 127).
    // Shaders see a flat centred waveform before any audio data arrives.
    private static readonly byte[]     _waveSnapLeft = Enumerable.Repeat((byte)127, OUT_W).ToArray();
    private static readonly byte[]     _waveSnapRight = Enumerable.Repeat((byte)127, OUT_W).ToArray();
    private static readonly byte[]     _waveSnapMono = Enumerable.Repeat((byte)127, OUT_W).ToArray();
    private static readonly Lock       _lock     = new();
    private static readonly Lock       _lifecycleLock = new();
    private static long _lastDataTick = Environment.TickCount64;
    private static long _lastRestartTick = long.MinValue;
    private static bool _startPending;

    public static bool IsRunning { get; private set; }

    // ------------------------------------------------------------------ lifecycle

    public static void Start()
    {
        var requestedSource = InputSource;
        var requestedDeviceId = SelectedDeviceId;
        lock (_lifecycleLock)
        {
            if (IsRunning || _startPending) return;
            _startPending = true;
        }

        ShaderLogger.Log($"AudioCapture.Start: requested source={requestedSource}, device='{requestedDeviceId}', floor={DbFloor:F1}dB, gate={NoiseGateAmplitude():F6}");

        var t = new Thread(() =>
        {
            try
            {
                var (cap, backend, sampleRate) = CreateCaptureBackend(requestedSource, requestedDeviceId);
                lock (_lifecycleLock)
                {
                    _capture = cap;
                    _captureBackend = backend;
                    _activeInputSource = requestedSource;
                    _activeDeviceId = requestedDeviceId;
                    SampleRate = sampleRate;
                    IsRunning = true;
                    _lastDataTick = Environment.TickCount64;
                }
                ShaderLogger.Log($"AudioCapture: started {backend} capture for source={requestedSource}, sampleRate={sampleRate}");
            }
            catch (Exception ex)
            {
                ShaderLogger.Log($"AudioCapture: failed to start source={requestedSource}, device='{requestedDeviceId}': {ex}");
                _capture?.Dispose();
                _capture = null;
                _captureBackend = "none";
                _activeInputSource = requestedSource;
                _activeDeviceId = requestedDeviceId;
            }
            finally
            {
                lock (_lifecycleLock)
                    _startPending = false;
            }
        })
        {
            IsBackground = true,
            Name         = "AudioCapture-Init"
        };
        t.Start();
    }

    public static void Stop()
    {
        IDisposable? capture = null;
        string backend;
        lock (_lifecycleLock)
        {
            if (!IsRunning && !_startPending) return;
            capture = _capture;
            backend = _captureBackend;
            _capture = null;
            _captureBackend = "none";
            _activeInputSource = InputSource;
            _activeDeviceId = SelectedDeviceId;
            IsRunning = false;
            _startPending = false;
        }

        try { capture?.Dispose(); }
        catch { }
        ResetSnapshotToSilence();
        ShaderLogger.Log($"AudioCapture: {backend} stopped");
    }

    public static void EnsureActive(bool enabled)
    {
        if (!enabled) return;

        long now = Environment.TickCount64;
        long staleFor = now - Interlocked.Read(ref _lastDataTick);

        if (staleFor >= SILENCE_TIMEOUT_MS)
        {
            if (staleFor < SILENCE_TIMEOUT_MS + 50)
                ShaderLogger.Log($"AudioCapture.EnsureActive: stale {staleFor} ms, clearing texture state");
            ResetSnapshotToSilence();
        }

        if (!IsRunning)
        {
            Start();
            return;
        }

        if (_activeInputSource != InputSource)
        {
            ShaderLogger.Log($"AudioCapture: input source changed from {_activeInputSource} to {InputSource}, restarting capture");
            Stop();
            Start();
            return;
        }

        if (InputSource == AudioInputSource.RenderLoopback && _activeDeviceId != SelectedDeviceId)
        {
            ShaderLogger.Log($"AudioCapture: render device changed from '{_activeDeviceId}' to '{SelectedDeviceId}', restarting capture");
            Stop();
            Start();
            return;
        }

        if (staleFor < RESTART_TIMEOUT_MS)
            return;

        long lastRestart = Interlocked.Read(ref _lastRestartTick);
        if (now - lastRestart < RESTART_TIMEOUT_MS)
            return;

        Interlocked.Exchange(ref _lastRestartTick, now);
        ShaderLogger.Log($"AudioCapture: no frames for {staleFor} ms, restarting {_captureBackend} capture");
        Stop();
        Start();
    }

    private static (IDisposable Capture, string Backend, int SampleRate) CreateCaptureBackend(AudioInputSource source, string deviceId)
    {
#if WINDOWS
        if (source == AudioInputSource.StereoMix)
        {
            var stereoMix = new WinmmCapture { DataAvailable = OnData };
            stereoMix.Start();
            return (stereoMix, "StereoMix", stereoMix.SampleRate);
        }

        var loopback = new WasapiLoopback(deviceId) { DataAvailable = OnData };
        loopback.Start();
        return (loopback, "WASAPI", loopback.SampleRate);
#else
        var pipewire = new LinuxPipeWireAudioCapture { DataAvailable = OnData };
        pipewire.Start();
        return (pipewire, "PipeWire", pipewire.SampleRate);
#endif
    }

    // ------------------------------------------------------------------ texture fill

    /// <summary>
    /// Fills <paramref name="dest"/> with OUT_W×2 RGBA8 texture data (row-major).
    /// dest must be at least OUT_W * 2 * 4 bytes.
    /// </summary>
    public static void FillTexture(Span<byte> dest)
    {
        lock (_lock)
        {
            for (int x = 0; x < OUT_W; x++)
            {
                byte fftLeft = StereoTexture ? _fftSnapLeft[x] : _fftSnapMono[x];
                byte fftRight = StereoTexture ? _fftSnapRight[x] : (byte)0;
                byte waveLeft = StereoTexture ? _waveSnapLeft[x] : _waveSnapMono[x];
                byte waveRight = StereoTexture ? _waveSnapRight[x] : (byte)0;

                int b0 = x * 4;
                dest[b0]     = fftLeft;
                dest[b0 + 1] = fftRight;
                dest[b0 + 2] = _fftSnapMono[x];
                dest[b0 + 3] = 255;

                int b1 = (OUT_W + x) * 4;
                dest[b1]     = waveLeft;
                dest[b1 + 1] = waveRight;
                dest[b1 + 2] = _waveSnapMono[x];
                dest[b1 + 3] = 255;
            }
        }
    }

    // ------------------------------------------------------------------ capture callback

    private static void OnData(float[] left, float[] right, int count)
    {
        Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
        float peak = Math.Max(Peak(left, count), Peak(right, count));
        float gate = NoiseGateAmplitude();
        lock (_lock)
        {
            if (peak < gate)
            {
                ResetSnapshotToSilence_NoLock();
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int pos = _writePos++ & (RING - 1);
                float l = left[i];
                float r = right[i];
                _ringLeft[pos] = l;
                _ringRight[pos] = r;
                _ringMono[pos] = (l + r) * 0.5f;
            }
            RefreshSnapshot();
        }
    }

    private static float NoiseGateAmplitude()
    {
        float floorDb = Math.Clamp(DbFloor, -80f, -1f);
        return MathF.Pow(10f, floorDb / 20f);
    }

    private static void ResetSnapshotToSilence()
    {
        lock (_lock)
            ResetSnapshotToSilence_NoLock();
    }

    private static void ResetSnapshotToSilence_NoLock()
    {
        Array.Clear(_ringLeft);
        Array.Clear(_ringRight);
        Array.Clear(_ringMono);
        Array.Clear(_fftSmoothedLeft);
        Array.Clear(_fftSmoothedRight);
        Array.Clear(_fftSmoothedMono);
        Array.Clear(_fftSnapLeft);
        Array.Clear(_fftSnapRight);
        Array.Clear(_fftSnapMono);
        Array.Fill(_waveSnapLeft, (byte)127);
        Array.Fill(_waveSnapRight, (byte)127);
        Array.Fill(_waveSnapMono, (byte)127);
        _writePos = 0;
    }

    private static float Peak(float[] buf, int count)
    {
        float p = 0f;
        for (int i = 0; i < count; i++) { float a = MathF.Abs(buf[i]); if (a > p) p = a; }
        return p;
    }

    // ------------------------------------------------------------------ snapshot (called under lock)

    private static void RefreshSnapshot()
    {
        int pos = _writePos;
        RefreshChannelSnapshot(_ringMono, _waveSnapMono, _fftSnapMono, _fftSmoothedMono, pos);

        if (!StereoTexture)
            return;

        RefreshChannelSnapshot(_ringLeft, _waveSnapLeft, _fftSnapLeft, _fftSmoothedLeft, pos);
        RefreshChannelSnapshot(_ringRight, _waveSnapRight, _fftSnapRight, _fftSmoothedRight, pos);
    }

    private static void RefreshChannelSnapshot(float[] ring, byte[] waveSnap, byte[] fftSnap, float[] fftSmoothed, int pos)
    {
        // Waveform: last OUT_W samples
        for (int x = 0; x < OUT_W; x++)
        {
            float s = ring[Wrap(pos - OUT_W + x)];
            waveSnap[x] = (byte)Math.Clamp((int)((s * 0.5f + 0.5f) * 255f), 0, 255);
        }

        // FFT: last FFT_N samples with Hann window
        for (int i = 0; i < FFT_N; i++)
        {
            float w = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FFT_N - 1));
            _fftBuf[i] = new Complex(ring[Wrap(pos - FFT_N + i)] * w, 0);
        }

        Fft(_fftBuf, FFT_N);

        int halfN = FFT_N / 2;
        for (int i = 0; i < halfN; i++)
            _mags[i] = (float)_fftBuf[i].Magnitude;

        float decay = Smoothing;
        float binHz = Math.Max(1, SampleRate) / (float)FFT_N;

        if (FrequencyScale == SpectrumMode.Linear)
        {
            // 1:1 bin mapping, no frequency window
            for (int x = 0; x < OUT_W; x++)
            {
                float mag = _mags[x * halfN / OUT_W];
                CommitBin(x, mag, decay, fftSnap, fftSmoothed);
            }
        }
        else
        {
            // Logarithmic frequency spacing within [MinFrequency, MaxFrequency]
            int minBin = Math.Clamp((int)MathF.Ceiling(MinFrequency / binHz), 1, halfN - 2);
            int maxBin = Math.Clamp((int)(MaxFrequency / binHz), minBin + 1, halfN - 1);
            float logMin = MathF.Log(minBin);
            float logRange = MathF.Log(maxBin) - logMin;

            for (int x = 0; x < OUT_W; x++)
            {
                float t0 = (float) x      / OUT_W;
                float t1 = (float)(x + 1) / OUT_W;
                int b0 = Math.Clamp((int)MathF.Exp(logMin + t0 * logRange), 0, halfN - 1);
                int b1 = Math.Clamp((int)MathF.Exp(logMin + t1 * logRange), b0, halfN - 1);

                float mag;
                if (FrequencyScale == SpectrumMode.Max)
                {
                    mag = 0f;
                    for (int b = b0; b <= b1; b++) if (_mags[b] > mag) mag = _mags[b];
                }
                else // Average
                {
                    float sum = 0f;
                    for (int b = b0; b <= b1; b++) sum += _mags[b];
                    mag = sum / (b1 - b0 + 1);
                }
                CommitBin(x, mag, decay, fftSnap, fftSmoothed);
            }
        }
    }

    private static void CommitBin(int x, float mag, float decay, byte[] fftSnap, float[] fftSmoothed)
    {
        // Normalise against the FFT window's expected peak magnitude so that
        // real audio sits within [0,1] rather than always saturating at 1.
        float db   = 20f * MathF.Log10(MathF.Max(mag, 1e-9f));
        float norm = (db - _dbRef - DbFloor) / (-DbFloor);
        float raw  = Math.Clamp(norm * 255f, 0f, 255f);

        // Separate EMA alphas for attack (rise) and decay (fall).
        // Attack alpha high → slow rise (suppresses hard peaks).
        // Decay alpha low  → fast fall (bars snap back between beats).
        float alpha = raw > fftSmoothed[x] ? Attack : decay;
        fftSmoothed[x] += (raw - fftSmoothed[x]) * (1f - alpha);
        fftSnap[x] = (byte)fftSmoothed[x];
    }

    private static int Wrap(int idx) => ((idx % RING) + RING) % RING;

    // ------------------------------------------------------------------ Cooley-Tukey FFT

    private static void Fft(Complex[] a, int n)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang  = -2 * Math.PI / len;
            var   wlen  = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k], v = a[i + k + len / 2] * w;
                    a[i + k]           = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }
}

public enum AudioInputSource
{
    RenderLoopback = 0,
    StereoMix = 1,
}

public enum SpectrumMode
{
    /// <summary>Log frequency scale, each output pixel = peak of its bin range.</summary>
    Max = 0,
    /// <summary>Log frequency scale, each output pixel = average of its bin range.</summary>
    Average = 1,
    /// <summary>Linear 1:1 bin mapping — full Nyquist range, no frequency windowing.</summary>
    Linear = 2,
}
