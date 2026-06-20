from pathlib import Path

from PIL import Image


SIZES = [256, 128, 64, 48, 32, 24, 16]


def main():
    root = Path(__file__).resolve().parents[1]
    assets = root / "assets"
    source = assets / "icon-preview-chatgpt-terminal.png"
    out = assets / "devspace-manager.ico"

    img = Image.open(source).convert("RGBA")
    variants = [img.resize((size, size), Image.Resampling.LANCZOS) for size in SIZES]
    variants[0].save(out, format="ICO", sizes=[(size, size) for size in SIZES], append_images=variants[1:])
    print(out)


if __name__ == "__main__":
    main()
