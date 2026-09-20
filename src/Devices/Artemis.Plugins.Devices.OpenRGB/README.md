# Artemis OpenRGB v6 device provider

This plugin connects Artemis to OpenRGB SDK servers using protocol v6. It vendors
the OpenRGB.NET and RGB.NET OpenRGB provider sources because their published
packages currently target protocol v4.

## Responsibilities

The plugin is deliberately limited to translating OpenRGB's reported state into
RGB.NET devices:

- negotiate and parse OpenRGB SDK protocol v6;
- listen for device-list and connection events;
- refresh RGB.NET devices after OpenRGB finishes detection;
- retry failed server connections without reloading the Artemis plugin; and
- expose the negotiated protocol version in the plugin settings UI.

It does not initiate OpenRGB hardware rescans, cache colors, replay effects, or
guess that two differently identified controllers are the same physical device.
USB backends that do not detect hotplug by themselves still require an OpenRGB
rescan.

## Identity contract

OpenRGB's numeric controller ID is a transport address and may be replaced on a
rescan. Artemis persistence therefore uses the exact controller `Location`,
namespaced by server, plus the exact RGB.NET split part:

```text
OpenRGB:<host>:<port>|Location:<location>|controller
OpenRGB:<host>:<port>|Location:<location>|zone:<index>
OpenRGB:<host>:<port>|Location:<location>|segment:<zone-index>:<segment-index>
```

If OpenRGB provides no location, the provider falls back to the current v6
controller ID. This fallback is intentionally not treated as persistent across
controller replacement. No name, model, position, IP, or topology heuristics are
used beyond the location value OpenRGB itself reports.

Within a controller, RGB.NET `LedId` values are authoritative. If OpenRGB returns
different LED IDs, Artemis treats the change as a real topology change.

## Lifecycle

`DeviceListUpdated`, `DetectionStarted`, `DetectionEnded`, and `ConnectionLost`
events drive reconciliation. Detection events suppress intermediate snapshots so
Artemis does not consume a partially rebuilt controller list. Failed operations
arm a one-shot five-second retry; each failure rearms it, so recovery has no fixed
attempt limit.

Artemis core owns logical device retention, layer bindings, rendering, and
database persistence. Those concerns must not be implemented in this plugin.

## Vendored sources

- [`OpenRGB.NET/ARTEMIS_NOTES.md`](OpenRGB.NET/ARTEMIS_NOTES.md) records the
  OpenRGB.NET base commit, protocol changes, and MIT license.
- [`RGB.NET.Devices.OpenRGB/ARTEMIS_NOTES.md`](RGB.NET.Devices.OpenRGB/ARTEMIS_NOTES.md)
  records the RGB.NET base tag, lifecycle changes, and LGPL-2.1-only license.

## Verification

The full plugin currently compiles against the companion Artemis core branch,
which adds provider-owned persistent identifiers. Check out `Artemis.Plugins`
and `Artemis.Upstream` as sibling directories and build Artemis core before
building the plugin.

Run the protocol and lifecycle tests:

```powershell
dotnet test src/Devices/Artemis.Plugins.Devices.OpenRGB/OpenRGB.NET.Tests/OpenRGB.NET.Tests.csproj -c Release
```

The end-to-end Windows test is:

1. Start Artemis and OpenRGB with the SDK server enabled.
2. Confirm the settings UI reports protocol v6.
3. Restart the OpenRGB service twice and verify effects recover each time.
4. Start an OpenRGB rescan and verify Artemis does not publish the partial list.
5. Unplug and reconnect a device, rescan if its OpenRGB backend requires it, and
   verify the device, layout, and layer bindings recover without restarting
   Artemis.

OpenRGB must report the same `Location` and LED IDs for binding preservation to
be provable. If either changes, remove/add behavior is expected.
