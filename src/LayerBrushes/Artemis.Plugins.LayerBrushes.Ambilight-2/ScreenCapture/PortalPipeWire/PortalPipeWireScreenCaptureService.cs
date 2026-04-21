using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wlroots;
using ScreenCapture.NET;
using Serilog;
using Tmds.DBus;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class PortalPipeWireScreenCaptureService : IScreenCaptureService, ICaptureBackendStatus
{
    private static readonly ILogger Logger = Log.ForContext<PortalPipeWireScreenCaptureService>();

    private const string PortalDestination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const int PortalVendorId = 0x504F;
    private const int PortalDeviceId = 0x5254;

    private readonly GraphicsCard _graphicsCard = new(0, "XDG Desktop Portal PipeWire", PortalVendorId, PortalDeviceId);
    private readonly PortalPipeWireSession _session;
    private readonly List<PortalPipeWireOutput> _outputs;
    private readonly Dictionary<Display, PortalPipeWireScreenCapture> _captures = [];

    public PortalPipeWireScreenCaptureService(CancellationToken cancellationToken = default)
    {
        AmbilightLinuxDiagnostics.Write(Logger, "initializing portal PipeWire capture service");
        Logger.Information("Initializing portal PipeWire capture service");
        _session = PortalPipeWireSession.Create(cancellationToken);
        _outputs = _session.Streams.Select((stream, index) =>
        {
            var display = new Display(index, stream.StableId, stream.Width, stream.Height, default, _graphicsCard);
            return new PortalPipeWireOutput(display, stream.NodeId, stream.StableId, stream.X, stream.Y);
        }).ToList();

        if (_outputs.Count == 0)
            throw new InvalidOperationException("No portal PipeWire displays were found.");

        AmbilightLinuxDiagnostics.Write(Logger, $"portal PipeWire capture service initialized with {_outputs.Count} display(s)");
        Logger.Information("Portal PipeWire capture service initialized with {Count} display(s)", _outputs.Count);
    }

    public string CaptureBackendName => "XDG Desktop Portal / PipeWire";

    public string CaptureBackendDetails => "Portal ScreenCast active; Direct PipeWire preferred with GStreamer fallback";

    public static bool IsSupported(out string? reason)
    {
        Logger.Information("Checking portal PipeWire support");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            reason = "WAYLAND_DISPLAY is not set";
            Logger.Information("Portal PipeWire unavailable: {Reason}", reason);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            reason = "DBUS_SESSION_BUS_ADDRESS is not set";
            Logger.Information("Portal PipeWire unavailable: {Reason}", reason);
            return false;
        }

        bool directPipeWireAvailable = DirectPipeWireFrameReader.IsRuntimeAvailable(out string? directReason);
        bool gstLaunchAvailable = WlrootsProcess.CanRun("gst-launch-1.0", "--version");
        bool gstPipeWireAvailable = WlrootsProcess.CanRun("gst-inspect-1.0", "pipewiresrc");
        bool gstVideoRateAvailable = WlrootsProcess.CanRun("gst-inspect-1.0", "videorate");
        bool gstVideoScaleAvailable = WlrootsProcess.CanRun("gst-inspect-1.0", "videoscale");
        bool gstVideoConvertAvailable = WlrootsProcess.CanRun("gst-inspect-1.0", "videoconvert");
        bool gstFdSinkAvailable = WlrootsProcess.CanRun("gst-inspect-1.0", "fdsink");
        bool gstreamerAvailable =
            gstLaunchAvailable &&
            gstPipeWireAvailable &&
            gstVideoRateAvailable &&
            gstVideoScaleAvailable &&
            gstVideoConvertAvailable &&
            gstFdSinkAvailable;

        AmbilightLinuxDiagnostics.Write(Logger,
            "Portal dependency check: " +
            $"directPipeWire={directPipeWireAvailable}{(directPipeWireAvailable ? "" : $" ({directReason})")} " +
            $"gst-launch={gstLaunchAvailable} pipewiresrc={gstPipeWireAvailable} videorate={gstVideoRateAvailable} " +
            $"videoscale={gstVideoScaleAvailable} videoconvert={gstVideoConvertAvailable} fdsink={gstFdSinkAvailable}");

        if (!directPipeWireAvailable && !gstreamerAvailable)
        {
            reason = $"neither direct PipeWire ({directReason}) nor GStreamer with pipewiresrc, videorate, videoscale, videoconvert, and fdsink is available. {LinuxPackageHint}";
            Logger.Information("Portal PipeWire unavailable: {Reason}", reason);
            return false;
        }

        if (!IsScreenCastPortalAvailable(out reason))
        {
            Logger.Information("Portal PipeWire unavailable: {Reason}", reason);
            return false;
        }

        reason = null;
        Logger.Information("Portal PipeWire support checks passed (direct={DirectPipeWireAvailable}, gstreamer={GStreamerAvailable})", directPipeWireAvailable, gstreamerAvailable);
        return true;
    }

    private const string LinuxPackageHint =
        "Install packages roughly equivalent to: Arch: xdg-desktop-portal xdg-desktop-portal-kde/gnome/hyprland/wlr pipewire wireplumber gst-plugin-pipewire gst-plugins-base gst-plugins-good; " +
        "Debian/Ubuntu: xdg-desktop-portal xdg-desktop-portal-kde/gnome/wlr pipewire wireplumber gstreamer1.0-pipewire gstreamer1.0-plugins-base gstreamer1.0-plugins-good; " +
        "Fedora: xdg-desktop-portal xdg-desktop-portal-kde/gnome/hyprland/wlr pipewire wireplumber pipewire-gstreamer gstreamer1-plugins-base gstreamer1-plugins-good.";

    private static bool IsScreenCastPortalAvailable(out string? reason)
    {
        try
        {
            using var connection = new Connection(Address.Session);
            connection.ConnectAsync().GetAwaiter().GetResult();

            IPortalIntrospectable portal = connection.CreateProxy<IPortalIntrospectable>(PortalDestination, PortalPath);
            string xml = portal.IntrospectAsync().GetAwaiter().GetResult();
            if (xml.Contains($"interface name=\"{ScreenCastInterface}\"", StringComparison.Ordinal))
            {
                reason = null;
                return true;
            }

            reason = "xdg-desktop-portal is running, but it does not expose org.freedesktop.portal.ScreenCast. On KDE this usually means xdg-desktop-portal-kde is missing, not running, or the session selected the wrong portal backend.";
            Logger.Debug("Portal desktop introspection did not include ScreenCast. Introspection XML: {Xml}", xml);
            return false;
        }
        catch (Exception ex)
        {
            reason = $"could not introspect xdg-desktop-portal ScreenCast support: {ex.GetBaseException().Message}";
            Logger.Debug(ex, "Could not introspect xdg-desktop-portal ScreenCast support");
            return false;
        }
    }

    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        yield return _graphicsCard;
    }

    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        return graphicsCard == _graphicsCard ? _outputs.Select(output => output.Display) : Enumerable.Empty<Display>();
    }

    public IScreenCapture GetScreenCapture(Display display)
    {
        lock (_captures)
        {
            if (_captures.TryGetValue(display, out PortalPipeWireScreenCapture? capture))
                return capture;

            PortalPipeWireOutput output = _outputs.FirstOrDefault(candidate => candidate.Display == display)
                ?? throw new ArgumentException($"Unknown portal PipeWire display '{display.DeviceName}'.", nameof(display));
            AmbilightLinuxDiagnostics.Write(Logger, $"creating portal PipeWire screen capture for {output.StableId} node={output.NodeId}");
            Logger.Information("Creating portal PipeWire screen capture for {Display} node={NodeId}", output.StableId, output.NodeId);
            capture = new PortalPipeWireScreenCapture(output, _session.PipeWireRemoteFd);
            _captures[display] = capture;
            return capture;
        }
    }

    public void Dispose()
    {
        foreach (PortalPipeWireScreenCapture capture in _captures.Values)
            capture.Dispose();
        _captures.Clear();
        _session.Dispose();
    }
}

[DBusInterface("org.freedesktop.DBus.Introspectable")]
public interface IPortalIntrospectable : IDBusObject
{
    Task<string> IntrospectAsync();
}
