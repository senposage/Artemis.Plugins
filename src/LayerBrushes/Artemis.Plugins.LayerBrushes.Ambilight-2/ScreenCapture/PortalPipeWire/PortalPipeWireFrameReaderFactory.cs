using System;
using System.Runtime.InteropServices;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal static class PortalPipeWireFrameReaderFactory
{
    private static readonly ILogger Logger = Log.ForContext(typeof(PortalPipeWireFrameReaderFactory));

    public static IPortalPipeWireFrameReader Create(PortalPipeWireOutput output, int pipeWireRemoteFd, bool forceGStreamer = false)
    {
        if (forceGStreamer)
        {
            AmbilightLinuxDiagnostics.Write(Logger, "direct PipeWire reader disabled by user setting; using GStreamer fallback");
            return new PortalPipeWireFrameReader(output, pipeWireRemoteFd);
        }

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
        private DirectPipeWireFrameReader? _directReader;
        private DateTimeOffset _directStartedAt;
        private int _directFpsLimit = 30;
        private bool _directDisabled;
        private string? _directUnavailableReason;

        public FallbackPortalPipeWireFrameReader(PortalPipeWireOutput output, int pipeWireRemoteFd)
        {
            _output = output;
            _pipeWireRemoteFd = pipeWireRemoteFd;
            _gstreamerReader = new PortalPipeWireFrameReader(output, pipeWireRemoteFd);
            TryStartDirectReader();
        }

        public string CaptureBackendName => _directReader?.CaptureBackendName ?? _gstreamerReader.CaptureBackendName;

        public string CaptureBackendDetails => _directReader != null
            ? _directReader.CaptureBackendDetails
            : _directDisabled
                ? $"GStreamer PipeWire fallback active; direct PipeWire unavailable: {_directUnavailableReason ?? "unknown reason"}"
                : _gstreamerReader.CaptureBackendDetails;

        public void Configure(int sourceDownscaleLevel, int fpsLimit)
        {
            if (!_directDisabled && _directReader == null)
                TryStartDirectReader();

            _directFpsLimit = fpsLimit;

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

        public bool TryUseLatestFrame(PortalPipeWireFrameConsumer consumer)
        {
            if (_directReader != null)
            {
                try
                {
                    if (_directReader.TryUseLatestFrame(consumer))
                        return true;
                }
                catch (Exception ex)
                {
                    DisableDirectReader($"direct PipeWire reader failed: {ex.GetBaseException().Message}", ex);
                }

                if (_directReader != null && DateTimeOffset.UtcNow - _directStartedAt < DirectStartupGrace)
                    return false;

                if (_directReader != null)
                    DisableDirectReader($"direct PipeWire produced no frames after {DirectStartupGrace.TotalSeconds:0} seconds; falling back to GStreamer", null);
            }

            return _gstreamerReader.TryUseLatestFrame(consumer);
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
                _directReader.Configure(0, _directFpsLimit);
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
            _directUnavailableReason = message;
            _directReader?.Dispose();
            _directReader = null;
        }
    }
}
