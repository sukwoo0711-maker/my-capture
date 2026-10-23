"""Synthesises the optional capture shutter sound (src/MyCapture.App/Assets/Sounds/shutter.wav).

Two short mechanical clicks, like a camera shutter closing twice:
  click 1 at 0 ms:  band-passed noise around 4.2 kHz (~6 ms) and a low thump gliding 180 -> 110 Hz (~40 ms)
  click 2 at 55 ms: band-passed noise around 2.6 kHz (~9 ms) and a low thump gliding 150 -> 90 Hz (~50 ms)
Each part rises in about 1 ms and decays exponentially. The mix is normalised to a -6 dBFS peak and written
as 16-bit PCM, mono, 48 kHz, about 110 ms long. The noise is seeded, so the output is reproducible.

Usage: python make_shutter.py [output.wav]   (standard library only)
"""

import math
import random
import struct
import sys
import wave
from pathlib import Path

RATE = 48_000
LENGTH_S = 0.110
PEAK_DBFS = -6.0
SEED = 0x5EED


def envelope(n, attack_s, length_s, peak):
    """Exponential rise to `peak` in `attack_s`, then an exponential decay to -80 dB at `length_s`."""
    floor = 1e-4
    out = []
    for i in range(n):
        t = i / RATE
        if t < attack_s:
            out.append(floor * (peak / floor) ** (t / attack_s))
        elif t < length_s:
            out.append(peak * (floor / peak) ** ((t - attack_s) / (length_s - attack_s)))
        else:
            out.append(0.0)
    return out


def band_pass(samples, centre_hz, q):
    """RBJ cookbook band-pass (constant 0 dB peak gain), direct form I."""
    w0 = 2 * math.pi * centre_hz / RATE
    alpha = math.sin(w0) / (2 * q)
    a0 = 1 + alpha
    b0, b1, b2 = alpha / a0, 0.0, -alpha / a0
    a1, a2 = -2 * math.cos(w0) / a0, (1 - alpha) / a0
    x1 = x2 = y1 = y2 = 0.0
    out = []
    for x in samples:
        y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1, y2, y1 = x1, x, y1, y
        out.append(y)
    return out


def hiss(rng, centre_hz, q, peak, length_s):
    n = int((length_s + 0.003) * RATE)
    noise = band_pass([rng.uniform(-1.0, 1.0) for _ in range(n)], centre_hz, q)
    env = envelope(n, 0.0012, length_s, peak)
    return [s * e for s, e in zip(noise, env)]


def thump(start_hz, end_hz, glide_s, peak, length_s):
    n = int((length_s + 0.003) * RATE)
    env = envelope(n, 0.0012, length_s, peak)
    phase = 0.0
    out = []
    for i in range(n):
        t = i / RATE
        # Exponential frequency glide, then held at the end frequency.
        f = start_hz * (end_hz / start_hz) ** min(1.0, t / glide_s)
        phase += 2 * math.pi * f / RATE
        out.append(math.sin(phase) * env[i])
    return out


def mix_into(target, part, offset_s):
    start = int(offset_s * RATE)
    for i, s in enumerate(part):
        if start + i < len(target):
            target[start + i] += s


def synthesise():
    rng = random.Random(SEED)
    total = [0.0] * int(LENGTH_S * RATE)
    # The same parts and levels as the approved browser sketch (feel bench, sound "shutter").
    mix_into(total, hiss(rng, 4200, 1.6, 0.9, 0.006), 0.0)
    mix_into(total, thump(180, 110, 0.030, 0.20, 0.040), 0.0)
    mix_into(total, hiss(rng, 2600, 1.8, 1.1, 0.009), 0.055)
    mix_into(total, thump(150, 90, 0.040, 0.22, 0.050), 0.055)
    # A 2 ms fade keeps the last sample at zero.
    fade = int(0.002 * RATE)
    for i in range(fade):
        total[len(total) - 1 - i] *= i / fade
    peak = max(abs(s) for s in total)
    gain = (10 ** (PEAK_DBFS / 20)) / peak
    return [s * gain for s in total]


def write(path, samples):
    path.parent.mkdir(parents=True, exist_ok=True)
    frames = b"".join(struct.pack("<h", max(-32768, min(32767, round(s * 32767)))) for s in samples)
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(frames)


def main():
    default = Path(__file__).resolve().parents[3] / "src" / "MyCapture.App" / "Assets" / "Sounds" / "shutter.wav"
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else default
    samples = synthesise()
    write(target, samples)
    peak = max(abs(s) for s in samples)
    print(f"{target}: {len(samples)} samples, {len(samples) / RATE * 1000:.0f} ms, peak {20 * math.log10(peak):.2f} dBFS")


if __name__ == "__main__":
    main()
