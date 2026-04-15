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

    private static readonly float[] _fftSmoothed = new float[OUT_W];

    private static WinmmCapture? _capture;
    private static readonly float[]   _ring    = new float[RING];
    private static volatile int        _writePos;

    private static readonly Complex[]  _fftBuf  = new Complex[FFT_N];
    private static readonly float[]    _mags    = new float[FFT_N / 2];
    private static readonly byte[]     _fftSnap  = new byte[OUT_W];
    // Pre-filled to 127 = silence midpoint (s=0 → (0×0.5+0.5)×255 = 127.5 → 127).
    // Shaders see a flat centred waveform before any audio data arrives.
    private static readonly byte[]     _waveSnap = Enumerable.Repeat((byte)127, OUT_W).ToArray();
    private static readonly Lock       _lock     = new();

    public static bool IsRunning { get; private set; }

    // ------------------------------------------------------------------ lifecycle

    public static void Start()
    {
        if (IsRunning) return;

        // WASAPI COM objects must be created on an MTA thread.
        // Enable() is called from the Artemis UI (STA) thread; creating COM objects there
        // forces cross-apartment marshaling on every capture callback, which blocks STA
        // and causes DryIoc service-creation timeouts in other audio plugins.
        var t = new Thread(() =>
        {
            try
            {
                var cap = new WinmmCapture();
                cap.DataAvailable = OnData;
                cap.Start();
                _capture  = cap;
                IsRunning = true;
                ShaderLogger.Log("AudioCapture: started WinMM capture");
            }
            catch (Exception ex)
            {
                ShaderLogger.Log($"AudioCapture: failed to start: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
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
        if (!IsRunning) return;
        try { _capture?.Dispose(); }
        catch { }
        _capture  = null;
        IsRunning = false;
        ShaderLogger.Log("AudioCapture: WinMM stopped");
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
                int b0 = x * 4;
                dest[b0]     = _fftSnap[x];
                dest[b0 + 1] = 0; dest[b0 + 2] = 0; dest[b0 + 3] = 255;

                int b1 = (OUT_W + x) * 4;
                dest[b1]     = _waveSnap[x];
                dest[b1 + 1] = 0; dest[b1 + 2] = 0; dest[b1 + 3] = 255;
            }
        }
    }

    // ------------------------------------------------------------------ capture callback

    private static int _frameCount;

    private static void OnData(float[] mono, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _ring[(_writePos++) % RING] = mono[i];
            RefreshSnapshot();

            // Log first few deliveries so we can confirm WASAPI is sending frames.
            int fc = ++_frameCount;
            if (fc <= 5 || fc == 100)
                ShaderLogger.Log($"AudioCapture.OnData: delivery #{fc}, samples={count}," +
                    $" peak={Peak(mono, count):F3}");
        }
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

        // Waveform: last OUT_W samples
        for (int x = 0; x < OUT_W; x++)
        {
            float s = _ring[Wrap(pos - OUT_W + x)];
            _waveSnap[x] = (byte)Math.Clamp((int)((s * 0.5f + 0.5f) * 255f), 0, 255);
        }

        // FFT: last FFT_N samples with Hann window
        for (int i = 0; i < FFT_N; i++)
        {
            float w = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FFT_N - 1));
            _fftBuf[i] = new Complex(_ring[Wrap(pos - FFT_N + i)] * w, 0);
        }

        Fft(_fftBuf, FFT_N);

        int halfN = FFT_N / 2;
        for (int i = 0; i < halfN; i++)
            _mags[i] = (float)_fftBuf[i].Magnitude;

        float decay = Smoothing;
        const float BIN_HZ = 44100f / FFT_N;

        if (FrequencyScale == SpectrumMode.Linear)
        {
            // 1:1 bin mapping, no frequency window
            for (int x = 0; x < OUT_W; x++)
            {
                float mag = _mags[x * halfN / OUT_W];
                CommitBin(x, mag, decay);
            }
        }
        else
        {
            // Logarithmic frequency spacing within [MinFrequency, MaxFrequency]
            int minBin = Math.Clamp((int)MathF.Ceiling(MinFrequency / BIN_HZ), 1, halfN - 2);
            int maxBin = Math.Clamp((int)(MaxFrequency / BIN_HZ), minBin + 1, halfN - 1);
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
                CommitBin(x, mag, decay);
            }
        }
    }

    private static void CommitBin(int x, float mag, float decay)
    {
        // Normalise against the FFT window's expected peak magnitude so that
        // real audio sits within [0,1] rather than always saturating at 1.
        float db   = 20f * MathF.Log10(MathF.Max(mag, 1e-9f));
        float norm = (db - _dbRef - DbFloor) / (-DbFloor);
        float raw  = Math.Clamp(norm * 255f, 0f, 255f);

        // Separate EMA alphas for attack (rise) and decay (fall).
        // Attack alpha high → slow rise (suppresses hard peaks).
        // Decay alpha low  → fast fall (bars snap back between beats).
        float alpha = raw > _fftSmoothed[x] ? Attack : decay;
        _fftSmoothed[x] += (raw - _fftSmoothed[x]) * (1f - alpha);
        _fftSnap[x] = (byte)_fftSmoothed[x];
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

public enum SpectrumMode
{
    /// <summary>Log frequency scale, each output pixel = peak of its bin range.</summary>
    Max = 0,
    /// <summary>Log frequency scale, each output pixel = average of its bin range.</summary>
    Average = 1,
    /// <summary>Linear 1:1 bin mapping — full Nyquist range, no frequency windowing.</summary>
    Linear = 2,
}
