using System;
using System.Threading;
using HPPH;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class PortalPipeWireCaptureZone : ICaptureZone
{
    private readonly object _lock = new();
    private byte[] _buffer;
    private volatile bool _updateRequested;

    public Display Display { get; }
    public IColorFormat ColorFormat => ColorBGRA.ColorFormat;
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public int DownscaleLevel { get; private set; }
    public int UnscaledWidth { get; private set; }
    public int UnscaledHeight { get; private set; }
    public ReadOnlySpan<byte> RawBuffer => _buffer;
    public IImage Image => Image<ColorBGRA>.Wrap(_buffer, Width, Height, Stride);
    public bool AutoUpdate { get; set; } = true;
    public bool IsUpdateRequested => _updateRequested;
    public event EventHandler? Updated;

    public PortalPipeWireCaptureZone(Display display, int x, int y, int width, int height, int downscaleLevel)
    {
        Display = display;
        X = x;
        Y = y;
        UnscaledWidth = width;
        UnscaledHeight = height;
        DownscaleLevel = downscaleLevel;
        RecalculateSize();
        _buffer = new byte[Width * Height * 4];
    }

    public void RequestUpdate() => _updateRequested = true;

    public IDisposable Lock()
    {
        Monitor.Enter(_lock);
        return new LockHandle(_lock);
    }

    public RefImage<T> GetRefImage<T>() where T : struct, IColor
    {
        return RefImage<T>.Wrap(_buffer, Width, Height, Stride);
    }

    internal void Update(int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        bool resize = false;
        if (x.HasValue) X = x.Value;
        if (y.HasValue) Y = y.Value;
        if (width.HasValue && width.Value != UnscaledWidth)
        {
            UnscaledWidth = width.Value;
            resize = true;
        }
        if (height.HasValue && height.Value != UnscaledHeight)
        {
            UnscaledHeight = height.Value;
            resize = true;
        }
        if (downscaleLevel.HasValue && downscaleLevel.Value != DownscaleLevel)
        {
            DownscaleLevel = downscaleLevel.Value;
            resize = true;
        }

        if (!resize)
            return;

        lock (_lock)
        {
            RecalculateSize();
            _buffer = new byte[Width * Height * 4];
        }
    }

    internal bool NeedsUpdate => AutoUpdate || _updateRequested;

    internal unsafe void CopyFromFrame(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceDownscaleLevel, int sourceStride, PortalPipeWirePixelFormat pixelFormat)
    {
        if (!NeedsUpdate)
            return;

        _updateRequested = false;
        int step = 1 << Math.Max(0, DownscaleLevel - sourceDownscaleLevel);
        int sourceX = X >> sourceDownscaleLevel;
        int sourceY = Y >> sourceDownscaleLevel;

        lock (_lock)
        {
            fixed (byte* dst = _buffer)
            {
                for (int dy = 0; dy < Height; dy++)
                {
                    int sy = Math.Min(sourceY + (dy * step), sourceHeight - 1);
                    byte* dstRow = dst + (dy * Stride);

                    for (int dx = 0; dx < Width; dx++)
                    {
                        int sx = Math.Min(sourceX + (dx * step), sourceWidth - 1);
                        int sourceIndex = (sy * sourceStride) + sx * 4;
                        byte* pixel = dstRow + dx * 4;
                        WriteBgraPixel(source, sourceIndex, pixel, pixelFormat);
                        pixel[3] = 255;
                    }
                }
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private static unsafe void WriteBgraPixel(ReadOnlySpan<byte> source, int sourceIndex, byte* destination, PortalPipeWirePixelFormat pixelFormat)
    {
        switch (pixelFormat)
        {
            case PortalPipeWirePixelFormat.Bgrx:
            case PortalPipeWirePixelFormat.Bgra:
                destination[0] = source[sourceIndex];
                destination[1] = source[sourceIndex + 1];
                destination[2] = source[sourceIndex + 2];
                break;
            case PortalPipeWirePixelFormat.Rgbx:
            case PortalPipeWirePixelFormat.Rgba:
                destination[0] = source[sourceIndex + 2];
                destination[1] = source[sourceIndex + 1];
                destination[2] = source[sourceIndex];
                break;
            case PortalPipeWirePixelFormat.Xrgb:
            case PortalPipeWirePixelFormat.Argb:
                destination[0] = source[sourceIndex + 3];
                destination[1] = source[sourceIndex + 2];
                destination[2] = source[sourceIndex + 1];
                break;
            case PortalPipeWirePixelFormat.Xbgr:
            case PortalPipeWirePixelFormat.Abgr:
                destination[0] = source[sourceIndex + 1];
                destination[1] = source[sourceIndex + 2];
                destination[2] = source[sourceIndex + 3];
                break;
        }
    }

    private void RecalculateSize()
    {
        Width = Math.Max(1, UnscaledWidth >> DownscaleLevel);
        Height = Math.Max(1, UnscaledHeight >> DownscaleLevel);
    }

    private sealed class LockHandle(object lockObj) : IDisposable
    {
        public void Dispose() => Monitor.Exit(lockObj);
    }
}
