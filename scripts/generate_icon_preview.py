from pathlib import Path

from PIL import Image, ImageDraw


SIZE = 256
BG = (255, 255, 255, 0)
GREEN = (16, 163, 127, 255)
DARK = (22, 36, 44, 255)
LIGHT = (236, 250, 246, 255)
SCREEN = (218, 242, 236, 255)
SHADOW = (6, 20, 18, 50)


def rounded_hex_points(cx: float, cy: float, radius: float):
    pts = []
    for i in range(6):
        angle = -90 + i * 60
        from math import cos, radians, sin

        pts.append(
            (
                cx + radius * cos(radians(angle)),
                cy + radius * sin(radians(angle)),
            )
        )
    return pts


def draw_chatgpt_mark(draw: ImageDraw.ImageDraw):
    cx = 118
    cy = 112
    radius = 52
    loop_w = 24
    pts = rounded_hex_points(cx, cy, radius)

    for i in range(6):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 2) % 6]
        draw.line((x1, y1, x2, y2), fill=GREEN, width=loop_w, joint="curve")

    draw.ellipse((cx - 18, cy - 18, cx + 18, cy + 18), fill=BG)


def draw_computer_badge(draw: ImageDraw.ImageDraw):
    x = 150
    y = 148
    w = 78
    h = 62

    draw.rounded_rectangle((x + 4, y + 6, x + w + 4, y + h + 6), radius=14, fill=SHADOW)
    draw.rounded_rectangle((x, y, x + w, y + h), radius=14, fill=DARK)
    draw.rounded_rectangle((x + 7, y + 7, x + w - 7, y + h - 15), radius=10, fill=SCREEN)
    draw.rounded_rectangle((x + 30, y + h - 6, x + w - 30, y + h + 4), radius=5, fill=DARK)
    draw.rounded_rectangle((x + 20, y + h + 2, x + w - 20, y + h + 11), radius=5, fill=GREEN)
    draw.line((x + 18, y + 17, x + w - 18, y + 17), fill=LIGHT, width=3)
    draw.line((x + 18, y + 27, x + w - 28, y + 27), fill=LIGHT, width=3)
    draw.ellipse((x + w - 22, y + h - 28, x + w - 12, y + h - 18), fill=GREEN)


def main():
    out_dir = Path(__file__).resolve().parents[1] / "assets"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "icon-preview-chatgpt-computer.png"

    img = Image.new("RGBA", (SIZE, SIZE), BG)
    draw = ImageDraw.Draw(img)
    draw_chatgpt_mark(draw)
    draw_computer_badge(draw)
    img.save(out_path)
    print(out_path)


if __name__ == "__main__":
    main()
