# OpenRGB Devices v6 — 2026.0923.1349 (test build)

This plugin build pairs with the Artemis device-hotplug recovery branch. It is
for local testing, not an official release.

## Changes

- Recover the same logical Artemis device when OpenRGB replaces its runtime
  location, using a provider-defined signature instead of hardware-specific
  VID/PID rules. Ambiguous devices are not merged.
- Preserve split motherboard zone identities and existing layer bindings across
  rescans and reconnects when their SDK-reported identity remains provable.
- Stop automatically requesting Direct mode for every OpenRGB device found at
  startup or reconnect.
- Add **Re-request Direct mode for devices with active layers** (enabled by
  default). When a trusted refresh reports a used SDK device outside Direct
  mode, request Direct for that individual device ID only. Unused mice,
  keyboards, and other SDK devices are left alone.
- If OpenRGB exposes one SDK device as several Artemis zones, its mode is shared:
  an active layer on any child makes that SDK device eligible for the request.
  The SDK cannot switch a single zone independently.
- Continue to wait for detection completion and a stable reduced device list
  before publishing removals to Artemis.

## Validation and limits

- Plugin compiles in Release; all 7 OpenRGB SDK tests pass.
- Matching Artemis core hotplug tests: 18 passed.
- Physical hotplug and Direct-mode recovery with this specific plugin build
  still need to be confirmed by the tester.
- A missing device with a signature shared by another missing device is not
  auto-merged. OpenRGB devices without an advertised Direct mode are not
  switched by this setting.
