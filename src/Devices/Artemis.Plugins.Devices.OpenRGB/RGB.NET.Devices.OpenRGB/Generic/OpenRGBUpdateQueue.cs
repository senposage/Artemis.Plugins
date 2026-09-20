using OpenRGB.NET;
using RGB.NET.Core;
using System;
using System.Linq;
using Color = RGB.NET.Core.Color;
using OpenRGBColor = OpenRGB.NET.Color;
using OpenRGBDevice = OpenRGB.NET.Device;

namespace RGB.NET.Devices.OpenRGB;

/// <inheritdoc />
/// <summary>
/// Represents the update-queue performing updates for OpenRGB devices.
/// </summary>
public sealed class OpenRGBUpdateQueue : UpdateQueue
{
    #region Properties & Fields

    private uint _controllerId;
    private readonly object _clientLock = new();
    private OpenRgbClient _openRGB;
    private readonly OpenRGBColor[] _colors;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenRGBUpdateQueue"/> class.
    /// </summary>
    /// <param name="updateTrigger">The update trigger used by this queue.</param>
    /// <param name="deviceId">The index used to identify the device.</param>
    /// <param name="client">The OpenRGB client used to send updates to the OpenRGB server.</param>
    /// <param name="device">The OpenRGB Device containing device-specific information.</param>
    public OpenRGBUpdateQueue(IDeviceUpdateTrigger updateTrigger, int deviceId, OpenRgbClient client, OpenRGBDevice device)
        : base(updateTrigger)
    {
        this._controllerId = client.GetControllerId(deviceId);
        this._openRGB = client;

        _colors = Enumerable.Range(0, device.Colors.Length)
                            .Select(_ => new OpenRGBColor())
                            .ToArray();
    }

    #endregion

    #region Methods

    internal bool UsesClient(OpenRgbClient client)
    {
        lock (_clientLock)
            return ReferenceEquals(_openRGB, client);
    }

    internal uint ControllerId
    {
        get
        {
            lock (_clientLock)
                return _controllerId;
        }
    }

    internal void ReplaceClient(OpenRgbClient client, uint controllerId)
    {
        lock (_clientLock)
        {
            _openRGB = client;
            _controllerId = controllerId;
        }
    }

    /// <inheritdoc />
    protected override bool Update(ReadOnlySpan<(object key, Color color)> dataSet)
    {
        try
        {
            foreach ((object key, Color color) in dataSet)
                _colors[(int)key] = new OpenRGBColor(color.GetR(), color.GetG(), color.GetB());

            lock (_clientLock)
            {
                if (!_openRGB.Connected)
                    return false;
                _openRGB.UpdateLedsByControllerId(_controllerId, _colors);
            }

            return true;
        }
        catch (Exception ex)
        {
            // A service restart is reported by the dedicated connection monitor.
            // Do not emit the same socket exception once per rendered frame.
            lock (_clientLock)
            {
                if (_openRGB.Connected)
                    OpenRGBDeviceProvider.Instance.Throw(ex);
            }
        }

        return false;
    }

    #endregion
}
