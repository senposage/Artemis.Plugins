# Artemis vendoring notes

This directory is based on `RGB.NET.Devices.OpenRGB` tag `v3.1.0` from
<https://github.com/RGB-D/RGB.NET> and remains licensed under LGPL-2.1-only.

Artemis-specific changes:

- `Reset()` disposes old OpenRGB SDK clients even when RGB.NET device teardown
  throws, so repeated reconnects do not retain dead sockets.
- `Dispose()` always releases the provider singleton. This prevents a failed
  teardown from making every later reload return the same disposed instance.
- The provider uses a guarded update trigger because RGB.NET 3.1's default
  trigger can retain an update exception and rethrow it from its `async void`
  `Stop()` during provider teardown (the failure investigated in Artemis PR
  #899).
- When protocol-v6 controller IDs are unchanged, SDK connections are replaced
  underneath the existing update queues. Artemis never sees the devices removed,
  preserving device-to-layer bindings across rescans and service restarts.
