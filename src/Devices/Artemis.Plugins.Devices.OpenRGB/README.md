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
- expose the negotiated protocol version in the plugin settings UI; and
- request Direct mode only for an individual OpenRGB SDK device that Artemis
  is using in an active layer.

It does not initiate OpenRGB hardware rescans, cache colors, replay effects, or
hardcode USB VID/PID values to infer identity.
USB backends that do not detect hotplug by themselves still require an OpenRGB
rescan.

## Identity contract

OpenRGB's numeric SDK device ID is a transport address and may be replaced on a
rescan. The primary Artemis identifier uses the OpenRGB `Location` when present,
namespaced by server, plus the exact RGB.NET split part:

```text
OpenRGB:<host>:<port>|Location:<location>|controller
OpenRGB:<host>:<port>|Location:<location>|zone:<index>
OpenRGB:<host>:<port>|Location:<location>|segment:<zone-index>:<segment-index>
```

If OpenRGB provides no location, the provider falls back to the current v6 SDK
device ID. The provider also supplies an opaque reconnection signature built
from SDK-reported server, manufacturer, model, device type, split-part identity,
and sorted LED IDs. Artemis persists that signature and identifier aliases. A
unique disconnected or stored-missing match reclaims its existing entity and
bindings when the runtime location changes. Ambiguous missing matches are left
unmerged, and an already connected device is never merged by signature with a
second concurrently present device of the same model.

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

## Direct-mode requests

The **Re-request Direct mode for devices with active layers** setting is on by
default. On a trusted SDK refresh, the plugin inspects each individual OpenRGB
SDK device. If it reports a non-Direct active mode, offers a `Direct` mode, and
at least one enabled Artemis device backed by that SDK device is used by an
enabled layer in an active profile, the plugin sends `UpdateMode` for that SDK
device ID only. It does not send a server-wide request, switch unrelated mice
or keyboards, or request Direct for an unused device. New devices are revisited
after Artemis attaches their layers; profile activation also queues a check.

One OpenRGB SDK device can be split into several Artemis zone/segment devices.
The SDK mode belongs to the OpenRGB device, not an individual zone: if any of
those Artemis children has an active layer, the mode request affects their
shared SDK device. A device with no advertised Direct mode is not switched;
the separate **Force using devices with no direct mode** setting only controls
whether such devices appear in Artemis.

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

When OpenRGB changes `Location`, the persisted reconnection signature can still
preserve bindings if it uniquely identifies the missing device. Changed LED
IDs remain a topology change and may leave affected layer bindings deferred.
