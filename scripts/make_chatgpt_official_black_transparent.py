from pathlib import Path

from PIL import Image


def main():
    assets = Path(__file__).resolve().parents[1] / "assets"
    source = assets / "chatgpt-logo-official-black.png"
    output = assets / "chatgpt-logo-official-black-transparent.png"

    src = Image.open(source).convert("RGBA")
    pixels = []
    for r, g, b, a in src.getdata():
        if a == 0:
            pixels.append((0, 0, 0, 0))
            continue

        # Preserve the official logo geometry and anti-aliased edges while
        # removing the white background.
        luma = int((r * 299 + g * 587 + b * 114) / 1000)
        alpha = max(0, min(255, 255 - luma))
        if alpha < 4:
            pixels.append((0, 0, 0, 0))
        else:
            pixels.append((0, 0, 0, alpha))

    out = Image.new("RGBA", src.size)
    out.putdata(pixels)
    out.save(output)
    print(output)


if __name__ == "__main__":
    main()
