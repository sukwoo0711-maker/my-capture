"""Compose honest Store screenshots from exported MyCapture window layers.

Python 3.12 and Pillow. Coordinates in scenes.json are raw layer pixels for
crop, canvas pixels for placement, and output/source pixels for scale.
No application, capture API, clipboard, input device, or network is used.
"""

from __future__ import annotations

import argparse
import io
import json
import math
from pathlib import Path, PurePosixPath
import re
import sys
import unicodedata


STYLE = {
    "canvas": (2560, 1440),
    "background_top": (0x16, 0x19, 0x1F),
    "background_bottom": (0x10, 0x13, 0x18),
    "title_color": (0xF3, 0xF5, 0xF8),
    "subline_color": (0x9A, 0xA5, 0xB5),
    "brand_red": (0xE5, 0x48, 0x3B),
    "font_ko": "C:/Windows/Fonts/NotoSansKR-VF.ttf",
    "font_en": "C:/Windows/Fonts/SegUIVar.ttf",
    "title_size": 132,
    "title_weight": 700,
    "title_top": 104,
    "title_line_height": 1.15,
    "title_max_lines": 2,
    "minimum_card_x_height": 7.0,
    "margin": 160,
    "subline_size": 44,
    "subline_weight": 400,
    "subline_gap": 32,
    "header_window_gap": 48,
    "shadow_opacity": 128,
    "shadow_blur": 60,
    "shadow_offset": (0, 24),
    "shadow_padding_factor": 3,
    "hairline_opacity": 23,
    "hairline_width": 1,
    "red_outline_width": 2,
    "comparison_top": 470,
    "comparison_column_gap": 160,
    "comparison_heading_size": 44,
    "comparison_heading_weight": 600,
    "comparison_heading_gap": 40,
    "comparison_rule_gap": 44,
    "comparison_rule_opacity": 26,
    "comparison_body_size": 60,
    "comparison_body_weight": 500,
    "comparison_line_height": 1.3,
    "comparison_item_gap": 30,
    "comparison_left_color": (0x9A, 0xA5, 0xB5),
    "comparison_right_color": (0xF3, 0xF5, 0xF8),
    "comparison_bottom_margin": 72,
    "contact_size": (640, 360),
    "contact_columns": 3,
    "qa_gap": 24,
    "qa_label_size": 22,
    "qa_label_weight": 600,
    "qa_label_height": 38,
    "png_compress_level": 9,
}

try:
    from PIL import Image, ImageChops, ImageCms, ImageDraw, ImageFilter, ImageFont, ImageOps
except ImportError as exc:
    raise SystemExit("ERROR: Pillow is required in this Python environment: " + str(exc)) from exc


class CompositionError(ValueError):
    """An input or layout cannot meet the composition contract."""


def require(condition, message):
    if not condition:
        raise CompositionError(message)


def number(value, label, *, positive=False):
    require(type(value) in (int, float) and math.isfinite(value), f"{label}: expected a finite number")
    require(not positive or value > 0, f"{label}: must be greater than zero")
    return value


def integer(value, label, *, positive=False):
    number(value, label, positive=positive)
    require(type(value) is int, f"{label}: expected an integer")
    return value


def text_value(value, label, *, optional=False):
    require(isinstance(value, str), f"{label}: expected text")
    require(value == value.strip(), f"{label}: remove leading or trailing whitespace")
    require(optional or bool(value), f"{label}: text is empty")
    require(not any(ord(c) < 32 and c != "\n" for c in value), f"{label}: control character")
    return value


def rect(value, size, label):
    require(isinstance(value, list) and len(value) == 4, f"{label}: expected [left, top, right, bottom]")
    x0, y0, x1, y1 = (integer(v, label) for v in value)
    require(0 <= x0 < x1 <= size[0] and 0 <= y0 < y1 <= size[1],
            f"{label}: {value} is outside {size[0]} x {size[1]}")
    return (x0, y0, x1, y1)


def read_json(path):
    require(path.is_absolute(), f"JSON path must be absolute: {path}")
    try:
        with path.open("r", encoding="utf-8-sig") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise CompositionError(f"Cannot read JSON {path}: {exc}") from exc


def layer_path(root, value, label):
    text_value(value, label)
    require("\\" not in value and ":" not in value, f"{label}: use a relative slash-separated path")
    relative = PurePosixPath(value)
    require(not relative.is_absolute() and ".." not in relative.parts and relative.parts,
            f"{label}: path must stay inside --layers")
    resolved = root.joinpath(*relative.parts).resolve()
    require(resolved.is_relative_to(root), f"{label}: resolved path is outside --layers")
    require(resolved.is_file(), f"Missing layer: {resolved}")
    return resolved


def title_check(scene, language):
    key = f"title_{language}"
    title = text_value(scene.get(key), f"scene {scene['id']} {key}")
    require(title == unicodedata.normalize("NFC", title), f"scene {scene['id']} {key}: use NFC text")
    require("\n" not in title, f"scene {scene['id']} {key}: keep the title on one logical line")
    require(not any(mark in title for mark in ("!", "?", "！", "？")),
            f"scene {scene['id']} {key}: exclamations and questions are not allowed")
    if language == "ko":
        length = len(re.sub(r"\s", "", title))
        require(6 <= length <= 14, f"scene {scene['id']} {key}: {length} characters excluding spaces; expected 6–14")
        require(not re.search(r"(하세요|해요|예요|이에요)[.]?$", title),
                f"scene {scene['id']} {key}: use a noun phrase or 합니다체")
    else:
        require(len(title) <= 34, f"scene {scene['id']} {key}: {len(title)} characters; maximum is 34")
        require(not title.endswith("."), f"scene {scene['id']} {key}: remove the final period")
    return title


class Fonts:
    def __init__(self):
        self.cache = {}
        self.axes_used = {}

    def get(self, language, size, weight):
        key = (language, size, weight)
        if key in self.cache:
            return self.cache[key]
        path = Path(STYLE[f"font_{language}"]).resolve()
        require(path.is_file(), f"Required font missing: {path}")
        try:
            font = ImageFont.truetype(str(path), size)
            axes = font.get_variation_axes()
            values, report = [], []
            weight_found = optical_found = False
            for axis in axes:
                name = axis["name"]
                name = name.decode("utf-8", errors="replace") if isinstance(name, bytes) else str(name)
                lowered = name.lower()
                value = axis["default"]
                if "weight" in lowered or lowered == "wght":
                    value, weight_found = weight, True
                elif "optical" in lowered or lowered == "opsz":
                    # The largest optical size is the Display end of Segoe UI Variable.
                    value, optical_found = axis["maximum"], True
                require(axis["minimum"] <= value <= axis["maximum"], f"Font axis out of range: {name}={value}")
                values.append(value)
                report.append({"axis": name, "value": value})
            require(weight_found, f"Required weight axis missing in {path}")
            require(language != "en" or optical_found, f"Required Display optical-size axis missing in {path}")
            font.set_variation_by_axes(values)
        except (OSError, ValueError) as exc:
            raise CompositionError(f"Cannot use the required variable font {path}: {exc}") from exc
        self.cache[key] = font
        self.axes_used[f"{language}/{size}/{weight}"] = report
        return font


def glyph_height(font, value):
    bounds = font.getmask(value).getbbox()
    require(bounds is not None, f"Font has no visible glyph for {value!r}")
    return bounds[3] - bounds[1]


def wrap_text(value, font, width):
    """Prefer word boundaries; split a word only when it cannot fit by itself."""
    lines = []
    for paragraph in value.split("\n"):
        require(bool(paragraph), "Empty lines are not allowed in composition text")
        line = ""
        for word in paragraph.split():
            trial = f"{line} {word}" if line else word
            if font.getlength(trial) <= width:
                line = trial
                continue
            if line:
                lines.append(line)
                line = ""
            for char in word:
                if line and font.getlength(line + char) > width:
                    lines.append(line)
                    line = ""
                require(font.getlength(char) <= width, "A text glyph is wider than its column")
                line += char
        if line:
            lines.append(line)
    return lines


def draw_lines(draw, value, font, x, y, width, line_height, color):
    lines = wrap_text(value, font, width)
    for line in lines:
        # 'lt' makes y the visible glyph top, independent of font ascender metrics.
        draw.text((x, y), line, font=font, fill=color, anchor="lt")
        y += line_height
    return y, lines


def header_layout(scene, language, fonts):
    title = title_check(scene, language)
    font = fonts.get(language, STYLE["title_size"], STYLE["title_weight"])
    width = STYLE["canvas"][0] - 2 * STYLE["margin"]
    lines = wrap_text(title, font, width)
    require(len(lines) <= STYLE["title_max_lines"], f"scene {scene['id']}: title needs more than two lines")
    line_height = round(STYLE["title_size"] * STYLE["title_line_height"])
    bottom = STYLE["title_top"] + (len(lines) - 1) * line_height + max(glyph_height(font, line) for line in lines)
    subline = text_value(scene.get(f"subline_{language}", ""), f"scene {scene['id']} subline_{language}", optional=True)
    require("\n" not in subline, f"scene {scene['id']}: subline must be one line")
    subfont = fonts.get(language, STYLE["subline_size"], STYLE["subline_weight"])
    require(subfont.getlength(subline) <= width, f"scene {scene['id']}: subline exceeds the available width")
    sub_top = bottom + STYLE["subline_gap"]
    if subline:
        bottom = sub_top + glyph_height(subfont, subline)
    return {"font": font, "lines": lines, "line_height": line_height,
            "subfont": subfont, "subline": subline, "sub_top": sub_top, "bottom": bottom}


def load_image(path, srgb_profile):
    require(path.is_absolute(), f"Image path must be absolute: {path}")
    try:
        with Image.open(path) as source:
            require(source.format == "PNG" or source.format == "JPEG", f"Unsupported image type: {path}")
            source.load()
            alpha = source.convert("RGBA").getchannel("A")
            color = source.convert("RGB")
            if source.info.get("icc_profile"):
                profile = ImageCms.ImageCmsProfile(io.BytesIO(source.info["icc_profile"]))
                color = ImageCms.profileToProfile(color, profile, srgb_profile, outputMode="RGB")
            result = color.convert("RGBA")
            result.putalpha(alpha)
            return result
    except (OSError, ValueError, ImageCms.PyCMSError) as exc:
        raise CompositionError(f"Cannot read image {path}: {exc}") from exc


def preflight(config, layers_root, selected, language, fonts, srgb_profile):
    require(isinstance(config, dict) and config.get("version") == 1, "scenes.json: expected version 1")
    scenes = config.get("scenes")
    require(isinstance(scenes, list) and len(scenes) == 9, "scenes.json: expected exactly nine scenes")
    require(all(isinstance(s, dict) and type(s.get("id")) is int for s in scenes), "Every scene needs an integer id")
    require([s["id"] for s in scenes] == list(range(1, 10)), "Scene ids must be 1 through 9 in order")
    for scene in scenes:
        title_check(scene, "ko")
        title_check(scene, "en")
    needed = [s for s in scenes if s["id"] in selected or s["id"] == 1]
    manifest = read_json((layers_root / "manifest.json").resolve())
    require(isinstance(manifest, dict) and isinstance(manifest.get("files"), list), "manifest.json: expected files array")
    entries = {}
    for entry in manifest["files"]:
        require(isinstance(entry, dict) and isinstance(entry.get("path"), str), "manifest.json: every file needs a path")
        require(entry["path"] not in entries, f"manifest.json: duplicate path {entry['path']}")
        entries[entry["path"]] = entry
    images, headers, layer_metrics = {}, {}, {}
    for scene in needed:
        sid = scene["id"]
        headers[sid] = header_layout(scene, language, fonts)
        layers = scene.get("layers")
        require(isinstance(layers, list), f"scene {sid}: layers must be an array")
        require(bool(layers) if sid != 9 else not layers, f"scene {sid}: scenes 1–8 need layers; scene 9 is type only")
        layer_metrics[sid] = []
        for index, layer in enumerate(layers):
            label = f"scene {sid} layer {index + 1}"
            require(isinstance(layer, dict), f"{label}: expected an object")
            path = layer_path(layers_root, layer.get("path"), label)
            require(layer["path"] in entries, f"{label}: absent from manifest.json: {layer['path']}")
            entry = entries[layer["path"]]
            width = integer(entry.get("width"), f"{label} manifest width", positive=True)
            height = integer(entry.get("height"), f"{label} manifest height", positive=True)
            render_scale = number(entry.get("renderScale"), f"{label} renderScale", positive=True)
            if path not in images:
                images[path] = load_image(path, srgb_profile)
            require(images[path].size == (width, height), f"{label}: actual image size {images[path].size} differs from manifest {(width, height)}")
            crop = rect(layer.get("crop"), (width, height), f"{label} crop")
            placement = layer.get("placement")
            require(isinstance(placement, list) and len(placement) == 2, f"{label}: placement must be [x, y]")
            x, y = (integer(v, f"{label} placement") for v in placement)
            scale = number(layer.get("scale"), f"{label} scale", positive=True)
            number(layer.get("z", 0), f"{label} z")
            output_size = (round((crop[2] - crop[0]) * scale), round((crop[3] - crop[1]) * scale))
            require(min(output_size) > 0 and max(output_size) <= 16384, f"{label}: invalid resized dimensions {output_size}")
            require(x < STYLE["canvas"][0] and y < STYLE["canvas"][1] and x + output_size[0] > 0 and y + output_size[1] > 0,
                    f"{label}: layer is completely outside the canvas")
            require(y >= headers[sid]["bottom"] + STYLE["header_window_gap"], f"{label}: layer overlaps title/subline whitespace")
            text_value(layer.get("final"), f"{label} final")
            layer_metrics[sid].append({"path": layer["path"], "crop": list(crop), "placement": placement,
                "raw_pixel_scale": scale, "source_render_scale": render_scale,
                "logical_pixel_scale": round(scale * render_scale, 4), "output_size": list(output_size),
                "ui_body_14_dip_px": round(14 * scale * render_scale, 2)})
        if scene.get("red_outline") is not None:
            require(sid != 9, "The comparison card does not use an accent outline")
            outline = scene["red_outline"]
            require(isinstance(outline, dict), f"scene {sid}: red_outline must be an object")
            rect(outline.get("rect"), STYLE["canvas"], f"scene {sid} red_outline")
            require(outline.get("width", STYLE["red_outline_width"]) == STYLE["red_outline_width"],
                    f"scene {sid}: use the STYLE red outline width")
    search = config.get("search_card")
    require(isinstance(search, dict), "scenes.json: search_card object missing")
    evidence_value = text_value(search.get("evidence_path"), "search_card evidence_path")
    evidence_path = Path(evidence_value)
    require(evidence_path.is_absolute(), "search_card evidence_path must be absolute")
    evidence_path = evidence_path.resolve()
    require(evidence_path.is_file(), f"Search evidence missing: {evidence_path}")
    evidence = load_image(evidence_path, srgb_profile)
    thumbnail = rect(search.get("thumbnail_rect"), evidence.size, "search_card thumbnail_rect")
    competitors = search.get("competitor_rects")
    require(isinstance(competitors, list) and competitors, "search_card: competitor_rects must contain at least one crop")
    for index, competitor in enumerate(competitors):
        rect(competitor, evidence.size, f"search_card competitor {index + 1}")
    card_width, card_height = thumbnail[2] - thumbnail[0], thumbnail[3] - thumbnail[1]
    expected_height = round(card_width * STYLE["canvas"][1] / STYLE["canvas"][0])
    require(abs(card_height - expected_height) <= 1, "search_card thumbnail_rect must measure the 16:9 screenshot, excluding card padding")
    card_scale = card_width / STYLE["canvas"][0]
    font = headers[1]["font"]
    x_bbox = font.getbbox("x")
    x_height = x_bbox[3] - x_bbox[1]
    require(x_height * card_scale >= STYLE["minimum_card_x_height"],
            f"Title x-height at card scale is {x_height * card_scale:.2f}px; increase STYLE title_size to reach {STYLE['minimum_card_x_height']}px")
    generator = manifest.get("generator")
    source_language = generator.get("language") if isinstance(generator, dict) else None
    if not isinstance(source_language, str):
        source_language = "unspecified"
    metrics = {"language": language, "canvas": STYLE["canvas"], "selected_scenes": sorted(selected),
        "search_card_size": [card_width, card_height], "search_card_scale": card_scale,
        "title_size": STYLE["title_size"], "title_x_bbox": list(x_bbox), "title_x_height_px": x_height,
        "title_x_ink_height_px": glyph_height(font, "x"),
        "title_x_height_at_card_px": round(x_height * card_scale, 3),
        "title_hangul_height_at_card_px": round(glyph_height(font, "가") * card_scale, 3) if language == "ko" else None,
        "margin": STYLE["margin"], "layers": layer_metrics,
        "prototype_source_language": source_language}
    return needed, images, headers, evidence, metrics


def fixed_icc(profile):
    """Return the sRGB profile bytes with a fixed creation date and no profile ID, so repeated runs write identical files."""
    data = bytearray(profile.tobytes())
    require(len(data) >= 128 and data[36:40] == b"acsp", "Unexpected ICC profile header")
    data[24:36] = (2026).to_bytes(2, "big") + (1).to_bytes(2, "big") + (1).to_bytes(2, "big") + bytes(6)
    data[84:100] = bytes(16)
    return bytes(data)


def background():
    width, height = STYLE["canvas"]
    strip = Image.new("RGB", (1, height))
    top, bottom = STYLE["background_top"], STYLE["background_bottom"]
    strip.putdata([tuple(round(a + (b - a) * y / (height - 1)) for a, b in zip(top, bottom)) for y in range(height)])
    return strip.resize((width, height), Image.Resampling.BILINEAR).convert("RGBA")


def composite_clipped(canvas, image, x, y):
    left, top = max(0, x), max(0, y)
    right, bottom = min(canvas.width, x + image.width), min(canvas.height, y + image.height)
    if right > left and bottom > top:
        crop = image.crop((left - x, top - y, right - x, bottom - y))
        canvas.alpha_composite(crop, (left, top))


def place_layer(canvas, source, layer):
    crop = source.crop(tuple(layer["crop"]))
    scale = layer["scale"]
    image = crop.resize((round(crop.width * scale), round(crop.height * scale)), Image.Resampling.LANCZOS)
    alpha = image.getchannel("A")
    pad = STYLE["shadow_blur"] * STYLE["shadow_padding_factor"]
    mask = Image.new("L", (image.width + 2 * pad, image.height + 2 * pad), 0)
    mask.paste(alpha, (pad, pad))
    mask = mask.filter(ImageFilter.GaussianBlur(STYLE["shadow_blur"]))
    mask = mask.point(lambda value: round(value * STYLE["shadow_opacity"] / 255))
    shadow = Image.new("RGBA", mask.size, (0, 0, 0, 0))
    shadow.putalpha(mask)
    x, y = layer["placement"]
    dx, dy = STYLE["shadow_offset"]
    composite_clipped(canvas, shadow, x - pad + dx, y - pad + dy)
    # Erode the original alpha, including its external boundary. This preserves
    # transparent corners and never invents a rounded frame over source pixels.
    border_width = STYLE["hairline_width"]
    padded = ImageOps.expand(alpha, border=border_width, fill=0)
    eroded = padded.filter(ImageFilter.MinFilter(2 * border_width + 1)).crop(
        (border_width, border_width, image.width + border_width, image.height + border_width))
    edge = ImageChops.subtract(alpha, eroded).point(lambda value: round(value * STYLE["hairline_opacity"] / 255))
    hairline = Image.new("RGBA", image.size, (255, 255, 255, 0))
    hairline.putalpha(edge)
    image.alpha_composite(hairline)
    image.putalpha(alpha)
    composite_clipped(canvas, image, x, y)


def comparison(canvas, scene, language, fonts, header):
    content = scene.get("comparison")
    require(isinstance(content, dict), "scene 9: comparison object missing")
    width = (canvas.width - 2 * STYLE["margin"] - STYLE["comparison_column_gap"]) // 2
    heading_font = fonts.get(language, STYLE["comparison_heading_size"], STYLE["comparison_heading_weight"])
    body_font = fonts.get(language, STYLE["comparison_body_size"], STYLE["comparison_body_weight"])
    draw = ImageDraw.Draw(canvas)
    top = STYLE["comparison_top"]
    require(top >= header["bottom"] + STYLE["header_window_gap"], "scene 9: columns overlap title whitespace")
    for column, side in enumerate(("left", "right")):
        x = STYLE["margin"] + column * (width + STYLE["comparison_column_gap"])
        heading = text_value(content.get(f"{side}_heading_{language}"), f"scene 9 {side} heading")
        require(heading_font.getlength(heading) <= width and "\n" not in heading, f"scene 9 {side}: heading does not fit")
        draw.text((x, top), heading, font=heading_font, fill=STYLE["subline_color"], anchor="lt")
        rule_y = top + STYLE["comparison_heading_size"] + STYLE["comparison_heading_gap"]
        rule = Image.new("RGBA", (width, 1), (255, 255, 255, STYLE["comparison_rule_opacity"]))
        canvas.paste(rule, (x, rule_y), rule)
        y = rule_y + STYLE["comparison_rule_gap"]
        color = STYLE[f"comparison_{side}_color"]
        items = content.get(f"{side}_{language}")
        require(isinstance(items, list) and items, f"scene 9 {side}: expected a non-empty text array")
        line_height = round(STYLE["comparison_body_size"] * STYLE["comparison_line_height"])
        for index, item in enumerate(items):
            value = text_value(item, f"scene 9 {side} item {index + 1}")
            lines = wrap_text(value, body_font, width)
            last_bottom = y + (len(lines) - 1) * line_height + max(glyph_height(body_font, line) for line in lines)
            require(last_bottom <= canvas.height - STYLE["comparison_bottom_margin"], f"scene 9 {side}: text exceeds the bottom margin")
            y, _ = draw_lines(draw, value, body_font, x, y, width, line_height, color)
            y += STYLE["comparison_item_gap"]


def render_scene(scene, language, images, layers_root, fonts, header):
    canvas = background()
    for layer in sorted(scene["layers"], key=lambda item: item.get("z", 0)):
        source = images[layer_path(layers_root, layer["path"], f"scene {scene['id']}")]
        place_layer(canvas, source, layer)
    if scene["id"] == 9:
        comparison(canvas, scene, language, fonts, header)
    if scene.get("red_outline"):
        x0, y0, x1, y1 = scene["red_outline"]["rect"]
        ImageDraw.Draw(canvas).rectangle((x0, y0, x1 - 1, y1 - 1), outline=STYLE["brand_red"], width=STYLE["red_outline_width"])
    draw = ImageDraw.Draw(canvas)
    for index, line in enumerate(header["lines"]):
        draw.text((STYLE["margin"], STYLE["title_top"] + index * header["line_height"]), line,
                  font=header["font"], fill=STYLE["title_color"], anchor="lt")
    if header["subline"]:
        draw.text((STYLE["margin"], header["sub_top"]), header["subline"], font=header["subfont"],
                  fill=STYLE["subline_color"], anchor="lt")
    return canvas.convert("RGB")


def contact_sheet(rendered, selected):
    width, height = STYLE["contact_size"]
    gap = STYLE["qa_gap"]
    columns = min(STYLE["contact_columns"], len(selected))
    rows = math.ceil(len(selected) / columns)
    sheet = Image.new("RGB", (columns * width + (columns + 1) * gap, rows * height + (rows + 1) * gap), STYLE["background_bottom"])
    for index, sid in enumerate(sorted(selected)):
        sheet.paste(rendered[sid].resize((width, height), Image.Resampling.LANCZOS),
                    (gap + index % columns * (width + gap), gap + index // columns * (height + gap)))
    return sheet


def search_card(first, evidence, config, language, fonts):
    thumb = config["thumbnail_rect"]
    width, height = thumb[2] - thumb[0], thumb[3] - thumb[1]
    cards = [evidence.crop(tuple(thumb)).convert("RGB"), first.resize((width, height), Image.Resampling.LANCZOS)]
    labels = ["현재 등록 이미지", "새 구성 시안"] if language == "ko" else ["Current listing", "New composition"]
    for index, bounds in enumerate(config["competitor_rects"]):
        # Keep evidence at its captured scale. The competitor crop is not zoomed.
        cards.append(evidence.crop(tuple(bounds)).convert("RGB"))
        labels.append(f"경쟁 앱 {index + 1}" if language == "ko" else f"Competitor {index + 1}")
    gap, label_height = STYLE["qa_gap"], STYLE["qa_label_height"]
    size = (sum(card.width for card in cards) + gap * (len(cards) + 1), max(card.height for card in cards) + 2 * gap + label_height)
    sheet = Image.new("RGB", size, STYLE["background_bottom"])
    font = fonts.get(language, STYLE["qa_label_size"], STYLE["qa_label_weight"])
    draw, x = ImageDraw.Draw(sheet), gap
    for card, label in zip(cards, labels):
        require(font.getlength(label) <= card.width, "QA label is wider than its evidence crop")
        draw.text((x, gap), label, font=font, fill=STYLE["subline_color"], anchor="lt")
        sheet.paste(card, (x, gap + label_height))
        x += card.width + gap
    return sheet


def parse_only(value):
    require(bool(re.fullmatch(r"[1-9](,[1-9])*", value)), "--only must be comma-separated scene ids, e.g. 1,2")
    ids = [int(item) for item in value.split(",")]
    require(len(ids) == len(set(ids)), "--only contains duplicate ids")
    return set(ids)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--layers", required=True, help="Raw window layers and manifest.json")
    parser.add_argument("--lang", required=True, choices=("ko", "en"))
    parser.add_argument("--out", required=True, help="Output directory")
    parser.add_argument("--scenes", default=str(Path(__file__).resolve().with_name("scenes.json")))
    parser.add_argument("--only", help="Scene ids, such as 1,2; search-card always freshly renders scene 1")
    args = parser.parse_args(argv)
    try:
        layers_root = Path(args.layers).resolve()
        out = Path(args.out).resolve()
        scenes_path = Path(args.scenes).resolve()
        require(layers_root.is_dir(), f"Layer directory missing: {layers_root}")
        require(not out.is_relative_to(layers_root), "--out must be outside the read-only --layers directory")
        require(not out.exists() or out.is_dir(), f"--out is not a directory: {out}")
        selected = parse_only(args.only) if args.only is not None else set(range(1, 10))
        config = read_json(scenes_path)
        fonts = Fonts()
        profile = ImageCms.ImageCmsProfile(ImageCms.createProfile("sRGB"))
        needed, images, headers, evidence, metrics = preflight(config, layers_root, selected, args.lang, fonts, profile)
        rendered = {scene["id"]: render_scene(scene, args.lang, images, layers_root, fonts, headers[scene["id"]]) for scene in needed}
        outputs = {f"{sid:02d}.png": rendered[sid] for sid in sorted(selected)}
        outputs["contact-sheet.png"] = contact_sheet(rendered, selected)
        outputs["search-card.png"] = search_card(rendered[1], evidence, config["search_card"], args.lang, fonts)
        # Finish all source, font, layout, and QA checks before the first write.
        protected = {scenes_path, (layers_root / "manifest.json").resolve(), Path(config["search_card"]["evidence_path"]).resolve(), *images}
        for name in outputs:
            destination = (out / name).resolve()
            require(destination.is_relative_to(out), f"Output path resolves outside --out: {out / name}")
            require(destination not in protected, f"Output would overwrite an input: {out / name}")
            require(not destination.exists() or destination.is_file(),
                    f"Output destination is not a regular file: {destination}")
        out.mkdir(parents=True, exist_ok=True)
        icc = fixed_icc(profile)
        for name, image in outputs.items():
            destination = (out / name).resolve()
            image.save(destination, format="PNG", optimize=True, compress_level=STYLE["png_compress_level"], icc_profile=icc)
            print(f"Saved {destination}")
        metrics["font_axes"] = fonts.axes_used
        metrics["output_files"] = [str((out / name).resolve()) for name in outputs]
        metrics["title_lines"] = {str(sid): headers[sid]["lines"] for sid in sorted(headers)}
        print(json.dumps(metrics, ensure_ascii=False, indent=2))
        return 0
    except (CompositionError, OSError, ValueError, ImageCms.PyCMSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
