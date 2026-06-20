from pathlib import Path

from PIL import Image, ImageDraw


SIZE = 256
GREEN = (116, 170, 156, 255)
DARK = (19, 28, 35, 255)
SCREEN = (225, 246, 242, 255)
SCREEN_LINE = (145, 197, 187, 255)
SHADOW = (0, 0, 0, 68)


def draw_computer_badge(draw: ImageDraw.ImageDraw):
    x = 148
    y = 150
    w = 82
    h = 60

    draw.rounded_rectangle((x + 4, y + 5, x + w + 4, y + h + 5), radius=15, fill=SHADOW)
    draw.rounded_rectangle((x, y, x + w, y + h), radius=14, fill=DARK)
    draw.rounded_rectangle((x + 8, y + 8, x + w - 8, y + h - 16), radius=8, fill=SCREEN)
    draw.line((x + 20, y + 22, x + w - 20, y + 22), fill=SCREEN_LINE, width=3)
    draw.line((x + 20, y + 33, x + w - 31, y + 33), fill=SCREEN_LINE, width=3)
    draw.rounded_rectangle((x + 33, y + h - 8, x + w - 33, y + h + 3), radius=5, fill=DARK)
    draw.rounded_rectangle((x + 22, y + h + 1, x + w - 22, y + h + 10), radius=5, fill=GREEN)


def main():
    assets = Path(__file__).resolve().parents[1] / "assets"
    source = assets / "chatgpt-logo-rendered.png"
    out_path = assets / "icon-preview-rendered-chatgpt-computer.png"

    src = Image.open(source).convert("RGBA")
    src = src.resize((SIZE, SIZE), Image.Resampling.LANCZOS)

    # Chrome screenshots SVGs against white. Remove the white page background.
    pixels = src.load()
    for y in range(src.height):
        for x in range(src.width):
            r, g, b, a = pixels[x, y]
            if r > 248 and g > 248 and b > 248:
                pixels[x, y] = (255, 255, 255, 0)

    draw = ImageDraw.Draw(src)
    draw_computer_badge(draw)
    src.save(out_path)
    print(out_path)


if __name__ == "__main__":
    main()
