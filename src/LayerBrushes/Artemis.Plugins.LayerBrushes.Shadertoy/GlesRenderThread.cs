using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>
/// Dedicated thread for all EGL/GL ES operations.
/// OpenGL ES contexts are thread-bound — every GL call must originate from
/// the thread that created and made the EGL context current.
/// </summary>
internal sealed class GlesRenderThread : IDisposable
{
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<Action> _queue = new();

    public GlesRenderThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SKSL-GLES" };
        _thread.Start();
    }

    /// <summary>Dispatches <paramref name="action"/> to the GL thread and blocks until it completes.
    /// Returns immediately (no-op) if the thread is shutting down — callers during plugin Disable()
    /// arrive after the GL context is already gone, so teardown GL calls would be meaningless.</summary>
    public void Invoke(Action action)
    {
        if (_cts.IsCancellationRequested) return;

        ExceptionDispatchInfo? error = null;
        using var gate = new ManualResetEventSlim(false);

        try
        {
            _queue.Add(() =>
            {
                try   { action(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                finally { gate.Set(); }
            }, _cts.Token);

            gate.Wait(_cts.Token);
        }
        catch (OperationCanceledException) { return; } // cancelled while queuing or waiting
        catch (ObjectDisposedException)    { return; } // queue disposed between the check and Add

        error?.Throw();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_queue.TryTake(out Action? action, 500))
                action();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(2000);
        _cts.Dispose();
        _queue.Dispose();
    }
}
