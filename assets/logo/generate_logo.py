#!/usr/bin/env python3
"""Eksportuje wspólną ikonę BorderlessMouse dla macOS i Windows.

Źródło: assets/logo/logo-1024.png (1024 × 1024).
Podmień ten plik, a następnie uruchom:
    python3 assets/logo/generate_logo.py
Wymaga Pillow.
"""
from pathlib import Path

from PIL import Image

OUT = Path(__file__).resolve().parent
ROOT = OUT.parent.parent
SOURCE = OUT / "logo-1024.png"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
ICNS_SIZES = (32, 64, 128, 256, 512, 1024)


def render(source, size):
    """Skaluje oryginalną grafikę, zachowując jej kompozycję i tło."""
    return source.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(source, path):
    source.save(path, format="ICO", sizes=[(size, size) for size in ICO_SIZES])


def write_icns(source, path):
    # Pillow zapisuje bezstratne reprezentacje PNG, w tym warianty Retina.
    source.save(
        path,
        format="ICNS",
        append_images=[render(source, size) for size in ICNS_SIZES],
    )


def main():
    with Image.open(SOURCE) as image:
        if image.size != (1024, 1024):
            raise ValueError(f"Ikona źródłowa musi mieć 1024 × 1024 px: {SOURCE}")
        source = image.convert("RGBA")

    render(source, 256).save(OUT / "logo-256.png")

    win_assets = ROOT / "windows" / "BorderlessMouse" / "Assets"
    render(source, 256).save(win_assets / "icon.png")
    render(source, 512).save(win_assets / "logo.png")
    write_ico(source, win_assets / "icon.ico")

    write_icns(
        source,
        ROOT / "macos" / "BorderlessMouse" / "Resources" / "AppIcon.icns",
    )
    print("Ikony PNG, ICO i ICNS zostały wyeksportowane z logo-1024.png.")


if __name__ == "__main__":
    main()
