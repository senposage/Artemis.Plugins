using Artemis.Core;
using Artemis.Core.Services;
using System;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

public class ShaderToyBootstrapper : PluginBootstrapper
{
    private const int RenderPumpIntervalMs = 16;

    private static IRenderService? _renderService;
    private static Timer? _renderPump;
    private static int _activeBrushes;
    private static int _requestInProgress;

    public override void OnPluginEnabled(Plugin plugin)
    {
        _renderService = plugin.Resolve<IRenderService>();
    }

    public override void OnPluginDisabled(Plugin plugin)
    {
        _renderPump?.Dispose();
        _renderPump = null;
        _renderService = null;
        _activeBrushes = 0;
        _requestInProgress = 0;
    }

    internal static void RegisterActiveBrush()
    {
        if (Interlocked.Increment(ref _activeBrushes) != 1)
            return;

        _renderPump ??= new Timer(_ => RequestRenderFromPump(), null, Timeout.Infinite, Timeout.Infinite);
        _renderPump.Change(0, RenderPumpIntervalMs);
    }

    internal static void UnregisterActiveBrush()
    {
        if (Interlocked.Decrement(ref _activeBrushes) > 0)
            return;

        Interlocked.Exchange(ref _activeBrushes, 0);
        _renderPump?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void RequestRenderFromPump()
    {
        if (_renderService == null || Volatile.Read(ref _activeBrushes) <= 0)
            return;

        if (Interlocked.Exchange(ref _requestInProgress, 1) == 1)
            return;

        try
        {
            _renderService.Surface.Update(true);
        }
        catch (Exception ex)
        {
            ShaderLogger.Log($"RenderPump: request failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _requestInProgress, 0);
        }
    }
}
