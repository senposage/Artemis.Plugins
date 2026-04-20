#if !WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Loopback audio capture on Linux via parecord (PulseAudio/PipeWire compat layer).
/// Spawns: parecord --raw --format=float32le --rate=48000 --channels=2 --device=@DEFAULT_MONITOR@ -
/// Reads raw F32LE stereo PCM and hands it to AudioCapture via DataAvailable.
/// Requires: pulseaudio-utils OR pipewire-pulse (provides parecord).
/// </summary>
internal sealed class LinuxPipeWireAudioCapture : IDisposable
{
    public Action<float[], float[], int>? DataAvailable;
    public int SampleRate { get; } = 48000;

    private const int Channels   = 2;
    private const int ChunkFrames = 512;

    private Process? _process;
    private Thread?  _thread;
    private volatile bool _stopping;

    public void Start()
    {
        ProcessStartInfo psi = BuildCaptureInfo();
        ShaderLogger.Log($"LinuxPipeWireAudioCapture: starting {psi.FileName} {psi.Arguments}");

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start audio capture — install pulseaudio-utils or pipewire-pulse (provides parec)", ex);
        }

        if (_process == null)
            throw new InvalidOperationException("Failed to start parec — is pulseaudio-utils or pipewire-pulse installed?");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            string err = await _process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(err))
                ShaderLogger.Log($"LinuxPipeWireAudioCapture stderr: {err}");
        });

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "LinuxAudioCapture" };
        _thread.Start();
    }

    // parec writes raw PCM to stdout by default — no need for a filename argument
    private ProcessStartInfo BuildCaptureInfo() => new("parec")
    {
        Arguments              = $"--format=float32le --rate={SampleRate} --channels={Channels} --device=@DEFAULT_MONITOR@",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
    };

    private void ReadLoop()
    {
        if (_process == null) return;

        int bytesPerFrame = Channels * sizeof(float);
        int bufBytes      = ChunkFrames * bytesPerFrame;
        byte[]  buf   = new byte[bufBytes];
        float[] left  = new float[ChunkFrames];
        float[] right = new float[ChunkFrames];
        var stream = _process.StandardOutput.BaseStream;

        while (!_stopping && !_process.HasExited)
        {
            int read = 0;
            while (read < bufBytes && !_stopping)
            {
                int n = stream.Read(buf, read, bufBytes - read);
                if (n == 0) break;
                read += n;
            }

            int frames = read / bytesPerFrame;
            if (frames == 0) break;

            for (int i = 0; i < frames; i++)
            {
                left[i]  = MemoryMarshal.Read<float>(buf.AsSpan(i * bytesPerFrame));
                right[i] = MemoryMarshal.Read<float>(buf.AsSpan(i * bytesPerFrame + sizeof(float)));
            }

            DataAvailable?.Invoke(left, right, frames);
        }

        ShaderLogger.Log("LinuxPipeWireAudioCapture: read loop ended");
    }

    public void Dispose()
    {
        _stopping = true;
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
    }
}
#endif
