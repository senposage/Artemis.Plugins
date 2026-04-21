using System;
using System.Collections.Generic;
using System.IO;
using ScreenCapture.NET;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wlroots;

internal sealed class WlrootsScreenCapture : IScreenCapture, ICaptureBackendStatus
{
    private readonly WlrootsOutput _output;
    private readonly List<WlrootsCaptureZone> _zones = [];
    private readonly object _zonesLock = new();

    public Display Display => _output.Display;
    public event EventHandler<ScreenCaptureUpdatedEventArgs>? Updated;
    public string CaptureBackendName => "wlroots";
    public string CaptureBackendDetails => $"wlroots/grim active; output={Display.DeviceName}";

    public WlrootsScreenCapture(WlrootsOutput output)
    {
        _output = output;
    }

    public ICaptureZone RegisterCaptureZone(int x, int y, int width, int height, int downscaleLevel)
    {
        ValidateCaptureZone(x, y, width, height, downscaleLevel);

        var zone = new WlrootsCaptureZone(Display, x, y, width, height, downscaleLevel);
        lock (_zonesLock)
            _zones.Add(zone);
        return zone;
    }

    public bool UnregisterCaptureZone(ICaptureZone captureZone)
    {
        lock (_zonesLock)
            return captureZone is WlrootsCaptureZone zone && _zones.Remove(zone);
    }

    public void UpdateCaptureZone(ICaptureZone captureZone, int? x = null, int? y = null, int? width = null, int? height = null, int? downscaleLevel = null)
    {
        if (captureZone is not WlrootsCaptureZone zone)
            throw new ArgumentException("Capture zone was not created by this wlroots capture.", nameof(captureZone));

        ValidateCaptureZone(x ?? zone.X, y ?? zone.Y, width ?? zone.UnscaledWidth, height ?? zone.UnscaledHeight, downscaleLevel ?? zone.DownscaleLevel);
        zone.Update(x, y, width, height, downscaleLevel);
    }

    public bool CaptureScreen()
    {
        WlrootsCaptureZone[] zones;
        lock (_zonesLock)
            zones = _zones.ToArray();

        bool anyUpdated = false;
        foreach (WlrootsCaptureZone zone in zones)
        {
            if (!zone.NeedsUpdate)
                continue;

            PpmImage image = CaptureZone(zone);
            zone.CopyFromRgb(image.Rgb, image.Width, image.Height);
            anyUpdated = true;
        }

        Updated?.Invoke(this, new ScreenCaptureUpdatedEventArgs(anyUpdated));
        return anyUpdated;
    }

    public void Restart()
    {
    }

    public void Dispose()
    {
        lock (_zonesLock)
            _zones.Clear();
    }

    private PpmImage CaptureZone(WlrootsCaptureZone zone)
    {
        string geometry = $"{_output.X + zone.X},{_output.Y + zone.Y} {zone.UnscaledWidth}x{zone.UnscaledHeight}";
        byte[] ppm = WlrootsProcess.RunBytes("grim", ["-t", "ppm", "-o", Display.DeviceName, "-g", geometry, "-"], TimeSpan.FromSeconds(5));
        return PpmImage.Parse(ppm);
    }

    private void ValidateCaptureZone(int x, int y, int width, int height, int downscaleLevel)
    {
        if (x < 0 || y < 0)
            throw new ArgumentException("Capture zone coordinates must be positive.");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Capture zone size must be positive.");
        if (downscaleLevel < 0)
            throw new ArgumentException("Downscale level must be positive.");
        if (x + width > Display.Width || y + height > Display.Height)
            throw new ArgumentException("Capture zone must fit inside the display.");
    }

    private readonly record struct PpmImage(int Width, int Height, byte[] Rgb)
    {
        public static PpmImage Parse(byte[] data)
        {
            int offset = 0;
            string magic = ReadToken(data, ref offset);
            if (magic != "P6")
                throw new InvalidDataException("grim returned an unsupported image format.");

            int width = int.Parse(ReadToken(data, ref offset));
            int height = int.Parse(ReadToken(data, ref offset));
            int maxValue = int.Parse(ReadToken(data, ref offset));
            if (maxValue != 255)
                throw new InvalidDataException("Only 8-bit PPM captures are supported.");

            if (offset < data.Length && data[offset] == '\r')
            {
                offset++;
                if (offset < data.Length && data[offset] == '\n')
                    offset++;
            }
            else if (offset < data.Length && char.IsWhiteSpace((char)data[offset]))
            {
                offset++;
            }
            int expected = width * height * 3;
            if (data.Length - offset < expected)
                throw new InvalidDataException("grim returned a truncated PPM image.");

            byte[] rgb = new byte[expected];
            Buffer.BlockCopy(data, offset, rgb, 0, expected);
            return new PpmImage(width, height, rgb);
        }

        private static string ReadToken(byte[] data, ref int offset)
        {
            SkipWhitespaceAndComments(data, ref offset);
            int start = offset;
            while (offset < data.Length && !char.IsWhiteSpace((char)data[offset]))
                offset++;
            return System.Text.Encoding.ASCII.GetString(data, start, offset - start);
        }

        private static void SkipWhitespaceAndComments(byte[] data, ref int offset)
        {
            while (true)
            {
                SkipWhitespace(data, ref offset);
                if (offset >= data.Length || data[offset] != '#')
                    return;
                while (offset < data.Length && data[offset] != '\n')
                    offset++;
            }
        }

        private static void SkipWhitespace(byte[] data, ref int offset)
        {
            while (offset < data.Length && char.IsWhiteSpace((char)data[offset]))
                offset++;
        }
    }
}
