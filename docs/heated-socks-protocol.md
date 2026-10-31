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
  every command to all selected socks (both by default; tapping a sock
  tile deselects it).
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
reports the voltage and a percentage using the same five steps as the
vendor app's battery bar (from app code):

| `VV` (decimal) | App bar |
|---|---|
| ≥ 39 | 100 % |
| 37–38 | 75 % |
| 35–36 | 50 % |
| 30–34 | 25 % |
| < 30 | 0 % (red) |

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

## Connection handling in the vendor app (from app code)

How the app manages the BLE link, for comparison. All of this comes from
`pages/index/index.vue` and `common/utils/bluetooth.js` in the bundle.
None of it is verified on the device. The app doesn't do much: it sets no
MTU, no connection priority and no connection parameters, and there is no
keep-alive apart from the battery poll.

### Startup and discovery

1. 100 ms after the page loads: reset the adapter and start a scan
   (`Refresh`, see below). After that, every 2.5 s: if the adapter is still
   not open, run `Refresh` again.
2. `Refresh` closes the adapter, which drops all links, opens it again and
   starts discovery 500 ms later. If starting discovery fails, it retries
   every 1.5 s.
3. Discovery uses the defaults: no duplicate reports, no interval, and it
   keeps running until the user connects by hand. Each device is reported
   once and filtered by `AF30` / name.
4. Auto-connect: once **both** stored IDs (`deviceId0`, `deviceId1`) have
   been seen, it connects to them. With only one sock found, it does not
   auto-connect.
5. On Android the app insists on location (GPS) being enabled. While the
   page is visible it keeps the screen on (`setWakelock`).

Long-pressing a sock tile runs `Refresh` without auto-connect, to pick the
sock again.

### Connect sequence

- The socks are connected **one after the other**, each with
  `createBLEConnection` and a **10 s timeout**.
- If either fails, the app shows "connection failed, retry" and stops. It
  does not retry on its own.
- After the last connect: wait **1100 ms on Android** (100 ms on iOS), then
  run `init`.
- `init` handles each sock with a **550 ms × index** offset (0 ms, 550 ms):
  get services → get characteristics of `FFF0` → enable notify on `FFF1` →
  send the battery query straight away.

### Commands

- Every command goes out **200 ms** after the UI action, as a write to
  `FFF2` on every selected sock. There is no queue, no debounce and no
  pacing between the socks. While the slider is being dragged, every
  change is sent.
- The battery query runs every **3 s** while the page is visible. It
  stops when the page is hidden and runs even when no sock is connected.

### Disconnect

- `onBLEConnectionStateChange` with `connected = false` → a "Disconnect"
  toast, and that sock is marked as not connected.
- If **no** sock is connected any more, the power switch in the UI goes
  to off and the timer back to 15 min. Nothing is sent to the socks.
- **There is no automatic reconnect** on a disconnect event.

### Reconnect (lazy, on the next write)

The app only reconnects when a write fails:

1. Write to `FFF2` fails →
2. `createBLEConnection` with a **4 s timeout** (not 10 s as on first
   connect) →
3. get characteristics of `FFF0`, enable notify on `FFF1` →
4. send the **same command again**.

If the reconnect fails too, it is only logged. The command is lost until
the next UI action or battery poll triggers another attempt. Because of the
3 s battery poll, a dropped sock is in practice retried about every 3 s.
There is no backoff and no retry limit.

### Timer

The timer slider sends command `02` to the sock and also starts a local
timer in the app. When that runs out, the app only resets its own UI
(switch off, timer 15 min) and sends nothing. Switching off at the end of
the timer is left to the sock (from app code, not verified).

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
