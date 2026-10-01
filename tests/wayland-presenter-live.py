#!/usr/bin/env python3
"""Optional KDE session test. Creates only a temporary presenter and observer.
Requires dbus-python and PyGObject; pass the packaged presenter executable.
"""
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import dbus
import dbus.service
import dbus.mainloop.glib
from gi.repository import GLib

dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
loop = GLib.MainLoop()
snapshots = []
errors = []

class Receiver(dbus.service.Object):
    @dbus.service.method('org.kdotool.callback', in_signature='ss', out_signature='', async_callbacks=('done', 'failed'))
    def DesktopSnapshot(self, epoch, payload, done, failed):
        snapshots.append(json.loads(payload))
        GLib.timeout_add(16, lambda: (done(), False)[1])

    @dbus.service.method('org.kdotool.callback', in_signature='s', out_signature='', async_callbacks=('done', 'failed'))
    def DesktopTick(self, epoch, done, failed):
        GLib.timeout_add(16, lambda: (done(), False)[1])

receiver = Receiver(bus, '/')
scratch = Path(tempfile.mkdtemp(prefix='matee-presenter-live-', dir=os.environ.get('MATEE_TEST_SCRATCH_DIR', '/tmp')))
source = Path('Assets/MATE ENGINE - Scripts/APIs/Resources/MateeDesktopObserver.txt').read_text()
source = source.replace('__BUS__', bus.get_unique_name()).replace('__EPOCH__', 'presenter-test').replace('__PID__', str(os.getpid()))
source = source.replace('className:resourceClass,', 'layer:w.layer, className:resourceClass,')
script_path = scratch / 'observer.js'
script_path.write_text(source)
scripting = dbus.Interface(bus.get_object('org.kde.KWin', '/Scripting'), 'org.kde.kwin.Scripting')
name = 'MateePresenterTest_' + str(os.getpid())
script_names = []
player = None
sequence = 0
phase = -1
outputs = []
expected = None
stopping = False

def publish():
    global sequence
    if stopping or expected is None:
        return True
    sequence += 1
    output, x, y, flags = expected
    header = bytearray(256)
    header[:8] = b'MATEEFRM'
    struct.pack_into('<8I', header, 8, 3, 64, 64, 256, 1, 16384, 0, 0)
    struct.pack_into('<QQ', header, 40, sequence, 0)
    struct.pack_into('<ffffII', header, 56, x, y, 96, 80, flags, 0)
    encoded = output['name'].encode()
    header[80:80 + len(encoded)] = encoded
    r = output['rect']
    struct.pack_into('<fffff', header, 208, r['x'], r['y'], r['width'], r['height'], output['scale'])
    pixels = bytes([40, 190, 240, 255]) * (64 * 32) + bytes(64 * 32 * 4)
    path = scratch / 'frames'
    with path.open('wb') as frame:
        frame.write(header + pixels + bytes(len(pixels)))
    return True

def advance():
    global phase, outputs, expected, player, stopping
    try:
        if not snapshots or 'error' in snapshots[-1]:
            raise AssertionError('Observer unavailable: ' + str(snapshots[-1:] or 'no snapshots'))
        if phase >= 0 and expected is not None:
            windows = [w for w in snapshots[-1]['windows'] if player and w['pid'] == player.pid]
            assert windows, 'Presenter is missing from KWin'
            w = windows[0]
            output, x, y, flags = expected
            r = w['rect']
            assert abs(r['x'] - x) <= 1 and abs(r['y'] - y) <= 1, (r, expected)
            assert r['width'] == 96 and r['height'] == 80, r
            assert w['layer'] == (9 if flags & 1 else 1), w['layer']
            assert w['own'], 'Presenter must be excluded from sitting seats and occluders'
            print('PASS output', output['name'], 'geometry', r, 'layer', w['layer'], flush=True)
        phase += 1
        if phase == 0:
            outputs = snapshots[-1]['outputs']
        if phase <= len(outputs):
            output = outputs[phase % len(outputs)]
            expected = output, output['rect']['x'] + 100, output['rect']['y'] + 100, (1 if phase < len(outputs) else 0)
            publish()
            return True
        stopping = True
        GLib.timeout_add(3500, finish)
        return False
    except Exception as error:
        errors.append(str(error))
        loop.quit()
        return False

def finish():
    try:
        assert player.poll() is None, 'Presenter exited unexpectedly'
        import time
        assert time.time() - (scratch/'input').stat().st_mtime > 1, 'Stale presenter kept publishing heartbeat'
        print('PASS stale-frame heartbeat stops', flush=True)
    except Exception as error:
        errors.append(str(error))
    loop.quit()
    return False

def load_observer():
    if snapshots or len(script_names) >= 8:
        return False
    script_name = name + '_' + str(len(script_names) + 1)
    script_id = bus.call_blocking('org.kde.KWin', '/Scripting', 'org.kde.kwin.Scripting', 'loadScript', 'ss', (str(script_path), script_name))
    assert script_id >= 0, 'KWin rejected observer script'
    script_names.append(script_name)
    dbus.Interface(bus.get_object('org.kde.KWin', '/Scripting/Script'+str(script_id)), 'org.kde.kwin.Script').run()
    return not snapshots

with (scratch/'presenter.log').open('w') as log:
    try:
        env = dict(os.environ, QT_QPA_PLATFORM='wayland', WAYLAND_DEBUG='client')
        env.pop('DISPLAY', None)
        player = subprocess.Popen([sys.argv[1], '--frame-file', str(scratch/'frames'), '--input-file', str(scratch/'input')], env=env, stdout=log, stderr=log)
        script_path.write_text(source.replace('__PRESENTER_PID__', str(player.pid)))
        load_observer()
        GLib.timeout_add(750, load_observer)
        GLib.timeout_add(100, publish)
        GLib.timeout_add(int(os.environ.get('MATEE_TEST_STEP_MS', '1200')), advance)
        GLib.timeout_add(40000, lambda: (errors.append('Test timeout'), loop.quit(), False)[-1])
        loop.run()
    finally:
        if player is not None:
            player.terminate()
            player.wait(timeout=5)
        for script_name in script_names:
            scripting.unloadScript(script_name)
print('Evidence:', scratch)
if errors:
    raise SystemExit('; '.join(errors))
