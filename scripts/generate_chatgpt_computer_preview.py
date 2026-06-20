from pathlib import Path
from xml.etree import ElementTree as ET

from PIL import Image, ImageDraw


SIZE = 256
SVG_SCALE = 2406
GREEN = (116, 170, 156, 255)
DARK = (21, 31, 38, 255)
SCREEN = (222, 243, 239, 255)
SCREEN_LINE = (158, 205, 196, 255)
WHITE = (255, 255, 255, 255)
SHADOW = (0, 0, 0, 70)


def parse_path_points(path_data: str):
    # The official logo SVG path uses only M/L/C/h/v/c/z commands. For a small
    # preview, sampling path coordinates is enough because the source keeps the
    # real ChatGPT geometry while Pillow handles the final raster composition.
    import re

    nums = [float(item) for item in re.findall(r"-?\d+(?:\.\d+)?", path_data)]
    points = []
    for i in range(0, len(nums) - 1, 2):
        points.append((nums[i], nums[i + 1]))
    return points


def scale_point(point, offset, scale):
    return (offset + point[0] * scale, offset + point[1] * scale)


def draw_logo_base(draw: ImageDraw.ImageDraw):
    margin = 24
    draw.rounded_rectangle((margin, margin, SIZE - margin, SIZE - margin), radius=52, fill=GREEN)


def draw_chatgpt_from_svg(draw: ImageDraw.ImageDraw):
    svg_path = Path(__file__).resolve().parents[1] / "assets" / "chatgpt-logo.svg"
    tree = ET.parse(svg_path)
    ns = {"svg": "http://www.w3.org/2000/svg"}
    path = tree.find(".//svg:path[@id='a']", ns)
    if path is None:
        return

    points = parse_path_points(path.attrib["d"])
    scale = 0.074
    offset = 39
    center = SIZE / 2
    import math

    for rotation in range(0, 360, 60):
        angle = math.radians(rotation)
        cos_a = math.cos(angle)
        sin_a = math.sin(angle)
        poly = []
        for x, y in points:
            sx, sy = scale_point((x, y), offset, scale)
            dx = sx - center
            dy = sy - center
            poly.append((center + dx * cos_a - dy * sin_a, center + dx * sin_a + dy * cos_a))
        draw.line(poly, fill=WHITE, width=6, joint="curve")


def draw_computer_badge(draw: ImageDraw.ImageDraw):
    x = 146
    y = 148
    w = 82
    h = 60

    draw.rounded_rectangle((x + 4, y + 5, x + w + 4, y + h + 5), radius=14, fill=SHADOW)
    draw.rounded_rectangle((x, y, x + w, y + h), radius=13, fill=DARK)
    draw.rounded_rectangle((x + 8, y + 8, x + w - 8, y + h - 16), radius=8, fill=SCREEN)
    draw.line((x + 20, y + 21, x + w - 20, y + 21), fill=SCREEN_LINE, width=3)
    draw.line((x + 20, y + 32, x + w - 30, y + 32), fill=SCREEN_LINE, width=3)
    draw.rounded_rectangle((x + 33, y + h - 8, x + w - 33, y + h + 3), radius=5, fill=DARK)
    draw.rounded_rectangle((x + 22, y + h + 1, x + w - 22, y + h + 10), radius=5, fill=GREEN)


def main():
    out_dir = Path(__file__).resolve().parents[1] / "assets"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "icon-preview-real-chatgpt-computer.png"

    img = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    draw = ImageDraw.Draw(img)
    draw_logo_base(draw)
    draw_chatgpt_from_svg(draw)
    draw_computer_badge(draw)
    img.save(out_path)
    print(out_path)


if __name__ == "__main__":
    main()
