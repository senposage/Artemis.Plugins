using System;
using System.Runtime.Versioning;
using System.Threading;
using HPPH;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wgc;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WgcCaptureZone : ICaptureZone
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

    public WgcCaptureZone(Display display, int x, int y, int width, int height, int downscaleLevel)
    {
        Display = display;
        X = x;
        Y = y;
        UnscaledWidth = width;
        UnscaledHeight = height;
        DownscaleLevel = downscaleLevel;

        Width = width >> downscaleLevel;
        Height = height >> downscaleLevel;
        if (Width < 1) Width = 1;
        if (Height < 1) Height = 1;

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

    internal void Update(int x, int? y, int? width, int? height, int? downscaleLevel)
    {
        if (x != X || y != Y || width != UnscaledWidth || height != UnscaledHeight || downscaleLevel != DownscaleLevel)
        {
            X = x;
            if (y.HasValue) Y = y.Value;
            if (width.HasValue) UnscaledWidth = width.Value;
            if (height.HasValue) UnscaledHeight = height.Value;
            if (downscaleLevel.HasValue) DownscaleLevel = downscaleLevel.Value;

            Width = UnscaledWidth >> DownscaleLevel;
            Height = UnscaledHeight >> DownscaleLevel;
            if (Width < 1) Width = 1;
            if (Height < 1) Height = 1;

            lock (_lock)
            {
                _buffer = new byte[Width * Height * 4];
            }
        }
    }

    /// <summary>
    /// Returns true if this zone needs new data this frame.
    /// </summary>
    internal bool NeedsUpdate => AutoUpdate || _updateRequested;

    /// <summary>
    /// Copies the relevant region from the captured frame data into this zone's buffer.
    /// <paramref name="appliedMipLevel"/> indicates how many downscale levels the GPU
    /// already applied via GenerateMips — source coordinates are shifted accordingly
    /// and only the remaining downscale is done by CPU pixel-skip.
    /// </summary>
    internal unsafe void CopyFromFrame(byte* sourceData, int sourceStride, int sourceWidth, int sourceHeight, int appliedMipLevel = 0)
    {
        if (!NeedsUpdate) return;
        _updateRequested = false;

        lock (_lock)
        {
            // Adjust zone coordinates into the (potentially downscaled) source space
            int srcX = Math.Min(X >> appliedMipLevel, sourceWidth);
            int srcY = Math.Min(Y >> appliedMipLevel, sourceHeight);
            int srcW = Math.Min(UnscaledWidth >> appliedMipLevel, sourceWidth - srcX);
            int srcH = Math.Min(UnscaledHeight >> appliedMipLevel, sourceHeight - srcY);
            if (srcW <= 0 || srcH <= 0) return;

            // GPU already applied 'appliedMipLevel' levels; only the remainder is CPU work
            int remainingDownscale = Math.Max(0, DownscaleLevel - appliedMipLevel);
            int step = 1 << remainingDownscale;
            int dstW = Width;
            int dstH = Height;

            fixed (byte* dst = _buffer)
            {
                for (int dy = 0; dy < dstH; dy++)
                {
                    int sy = srcY + Math.Min(dy * step, srcH - 1);
                    byte* srcRow = sourceData + (sy * sourceStride);
                    byte* dstRow = dst + (dy * dstW * 4);

                    for (int dx = 0; dx < dstW; dx++)
                    {
                        int sx = srcX + Math.Min(dx * step, srcW - 1);
                        *(int*)(dstRow + dx * 4) = *(int*)(srcRow + sx * 4);
                    }
                }
            }
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class LockHandle(object lockObj) : IDisposable
    {
        public void Dispose() => Monitor.Exit(lockObj);
    }
}
