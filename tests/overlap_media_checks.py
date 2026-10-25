"""Exercise real local files, TCP handoffs, decoded pixels and resampled PCM output."""
import csv
import array
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import threading
import time
from short_media_checks import connect

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'tests/out/overlap-media'
FIXTURE = ROOT / 'tests/out/PlaybackBoundaryChecks.exe'
ENV = os.environ.copy()
ENV['PATH'] = str(ROOT / 'x64/Debug') + os.pathsep + ENV.get('PATH', '')
ENV['OMNIVCAM_TRACE_AUDIO'] = '1'


def generate():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, marker, fps, rate, value, length in [('old', 20, '24', 44100, 1000, '1.2'),
            ('new', 220, '30', 32000, 3000, '1.2'), ('standby', 100, '60', 48000, 2000, '1.2'),
            ('short', 160, '60', 44100, 1500, '0.033333')]:
        image = OUT / (name + '.ppm')
        image.write_bytes(b'P6\n32 32\n255\n' + bytes([marker]) * (32 * 32 * 3))
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-loop', '1', '-i', str(image),
            '-f', 'lavfi', '-i', f'aevalsrc={value}/32768:s={rate}', '-t', length, '-r', fps,
            '-c:v', 'rawvideo', '-pix_fmt', 'bgr24', '-c:a', 'pcm_s16le', '-ac', '1', str(OUT / (name + '.avi'))],
            check=True, capture_output=True)

    subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-loop', '1', '-i', str(OUT / 'short.ppm'), '-t', '1.2', '-r', '30', '-c:v', 'rawvideo', '-pix_fmt', 'bgr24', '-an',
        str(OUT / 'silent.avi')], check=True, capture_output=True)


def rows(path):
    try:
        with path.open() as stream:
            return [tuple(map(int, row)) for row in csv.reader(stream) if row]
    except (FileNotFoundError, ValueError):
        return []


def wait_until(predicate, message, timeout=6):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(0.01)
    raise AssertionError(message)


class Backend:
    def __init__(self, name, fps=60, audio_samples=480, channels=1):
        self.name = name
        self.trace = OUT / (name + '.csv')
        self.audio_trace = Path(str(self.trace) + '.audio.csv')
        self.pcm_trace = Path(str(self.trace) + '.s16le')
        self.audio_samples = audio_samples; self.channels = channels
        self.log = (OUT / (name + '.log')).open('wb')
        (OUT / 'config.ini').write_text('[config]\nhw_decode=none\nauto_deinterlace=0\nvideo_frame_buffer=128\naudio_frame_buffer=256\ntimeout=30000000\n', encoding='utf-8')
        with socket.socket() as reserve:
            reserve.bind(('127.0.0.1', 0)); port = reserve.getsockname()[1]
        environment = dict(ENV, OMNIVCAM_TEST_AUDIO_SAMPLES=str(audio_samples), OMNIVCAM_TEST_AUDIO_CHANNELS=str(channels), OMNIVCAM_TRACE_PCM='1')
        self.process = subprocess.Popen([str(FIXTURE), '--serve-short', str(port), str(fps), str(OUT), str(self.trace)],
            stdin=subprocess.PIPE, stdout=self.log, stderr=subprocess.STDOUT, env=environment)
        self.sock = connect(port, self.process)
        self.sock.settimeout(None)
        self.replies = queue.Queue(); self.events = queue.Queue(); self.all_events = []
        def receive():
            try:
                with self.sock.makefile('r', encoding='utf-8', errors='replace') as stream:
                    for line in stream:
                        line = line.strip()
                        if line.startswith('EVENT '):
                            self.all_events.append((time.monotonic(), line)); self.events.put(line)
                        elif line.startswith(('OK', 'ERR')):
                            self.replies.put(line)
            except OSError:
                pass
        threading.Thread(target=receive, daemon=True).start()

    def command(self, text, ok=True):
        self.sock.sendall((text + '\n').encode())
        reply = self.replies.get(timeout=6)
        assert reply.startswith('OK' if ok else 'ERR'), (text, reply)
        return reply

    def media(self, verb, name, options=''):
        return self.command(verb + ' ' + str(OUT / (name + '.avi')) + ('\t' + options if options else ''))

    def started(self, after=0):
        deadline = time.monotonic() + 6
        while time.monotonic() < deadline:
            event = self.events.get(timeout=6)
            if event.startswith('EVENT INPUT_STARTED '):
                fields = dict(item.split('=', 1) for item in event.split() if '=' in item)
                batch = int(fields['frame_id'])
                if batch > after:
                    return batch
        raise AssertionError('No actual input-start event')

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.write(b'\n'); self.process.stdin.flush()
            try:
                self.process.wait(timeout=12)
            except subprocess.TimeoutExpired:
                self.process.kill(); self.process.wait(); raise
        self.sock.close(); self.log.close()
        assert self.process.returncode == 0, (self.name, self.process.returncode)


def handoff(name, overlap, fps=60, immediate=False, replace=False):
    backend = Backend(name, fps)
    try:
        backend.media('PLAY', 'old', 'overlap=0.1,queue_left=2,queue_center=4,queue_right=8')
        old_id = backend.started()
        wait_until(lambda: sum(row[2] == old_id and row[1] == 20 for row in rows(backend.trace)) >= 3, 'Old source reaches actual output')
        if replace:
            backend.media('PLAY_NEXT', 'standby', 'overlap=0.1')
            def prefetched():
                text = backend.command('STATUS')
                fields = dict(item.split('=', 1) for item in text.split() if '=' in item)
                return int(fields.get('input_frame_id', 0)) > old_id and int(fields.get('actual_frame_id', 0)) == old_id
            wait_until(prefetched, 'Standby is decoded ahead of actual output')
        option = f'overlap={overlap}' + (',queue_left=1,queue_center=4,queue_right=6' if overlap else '')
        if replace:
            option += f',after_frame_id={old_id}'
        backend.media('PLAY' if immediate else 'PLAY_NEXT', 'new', option)
        new_id = backend.started(old_id)
        target = wait_until(lambda: [r for r in rows(backend.trace) if r[2] == new_id and r[1] == 220] or None, 'New source finishes its blend')
        wait_until(lambda: len([r for r in rows(backend.audio_trace) if r[3] == new_id]) >= max(35, int(overlap * 100) + 5), 'New audio reaches actual output')
        video = [row for row in rows(backend.trace) if row[2] == new_id]
        audio = [row for row in rows(backend.audio_trace) if row[3] == new_id]
        blended = [row[1] for row in video if 20 < row[1] < 220]
        mixed_audio = [row[1] for row in audio if 1100 < row[1] < 2900]
        if overlap:
            assert len(blended) >= max(1, int(overlap * fps) - 2), (name, 'missing picture ramp', video)
            assert blended == sorted(blended), (name, 'non-monotonic picture ramp', blended)
            assert mixed_audio, (name, 'missing PCM ramp', audio)
            assert not any(abs(row[1]) < 100 or abs(row[2]) < 100 for row in audio[:max(1, int(overlap * 100))]), (name, 'silence inside overlap', audio)
        else:
            assert not blended and not mixed_audio, (name, 'zero duration still blended')
        assert all(video[i][0] < video[i + 1][0] for i in range(len(video) - 1)), 'Video clock remains monotonic'
        assert all(audio[i][4] < audio[i + 1][4] for i in range(len(audio) - 1)), 'Audio PTS remains monotonic'
        assert sum('INPUT_ENDED ' in event and f'frame_id={old_id} ' in event for _, event in backend.all_events) == 1, (name, 'old end event count', backend.all_events)
        result = dict(name=name, fps=fps, overlap=overlap, blended_pictures=len(blended), mixed_audio_blocks=len(mixed_audio))
        print('PASS:', result)
        return result
    finally:
        backend.close()


def short_and_failure():
    backend = Backend('short-and-failure')
    try:
        backend.media('PLAY', 'old')
        current = backend.started()
        backend.media('PLAY_NEXT', 'short')
        short_id = backend.started(current)
        backend.media('PLAY_NEXT', 'new')
        next_id = backend.started(short_id)
        wait_until(lambda: any(r[2] == next_id and r[1] == 220 for r in rows(backend.trace)), 'Short-file successor reaches output')
        assert any(r[2] == short_id and r[1] == 160 for r in rows(backend.trace)), 'Very short target retains an actual full picture'
        backend.command('PLAY_NEXT ' + str(OUT / 'missing.avi') + '\toverlap=0.25')
        wait_until(lambda: 'open:"' in (OUT / 'short-and-failure.log').read_text(errors='replace') or 'error' in backend.command('STATUS'), 'Missing target reports failure')
        backend.command('PLAY_NEXT')
        backend.media('PLAY', 'old', 'overlap=0')
        recovery_id = backend.started(next_id)
        wait_until(lambda: any(r[2] == recovery_id and r[1] == 20 for r in rows(backend.trace)), 'Playback recovers after failed input')
        backend.command('PLAY ' + str(OUT / 'old.avi') + '\toverlap=nan', ok=False)
        backend.command('PLAY_NEXT ' + str(OUT / 'old.avi') + '\toverlap=-1', ok=False)
        backend.command('PAUSE'); backend.command('RESUME'); backend.command('STOP')
        print('PASS: very short target, failed open, recovery, malformed options, pause/resume/stop')
    finally:
        backend.close()


def missing_audio():
    for source, target, old_value, new_value in [('old', 'silent', 1000, 0), ('silent', 'new', 0, 3000)]:
        backend = Backend(source + '-to-' + target)
        try:
            backend.media('PLAY', source)
            current = backend.started()
            backend.media('PLAY_NEXT', target)
            next_id = backend.started(current)
            wait_until(lambda: len([r for r in rows(backend.audio_trace) if r[3] == next_id]) >= 25,
                'Missing-track handoff completes')
            audio = [r for r in rows(backend.audio_trace) if r[3] == next_id]
            values = [r[1] for r in audio[:10]]
            assert any(min(old_value, new_value) + 50 < value < max(old_value, new_value) - 50 for value in values), (source, target, 'missing audio fade', audio)
            assert abs(audio[20][1] - new_value) < 20, (source, target, 'incorrect audio after fade', audio)
            assert sum('INPUT_ENDED ' in event and f'frame_id={current} ' in event for _, event in backend.all_events) == 1
            print('PASS: missing audio track', source, '->', target)
        finally:
            backend.close()


def main():
    generate()
    results = [handoff('next-default', 0.1), handoff('next-quarter-30', 0.25, fps=30),
        handoff('next-disabled', 0), handoff('immediate-play', 0.1, immediate=True),
        handoff('replace-prefetch', 0.1, replace=True)]
    short_and_failure()
    missing_audio()
    (OUT / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('All real-media overlap checks passed.')


if __name__ == '__main__':
    main()
