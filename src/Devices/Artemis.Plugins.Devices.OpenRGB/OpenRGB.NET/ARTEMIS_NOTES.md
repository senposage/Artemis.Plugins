# OpenRGB.NET protocol v6 fork

This directory is based on `diogotr7/OpenRGB.NET` commit
`442b448c2ee76c554923918723d47121b6256552` (version 3.1.1) and retains its MIT
license.

Artemis carries this source copy because the latest published OpenRGB.NET package
supports protocol v4, while OpenRGB 1.0 uses protocol v6. The local changes add:

- protocol v5/v6 payload parsing;
- protocol v6 stable controller-ID routing;
- tolerance for unsolicited v6 server packets; and
- a working `DeviceListUpdated` event for hotplug monitoring.

The public API and assembly version remain compatible with OpenRGB.NET 3.1.1 so
`RGB.NET.Devices.OpenRGB` can consume this implementation without modification.

Protocol reference:
https://github.com/CalcProgrammer1/OpenRGB/blob/master/Documentation/OpenRGBSDK.md
