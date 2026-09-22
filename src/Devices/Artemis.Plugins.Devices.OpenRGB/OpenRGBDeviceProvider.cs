using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Artemis.Core;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Services;
using RGB.NET.Core;
using RGB.NET.Devices.OpenRGB;
using Serilog;
using RGBDeviceProvider = RGB.NET.Devices.OpenRGB.OpenRGBDeviceProvider;
using Timer = System.Timers.Timer;

namespace Artemis.Plugins.Devices.OpenRGB;

[PluginFeature(Name = "OpenRGB Device Provider")]
public class OpenRGBDeviceProvider : DeviceProvider
{
    private readonly ILogger _logger;
    private readonly IDeviceService _deviceService;
    private readonly PluginSetting<List<OpenRGBServerDefinition>> _deviceDefinitionsSettings;
    private readonly PluginSetting<bool> _forceAddAllDevicesSetting;
    private readonly Timer _refreshTimer;
    private readonly Timer _reconnectTimer;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly HashSet<OpenRGBServerDefinition> _detectingServers = [];
    private readonly Dictionary<string, string> _sdkStatuses = [];
    private readonly object _stateLock = new();
    private volatile bool _running;

    public OpenRGBDeviceProvider(IDeviceService deviceService, PluginSettings settings, ILogger logger)
    {
        _logger = logger;
        _deviceService = deviceService;
        _forceAddAllDevicesSetting = settings.GetSetting("ForceAddAllDevices", false);
        _deviceDefinitionsSettings = settings.GetSetting("DeviceDefinitions", new List<OpenRGBServerDefinition>
        {
            new() { ClientName = "Artemis", Ip = "127.0.0.1", Port = 6742 }
        });

        CreateMissingLedsSupported = false;
        RemoveExcessiveLedsSupported = true;
        _refreshTimer = new Timer(1500) { AutoReset = false };
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _reconnectTimer = new Timer(5000) { AutoReset = false };
        _reconnectTimer.Elapsed += OnReconnectTimerElapsed;
    }

    public override RGBDeviceProvider RgbDeviceProvider => RGBDeviceProvider.Instance;

    public override string GetDeviceIdentifier(IRGBDevice device) =>
        device is IOpenRGBDevice openRgbDevice ? openRgbDevice.PersistentId : base.GetDeviceIdentifier(device);

    public string SdkStatus
    {
        get
        {
            lock (_stateLock)
                return _sdkStatuses.Count == 0
                    ? "SDK status: not connected"
                    : "SDK status: " + string.Join("; ", _sdkStatuses.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} — {pair.Value}"));
        }
    }

    public override void Enable()
    {
        _running = true;
        RGBDeviceProvider provider = RgbDeviceProvider;

        provider.Exception += Provider_OnException;
        provider.DeviceListUpdated += ProviderOnDeviceListUpdated;
        provider.ConnectionLost += ProviderOnConnectionLost;
        provider.DetectionStarted += ProviderOnDetectionStarted;
        provider.DetectionEnded += ProviderOnDetectionEnded;

        foreach (OpenRGBServerDefinition definition in _deviceDefinitionsSettings.Value)
            provider.DeviceDefinitions.Add(definition);
        provider.ForceAddAllDevices = _forceAddAllDevicesSetting.Value;

        try
        {
            _deviceService.AddDeviceProvider(this);
        }
        catch
        {
            DetachProviderEvents(provider);
            provider.Dispose();
            _running = false;
            throw;
        }

        UpdateStatuses();
        if (provider.DeviceDefinitions.Any(definition => !definition.Connected))
            _reconnectTimer.Start();
    }

    public override void Disable()
    {
        _running = false;
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        RGBDeviceProvider provider = RgbDeviceProvider;

        try
        {
            _deviceService.RemoveDeviceProvider(this);
        }
        finally
        {
            DetachProviderEvents(provider);
            provider.Dispose();
            lock (_stateLock)
            {
                _detectingServers.Clear();
                _sdkStatuses.Clear();
            }
        }
    }

    private void DetachProviderEvents(RGBDeviceProvider provider)
    {
        provider.Exception -= Provider_OnException;
        provider.DeviceListUpdated -= ProviderOnDeviceListUpdated;
        provider.ConnectionLost -= ProviderOnConnectionLost;
        provider.DetectionStarted -= ProviderOnDetectionStarted;
        provider.DetectionEnded -= ProviderOnDetectionEnded;
    }

    private void Provider_OnException(object sender, ExceptionEventArgs args) =>
        _logger.Debug(args.Exception, "OpenRGB Exception: {message}", args.Exception.Message);

    private void ProviderOnDetectionStarted(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            _detectingServers.Add(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "device detection in progress";
        }
    }

    private void ProviderOnDetectionEnded(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
            _detectingServers.Remove(definition);
        QueueReconcile();
    }

    private void ProviderOnDeviceListUpdated(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            if (_detectingServers.Contains(definition))
                return;
        }

        QueueReconcile();
    }

    private void ProviderOnConnectionLost(OpenRGBServerDefinition definition)
    {
        definition.Connected = false;
        lock (_stateLock)
        {
            _detectingServers.Remove(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "disconnected";
        }
        _reconnectTimer.Start();
    }

    private void QueueReconcile()
    {
        if (!_running)
            return;

        // OpenRGB emits several list changes while a detection pass is settling. Wait
        // for a short quiet period so Artemis never sees an intermediate controller list.
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e) => await ReconcileDeviceList();

    private async Task ReconcileDeviceList()
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            if (!RgbDeviceProvider.TryRefreshDevices())
                _reconnectTimer.Start();
            UpdateStatuses();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            bool repaired = RgbDeviceProvider.DeviceDefinitions.Any(definition => !definition.Connected)
                ? RgbDeviceProvider.TryReconnectDevices()
                : RgbDeviceProvider.TryRefreshDevices();
            if (!repaired)
            {
                _reconnectTimer.Start();
                return;
            }

            UpdateStatuses();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void UpdateStatuses()
    {
        lock (_stateLock)
        {
            foreach (OpenRGBServerDefinition definition in RgbDeviceProvider.DeviceDefinitions)
                _sdkStatuses[GetDefinitionKey(definition)] = definition.Connected
                    ? $"protocol v{RgbDeviceProvider.ProtocolVersionFor(definition)}, connected"
                    : $"disconnected{(string.IsNullOrWhiteSpace(definition.LastError) ? string.Empty : $": {definition.LastError}")}";
        }
    }

    private static string GetDefinitionKey(OpenRGBServerDefinition definition) => $"{definition.Ip}:{definition.Port}";
}
