"""Build the 4.4.0 landing media from the Store composites (2560x1440, headline on top).

Outputs per language into OUT: hero-<l>.mp4/.webm/.jpg (1920x1080 loop: card -> redact -> pin),
redact-<l>.webp, pin-<l>.webp, library-<l>.webp (1600 wide), og-<l>.jpg (1200x630), and
readme-demo-<l>.gif (720 wide) for the repository README.
"""
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

SRC = Path(sys.argv[1])          # .../artifacts/store/screenshots/4.4.0
OUT = Path(sys.argv[2])
FF = sys.argv[3]
OUT.mkdir(parents=True, exist_ok=True)

BG = (16, 19, 24)                # composite background
# App areas below the headline (x0, y0, x1, y1) in composite pixels, measured on both languages.
CROPS = {
    "01": (160, 400, 2410, 1440),   # auto-redact in the editor
    "02": (160, 410, 2560, 1440),   # reference pinned above code
    "04": (160, 420, 2470, 1440),   # capture card over code
    "06": (160, 360, 2410, 1302),   # library search by text
}
W, H, FPS = 1920, 1080, 30
SCENE, FADE = 3.0, 0.4             # seconds per scene, crossfade
ORDER = ["04", "01", "02"]         # capture card -> redact -> pin


def crop(lang, n):
    return Image.open(SRC / lang / f"{n}.png").convert("RGB").crop(CROPS[n])


def frame(img, t, zoom=0.025):
    """Scene image fitted to 1760 px wide, slowly zooming 1.00 -> 1 + zoom around its center."""
    z = 1.0 + zoom * (t / SCENE)
    scale = 1760 / img.width * z
    sw, sh = img.width * scale, img.height * scale
    ox, oy = (W - sw) / 2, (H - sh) / 2
    # Affine maps output -> input coordinates (sub-pixel, so the zoom does not jitter).
    inv = (1 / scale, 0, -ox / scale, 0, 1 / scale, -oy / scale)
    canvas = img.transform((W, H), Image.AFFINE, inv, resample=Image.BICUBIC, fillcolor=BG)
    return np.asarray(canvas, dtype=np.float32)


def render(scenes, zoom):
    """Frame 0 is the first scene; scenes change by dipping to the background, and the last
    FADE seconds dip back into the first scene so the loop has no visible seam."""
    step = SCENE - FADE
    total = step * len(scenes)
    bg = np.empty((H, W, 3), np.float32); bg[:] = BG
    frames = []
    for i in range(round(total * FPS)):
        t = i / FPS + FADE                     # shift so frame 0 shows scene 0 fully
        k = int(t // step) % len(scenes)
        local = t - (t // step) * step
        if local < FADE:                       # dip: previous scene -> background -> scene k
            half = FADE / 2
            if local < half:
                w = local / half
                a = frame(scenes[k - 1], local + FADE + step, zoom) * (1 - w) + bg * w
            else:
                w = (local - half) / half
                a = bg * (1 - w) + frame(scenes[k], local + FADE, zoom) * w
        else:
            a = frame(scenes[k], local + FADE, zoom)
        frames.append(np.clip(a + 0.5, 0, 255).astype(np.uint8))
    return frames


def encode(frames, args, out):
    raw = b"".join(f.tobytes() for f in frames)
    base = [FF, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-"]
    subprocess.run(base + args + [str(out)], input=raw, check=True)


def hero(lang):
    scenes = [crop(lang, n) for n in ORDER]
    frames = render(scenes, 0.025)
    tags = ["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]
    vf = ["-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", "-an"]
    encode(frames, vf + ["-c:v", "libx264", "-preset", "slow", "-crf", "25", "-profile:v", "high", *tags,
                         "-movflags", "+faststart"], OUT / f"hero-{lang}.mp4")
    encode(frames, vf + ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "42", "-row-mt", "1", "-deadline", "good",
                         "-cpu-used", "2", *tags], OUT / f"hero-{lang}.webm")
    Image.fromarray(frames[0]).save(OUT / f"hero-{lang}.jpg", quality=90)
    still = render(scenes, 0.0)                # no zoom keeps the GIF small
    encode(still, ["-vf", "fps=10,scale=720:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=64:stats_mode=diff[p];"
                   "[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"], OUT / f"readme-demo-{lang}.gif")


def stills(lang):
    for name, n in (("redact", "01"), ("pin", "02"), ("library", "06")):
        img = crop(lang, n)
        img = img.resize((1600, round(img.height * 1600 / img.width)), Image.LANCZOS)
        img.save(OUT / f"{name}-{lang}.webp", quality=86, method=6)
        print(name, lang, img.size)
    pin = crop(lang, "02")
    pin = pin.resize((round(pin.width * 630 / pin.height), 630), Image.LANCZOS)
    pin.crop((pin.width - 1200, 0, pin.width, 630)).save(OUT / f"og-{lang}.jpg", quality=88)


for lang in ("ko", "en"):
    stills(lang)
    hero(lang)
print("done")
