"""Cut the 30-second Store trailers into 1080x1920 launch shorts, covers and stills.

All generated media and evidence go to artifacts/launch-4.4.0 (gitignored); nothing
is deleted. Beat indices refer to the measured beat grid in the trailer's music.js.
The script records automated checks; looking at the frames and listening stay manual.
"""
from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import io
import json
import math
import re
import subprocess
from pathlib import Path

import imageio_ffmpeg
import numpy as np
from PIL import Image, ImageCms, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[4]
INPUT = Path("C:/GitRepositories/my-capture/artifacts/promo-2026-10-18").resolve()
OUT = (ROOT / "artifacts/launch-4.4.0").resolve()
WORK = OUT / "render-work"
BEAT_FILE = INPUT / "source/video/film-v2/js/music.js"
FFMPEG = Path(imageio_ffmpeg.get_ffmpeg_exe()).resolve()
EXPECTED_FFMPEG = INPUT / "source/video/venv/Lib/site-packages/imageio_ffmpeg/binaries/ffmpeg-win-x86_64-v7.1.exe"
if FFMPEG != EXPECTED_FFMPEG:
    raise RuntimeError(f"Use the supplied imageio-ffmpeg binary: {EXPECTED_FFMPEG}; found {FFMPEG}")
# Panel centred horizontally, raised above the Shorts/Reels/TikTok button column and
# description area; captions sit at a fixed height above the tallest panel.
FPS, WIDTH, HEIGHT, PANEL_X, PANEL_W, PANEL_CY = 60, 1080, 1920, 40, 1000, 900
CAPTION_CY, CAPTION_MAX_W = 360, 900
CREAM = "#FFF8F4"
TAGS = ["-colorspace", "bt709", "-color_primaries", "bt709",
        "-color_trc", "bt709", "-color_range", "tv"]
ICC = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB")).tobytes()
CAPTIONS = {
    "ko": ["Ctrl+Shift+C 영역 캡처", "Ctrl+Shift+C 영역 캡처", "캡처 직후 키 하나로 다음 동작",
           "Ctrl+Shift+R 개인정보 가리기", "주석은 레이어로 남습니다",
           "F3 다른 창 위에 핀", ""],
    "en": ["Ctrl+Shift+C  Region capture", "Ctrl+Shift+C  Region capture", "One key for the next step",
           "Ctrl+Shift+R  Redact personal details", "Annotations stay editable as layers",
           "F3  Pin above other windows", ""],
}
# Crops (x, y, w, h in source pixels) were measured on frames with a 120 px grid.
# capture-window: the whole inquiry window, mode bar and Ctrl/Shift/C keycaps (2.0-5.4 s).
# capture-select: the selection after the trailer camera zooms in, plus the card it
#   shrinks into at the lower right (5.4-8.75 s).
# quick: the whole quick card and the E keycap below it.
# redact: starts after the camera has zoomed into the canvas; all redacted rows and the
#   Ctrl/Shift/R keycaps (the trailer itself already cuts the canvas on the right).
# annotation: the whole canvas width with both markers, the rectangle, arrow and label.
# pin: the whole code window, the pin at its upper right and the F3 keycap.
# ending: the logo mark, the name and both text lines.
# Panel edges fade into the background over FEATHER output pixels so the crop edge
# never shows as a line; the two capture crops keep a hard top edge because the mode
# bar and the size chip sit at the top of the source frame.
FEATHER = 36
CUTS = [("capture-window", 3, 10, (170, 0, 1580, 1070), (FEATHER, 6, FEATHER, FEATHER)),
        ("capture-select", 10, 17, (120, 0, 1680, 1010), (FEATHER, 6, FEATHER, FEATHER)),
        ("quick", 19, 22, (540, 270, 840, 760)),
        ("redact", 26, 31, (420, 240, 1130, 800)),
        ("annotation", 32, 38, (80, 0, 1720, 985)),
        ("pin", 42, 46, (180, 40, 1570, 1000)),
        ("ending", 52, 60, (530, 320, 860, 640))]


def save_png(image, path):
    image.save(path, format="PNG", icc_profile=ICC)


def run(args, label, report):
    """No shell; persist the actual command, cwd, exit code and stderr."""
    command = [str(a) for a in args]
    number = len(report["commands"]) + 1
    log = WORK / f"{report['language']}-{number:03d}-{label}.log"
    record = {"label": label, "cwd": str(ROOT), "command": command,
              "exit_code": "N/A", "status": "NOT RUN", "log": str(log)}
    report["commands"].append(record)
    try:
        result = subprocess.run(command, cwd=ROOT, capture_output=True, check=False)
    except OSError as error:
        record["reason"] = str(error)
        raise
    log.write_bytes(result.stderr)
    record.update(exit_code=result.returncode,
                  status="PASS" if result.returncode == 0 else "FAIL")
    if result.returncode:
        raise RuntimeError(f"{label}: exit {result.returncode}; see {log}")
    return result.stdout, result.stderr.decode("utf-8", errors="replace")


def ff(args, label, report):
    return run([FFMPEG, "-hide_banner", "-nostdin", "-y", *args], label, report)


def frame(source, at, report, label, crop=None, size=None):
    filters = []
    if crop:
        x, y, w, h = crop
        filters.append(f"crop={w}:{h}:{x}:{y}")
    w, h = size or (0, 0)
    filters.append(f"scale={w or 'iw'}:{h or 'ih'}:flags=lanczos:"
                   "in_color_matrix=bt709:out_color_matrix=bt709:in_range=tv:out_range=pc")
    filters.append("format=rgb24")
    data, _ = ff(["-ss", f"{at:.9f}", "-i", source, "-map", "0:v:0",
                  "-vf", ",".join(filters), "-frames:v", "1", "-an",
                  "-c:v", "png", "-f", "image2pipe", "pipe:1"], label, report)
    with Image.open(io.BytesIO(data)) as image:
        return image.convert("RGB")


def font(lang, size):
    path = Path("C:/Windows/Fonts/NotoSansKR-VF.ttf" if lang == "ko"
                else "C:/Windows/Fonts/seguisb.ttf").resolve()
    face = ImageFont.truetype(str(path), size)
    if lang == "ko":
        axes = face.get_variation_axes()
        face.set_variation_by_axes([700 if b"weight" in a["name"].lower()
                                    else a["default"] for a in axes])
    return face


def caption(lang, text, destination):
    image = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    if text:
        words = text.split()
        candidates = [text] + [" ".join(words[:i]) + "\n" + " ".join(words[i:])
                               for i in range(1, len(words))]
        for size in range(68, 59, -1):
            face = font(lang, size)
            fits = [s for s in candidates
                    if max(draw.textlength(line, font=face) for line in s.splitlines()) <= CAPTION_MAX_W]
            if fits:
                line = min(fits, key=lambda s: (len(s.splitlines()),
                           max(draw.textlength(p, font=face) for p in s.splitlines())))
                draw.multiline_text((WIDTH // 2, CAPTION_CY), line, font=face, fill=CREAM,
                                    anchor="mm", align="center", spacing=16)
                break
        else:
            raise ValueError(f"Caption does not fit at 60 px: {text}")
    save_png(image, destination)


def segments(lang):
    source = BEAT_FILE.read_text(encoding="utf-8")
    music = json.loads(re.search(r"window\.MUSIC\s*=\s*(\{.*?\});", source, re.S)[1])
    result, elapsed, previous = [], 0.0, 0
    for i, (name, first, last, crop, *rest) in enumerate(CUTS):
        feather = list(rest[0]) if rest else [FEATHER] * 4
        start, end = music["beats"][first], music["beats"][last]
        elapsed += end - start
        boundary = math.floor(elapsed * FPS + 0.5)
        h = round(PANEL_W * crop[3] / crop[2] / 2) * 2
        result.append(dict(name=name, source_in=start, source_out=end,
                           beat_indices=[first, last], beats=last-first, crop=list(crop), feather=feather,
                           caption=CAPTIONS[lang][i], first_frame=previous,
                           frames=boundary-previous, panel=[PANEL_X, PANEL_CY-h//2, PANEL_W, h],
                           cumulative_timing_error_seconds=boundary/FPS-elapsed,
                           visual_check="NOT RUN: open source/output comparison frames"))
        previous = boundary
    return result


def edge_mask(size, feather, destination):
    """Full-range grey alpha: 255 inside, smoothstep ramps to 0 over each edge's width."""
    width, height = size
    left, top, right, bottom = feather

    def ramp(n, a, b):
        t = np.ones(n)
        i = np.arange(n, dtype=float)
        if a:
            t = np.minimum(t, np.clip((i + 0.5) / a, 0, 1))
        if b:
            t = np.minimum(t, np.clip((n - i - 0.5) / b, 0, 1))
        return t * t * (3 - 2 * t)

    alpha = np.outer(ramp(height, top, bottom), ramp(width, left, right))
    Image.fromarray(np.rint(alpha * 255).astype(np.uint8), "L").save(destination, format="PNG")


def render(source, lang, cuts, background, output, report):
    parts = []
    for cut in cuts:
        name = cut["name"]
        part, overlay = WORK / f"{lang}-{name}.mkv", WORK / f"{lang}-{name}-caption.png"
        mask = WORK / f"{lang}-{name}-mask.png"
        caption(lang, cut["caption"], overlay)
        x, y, w, h = cut["crop"]
        px, py, pw, ph = cut["panel"]
        edge_mask((pw, ph), cut["feather"], mask)
        graph = (f"[0:v]fps={FPS}:start_time=0:round=near,trim=end_frame={cut['frames']},"
                 f"setpts=N/({FPS}*TB),crop={w}:{h}:{x}:{y},scale={pw}:{ph}:flags=lanczos:"
                 "in_color_matrix=bt709:out_color_matrix=bt709:in_range=tv:out_range=tv,"
                 "format=yuva444p,setsar=1[crop];"
                 "[3:v]format=gray,setsar=1[mask];"
                 "[crop][mask]alphamerge[panel];"
                 "[1:v]scale=iw:ih:in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv444p[bg];"
                 "[2:v]scale=iw:ih:in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuva444p[cap];"
                 f"[bg][panel]overlay={px}:{py}:format=yuv444:shortest=1[base];"
                 "[base][cap]overlay=0:0:format=yuv444:shortest=1,format=yuv444p[v]")
        ff(["-ss", str(cut["source_in"]), "-i", source,
            "-loop", "1", "-framerate", FPS, "-i", background,
            "-loop", "1", "-framerate", FPS, "-i", overlay,
            "-loop", "1", "-framerate", FPS, "-i", mask,
            "-filter_complex", graph, "-map", "[v]", "-an", "-frames:v", cut["frames"],
            "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv444p", *TAGS, part], name, report)
        parts.append(part)
    concat = WORK / f"{lang}-segments.txt"
    concat.write_text("".join(f"file '{p.as_posix()}'\n" for p in parts), encoding="utf-8")
    audio = f"[1:a]asplit={len(cuts)}" + "".join(f"[s{i}]" for i in range(len(cuts))) + ";"
    for i, cut in enumerate(cuts):
        # Symmetric 6 ms handles cancel exactly in each 12 ms crossfade.
        a = cut["source_in"] - (0.006 if i else 0)
        b = cut["source_out"] + (0.006 if i < len(cuts)-1 else 0)
        audio += (f"[s{i}]atrim=start={a:.9f}:end={b:.9f},asetpts=PTS-STARTPTS,"
                  f"aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a{i}];")
    current = "a0"
    for i in range(1, len(cuts)):
        audio += f"[{current}][a{i}]acrossfade=ns=576:c1=tri:c2=tri[j{i}];"
        current = f"j{i}"
    duration = sum(c["frames"] for c in cuts) / FPS
    audio += (f"[{current}]apad=whole_dur={duration:.9f},atrim=duration={duration:.9f},"
              f"afade=t=out:st={duration-0.3:.9f}:d=0.3[a]")
    ff(["-f", "concat", "-safe", "0", "-i", concat, "-i", source,
        "-filter_complex", audio, "-map", "0:v:0", "-map", "[a]",
        # FFmpeg 7 takes colour tags from the frames, so set them on the frames as well.
        "-vf", "scale=iw:ih:in_color_matrix=bt709:out_color_matrix=bt709:in_range=tv:out_range=tv,format=yuv420p,"
               "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
        "-c:v", "libx264", "-profile:v", "high", "-crf", "18", "-preset", "slow",
        "-pix_fmt", "yuv420p", "-r", FPS, "-frames:v", sum(c["frames"] for c in cuts),
        *TAGS, "-c:a", "aac", "-b:a", "192k",
        "-ar", "48000", "-ac", "2", "-movflags", "+faststart", "-t", f"{duration:.9f}", output], "encode", report)


def named(cuts, name):
    return next(c for c in cuts if c["name"] == name)


def stills(source, output, lang, cuts, graphite, report):
    cut = named(cuts, "redact")
    at = cut["first_frame"]/FPS + (14.8-cut["source_in"])
    cover = OUT / f"cover-{lang}-1080x1920.png"
    save_png(frame(output, at, report, "cover"), cover)
    comparison = Image.new("RGB", (1080, 1350), graphite)
    draw = ImageDraw.Draw(comparison)
    draw.text((540, 65), "Ctrl+Shift+R", font=font(lang, 48), fill=CREAM, anchor="mm")
    labels = ("가리기 전", "가리기 후") if lang == "ko" else ("Before", "After")
    for i, (t, crop) in enumerate([(12.4, (482, 320, 500, 250)), (14.8, (532, 324, 900, 450))]):
        top = 190 + i*580
        draw.text((60, top-54), labels[i], font=font(lang, 36), fill=CREAM)
        comparison.paste(frame(source, t, report, f"comparison-{i}", crop, (960, 480)), (60, top))
    save_png(comparison, OUT / f"redact-before-after-{lang}-1080x1350.png")


def checks(source, output, lang, cuts, probe, report):
    directory = OUT / f"frames-{lang}"
    directory.mkdir(parents=True, exist_ok=True)
    samples = [(f"{i+1:02d}-{c['name']}-middle", (c["first_frame"]+c["frames"]//2)/FPS)
               for i, c in enumerate(cuts)]
    samples += [(f"seam-{i+1:02d}-plus-two", (c["first_frame"]+2)/FPS)
                for i, c in enumerate(cuts[1:])]
    sheet = Image.new("RGB", (1080, math.ceil(len(samples) / 3) * 690), (24, 28, 35))
    draw = ImageDraw.Draw(sheet)
    for i, (name, t) in enumerate(samples):
        picture = frame(output, t, report, name)
        save_png(picture, directory / f"{name}.png")
        picture.thumbnail((360, 640), Image.Resampling.LANCZOS)
        left, top = (i % 3)*360, (i // 3)*690
        sheet.paste(picture, (left, top+40))
        draw.text((left+8, top+8), name, font=font("en", 19), fill=CREAM)
    save_png(sheet, OUT / f"contact-sheet-{lang}.png")
    if probe:
        data, _ = run([probe, "-v", "error", "-count_frames", "-show_streams", "-show_format",
                       "-of", "json", output], "ffprobe", report)
        metadata = json.loads(data)
        (OUT / f"ffprobe-{lang}.json").write_bytes(data)
        video = next(s for s in metadata["streams"] if s["codec_type"] == "video")
        audio = next(s for s in metadata["streams"] if s["codec_type"] == "audio")
        expected = dict(width=1080, height=1920, codec_name="h264", profile="High", pix_fmt="yuv420p",
                        color_space="bt709", color_transfer="bt709", color_primaries="bt709", color_range="tv")
        issues = [f"{k}: {video.get(k)!r}" for k, v in expected.items() if video.get(k) != v]
        for condition, message in [(video.get("r_frame_rate") == "60/1", "frame rate"),
                                   (int(video.get("nb_read_frames", 0)) == sum(c["frames"] for c in cuts), "frame count"),
                                   (18 <= float(metadata["format"]["duration"]) <= 22, "duration"),
                                   (audio.get("codec_name") == "aac" and audio.get("sample_rate") == "48000"
                                    and audio.get("channels") == 2, "audio format")]:
            if not condition:
                issues.append(message)
        report["ffprobe"] = dict(status="FAIL" if issues else "PASS", issues=issues, metadata=metadata)
    else:
        # imageio-ffmpeg ships no ffprobe: read the stream header and count decoded frames.
        _, header = ff(["-i", output, "-map", "0", "-c", "copy", "-f", "null", "-"], "stream-header", report)
        _, decoded = ff(["-i", output, "-map", "0:v:0", "-f", "null", "-"], "frame-count", report)
        video = re.search(r"Video: (.+)", header)[1]
        audio = re.search(r"Audio: (.+)", header)[1]
        frames = int(re.findall(r"frame=\s*(\d+)", decoded)[-1])
        h, m, s = re.search(r"Duration: (\d+):(\d+):([\d.]+)", header).groups()
        duration = int(h) * 3600 + int(m) * 60 + float(s)
        issues = [want for want in ("h264 (High)", "yuv420p(tv, bt709, progressive)", "1080x1920", "60 fps")
                  if want not in video]
        issues += [want for want in ("aac", "48000 Hz", "stereo") if want not in audio]
        if frames != sum(c["frames"] for c in cuts):
            issues.append(f"frame count {frames}")
        if not 18 <= duration <= 22:
            issues.append(f"duration {duration}")
        report["ffprobe"] = dict(status="FAIL" if issues else "PASS", method="ffmpeg -i header and decode",
                                 video=video.strip(), audio=audio.strip(), frames=frames,
                                 duration_seconds=duration, issues=issues)
    for name, path in [("source", source), ("output", output)]:
        _, text = ff(["-i", path, "-map", "0:a:0", "-vn", "-af", "ebur128=peak=true", "-f", "null", "-"], f"loudness-{name}", report)
        integrated = re.findall(r"\bI:\s*(-?[\d.]+)\s+LUFS", text)
        peak = re.findall(r"Peak:\s*(-?[\d.]+)\s+dBFS", text)
        report[f"loudness_{name}"] = dict(integrated_lufs=float(integrated[-1]) if integrated else None,
                                          true_peak_dbfs=float(peak[-1]) if peak else None,
                                          status="PASS" if integrated else "FAIL")
    # Use uniform interiors, not antialiased/scaled edge pixels, for matrix checks.
    cut, source_time = named(cuts, "annotation"), 18.45
    output_time = cut["first_frame"]/FPS + source_time-cut["source_in"]
    original = np.asarray(frame(source, source_time, report, "color-source"))
    encoded = np.asarray(frame(output, output_time, report, "color-output"))
    bg1 = encoded[1700:1780, 980:1040]
    colors = {"background": (np.array(report["sampled_graphite_rgb"], dtype=float),
                              np.median(bg1, axis=(0, 1)))}
    report["color_sampling"] = dict(background_source_seconds=0, background_source_rect=[1800, 1020, 100, 50],
                                   background_output_rect=[980, 1700, 60, 80],
                                   annotation_source_seconds=source_time, annotation_output_seconds=output_time,
                                   annotation_method="Median of seven-pixel-eroded red interiors; mapped to output panel")
    channels = original.astype(np.int16)
    mask = (channels[:, :, 0] > 130) & (channels[:, :, 0]-channels[:, :, 1] > 70) & (channels[:, :, 2] < 110)
    interior = mask.copy()
    interior[:3, :] = interior[-3:, :] = False
    interior[:, :3] = interior[:, -3:] = False
    for dy in range(-3, 4):
        for dx in range(-3, 4):
            interior &= np.roll(mask, (dy, dx), axis=(0, 1))
    x, y, w, h = cut["crop"]
    inside = np.zeros_like(interior)
    inside[y + 4:y + h - 4, x + 4:x + w - 4] = True
    ys, xs = np.where(interior & inside)
    if len(xs) >= 20:
        px, py, pw, ph = cut["panel"]
        ox = np.rint(px+(xs-x+0.5)*pw/w-0.5).astype(int)
        oy = np.rint(py+(ys-y+0.5)*ph/h-0.5).astype(int)
        colors["red_annotation"] = (np.median(original[ys, xs], axis=0), np.median(encoded[oy, ox], axis=0))
    else:
        report["red_annotation"] = dict(status="FAIL", reason="No sufficiently broad red interior found")
    for name, (a, b) in colors.items():
        delta = np.abs(a-b)
        report[name] = dict(status="PASS" if max(delta) <= 3 else "FAIL", source_rgb=a.tolist(),
                            output_rgb=b.tolist(), absolute_difference=delta.tolist(), target_per_channel=3)
    report["manual_qa"] = "NOT RUN: inspect every middle/seam frame, source clipping, ending text and listen to all seams"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lang", choices=["ko", "en", "all"], default="all")
    parser.add_argument("--ffprobe", type=Path, help="Absolute path to a separately available ffprobe executable")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    if args.ffprobe and (not args.ffprobe.is_absolute() or not args.ffprobe.is_file()):
        parser.error("--ffprobe must be an existing absolute executable path")
    OUT.mkdir(parents=True, exist_ok=True)
    WORK.mkdir(parents=True, exist_ok=True)
    incomplete = False
    for lang in (["ko", "en"] if args.lang == "all" else [args.lang]):
        source = INPUT / ("v2/MyCapture-trailer-store-1080p60.mp4" if lang == "ko"
                          else "v2-en/MyCapture-trailer-store-en-1080p60.mp4")
        output = OUT / f"MyCapture-vertical-{lang}-1080x1920.mp4"
        cuts = segments(lang)
        report = dict(language=lang, script_status="final", source=str(source), output=str(output),
                      segments=cuts, commands=[], execution_status="RUNNING")
        try:
            version, _ = ff(["-version"], "ffmpeg-version", report)
            report["ffmpeg_version"] = version.decode("utf-8", errors="replace").splitlines()[0]
            graphite = tuple(int(x) for x in np.median(np.asarray(frame(source, 0, report, "background-sample"))[1020:1070, 1800:1900], axis=(0, 1)))
            report["sampled_graphite_rgb"] = graphite
            if not args.verify_only:
                background = WORK / f"{lang}-background.png"
                save_png(Image.new("RGB", (WIDTH, HEIGHT), graphite), background)
                render(source, lang, cuts, background, output, report)
                stills(source, output, lang, cuts, graphite, report)
            checks(source, output, lang, cuts, args.ffprobe, report)
            required = [output, OUT / f"cover-{lang}-1080x1920.png",
                        OUT / f"redact-before-after-{lang}-1080x1350.png", OUT / f"contact-sheet-{lang}.png"]
            missing = [str(p) for p in required if not p.is_file()]
            report["deliverables"] = dict(status="FAIL" if missing else "PASS", missing=missing)
            report["file_sizes_bytes"] = {str(p): p.stat().st_size for p in required if p.is_file()}
            issues = [name for name, value in report.items()
                      if isinstance(value, dict) and value.get("status") in ("FAIL", "NOT RUN")]
            report["automated_checks"] = dict(status="INCOMPLETE" if issues else "PASS", issues=issues)
            incomplete |= bool(issues)
            report["execution_status"] = "FINISHED; manual QA remains pending"
        except Exception as error:
            report.update(execution_status="FAILED", error=str(error))
            raise
        finally:
            (OUT / f"verification-{lang}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 2 if incomplete else 0


if __name__ == "__main__":
    raise SystemExit(main())
