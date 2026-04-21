using System;
using System.Runtime.InteropServices;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal static class PortalPipeWireFrameReaderFactory
{
    private static readonly ILogger Logger = Log.ForContext(typeof(PortalPipeWireFrameReaderFactory));

    public static IPortalPipeWireFrameReader Create(PortalPipeWireOutput output, int pipeWireRemoteFd)
    {
        if (Environment.GetEnvironmentVariable("ARTEMIS_PIPEWIRE_DIRECT") == "0")
        {
            AmbilightLinuxDiagnostics.Write(Logger, "direct PipeWire reader disabled by ARTEMIS_PIPEWIRE_DIRECT=0; using GStreamer fallback");
            return new PortalPipeWireFrameReader(output, pipeWireRemoteFd);
        }

        if (!DirectPipeWireFrameReader.IsRuntimeAvailable(out string? reason))
        {
            AmbilightLinuxDiagnostics.Write(Logger, $"direct PipeWire reader unavailable ({reason}); using GStreamer fallback");
            return new PortalPipeWireFrameReader(output, pipeWireRemoteFd);
        }

        return new FallbackPortalPipeWireFrameReader(output, pipeWireRemoteFd);
    }

    private sealed class FallbackPortalPipeWireFrameReader : IPortalPipeWireFrameReader
    {
        private static readonly TimeSpan DirectStartupGrace = TimeSpan.FromSeconds(3);

        private readonly PortalPipeWireOutput _output;
        private readonly int _pipeWireRemoteFd;
        private readonly PortalPipeWireFrameReader _gstreamerReader;
        private IPortalPipeWireFrameReader? _directReader;
        private DateTimeOffset _directStartedAt;
        private bool _directDisabled;

        public FallbackPortalPipeWireFrameReader(PortalPipeWireOutput output, int pipeWireRemoteFd)
        {
            _output = output;
            _pipeWireRemoteFd = pipeWireRemoteFd;
            _gstreamerReader = new PortalPipeWireFrameReader(output, pipeWireRemoteFd);
            TryStartDirectReader();
        }

        public void Configure(int sourceDownscaleLevel, int fpsLimit)
        {
            if (!_directDisabled && _directReader == null)
                TryStartDirectReader();

            try
            {
                _directReader?.Configure(sourceDownscaleLevel, fpsLimit);
            }
            catch (Exception ex)
            {
                DisableDirectReader($"direct PipeWire configure failed: {ex.GetBaseException().Message}", ex);
            }

            _gstreamerReader.Configure(sourceDownscaleLevel, fpsLimit);
        }

        public bool TryCopyLatestFrame(ref byte[] destination, out int sourceWidth, out int sourceHeight, out int sourceDownscaleLevel)
        {
            if (_directReader != null)
            {
                try
                {
                    if (_directReader.TryCopyLatestFrame(ref destination, out sourceWidth, out sourceHeight, out sourceDownscaleLevel))
                        return true;
                }
                catch (Exception ex)
                {
                    DisableDirectReader($"direct PipeWire reader failed: {ex.GetBaseException().Message}", ex);
                }

                if (_directReader != null && DateTimeOffset.UtcNow - _directStartedAt < DirectStartupGrace)
                {
                    sourceWidth = 0;
                    sourceHeight = 0;
                    sourceDownscaleLevel = 0;
                    return false;
                }

                if (_directReader != null)
                    DisableDirectReader($"direct PipeWire produced no frames after {DirectStartupGrace.TotalSeconds:0} seconds; falling back to GStreamer", null);
            }

            return _gstreamerReader.TryCopyLatestFrame(ref destination, out sourceWidth, out sourceHeight, out sourceDownscaleLevel);
        }

        public void Restart()
        {
            if (!_directDisabled)
            {
                _directReader?.Dispose();
                _directReader = null;
                TryStartDirectReader();
            }

            _gstreamerReader.Restart();
        }

        public void Dispose()
        {
            _directReader?.Dispose();
            _directReader = null;
            _gstreamerReader.Dispose();
        }

        private void TryStartDirectReader()
        {
            try
            {
                _directReader = new DirectPipeWireFrameReader(_output, _pipeWireRemoteFd);
                _directStartedAt = DateTimeOffset.UtcNow;
                AmbilightLinuxDiagnostics.Write(Logger, $"started direct PipeWire reader for {_output.StableId}");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or InvalidOperationException or ExternalException)
            {
                DisableDirectReader($"direct PipeWire startup failed: {ex.GetBaseException().Message}; using GStreamer fallback", ex);
            }
        }

        private void DisableDirectReader(string message, Exception? exception)
        {
            if (_directDisabled)
                return;

            if (exception == null)
                Logger.Warning("{Message}", message);
            else
                Logger.Warning(exception, "{Message}", message);

            AmbilightLinuxDiagnostics.Write(Logger, message);
            _directDisabled = true;
            _directReader?.Dispose();
            _directReader = null;
        }
    }
}
