"""Record full stereo waveforms through the real overlap output path."""
import array
import json
import math
import subprocess
from overlap_media_checks import Backend, OUT, generate, rows, wait_until


def generate_waves():
    generate()
    for name, image, rate, frequencies, length in [('wave-old', 'old', 44100, (997, 607), '1.6'), ('wave-new', 'new', 32000, (523, 353), '1.6'),
            ('wave-long-old', 'old', 44100, (997, 607), '4.8'), ('wave-long-new', 'new', 32000, (523, 353), '4.8')]:
        signal = f'aevalsrc=0.25*sin(2*PI*{frequencies[0]}*t)|0.20*sin(2*PI*{frequencies[1]}*t):s={rate}'
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-loop', '1', '-i', str(OUT / (image + '.ppm')),
            '-f', 'lavfi', '-i', signal, '-t', length, '-r', '30', '-c:v', 'rawvideo', '-pix_fmt', 'bgr24',
            '-c:a', 'pcm_s16le', '-ac', '2', str(OUT / (name + '.avi'))], check=True, capture_output=True)


def waveform_check(duration, block=1024, immediate=False):
    name = f'wave-{duration}-{block}-' + ('play' if immediate else 'next')
    backend = Backend(name, audio_samples=block, channels=2)
    try:
        prefix = 'wave-long-' if duration >= 1 else 'wave-'
        backend.media('PLAY', prefix + 'old', f'overlap={duration}')
        old_id = backend.started()
        wait_until(lambda: len([r for r in rows(backend.audio_trace) if r[3] == old_id]) >= 8, 'Old tone reaches output')
        backend.media('PLAY' if immediate else 'PLAY_NEXT', prefix + 'new', f'overlap={duration}')
        new_id = backend.started(old_id)
        wait_until(lambda: len([r for r in rows(backend.audio_trace) if r[3] == new_id]) >= math.ceil((duration + 0.2) * 48000 / block), 'Tone handoff completes')
    finally:
        backend.close()
    metadata = rows(backend.audio_trace)
    pcm = array.array('h'); pcm.frombytes(backend.pcm_trace.read_bytes())
    first_new = next(i for i, row in enumerate(metadata) if row[3] == new_id) * block
    first_old = next(i for i, row in enumerate(metadata) if row[3] == old_id) * block
    def spikes(start, end, channel):
        differences = [(abs(pcm[i*2+channel] - pcm[(i-1)*2+channel]), i-first_new) for i in range(max(1,start), min(end,len(pcm)//2))]
        return sorted(differences, reverse=True)[:8]
    result = dict(name=name, steady=[spikes(first_old+2048, first_new-block, ch) for ch in range(2)],
        fade=[spikes(first_new-block, first_new+math.ceil(duration*48000)+block, ch) for ch in range(2)])
    for channel, limit in enumerate((1500, 800)):
        assert result['steady'][channel][0][0] < limit, (name, 'click before overlap', result)
        if duration:
            assert result['fade'][channel][0][0] < limit, (name, 'click during handoff/overlap', result)
    print('PASS:', name, 'max steady steps', [v[0][0] for v in result['steady']],
        'max handoff steps', [v[0][0] for v in result['fade']])
    return result


def main():
    generate_waves()
    results = [waveform_check(duration, block) for block in (480, 1024) for duration in (0, 0.1, 0.25, 1.5)]
    results.extend(waveform_check(duration, 1024, True) for duration in (0.1, 1.5))
    (OUT / 'audio-results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('All 10 stereo waveform checks passed, including 1.5-second overlaps.')


if __name__ == '__main__':
    main()
