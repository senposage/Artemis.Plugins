using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ScreenCapture.NET;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.Wlroots;

internal sealed class WlrootsScreenCaptureService : IScreenCaptureService
{
    private static readonly ILogger Logger = Log.ForContext<WlrootsScreenCaptureService>();

    private const int WlrootsVendorId = 0x574C;
    private const int WlrootsDeviceId = 0x0001;

    private readonly GraphicsCard _graphicsCard = new(0, "wlroots Wayland compositor", WlrootsVendorId, WlrootsDeviceId);
    private readonly List<WlrootsOutput> _outputs;
    private readonly Dictionary<Display, WlrootsScreenCapture> _captures = [];

    public WlrootsScreenCaptureService()
    {
        Logger.Information("Initializing wlroots capture service");
        _outputs = EnumerateOutputs().ToList();
        if (_outputs.Count == 0)
            throw new InvalidOperationException("No active wlroots outputs were found.");
        Logger.Information("wlroots capture service initialized with {Count} output(s)", _outputs.Count);
    }

    public static bool IsSupported(out string? reason)
    {
        Logger.Information("Checking wlroots support");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            reason = "WAYLAND_DISPLAY is not set";
            Logger.Information("wlroots unavailable: {Reason}", reason);
            return false;
        }

        if (!WlrootsProcess.CanRun("grim", "-h"))
        {
            reason = $"grim is not available. {LinuxPackageHint}";
            Logger.Information("wlroots unavailable: {Reason}", reason);
            return false;
        }

        if (!WlrootsProcess.CanRun("wlr-randr", "--json") &&
            !WlrootsProcess.CanRun("swaymsg", "-t", "get_outputs") &&
            !WlrootsProcess.CanRun("hyprctl", "monitors", "-j"))
        {
            reason = $"no wlroots output enumeration tool is available. {LinuxPackageHint}";
            Logger.Information("wlroots unavailable: {Reason}", reason);
            return false;
        }

        reason = null;
        Logger.Information("wlroots support checks passed");
        return true;
    }

    private const string LinuxPackageHint =
        "Install packages roughly equivalent to: Arch: grim wlr-randr or sway/hyprland; " +
        "Debian/Ubuntu: grim wlr-randr or sway; Fedora: grim wlr-randr or sway/hyprland.";

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
            if (_captures.TryGetValue(display, out WlrootsScreenCapture? capture))
                return capture;

            WlrootsOutput output = _outputs.FirstOrDefault(candidate => candidate.Display == display)
                ?? throw new ArgumentException($"Unknown wlroots display '{display.DeviceName}'.", nameof(display));
            Logger.Information("Creating wlroots screen capture for {Display}", output.Display.DeviceName);
            capture = new WlrootsScreenCapture(output);
            _captures[display] = capture;
            return capture;
        }
    }

    public void Dispose()
    {
        foreach (WlrootsScreenCapture capture in _captures.Values)
            capture.Dispose();
        _captures.Clear();
    }

    private IEnumerable<WlrootsOutput> EnumerateOutputs()
    {
        Func<IEnumerable<WlrootsOutput>>[] providers =
        [
            EnumerateWlrRandrOutputs,
            EnumerateSwayOutputs,
            EnumerateHyprlandOutputs
        ];

        foreach (Func<IEnumerable<WlrootsOutput>> provider in providers)
        {
            IEnumerable<WlrootsOutput> outputs;
            try
            {
                outputs = provider().ToList();
            }
            catch
            {
                continue;
            }

            if (outputs.Any())
                return outputs;
        }

        return [];
    }

    private IEnumerable<WlrootsOutput> EnumerateWlrRandrOutputs()
    {
        string json = WlrootsProcess.RunText("wlr-randr", ["--json"], TimeSpan.FromSeconds(2));
        using JsonDocument document = JsonDocument.Parse(json);
        int index = 0;
        foreach (JsonElement output in document.RootElement.EnumerateArray())
        {
            if (!GetBool(output, "enabled", true))
                continue;

            string? name = GetString(output, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            JsonElement? mode = output.TryGetProperty("modes", out JsonElement modes)
                ? modes.EnumerateArray().FirstOrDefault(candidate => GetBool(candidate, "current", false))
                : null;
            if (mode == null)
                continue;

            int width = GetInt(mode.Value, "width");
            int height = GetInt(mode.Value, "height");
            if (width <= 0 || height <= 0)
                continue;

            int x = 0;
            int y = 0;
            if (output.TryGetProperty("position", out JsonElement position))
            {
                x = GetInt(position, "x");
                y = GetInt(position, "y");
            }

            yield return CreateOutput(index++, name, width, height, x, y);
        }
    }

    private IEnumerable<WlrootsOutput> EnumerateSwayOutputs()
    {
        string json = WlrootsProcess.RunText("swaymsg", ["-t", "get_outputs"], TimeSpan.FromSeconds(2));
        using JsonDocument document = JsonDocument.Parse(json);
        int index = 0;
        foreach (JsonElement output in document.RootElement.EnumerateArray())
        {
            if (!GetBool(output, "active", false))
                continue;

            string? name = GetString(output, "name");
            if (string.IsNullOrWhiteSpace(name) || !output.TryGetProperty("rect", out JsonElement rect))
                continue;

            int width = GetInt(rect, "width");
            int height = GetInt(rect, "height");
            if (width <= 0 || height <= 0)
                continue;

            yield return CreateOutput(index++, name, width, height, GetInt(rect, "x"), GetInt(rect, "y"));
        }
    }

    private IEnumerable<WlrootsOutput> EnumerateHyprlandOutputs()
    {
        string json = WlrootsProcess.RunText("hyprctl", ["monitors", "-j"], TimeSpan.FromSeconds(2));
        using JsonDocument document = JsonDocument.Parse(json);
        int index = 0;
        foreach (JsonElement output in document.RootElement.EnumerateArray())
        {
            if (GetBool(output, "disabled", false))
                continue;

            string? name = GetString(output, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            int width = GetInt(output, "width");
            int height = GetInt(output, "height");
            if (width <= 0 || height <= 0)
                continue;

            yield return CreateOutput(index++, name, width, height, GetInt(output, "x"), GetInt(output, "y"));
        }
    }

    private WlrootsOutput CreateOutput(int index, string name, int width, int height, int x, int y)
    {
        var display = new Display(index, name, width, height, default, _graphicsCard);
        return new WlrootsOutput(display, x, y);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out bool value) => value,
            _ => fallback
        };
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return 0;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int intValue))
            return intValue;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double doubleValue))
            return (int)Math.Round(doubleValue);

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), CultureInfo.InvariantCulture, out double parsedValue))
            return (int)Math.Round(parsedValue);

        return 0;
    }
}
