"""BLE <-> MQTT bridge for the DRCODC heated socks.

Runs on a Linux box near the socks (here: the PiKVM) and holds the BLE links,
because Windows kept dropping one sock every few minutes while BlueZ held the
same sock with the same link parameters without a single drop. ReonOSC talks
to this bridge over MQTT instead of using Windows Bluetooth.

The bridge finds the socks itself and adopts at most `max_socks` (default 2).
An advert only makes a device a candidate (the name "AC632N_1(BLE)" is just
the chip SDK's default and shared by unrelated JieLi gadgets); it is adopted
only after a test connection shows the sock's GATT layout (FFF0 with FFF2
write + FFF1 notify) and it answers the battery query in the sock's frame
format. Nothing else is written before that, so a wrong device never heats.

Topics (base defaults to "sockbridge", <id> = MAC without colons, lower case):
  <base>/availability   online / offline (retained, last will)
  <base>/adopted        JSON list of adopted MACs (retained; the bridge's
                        memory, since the PiKVM's root file system is read-only)
  <base>/<id>/state     JSON, retained: mac, connected, health, battery_v, target
  <base>/<id>/set       JSON {"target": <deg C, 0 = off>} from ReonOSC
  <base>/<id>/forget    any payload: drop this sock so another can be adopted

Safety: the bridge only applies targets it was sent. A sock is left alone
until the first command, and switched off whenever no command arrived for
`command_timeout_s` (ReonOSC repeats its target as a heartbeat).
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

SERVICE = "0000fff0-0000-1000-8000-00805f9b34fb"
NOTIFY = "0000fff1-0000-1000-8000-00805f9b34fb"
WRITE = "0000fff2-0000-1000-8000-00805f9b34fb"
ADVERT_SERVICE = "0000af30-0000-1000-8000-00805f9b34fb"
ADVERT_NAMES = {"AC632N_1(BLE)", "DSJM"}   # the vendor app's filter
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


def battery_from_frame(data):
    """Volts from a sock battery report (BB 01 01 VV sum 55), else None."""
    if (len(data) >= 6 and data[0] == 0xBB and data[1] == 0x01 and data[2] == 1
            and data[4] == (data[1] + data[2] + data[3]) & 0xFF and data[5] == 0x55):
        v = data[3] / 10
        if 2.5 <= v <= 4.5:
            return v
    return None


def bluez_path(mac, adapter):
    return f"/org/bluez/{adapter}/dev_" + mac.upper().replace(":", "_")


class Sock:
    def __init__(self, bridge, mac):
        self.bridge = bridge
        self.mac = mac.upper()
        self.id = self.mac.replace(":", "").lower()
        self.path = bluez_path(self.mac, bridge.adapter)
        self.client = None
        self.task = None
        self.connected = False
        self.health = "connecting"
        self.battery_v = None
        # What ReonOSC asked for. None until it does: after a bridge restart
        # the sock is left alone (no off-blip) until a command arrives or the
        # command timeout passes, which then forces it off.
        self.desired = None
        self.last_cmd = time.monotonic()
        self.sent = None          # last target written to the sock
        self.seen = asyncio.Event()
        self.last_advert = 0.0
        self.down_since = time.monotonic()
        self.up_since = time.monotonic()
        self.fails = 0
        self.mode = "pending"

    # -- target handling ---------------------------------------------------

    def effective_target(self):
        """Target to write, or None to leave the sock as it is."""
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
        if t is None:
            return
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
        v = battery_from_frame(data)
        if v is not None and v != self.battery_v:
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
        """True if BlueZ has a device object for this sock."""
        try:
            (await get_global_bluez_manager()).get_device_address(self.path)
            return True
        except Exception:
            return False

    async def trust(self):
        # Experiment: BlueZ dropped the unpaired socks from its device list
        # after a disconnect (TemporaryTimeout = 0 didn't prevent it), which
        # forced the slow wait-for-advert path. Trusted devices may be kept.
        proc = await asyncio.create_subprocess_exec(
            "busctl", "set-property", "org.bluez", self.path, "org.bluez.Device1", "Trusted", "b", "true",
            stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.PIPE)
        _, err = await proc.communicate()
        if proc.returncode:
            log(self.id, f"trust failed: {err.decode().strip()}")

    async def run(self):
        while True:
            attach = await self.os_connected()
            # A known device can be connected without waiting for an advert:
            # BlueZ keeps the connection request pending in the controller,
            # which then connects on the very first advert. Waiting for an
            # advert first and connecting afterwards always misses that one
            # and needs the next, and one sock sends only every 10-40 s.
            known = attach or await self.bluez_knows()
            if not known and self.mode != "advert":
                log(self.id, "BlueZ doesn't know the sock: waiting for an advert")
            self.mode = "attach" if attach else "pending" if known else "advert"
            t0 = time.monotonic()
            if not known:
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
                                                  timeout=30, bluez={"adapter": self.bridge.adapter})
                        await self.client.connect()
                    finally:
                        self.bridge.wake_scanner()
                if attach:
                    log(self.id, "attached to existing link")
                await self.client.start_notify(NOTIFY, self.on_notify)
            except Exception as e:
                self.fails += 1
                # One line per attempt while a sock is down: the gaps between
                # attempts are what we need to see.
                log(self.id, f"attempt {self.fails} ({self.mode}) failed after "
                             f"{time.monotonic() - t0:.1f} s: {type(e).__name__}: {e}")
                await asyncio.sleep(0.5)
                continue

            log(self.id, f"connected after {time.monotonic() - self.down_since:.0f} s down "
                         f"({self.fails} failed attempts, last via {self.mode})")
            self.fails = 0
            self.up_since = time.monotonic()
            self.connected, self.health, self.sent = True, "connected", None
            await self.trust()
            await self.apply_target(force=True)
            await self.write(BATTERY_QUERY)
            tick = asyncio.create_task(self.tick(gone))
            relax = asyncio.create_task(self.relax_link(gone))
            try:
                await gone.wait()
            finally:
                tick.cancel()
                relax.cancel()
            up_min = (time.monotonic() - self.up_since) / 60
            self.connected, self.health, self.sent = False, "connecting", None
            self.down_since = time.monotonic()
            log(self.id, f"disconnected (link lasted {up_min:.1f} min); "
                         f"BlueZ still knows it: {await self.bluez_knows()}")
            self.publish()

    async def hci_handle(self):
        proc = await asyncio.create_subprocess_exec(
            "hcitool", "-i", self.bridge.adapter, "con",
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
        out, _ = await proc.communicate()
        for line in out.decode().splitlines():
            if self.mac in line.upper() and "handle" in line:
                return int(line.split("handle")[1].split()[0])
        return None

    async def relax_link(self, gone):
        """About 7 s after connecting the sock asks for interval 30 ms with
        latency 10 and a 6 s supervision timeout. With those, the weaker sock
        answered with seconds of delay and then went silent until the link
        timed out (btmon: Disconnect, reason Connection Timeout 0x08). As the
        central we may set the parameters back: latency 0 (it must listen at
        every event) and a 20 s timeout, so a short fade doesn't end the link.
        Done once: the sock asks only once, and every connection update is a
        moment where a weak link can be lost (a drop came 5 s after a
        repeated update)."""
        first = True
        await asyncio.sleep(10)
        while not gone.is_set() and first:
            handle = await self.hci_handle()
            if handle is None:
                log(self.id, "link update skipped: no HCI handle")
                return
            else:
                proc = await asyncio.create_subprocess_exec(
                    "hcitool", "-i", self.bridge.adapter, "lecup", str(handle), "24", "40", "0", "2000",
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
                out, err = await proc.communicate()
                msg = (out + err).decode().strip()
                if proc.returncode or first:
                    log(self.id, f"link update to 30-50 ms / latency 0 / 20 s timeout: "
                                 f"{'failed: ' + msg if proc.returncode else 'requested'}")
                first = False

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

    async def forget(self):
        if self.task:
            self.task.cancel()
        if self.client and self.connected:
            try:
                await self.write(set_temp_frame(0))
                await self.client.disconnect()
            except Exception:
                pass
        # An empty retained message deletes the retained state.
        self.bridge.mqtt.publish(f"{self.bridge.base}/{self.id}/state", b"", qos=1, retain=True)


class Bridge:
    def __init__(self, cfg):
        self.base = cfg.get("base_topic", "sockbridge")
        # BlueZ adapter to use, e.g. "hci1" for a USB dongle with a proper
        # antenna instead of the Pi's on-board chip.
        self.adapter = cfg.get("adapter", "hci0")
        self.command_timeout = cfg.get("command_timeout_s", 120)
        self.max_socks = cfg.get("max_socks", 2)
        self.socks = {}               # mac -> Sock
        self.by_id = {}               # id -> Sock
        self.rejected = set()         # MACs that turned out not to be socks
        self.retry_after = {}         # mac -> monotonic time; failed verification connects
        self.verifying = False
        self.adopted_loaded = asyncio.Event()
        self.connect_lock = asyncio.Lock()
        self.scanner_wakeup = asyncio.Event()
        self.devices = {}
        self.scanner = BleakScanner(detection_callback=self.on_advert, scanning_mode="active", bluez={"adapter": self.adapter})
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

    # -- adoption ----------------------------------------------------------------

    def add_sock(self, mac, reason):
        mac = mac.upper()
        if mac in self.socks:
            return
        s = Sock(self, mac)
        self.socks[mac], self.by_id[s.id] = s, s
        s.task = asyncio.ensure_future(s.run())
        log(s.id, f"adopted ({reason}); {len(self.socks)}/{self.max_socks} socks")
        self.publish_adopted()
        s.publish()

    async def remove_sock(self, sock_id):
        s = self.by_id.pop(sock_id, None)
        if not s:
            return
        self.socks.pop(s.mac, None)
        await s.forget()
        log(sock_id, f"forgotten; {len(self.socks)}/{self.max_socks} socks")
        self.publish_adopted()
        self.wake_scanner()

    def publish_adopted(self):
        self.publish("adopted", sorted(self.socks))

    def is_candidate(self, device, adv):
        uuids = [u.lower() for u in (adv.service_uuids or [])]
        return ADVERT_SERVICE in uuids or (adv.local_name or device.name) in ADVERT_NAMES

    async def verify(self, device):
        """Test connection to a candidate. Adopt it only if it has the sock's
        GATT layout and answers the battery query in the sock's format.
        Writes nothing but the battery query."""
        mac = device.address.upper()
        replies = []
        try:
            async with self.connect_lock:
                await self.scanner_stop()
                try:
                    # The connection can only happen on the sock's next advert,
                    # which can be a minute away; a short timeout plus a retry
                    # cooldown missed most of them.
                    client = BleakClient(device, timeout=90, bluez={"adapter": self.adapter})
                    await client.connect()
                finally:
                    self.wake_scanner()
            try:
                svc = client.services.get_service(SERVICE)
                chars = {c.uuid: c for c in svc.characteristics} if svc else {}
                w, n = chars.get(WRITE), chars.get(NOTIFY)
                if not (w and n and "notify" in n.properties
                        and ("write" in w.properties or "write-without-response" in w.properties)):
                    log(f"{mac}: no sock GATT layout — ignoring this device")
                    self.rejected.add(mac)
                    await client.disconnect()
                    return
                # One sock answered the first GATT request right after
                # connecting with ATT "Unlikely Error"; retry the steps once
                # and log which step it was.
                step = "?"
                for attempt in (1, 2):
                    try:
                        step = "enable notify (FFF1)"
                        await client.start_notify(NOTIFY, lambda _, d: replies.append(bytes(d)))
                        step = "battery query (FFF2)"
                        await client.write_gatt_char(WRITE, BATTERY_QUERY, response=True)
                        break
                    except Exception as e:
                        if attempt == 2:
                            raise
                        log(f"{mac}: {step} failed ({type(e).__name__}: {e}) — retrying in 1.5 s")
                        try:
                            await client.stop_notify(NOTIFY)
                        except Exception:
                            pass
                        await asyncio.sleep(1.5)
                for _ in range(30):
                    if any(battery_from_frame(r) is not None for r in replies):
                        break
                    await asyncio.sleep(0.1)
                await client.stop_notify(NOTIFY)
            except Exception as e:
                log(f"{mac}: verification failed at {step} ({type(e).__name__}: {e})")
                await client.disconnect()
                self.retry_after[mac] = time.monotonic() + 5
                return
            if not any(battery_from_frame(r) is not None for r in replies):
                log(f"{mac}: no sock battery reply ({[r.hex() for r in replies]}) — ignoring this device")
                self.rejected.add(mac)
                await client.disconnect()
                return
            # Keep the link: the new Sock attaches to it, so a sock that
            # advertises rarely doesn't have to be found a second time.
            if len(self.socks) >= self.max_socks:
                stale = self.stale_sock()
                if stale is None:
                    await client.disconnect()
                    return
                log(stale.id, f"replaced by {mac}: not heard for {self.STALE_AFTER // 60}+ min "
                              "(the socks come back under a new MAC after a power loss)")
                await self.remove_sock(stale.id)
            self.add_sock(mac, "verified")
        except Exception as e:
            log(f"{mac}: test connection failed ({type(e).__name__}: {e})")
            self.retry_after[mac] = time.monotonic() + 5
        finally:
            self.verifying = False

    # -- MQTT (runs on paho's thread; hand over to asyncio) -------------------

    def on_mqtt_connect(self, client, _u, _f, rc, _p=None):
        log(f"mqtt connected ({rc})")
        client.publish(f"{self.base}/availability", "online", qos=1, retain=True)
        client.subscribe(f"{self.base}/adopted", qos=1)
        client.subscribe(f"{self.base}/+/set", qos=1)
        client.subscribe(f"{self.base}/+/forget", qos=1)
        for s in list(self.socks.values()):
            s.publish()

    def on_mqtt_message(self, _c, _u, msg):
        if not self.loop:
            return
        parts = msg.topic.split("/")
        if msg.topic == f"{self.base}/adopted":
            self.loop.call_soon_threadsafe(self.on_adopted_list, msg.payload)
            return
        if len(parts) < 2:
            return
        sock_id, kind = parts[-2], parts[-1]
        if kind == "forget":
            asyncio.run_coroutine_threadsafe(self.remove_sock(sock_id), self.loop)
            return
        try:
            payload = json.loads(msg.payload or b"{}")
            target = payload["target"] if isinstance(payload, dict) else payload
        except Exception:
            log(f"bad command on {msg.topic}: {msg.payload!r}")
            return
        sock = self.by_id.get(sock_id)
        if sock:
            self.loop.call_soon_threadsafe(sock.on_command, target)

    def on_adopted_list(self, payload):
        # Our own retained list comes back on every subscribe; only the first
        # one after start matters (restoring the socks we had).
        if self.adopted_loaded.is_set():
            return
        try:
            for mac in json.loads(payload or b"[]")[: self.max_socks]:
                self.add_sock(mac, "remembered")
        except Exception:
            log(f"bad adopted list: {payload!r}")
        self.adopted_loaded.set()

    def publish(self, sub, obj):
        self.mqtt.publish(f"{self.base}/{sub}", json.dumps(obj), qos=1, retain=True)

    # -- scanning ----------------------------------------------------------------

    def on_advert(self, device, adv):
        mac = device.address.upper()
        sock = self.socks.get(mac)
        if sock:
            self.devices[mac] = device
            sock.last_advert = time.monotonic()
            if sock.health == "absent":
                sock.health = "connecting"
                sock.publish()
            sock.seen.set()
            return
        if (self.adopted_loaded.is_set() and not self.verifying and mac not in self.rejected
                and time.monotonic() >= self.retry_after.get(mac, 0)
                and self.is_candidate(device, adv)
                and (len(self.socks) < self.max_socks or self.stale_sock() is not None)):
            self.verifying = True
            log(f"{mac}: candidate (rssi {adv.rssi}) — test connection")
            asyncio.ensure_future(self.verify(device))

    STALE_AFTER = 600

    def stale_sock(self):
        """An adopted sock that has been neither connected nor heard for
        STALE_AFTER s. The socks come back under a new MAC after a power
        loss (seen in the field), so a full bridge would otherwise lock out
        the very same socks."""
        now = time.monotonic()
        stale = [s for s in self.socks.values()
                 if not s.connected and now - max(s.down_since, s.last_advert) > self.STALE_AFTER]
        return min(stale, key=lambda s: max(s.down_since, s.last_advert), default=None)

    def wake_scanner(self):
        self.scanner_wakeup.set()

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
        # Discovery runs while there is room for another sock or an adopted
        # one is down and unknown to BlueZ. Known ones reconnect through a
        # pending connection, which a discovery scan would only compete with.
        while True:
            async with self.connect_lock:
                need = len(self.socks) < self.max_socks
                for s in list(self.socks.values()):
                    if not s.connected and not await s.bluez_knows():
                        need = True
                if need:
                    await self.scanner_start()
                else:
                    await self.scanner_stop()
            self.scanner_wakeup.clear()
            try:
                await asyncio.wait_for(self.scanner_wakeup.wait(), timeout=2)
            except asyncio.TimeoutError:
                pass

    def set_initial_link_timeout(self):
        # New connections start with the kernel default supervision timeout
        # of 420 ms. On a fresh adapter the weaker sock then failed with
        # "Connection Failed to be Established" within a second; 20 s gives
        # it time until relax_link() settles the parameters.
        path = f"/sys/kernel/debug/bluetooth/{self.adapter}/supervision_timeout"
        try:
            with open(path, "w") as f:
                f.write("2000")
        except OSError as e:
            log(f"could not set {path}: {e}")

    async def run(self, initial):
        self.loop = asyncio.get_running_loop()
        self.set_initial_link_timeout()
        # systemd stops us with SIGTERM; turn that into a cancellation so the
        # finally block below still switches the socks off.
        main = asyncio.current_task()
        self.loop.add_signal_handler(signal.SIGTERM, main.cancel)
        self.mqtt.loop_start()
        for mac in initial[: self.max_socks]:
            self.add_sock(mac, "config")
        # Restore the socks we had before adopting anything new, so a
        # neighbour's sock can't take a slot after a restart.
        try:
            await asyncio.wait_for(self.adopted_loaded.wait(), timeout=10)
        except asyncio.TimeoutError:
            log("no adopted list from MQTT (first run or broker slow)")
            self.adopted_loaded.set()
        log(f"bridge up: {len(self.socks)}/{self.max_socks} socks, command timeout {self.command_timeout} s")
        try:
            await self.scanner_keeper()
        finally:
            for s in list(self.socks.values()):
                if s.connected:
                    await s.write(set_temp_frame(0))
            self.mqtt.publish(f"{self.base}/availability", "offline", qos=1, retain=True)
            self.mqtt.loop_stop()


if __name__ == "__main__":
    with open(sys.argv[1] if len(sys.argv) > 1 else "/opt/sockbridge/config.json") as f:
        config = json.load(f)
    try:
        asyncio.run(Bridge(config).run(config.get("socks", [])))
    except (KeyboardInterrupt, asyncio.CancelledError):
        pass
