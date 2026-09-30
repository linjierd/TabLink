"""Bounded selected-VDD-only diagnostic; never changes display modes or other processes."""
import argparse
import collections
import ctypes
import json
import pathlib
import statistics
import subprocess
import time
import uuid

p = argparse.ArgumentParser()
p.add_argument('--project-root', type=pathlib.Path, required=True)
p.add_argument('--capture-exe', type=pathlib.Path, required=True)
p.add_argument('--reference-exe', type=pathlib.Path, required=True)
p.add_argument('--display', required=True)
p.add_argument('--source-fps', type=int, default=90)
p.add_argument('--resample-fps', type=int)
p.add_argument('--no-duplicates', action='store_true')
p.add_argument('--motion-kind', choices=['gdi', 'd3d'], default='gdi')
p.add_argument('--output-dir', type=pathlib.Path)
a = p.parse_args()
root = a.project_root.resolve()
probe = root / 'tests/TabLink.Video.Tests/bin/Release/net10.0-windows/TabLink.Video.Tests.exe'
health_path = root / 'dist/TabLink/diagnostics/session-health.json'
flags = subprocess.CREATE_NO_WINDOW
dest = a.output_dir or pathlib.Path(__file__).resolve().parent / ('motion-results-' + uuid.uuid4().hex)
dest.mkdir(exist_ok=False)

def get_target():
    health = json.loads(health_path.read_text(encoding='utf-8-sig'))
    known = [d for d in health.get('displays', []) if d['DeviceName'] == a.display
             and d['IsTabLinkCompatible'] and not d['IsPrimary']]
    if len(known) != 1 or not health.get('receiving'):
        raise RuntimeError('No unique active verified TabLink display/session; capture refused')
    outputs = json.loads(subprocess.check_output([str(probe), '--dxgi-probe'], creationflags=flags))
    expected = known[0]['Bounds']
    matched = [o for o in outputs if o['DeviceName'] == a.display and o['Attached']
               and all(o['Bounds'][k] == expected[k] for k in ('X', 'Y', 'Width', 'Height'))]
    if len(matched) != 1:
        raise RuntimeError('No exact unique DXGI output/native bounds; capture refused')
    return matched[0]

target = get_target()
bounds = target['Bounds']
source = (f'ddagrab=output_idx={target["OutputIndex"]}:framerate={a.source_fps}:'
          f'video_size={bounds["Width"]}x{bounds["Height"]}:draw_mouse=1:output_fmt=bgra:'
          f'dup_frames={0 if a.no_duplicates else 1}')
if a.resample_fps:
    source += f',fps={a.resample_fps}'
args = [str(a.capture_exe), '-hide_banner', '-loglevel', 'info', '-y',
        '-init_hw_device', f'd3d11va=tablink:{target["AdapterIndex"]}', '-filter_hw_device', 'tablink',
        '-filter_complex', source + '[capture]', '-map', '[capture]', '-t', '10',
        '-an', '-sn', '-dn', '-c:v', 'h264_nvenc', '-preset', 'p1', '-tune', 'ull',
        '-rc', 'cbr', '-b:v', '30M', '-maxrate', '30M', '-bufsize', '1M', '-rc-lookahead', '0',
        '-zerolatency', '1', '-delay', '0', '-profile:v', 'high', '-aud', '1', '-forced-idr', '1',
        '-bf', '0', '-g', '90', '-fps_mode', 'passthrough', '-flush_packets', '1', '-f', 'h264',
        str(dest / 'capture.h264')]
(dest / 'command.json').write_text(json.dumps(args, indent=2))
encoder = motion = None
with (dest / 'motion.json').open('w') as motion_log, (dest / 'motion-error.txt').open('w') as motion_error, (dest / 'ffmpeg.log').open('w') as log:
    try:
        motion = subprocess.Popen([str(probe), '--d3d-motion-probe' if a.motion_kind == 'd3d' else '--motion-probe', a.display, '12'],
                                  stdout=motion_log, stderr=motion_error, creationflags=flags)
        time.sleep(.5)
        if motion.poll() is not None:
            raise RuntimeError('Motion window did not start')
        assert get_target() == target, 'DXGI target changed before process start'
        start = time.perf_counter()
        encoder = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                   stderr=log, creationflags=flags)
        while encoder.poll() is None:
            if time.perf_counter() - start > 14:
                raise RuntimeError('Bounded capture exceeded 14 seconds')
            assert get_target() == target, 'DXGI target changed during capture'
            time.sleep(.25)
        elapsed = time.perf_counter() - start
        create, exit_time, kernel, user = (ctypes.c_uint64() for _ in range(4))
        if not ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(encoder._handle)),
                ctypes.byref(create), ctypes.byref(exit_time), ctypes.byref(kernel), ctypes.byref(user)):
            raise ctypes.WinError()
        cpu = (kernel.value + user.value) / 1e7
        if encoder.returncode:
            raise RuntimeError(f'Encoder failed ({encoder.returncode}); see {dest}/ffmpeg.log')
        motion.wait(timeout=5)
    finally:
        for child in (encoder, motion):
            if child is not None and child.poll() is None:
                child.kill()
                child.wait()

decoded = subprocess.run([str(a.reference_exe), '-hide_banner', '-loglevel', 'error', '-i',
        str(dest / 'capture.h264'), '-vf', 'crop=440:20:48:220,format=gray', '-fps_mode',
        'passthrough', '-f', 'rawvideo', 'pipe:1'], capture_output=True, check=True,
        timeout=30, creationflags=flags)
size = 440 * 20
assert len(decoded.stdout) % size == 0
ids = []
for off in range(0, len(decoded.stdout), size):
    frame = decoded.stdout[off:off + size]
    value = 0
    for bit in range(16):
        level = statistics.mean(frame[y * 440 + bit * 28 + x] for y in range(6, 14) for x in range(6, 14))
        if level > 128:
            value |= 1 << bit
    ids.append(value)
motion_report = json.loads((dest / 'motion.json').read_text())
frame_limit = motion_report.get('PresentCount', 1080)
assert ids and all(0 <= i < frame_limit for i in ids), 'Invalid/missing source barcode'
assert all(b >= a for a, b in zip(ids, ids[1:])), 'Source barcode went backwards'
transitions = sum(a != b for a, b in zip(ids, ids[1:]))
if 'PresentTimes' in motion_report:
    span = motion_report['PresentTimes'][ids[-1]] - motion_report['PresentTimes'][ids[0]]
else:
    span = (ids[-1] - ids[0]) / 90
report = dict(Directory=str(dest), CaptureExe=str(a.capture_exe), SourceFilter=source,
              Target=target, WallSeconds=elapsed, CpuSeconds=cpu, OneCoreCpuPercent=cpu / elapsed * 100,
              EncodedFrames=len(ids), UniqueSourceIds=len(set(ids)), ActualUniqueFps=transitions / 10,
              SourceSpanSeconds=span, UniqueFpsBySourceClock=transitions / span if span else 0,
              FirstSourceId=ids[0], LastSourceId=ids[-1],
              PerIdChangeCounts=dict(collections.Counter(b-a for a,b in zip(ids,ids[1:]))))
(dest / 'result.json').write_text(json.dumps(report, indent=2))
print(json.dumps(report, indent=2))
print(json.dumps({k:v for k,v in motion_report.items() if k!='PresentTimes'},indent=2))
