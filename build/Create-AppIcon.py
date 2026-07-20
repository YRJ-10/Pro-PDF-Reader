from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIRECTORY = ROOT / "ProPdfReader" / "Assets"
CANVAS_SIZE = 1024
SOURCE_PATH = ROOT / "appicon.png"


def main() -> None:
    ASSET_DIRECTORY.mkdir(parents=True, exist_ok=True)
    image = Image.open(SOURCE_PATH).convert("RGBA")
    if image.size != (CANVAS_SIZE, CANVAS_SIZE):
        image = image.resize((CANVAS_SIZE, CANVAS_SIZE), Image.Resampling.LANCZOS)
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
