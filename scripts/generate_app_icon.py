from __future__ import annotations

import binascii
import struct
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ASSETS_DIR = ROOT / "timbre" / "Assets"
SVG_PATH = ASSETS_DIR / "AppIcon.svg"
ICO_PATH = ASSETS_DIR / "AppIcon.ico"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SVG_NS = {"svg": "http://www.w3.org/2000/svg"}


def parse_color(value: str, opacity: float = 1.0) -> tuple[int, int, int, int]:
    value = value.strip()
    if not value.startswith("#") or len(value) != 7:
        raise ValueError(f"Unsupported color: {value}")

    red = int(value[1:3], 16)
    green = int(value[3:5], 16)
    blue = int(value[5:7], 16)
    alpha = round(max(0.0, min(1.0, opacity)) * 255)
    return red, green, blue, alpha


def over(destination: tuple[int, int, int, int], source: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    sr, sg, sb, sa = source
    dr, dg, db, da = destination

    source_alpha = sa / 255.0
    destination_alpha = da / 255.0
    output_alpha = source_alpha + destination_alpha * (1.0 - source_alpha)

    if output_alpha <= 0:
        return 0, 0, 0, 0

    red = round((sr * source_alpha + dr * destination_alpha * (1.0 - source_alpha)) / output_alpha)
    green = round((sg * source_alpha + dg * destination_alpha * (1.0 - source_alpha)) / output_alpha)
    blue = round((sb * source_alpha + db * destination_alpha * (1.0 - source_alpha)) / output_alpha)
    alpha = round(output_alpha * 255)
    return red, green, blue, alpha


def inside_rounded_rect(x: float, y: float, left: float, top: float, width: float, height: float, radius: float) -> bool:
    if x < left or y < top or x >= left + width or y >= top + height:
        return False

    x -= left
    y -= top

    if radius <= x < width - radius or radius <= y < height - radius:
        return True

    corner_x = radius if x < radius else width - radius
    corner_y = radius if y < radius else height - radius
    delta_x = x - corner_x
    delta_y = y - corner_y
    return delta_x * delta_x + delta_y * delta_y <= radius * radius


def load_icon_definition() -> tuple[
    float,
    float,
    tuple[float, float, float, float, float, tuple[int, int, int, int]],
    list[tuple[float, float, float, float, tuple[int, int, int, int]]],
]:
    tree = ET.parse(SVG_PATH)
    root = tree.getroot()
    _, _, width_text, height_text = root.attrib["viewBox"].split()
    canvas_width = float(width_text)
    canvas_height = float(height_text)

    group = root.find("svg:g", SVG_NS)
    if group is None:
        raise ValueError("Expected a <g> element in AppIcon.svg")

    rectangles = group.findall("svg:rect", SVG_NS)
    if not rectangles:
        raise ValueError("Expected at least one <rect> element in AppIcon.svg")

    background = rectangles[0]
    background_x = float(background.attrib.get("x", "0"))
    background_y = float(background.attrib.get("y", "0"))
    background_width = float(background.attrib["width"])
    background_height = float(background.attrib["height"])
    radius = float(background.attrib.get("rx", background.attrib.get("ry", "0")))
    background_color = parse_color(background.attrib["fill"])
    background_rect = (
        background_x,
        background_y,
        background_width,
        background_height,
        radius,
        background_color,
    )

    overlay_rectangles: list[tuple[float, float, float, float, tuple[int, int, int, int]]] = []
    for rect in rectangles[1:]:
        x = float(rect.attrib.get("x", "0"))
        y = float(rect.attrib.get("y", "0"))
        rect_width = float(rect.attrib["width"])
        rect_height = float(rect.attrib["height"])
        opacity = float(rect.attrib.get("fill-opacity", "1"))
        color = parse_color(rect.attrib["fill"], opacity)
        overlay_rectangles.append((x, y, x + rect_width, y + rect_height, color))

    return canvas_width, canvas_height, background_rect, overlay_rectangles


def sample_pixel(
    x: float,
    y: float,
    background_rect: tuple[float, float, float, float, float, tuple[int, int, int, int]],
    rectangles: list[tuple[float, float, float, float, tuple[int, int, int, int]]],
) -> tuple[int, int, int, int]:
    pixel = (0, 0, 0, 0)
    background_x, background_y, background_width, background_height, radius, background_color = background_rect

    if inside_rounded_rect(x, y, background_x, background_y, background_width, background_height, radius):
        pixel = background_color

    for left, top, right, bottom, color in rectangles:
        if left <= x < right and top <= y < bottom:
            pixel = over(pixel, color)

    return pixel


def render_png(size: int) -> bytes:
    canvas_width, canvas_height, background_rect, rectangles = load_icon_definition()
    pixels = bytearray()

    samples_per_axis = 4 if size <= 64 else 2
    step = 1.0 / samples_per_axis
    offsets = [step * (index + 0.5) for index in range(samples_per_axis)]
    total_samples = samples_per_axis * samples_per_axis
    scale_x = canvas_width / size
    scale_y = canvas_height / size

    for pixel_y in range(size):
        for pixel_x in range(size):
            total_red = 0.0
            total_green = 0.0
            total_blue = 0.0
            total_alpha = 0.0

            for offset_y in offsets:
                for offset_x in offsets:
                    source_x = (pixel_x + offset_x) * scale_x
                    source_y = (pixel_y + offset_y) * scale_y
                    red, green, blue, alpha = sample_pixel(
                        source_x,
                        source_y,
                        background_rect,
                        rectangles,
                    )
                    total_red += red
                    total_green += green
                    total_blue += blue
                    total_alpha += alpha

            pixels.extend(
                (
                    round(total_red / total_samples),
                    round(total_green / total_samples),
                    round(total_blue / total_samples),
                    round(total_alpha / total_samples),
                )
            )

    raw_rows = bytearray()
    stride = size * 4
    for row in range(size):
        raw_rows.append(0)
        start = row * stride
        raw_rows.extend(pixels[start : start + stride])

    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(bytes(raw_rows), level=9))
        + png_chunk(b"IEND", b"")
    )


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + chunk_type
        + data
        + struct.pack(">I", binascii.crc32(chunk_type + data) & 0xFFFFFFFF)
    )


def write_ico() -> None:
    pngs = [(size, render_png(size)) for size in SIZES]

    icon_directory = struct.pack("<HHH", 0, 1, len(pngs))
    directory_entries = bytearray()
    image_data = bytearray()
    offset = 6 + 16 * len(pngs)

    for size, png in pngs:
        directory_entries.extend(
            struct.pack(
                "<BBBBHHII",
                0 if size == 256 else size,
                0 if size == 256 else size,
                0,
                0,
                1,
                32,
                len(png),
                offset,
            )
        )
        image_data.extend(png)
        offset += len(png)

    ICO_PATH.write_bytes(icon_directory + directory_entries + image_data)
    print(f"Wrote {ICO_PATH}")


if __name__ == "__main__":
    write_ico()
