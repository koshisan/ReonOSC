"""BLE <-> MQTT bridge for the DRCODC heated socks.

Runs on a Linux box near the socks (here: the PiKVM) and holds the BLE links,
because Windows kept dropping one sock every few minutes while BlueZ held the
same sock with the same link parameters without a single drop. ReonOSC talks
to this bridge over MQTT instead of using Windows Bluetooth.

Topics (base defaults to "sockbridge", <id> = MAC without colons, lower case):
  <base>/availability   online / offline (retained, last will)
  <base>/<id>/state     JSON, retained: connected, health, battery_v, target
  <base>/<id>/set       JSON {"target": <deg C, 0 = off>} from ReonOSC

Safety: the bridge only applies targets it was sent. Until the first command,
and whenever no command arrived for `command_timeout_s` (ReonOSC repeats its
target as a heartbeat), the target is 0 = heating off.
"""

import asyncio
import json
import signal
import sys
import time

import paho.mqtt.client as mqtt
from bleak import BleakClient, BleakScanner
from bleak.backends.bluezdbus.manager import get_global_bluez_manager
from bleak.backends.device import BLEDevice

NOTIFY = "0000fff1-0000-1000-8000-00805f9b34fb"
WRITE = "0000fff2-0000-1000-8000-00805f9b34fb"
TEMP_MIN, TEMP_MAX = 35, 70


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def frame(cmd, data):
    body = bytes([cmd, len(data), *data])
    return bytes([0xAA]) + body + bytes([sum(body) & 0xFF, 0x55])


def set_temp_frame(t):
    if t:
        t = max(TEMP_MIN, min(TEMP_MAX, t))
    return frame(0x01, [t])


BATTERY_QUERY = frame(0x04, [])


class Sock:
    def __init__(self, bridge, mac):
        self.bridge = bridge
        self.mac = mac.upper()
        self.id = self.mac.replace(":", "").lower()
        self.path = "/org/bluez/hci0/dev_" + self.mac.replace(":", "_")
        self.client = None
        self.connected = False
        self.health = "connecting"
        self.battery_v = None
        self.desired = 0          # what ReonOSC asked for (0 until it does)
        self.last_cmd = 0.0       # monotonic time of the last command
        self.sent = None          # last target written to the sock
        self.seen = asyncio.Event()
        self.last_advert = 0.0
        self.down_since = time.monotonic()
        self.fails = 0

    # -- target handling ---------------------------------------------------

    def effective_target(self):
        if time.monotonic() - self.last_cmd > self.bridge.command_timeout:
            return 0
        return self.desired

    def on_command(self, target):
        self.desired = max(0, min(TEMP_MAX, int(target)))
        self.last_cmd = time.monotonic()
        asyncio.ensure_future(self.apply_target())   # now, not on the next tick

    async def write(self, data):
        try:
            await self.client.write_gatt_char(WRITE, data, response=True)
            return True
        except Exception as e:
            log(self.id, f"write failed: {e}")
            return False

    async def apply_target(self, force=False):
        t = self.effective_target()
        if self.connected and (force or t != self.sent):
            if await self.write(set_temp_frame(t)):
                if t != self.sent:
                    log(self.id, f"target {t} °C" if t else "target off")
                self.sent = t
                self.publish()

    # -- state --------------------------------------------------------------

    def publish(self):
        self.bridge.publish(f"{self.id}/state", {
            "mac": self.mac,
            "connected": self.connected,
            "health": self.health,
            "battery_v": self.battery_v,
            "target": self.sent if self.connected else None,
        })

    def on_notify(self, _, data):
        if len(data) >= 6 and data[0] == 0xBB and data[1] == 0x01 and data[2] == 1:
            v = data[3] / 10
            if 2.5 <= v <= 4.5 and v != self.battery_v:
                self.battery_v = v
                self.publish()

    # -- link -----------------------------------------------------------------

    async def os_connected(self):
        """True if BlueZ already holds a link to this sock: a connected sock
        sends no adverts, so waiting for one would wait forever."""
        try:
            return (await get_global_bluez_manager()).is_connected(self.path)
        except Exception:
            return False

    async def bluez_knows(self):
        """True if BlueZ has a device object for this sock (it has been seen
        before; TemporaryTimeout=0 keeps it after a disconnect)."""
        try:
            (await get_global_bluez_manager()).get_device_address(self.path)
            return True
        except Exception:
            return False

    async def run(self):
        while True:
            attach = await self.os_connected()
            # A known device can be connected without waiting for an advert:
            # BlueZ keeps the connection request pending in the controller,
            # which then connects on the very first advert. Waiting for an
            # advert first and connecting afterwards always misses that one
            # and needs the next, and one sock sends only every 10-40 s.
            known = attach or await self.bluez_knows()
            if not known:
                # Wait for an advert from the scanner. One sock advertises
                # only every 10-40 s (sometimes minutes), so the scanner runs
                # all the time and we connect right after it hears one.
                self.seen.clear()
                down = time.monotonic() - self.down_since
                if down > 300 and self.health != "absent" and time.monotonic() - self.last_advert > 180:
                    self.health = "absent"
                    self.publish()
                try:
                    await asyncio.wait_for(self.seen.wait(), timeout=30)
                except asyncio.TimeoutError:
                    continue
            gone = asyncio.Event()
            try:
                async with self.bridge.connect_lock:
                    if not attach:
                        await self.bridge.scanner_stop()
                    try:
                        # With BlueZ's object path, bleak connects (or attaches
                        # to an existing link) without scanning first.
                        dev = (BLEDevice(self.mac, None, {"path": self.path, "props": {}}) if known
                               else self.bridge.devices.get(self.mac) or self.mac)
                        # Held under connect_lock: one pending LE connection at
                        # a time, so a second down sock gets its turn every 30 s.
                        self.client = BleakClient(dev, disconnected_callback=lambda _: gone.set(),
                                                  timeout=30)
                        await self.client.connect()
                    finally:
                        if not known:
                            await self.bridge.scanner_start()
                if attach:
                    log(self.id, "attached to existing link")
                await self.client.start_notify(NOTIFY, self.on_notify)
            except Exception as e:
                self.fails += 1
                if self.fails == 1 or self.fails % 10 == 0:
                    log(self.id, f"connect attempt {self.fails} failed: {type(e).__name__}: {e}")
                await asyncio.sleep(0.5)
                continue

            log(self.id, f"connected after {time.monotonic() - self.down_since:.0f} s ({self.fails} failed attempts)")
            self.fails = 0
            self.connected, self.health, self.sent = True, "connected", None
            await self.apply_target(force=True)
            await self.write(BATTERY_QUERY)
            tick = asyncio.create_task(self.tick(gone))
            await gone.wait()
            tick.cancel()
            up_min = (time.monotonic() - self.down_since) / 60
            self.connected, self.health, self.sent = False, "connecting", None
            self.down_since = time.monotonic()
            log(self.id, f"disconnected (link lasted {up_min:.1f} min)")
            self.publish()

    async def tick(self, gone):
        n = 0
        while not gone.is_set():
            await asyncio.sleep(5)
            n += 1
            await self.apply_target()                 # picks up command timeouts
            if n % 6 == 0:                            # every 30 s
                await self.write(BATTERY_QUERY)
            if n % 12 == 0:                           # every 60 s: re-assert the
                await self.apply_target(force=True)   # target (cuff button etc.)


class Bridge:
    def __init__(self, cfg):
        self.base = cfg.get("base_topic", "sockbridge")
        self.command_timeout = cfg.get("command_timeout_s", 120)
        self.socks = {s.mac: s for s in (Sock(self, m) for m in cfg["socks"])}
        self.by_id = {s.id: s for s in self.socks.values()}
        self.connect_lock = asyncio.Lock()
        self.devices = {}
        self.scanner = BleakScanner(detection_callback=self.on_advert, scanning_mode="active")
        self.scanning = False
        self.loop = None

        self.mqtt = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=f"sockbridge-{int(time.time())}")
        if cfg.get("mqtt_username"):
            self.mqtt.username_pw_set(cfg["mqtt_username"], cfg.get("mqtt_password"))
        self.mqtt.will_set(f"{self.base}/availability", "offline", qos=1, retain=True)
        self.mqtt.on_connect = self.on_mqtt_connect
        self.mqtt.on_message = self.on_mqtt_message
        self.mqtt.reconnect_delay_set(1, 30)
        self.mqtt.connect_async(cfg["mqtt_host"], cfg.get("mqtt_port", 1883), keepalive=30)

    # -- MQTT (runs on paho's thread; hand over to asyncio) -------------------

    def on_mqtt_connect(self, client, _u, _f, rc, _p=None):
        log(f"mqtt connected ({rc})")
        client.publish(f"{self.base}/availability", "online", qos=1, retain=True)
        client.subscribe(f"{self.base}/+/set", qos=1)
        for s in self.socks.values():
            s.publish()

    def on_mqtt_message(self, _c, _u, msg):
        sock_id = msg.topic.split("/")[-2]
        try:
            payload = json.loads(msg.payload or b"{}")
            target = payload["target"] if isinstance(payload, dict) else payload
        except Exception:
            log(f"bad command on {msg.topic}: {msg.payload!r}")
            return
        sock = self.by_id.get(sock_id)
        if sock and self.loop:
            self.loop.call_soon_threadsafe(sock.on_command, target)

    def publish(self, sub, obj):
        self.mqtt.publish(f"{self.base}/{sub}", json.dumps(obj), qos=1, retain=True)

    # -- scanning ----------------------------------------------------------------

    def on_advert(self, device, _adv):
        sock = self.socks.get(device.address.upper())
        if sock:
            self.devices[sock.mac] = device
            sock.last_advert = time.monotonic()
            if sock.health == "absent":
                sock.health = "connecting"
                sock.publish()
            sock.seen.set()

    async def scanner_start(self):
        if not self.scanning:
            try:
                await self.scanner.start()
                self.scanning = True
            except Exception as e:
                log(f"scanner start failed: {e}")

    async def scanner_stop(self):
        if self.scanning:
            try:
                await self.scanner.stop()
            except Exception:
                pass
            self.scanning = False

    async def scanner_keeper(self):
        # Discovery is only needed for socks BlueZ doesn't know yet; known
        # ones are reconnected through a pending connection, and a discovery
        # scan would just compete with it for the radio.
        while True:
            async with self.connect_lock:
                need = [s for s in self.socks.values() if not s.connected and not await s.bluez_knows()]
                if need:
                    await self.scanner_start()
                else:
                    await self.scanner_stop()
            await asyncio.sleep(2)

    async def run(self):
        self.loop = asyncio.get_running_loop()
        # systemd stops us with SIGTERM; turn that into a cancellation so the
        # finally block below still switches the socks off.
        main = asyncio.current_task()
        self.loop.add_signal_handler(signal.SIGTERM, main.cancel)
        self.mqtt.loop_start()
        log("bridge up for", ", ".join(self.socks), f"(command timeout {self.command_timeout} s)")
        try:
            await asyncio.gather(self.scanner_keeper(), *(s.run() for s in self.socks.values()))
        finally:
            for s in self.socks.values():
                if s.connected:
                    await s.write(set_temp_frame(0))
            self.mqtt.publish(f"{self.base}/availability", "offline", qos=1, retain=True)
            self.mqtt.loop_stop()


if __name__ == "__main__":
    with open(sys.argv[1] if len(sys.argv) > 1 else "/opt/sockbridge/config.json") as f:
        config = json.load(f)
    try:
        asyncio.run(Bridge(config).run())
    except (KeyboardInterrupt, asyncio.CancelledError):
        pass
