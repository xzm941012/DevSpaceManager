from pathlib import Path

from PIL import Image, ImageDraw


SIZE = 256
GREEN = (116, 170, 156, 255)
ACCENT = (16, 185, 129, 255)
DARK = (16, 24, 32, 255)
PANEL = (23, 35, 45, 255)
TEXT = (218, 251, 241, 255)
SHADOW = (0, 0, 0, 70)


def draw_terminal_badge(draw: ImageDraw.ImageDraw):
    x = 148
    y = 150
    w = 82
    h = 62

    draw.rounded_rectangle((x + 4, y + 5, x + w + 4, y + h + 5), radius=15, fill=SHADOW)
    draw.rounded_rectangle((x, y, x + w, y + h), radius=14, fill=DARK)
    draw.rounded_rectangle((x + 7, y + 8, x + w - 7, y + h - 8), radius=9, fill=PANEL)

    draw.line((x + 19, y + 25, x + 28, y + 32), fill=ACCENT, width=5)
    draw.line((x + 28, y + 32, x + 19, y + 39), fill=ACCENT, width=5)
    draw.line((x + 39, y + 39, x + 62, y + 39), fill=TEXT, width=5)

    draw.rounded_rectangle((x + 8, y + 8, x + w - 8, y + 15), radius=3, fill=GREEN)


def remove_white_background(img: Image.Image):
    pixels = img.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = pixels[x, y]
            if r > 248 and g > 248 and b > 248:
                pixels[x, y] = (255, 255, 255, 0)


def main():
    assets = Path(__file__).resolve().parents[1] / "assets"
    source = assets / "chatgpt-logo-rendered.png"
    out_path = assets / "icon-preview-chatgpt-terminal.png"

    img = Image.open(source).convert("RGBA").resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    remove_white_background(img)
    draw_terminal_badge(ImageDraw.Draw(img))
    img.save(out_path)
    print(out_path)


if __name__ == "__main__":
    main()
