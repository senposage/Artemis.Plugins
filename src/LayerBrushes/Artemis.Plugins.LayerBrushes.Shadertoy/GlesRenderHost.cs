using System;
using System.Threading;

namespace Artemis.Plugins.LayerBrushes.Shadertoy;

/// <summary>Owns one GL worker thread and the EGL context bound to it.</summary>
internal sealed class GlesRenderHost : IDisposable
{
    private static readonly Lock ContextCreationLock = new();

    public GlesRenderThread Thread { get; }

    private readonly GlesContext _context;

    public GlesRenderHost()
    {
        Thread = new GlesRenderThread();
        try
        {
            lock (ContextCreationLock)
                _context = new GlesContext(Thread);
        }
        catch
        {
            Thread.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        Thread.Dispose();
    }
}
