# ReonOSC

A Windows tray app that drives a **Sony Reon Pocket 3** via BLE in response to
OSC messages — for VRChat haptics, Pebble Feel compat, and friends. Built on
.NET 8 / WinForms with the WinRT Bluetooth APIs, so no external BLE drivers
or shims.

Companion to [reon-pocket-py](https://github.com/koshisan/reon-pocket-py)
(the Python BLE tool). Both share the same bond token storage location, so
pairing once via either tool works for both.

## Features

- Tray icon with show/hide/stop/exit menu
- Manual control GUI (mode + level)
- OSC UDP listener on a port picked in the GUI
- Configurable OSC address templates (defaults below)
- Two configurable preset levels for OSC bool triggers
- Auto-connect on start, optional start-to-tray
- Live telemetry display (plate / heatsink / board / ambient °C)

## OSC inputs (defaults)

| Address                  | Type  | Effect |
|---|---|---|
| `/PFHotHigh`             | bool  | When true → heat at "Heat Touch" preset level (compat with Shiftall Pebble Feel) |
| `/ChairOSC/v1/water`     | bool  | When true → cool at "Cold Water" preset level |
| `/ChairOSC/v1/cold`      | float | Mapped to cool level 0..3 by quartile (≤0.25→L0, ≤0.5→L1, ≤0.75→L2, else L3); 0.0 = no contribution |
| `/ChairOSC/v1/heat`      | float | Same mapping, heat side |

All four addresses can be rewritten in the GUI to match whatever your sender
emits (e.g. VRChat's `/avatar/parameters/...` prefix).

### Resolution policy

The device can only be in one direction at a time. If both heat and cool are
requested simultaneously, the **most-recently-changed source wins**. If a
source updates but its value is 0, that source no longer contributes. If
nothing is contributing, the device is stopped.

Manual override in the GUI takes priority over OSC inputs while enabled.

## Build & run

Requires:
- Windows 10 17763+ (for the WinRT BLE APIs)
- .NET 8 SDK
- A BLE adapter

```powershell
git clone https://github.com/koshisan/ReonOSC
cd ReonOSC
dotnet build -c Release
dotnet run --project ReonOSC
```

Or open `ReonOSC.sln` in Visual Studio 2022 (Community is fine) with the
".NET desktop development" workload installed.

## First run

1. Put the Reon in pair mode (long-press the device's button).
2. Click **Pair (device in pair mode)…** in the GUI.
3. The app scans for `RNP-3`, generates a fresh 17-byte token, writes it, and
   persists it to `%APPDATA%\reon\token.json`.
4. Click **Connect**.
5. Set the OSC port and click **Start**.

The Sony app will be locked out at this point because we overwrote the token.
If you want both clients to work, use `reon-pocket-py`'s `tools/parse_btsnoop.py`
to extract the Sony token, then pair here with that exact token instead of a
random one — see the Python repo for details.

## Settings file

`%APPDATA%\reon\reonosc.json`. Editable manually if the GUI's tougher
to reach (port conflicts, etc).

## Status

Alpha. Works end-to-end on a single test rig. PRs welcome.

## License

MIT.
