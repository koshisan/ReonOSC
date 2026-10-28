# Heated socks BLE protocol (DRCODC app)

Notes for adding a pair of generic app-controlled heated socks to ReonOSC.
The protocol was read out of the vendor app (DRCODC 1.3.6, a uni-app
bundle, `__UNI__A9ACCF5`) and checked against the real socks on
2026-10-27.

Status: **verified** = seen working on the device, **from app code** =
read from the APK, not yet confirmed on the device.

## Hardware

- Sold on Amazon as "Electric Heated Socks, 10,000 mAh, Temperature
  Controlled by App, 35–70 °C" (brand "Generic").
- Each sock has its own controller in the cuff (button + two rows of four
  LEDs: battery and temperature). The battery pack is only a power bank; on
  its own it switches off and does not advertise.
- One BLE peripheral per sock. The app remembers two device IDs and sends
  every command to both.
- Chip: JieLi AC632N.

| Sock | Address |
|---|---|
| a | `ED:88:26:79:BF:B4` |
| b | `33:3B:4C:46:D1:76` |

## Advertising (verified)

- Local name: `AC632N_1(BLE)`
- Service UUID: `0000af30-0000-1000-8000-00805f9b34fb`
- Manufacturer data: company `0x05D6`, payload `08 00 4a 4c 41 49 53 44 4b`
  (`"JLAISDK"`)

The app accepts a device if the first advertised service UUID is `AF30` or
the name is `DSJM` or `AC632N_1(BLE)`.

## GATT (verified)

| Service | Characteristic | Properties | Use |
|---|---|---|---|
| `FFF0` | `FFF2` | write | commands |
| `FFF0` | `FFF1` | notify | replies |
| `AE00` | `AE01` / `AE02` | write-no-rsp / notify | JieLi OTA/RCSP, ignore |

`FFF2` must be written **with response**. Writes without response are
silently dropped.

No pairing, bonding or handshake. The `Send_` handshake with a CRC16 in the
app code is never called.

## Frame format

```
host → sock:  AA  cmd  len  data…  sum  55
sock → host:  BB  cmd  len  data…  sum  55

sum = (cmd + len + data…) & 0xFF
```

## Commands

| Command | Frame | Notes | Status |
|---|---|---|---|
| Set temperature | `AA 01 01 TT (01+01+TT) 55` | `TT` = target °C, 35–70 | verified |
| Heating off | `AA 01 01 00 02 55` | same command with 0 | verified |
| Timer | `AA 02 01 MM (02+01+MM) 55` | `MM` = minutes, 0 = no timer; the app sends 0 for anything ≤ 15 | from app code |
| Battery query | `AA 04 00 04 55` | the app polls every 3 s | verified |

Examples:

```
50 °C  AA 01 01 32 34 55
70 °C  AA 01 01 46 48 55
off    AA 01 01 00 02 55
```

The app's "Quick heater" switch (快速加热) is not a separate mode. It just
sends the normal temperature frame: on → 70 °C, off → 50 °C. It also
moves the slider to that value (from app code).

The sock does not reply to temperature or timer writes. The temperature LEDs
change immediately. 40 °C seems to light no LED, so use ≥ 50 °C for
visual tests.

There is no power-off command. "Off" in the app is the temperature 0 frame.
The cuff button changes the setting locally; it could not switch the socks
off during testing. What exactly it cycles through, and whether it sends a
notify, is not known yet.

## Notifications

| Frame | Meaning | Status |
|---|---|---|
| `BB 01 01 VV sum 55` | battery **voltage** in 0.1 V (`VV`, e.g. `0x25` = 3.7 V) | verified |

The battery byte is not a percentage, even though the vendor app code
calls it one. Over an hour of heating it fell from 39 to 34 on both socks,
jittering ±1 as the heater switched (load sag), while the cuff showed 1–2
of 4 battery LEDs. That fits a single Li-ion cell at 3.9 → 3.4 V. ReonOSC
reports the voltage and a rough linear estimate (3.3 V = 0 %,
4.1 V = 100 %), which moves in ~12 % steps at this resolution.

The app treats every notify with byte 1 = `01` as a battery report and only
logs everything else. During testing nothing else arrived: **the socks do
not report their actual temperature.**

## Discovery on Windows

The socks' advertisements come in slowly on Windows: about once per 30 s
at best, sometimes not for several minutes, while an Android phone finds
both instantly. Confirmed on two separate hosts with different BLE
dongles (Barrot BT 5.4 USB on a test laptop, and the gaming PC with its
own built-in adapter), so this is a Windows/WinRT thing, not a dongle
quirk. Once connected, writes take effect immediately.

A worse side effect of the same thing: `FromBluetoothAddressAsync`
returns null whenever the OS cache doesn't currently hold the device —
notably right after a reboot, before the first advertisement comes in.
So the client should:

- connect once and keep the connection open,
- reconnect in the background on disconnect,
- scan for the specific MAC before each open attempt, so the OS cache
  gets populated and `FromBluetoothAddressAsync` has something to
  return,
- not depend on a fresh scan for each command.

## Cloud

The vendor backend (`*.ble.sunny-tech.com`) is only used for a remote
partner control feature over a WebSocket. Local control does not touch it.

## Thermal behaviour

Classic heating wires, no active cooling. Worn, the heat is clearly felt
about **40–60 s** after switching on (tested with the vendor app).
Cool-down has not been measured yet.

## Open questions

- Cool-down time.
- What the cuff button cycles through, and whether it reports changes.
- Timer command on the device.
