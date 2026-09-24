using Artemis.Core;
using Artemis.Core.Services;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

public class ShaderToyBootstrapper : PluginBootstrapper
{
    private static IRenderService? _renderService;

    public override void OnPluginEnabled(Plugin plugin)
    {
        _renderService = plugin.Resolve<IRenderService>();
    }

    public override void OnPluginDisabled(Plugin plugin)
    {
        _renderService = null;
    }

    internal static void RequestRender()
    {
        _renderService?.Surface.Update(true);
    }
}
