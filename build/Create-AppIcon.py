from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIRECTORY = ROOT / "ProPdfReader" / "Assets"
CANVAS_SIZE = 1024


def scaled(value: int) -> int:
    return value * 4


def create_icon() -> Image.Image:
    image = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (scaled(16), scaled(16), scaled(240), scaled(240)),
        radius=scaled(36),
        fill="#252927",
    )
    draw.rounded_rectangle(
        (scaled(55), scaled(35), scaled(203), scaled(225)),
        radius=scaled(13),
        fill="#111412",
    )
    draw.rounded_rectangle(
        (scaled(49), scaled(29), scaled(197), scaled(219)),
        radius=scaled(13),
        fill="#FAFAF6",
    )

    draw.polygon(
        [
            (scaled(151), scaled(29)),
            (scaled(197), scaled(75)),
            (scaled(197), scaled(29)),
        ],
        fill="#E45A4F",
    )
    draw.polygon(
        [
            (scaled(151), scaled(29)),
            (scaled(151), scaled(75)),
            (scaled(197), scaled(75)),
        ],
        fill="#D9DDD8",
    )

    line_color = "#545B57"
    draw.rounded_rectangle(
        (scaled(72), scaled(101), scaled(174), scaled(113)),
        radius=scaled(6),
        fill=line_color,
    )
    draw.rounded_rectangle(
        (scaled(72), scaled(126), scaled(174), scaled(138)),
        radius=scaled(6),
        fill="#F2C84F",
    )
    draw.rounded_rectangle(
        (scaled(72), scaled(151), scaled(152), scaled(163)),
        radius=scaled(6),
        fill=line_color,
    )
    draw.polygon(
        [
            (scaled(70), scaled(188)),
            (scaled(94), scaled(188)),
            (scaled(94), scaled(226)),
            (scaled(82), scaled(217)),
            (scaled(70), scaled(226)),
        ],
        fill="#3A9B78",
    )

    return image


def main() -> None:
    ASSET_DIRECTORY.mkdir(parents=True, exist_ok=True)
    image = create_icon()
    png_path = ASSET_DIRECTORY / "ProPdfReader.png"
    icon_path = ASSET_DIRECTORY / "ProPdfReader.ico"

    image.resize((256, 256), Image.Resampling.LANCZOS).save(png_path)
    image.save(
        icon_path,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
