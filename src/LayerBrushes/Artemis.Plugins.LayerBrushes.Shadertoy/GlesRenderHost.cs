using System;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>Owns one GL worker thread and the EGL context bound to it.</summary>
internal sealed class GlesRenderHost : IDisposable
{
    public GlesRenderThread Thread { get; }

    private readonly GlesContext _context;

    public GlesRenderHost()
    {
        Thread = new GlesRenderThread();
        _context = new GlesContext(Thread);
    }

    public void Dispose()
    {
        _context.Dispose();
        Thread.Dispose();
    }
}
