"""Real-media/TCP integration check for the native playback engine; no installed camera required."""
import csv
import hashlib
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'tests/out/short-media'
FIXTURE = ROOT / 'tests/out/PlaybackBoundaryChecks.exe'
ENV = os.environ.copy()
ENV['PATH'] = str(ROOT / 'x64/Debug') + os.pathsep + ENV.get('PATH', '')


def connect(port, process):
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError('Backend exited during startup')
        try:
            return socket.create_connection(('127.0.0.1', port), timeout=0.2)
        except OSError:
            time.sleep(0.02)
    raise AssertionError('Backend did not listen')


def run_case(kind, fps, repeat, loop=False, buffer=30):
    with socket.socket() as reserve:
        reserve.bind(('127.0.0.1', 0))
        port = reserve.getsockname()[1]
    name = f'{kind}-{fps}-' + ('same' if repeat else 'mixed')
    if loop:
        name += f'-loop-buffer{buffer}'
    (OUT / 'config.ini').write_text(f'[config]\nhw_decode=none\nauto_deinterlace=0\nvideo_frame_buffer={buffer}\naudio_frame_buffer={buffer}\ntimeout=30000000\n', encoding='utf-8')
    trace = OUT / (name + '.csv')
    log = open(OUT / (name + '.log'), 'wb')
    process = subprocess.Popen([str(FIXTURE), '--serve-short', str(port), str(fps), str(OUT), str(trace)],
                               stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, env=ENV)
    replies, events = queue.Queue(), queue.Queue()
    sock = None
    try:
        sock = connect(port, process)
        sock.settimeout(None)

        def receive():
            try:
                with sock.makefile('r', encoding='utf-8', errors='replace') as reader:
                    for line in reader:
                        line = line.rstrip('\r\n')
                        item = (time.monotonic(), line)
                        if line.startswith('EVENT '):
                            events.put(item)
                        elif line.startswith(('OK', 'ERR')):
                            replies.put(item)
            except OSError:
                pass

        threading.Thread(target=receive, daemon=True).start()

        def command(text):
            start = time.monotonic()
            sock.sendall((text + '\n').encode('utf-8'))
            _, reply = replies.get(timeout=5)
            assert reply.startswith('OK'), (text, reply)
            return start, reply

        extension = 'mp4' if kind == 'mpeg4' else 'avi'
        counts = [1] * 15 if repeat else [1, 2, 3] * 5
        files = [OUT / f'{kind}-{fps}-{count}.{extension}' for count in counts]
        files.append(OUT / f'{kind}-{fps}-{fps}.{extension}')
        inputs = [str(path) + '\toverlap=0' + (',loop=1' if loop and item < len(counts) else '') for item, path in enumerate(files)]
        hashes = [hashlib.sha256(text.encode()).hexdigest() for text in inputs]
        command('PLAY ' + inputs[0])
        if not loop:
            command('PLAY_NEXT ' + inputs[1])
        index = 0
        starts, eof_ids, ended_ids = [], [], []
        submitted = {0: time.monotonic(), 1: time.monotonic()}
        all_events = []
        all_loop_ids = []
        deadline = time.monotonic() + 15
        while index < len(files) and time.monotonic() < deadline:
            timestamp, line = events.get(timeout=5)
            all_events.append(line)
            fields = dict(field.split('=', 1) for field in line.split() if '=' in field)
            frame_id = int(fields.get('frame_id', 0))
            if line.startswith('EVENT INPUT_EOF '):
                eof_ids.append(frame_id)
            elif line.startswith('EVENT INPUT_ENDED ') and fields.get('input') in hashes:
                ended_ids.append(frame_id)
            elif line.startswith('EVENT INPUT_STARTED '):
                if loop and fields.get('input') == hashes[0]:
                    all_loop_ids.append(frame_id)
                    if index == len(counts):
                        continue
                assert fields.get('input') == hashes[index], (name, index, line, hashes[index])
                assert not starts or frame_id > starts[-1][1], (name, 'Repeated or out-of-order batch', line)
                starts.append((timestamp, frame_id))
                if loop and index == len(counts) - 1:
                    command('PLAY_NEXT ' + str(files[-1]) + f'\tafter_frame_id={frame_id},overlap=0')
                elif not loop and index > 0 and index + 1 < len(files):
                    submitted[index + 1], _ = command('PLAY_NEXT ' + inputs[index + 1])
                index += 1
        assert index == len(files), (name, 'Missing actual-start events', index, all_events)
        _, status = command('STATUS')
        assert f'actual_frame_id={starts[-1][1]}' in status and f'actual_input={hashes[-1]}' in status, (name, status)
        # Let the final boundary's end notification arrive before collecting it.
        time.sleep(0.03)
        while not events.empty():
            _, line = events.get_nowait()
            if line.startswith('EVENT INPUT_ENDED ') and line.split('input=')[-1] in hashes:
                ended_ids.append(int(line.split('frame_id=')[1].split()[0]))
        if loop:
            assert len(ended_ids) == 1 and ended_ids[0] in all_loop_ids, (name, 'Internal loops must not complete the interactive item', ended_ids, all_loop_ids)
        else:
            assert ended_ids == [item[1] for item in starts[:-1]], (name, 'Missing/duplicate end events', ended_ids, starts)
        actual_intervals = [(starts[i + 1][0] - starts[i][0]) * 1000 for i in range(len(starts) - 1)]
        ideal_intervals = [count * 1000 / fps for count in counts]
        excess = [max(0, actual - ideal) for actual, ideal in zip(actual_intervals, ideal_intervals)]
        (OUT / (name + '-events.json')).write_text(json.dumps(all_events, indent=2), encoding='utf-8')
        command('STOP')
        process.stdin.write(b'\n')
        process.stdin.flush()
        assert process.wait(timeout=12) == 0, (name, 'Backend shutdown failed')
        source_markers = [(clip, frame, value + frame * 8) for clip, value in enumerate((72, 112, 152)) for frame in range(clip + 1)] + [(3, 0, 210)]
        expected_markers = [(clip, frame, round((value - 16) * 255 / 219) if kind == 'mpeg4' else value) for clip, frame, value in source_markers]
        visible_frames = []
        visible_runs = []
        visible_run_frames = []
        for row in csv.reader(trace.open()):
            timestamp, marker = map(int, row[:2])
            nearest = min(expected_markers, key=lambda item: abs(item[2] - marker))
            if abs(nearest[2] - marker) > 3:
                continue
            source_frame = nearest[:2]
            if not visible_frames or visible_frames[-1] != source_frame:
                visible_frames.append(source_frame)
            if not visible_runs or visible_runs[-1] != nearest[0]:
                visible_runs.append(nearest[0])
                visible_run_frames.append(1)
            else:
                visible_run_frames[-1] += 1
        expected_runs = [0, 3] if repeat else [0, 1, 2] * 5 + [3]
        assert visible_runs == expected_runs, (name, 'Actual video skipped/reordered a short item', visible_runs, expected_runs)
        expected_frames = ([(0, 0), (3, 0)] if repeat else [(clip, frame) for _ in range(5) for clip in range(3) for frame in range(clip + 1)] + [(3, 0)])
        assert visible_frames == expected_frames, (name, 'Actual video skipped/reordered a source frame', visible_frames, expected_frames)
        enough_output = visible_run_frames[0] >= len(counts) if repeat else all(actual >= expected for actual, expected in zip(visible_run_frames, counts))
        assert enough_output, (name, 'A short item did not reach actual video output', visible_run_frames)
        result = dict(case=name, short_items=len(counts), started=len(starts), ended=len(ended_ids),
                      source_durations_ms=sorted(set(round(value, 2) for value in ideal_intervals)),
                      min_transition_ms=round(min(actual_intervals), 2), max_transition_ms=round(max(actual_intervals), 2),
                      max_extra_hold_ms=round(max(excess), 2),
                      intervals_ms=[round(value, 2) for value in actual_intervals], visible_run_frames=visible_run_frames)
        print(json.dumps(result), flush=True)
        return result
    except Exception:
        (OUT / (name + '-failed-events.json')).write_text(json.dumps(locals().get('all_events', []), indent=2), encoding='utf-8')
        try:
            print(command('STATUS')[1], file=sys.stderr, flush=True)
        except Exception:
            pass
        print(f'FAILED: {name}; backend log: {OUT / (name + ".log")}', file=sys.stderr, flush=True)
        raise
    finally:
        if process.poll() is None:
            try:
                process.stdin.write(b'\n')
                process.stdin.flush()
                process.wait(timeout=12)
            except (OSError, subprocess.TimeoutExpired):
                process.kill()
                process.wait()
        if sock:
            sock.close()
        log.close()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / 'config.ini').write_text('[config]\nhw_decode=none\nauto_deinterlace=0\nvideo_frame_buffer=30\naudio_frame_buffer=50\ntimeout=30000000\n', encoding='utf-8')
    subprocess.run([str(FIXTURE), '--generate-short', str(OUT)], check=True, env=ENV, stdout=subprocess.DEVNULL)
    results = []
    for fps in (() if '--loops-only' in sys.argv else (30, 60)):
        for kind in ('raw', 'av', 'mpeg4'):
            for repeat in (False, True):
                results.append(run_case(kind, fps, repeat))
    for fps in (30, 60):
        for kind in ('raw', 'av', 'mpeg4'):
            for buffer in (30, 128, 256):
                results.append(run_case(kind, fps, True, loop=True, buffer=buffer))
    (OUT / ('loop-results.json' if '--loops-only' in sys.argv else 'results.json')).write_text(json.dumps(results, indent=2), encoding='utf-8')
    print(f'PASS: {sum(item["short_items"] for item in results)} short real-media activations across {len(results)} cases; no missing/reordered starts, ends, or source pictures.')


if __name__ == '__main__':
    main()
