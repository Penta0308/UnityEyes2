import argparse
import json
import re
from pathlib import Path

NUMBER = re.compile(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?")
KEYS = ("interior_margin_2d", "caruncle_2d", "iris_2d")


def scale_vector(text, sx, sy):
    values = [float(x) for x in NUMBER.findall(text)]
    values[0] *= sx
    values[1] *= sy
    return f"({values[0]:.4f}, {values[1]:.4f}, {values[2]:.4f})"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", nargs="?", default="out_unityeyes/imgs")
    parser.add_argument("--source-width", type=int, default=512)
    parser.add_argument("--source-height", type=int, default=320)
    parser.add_argument("--target-width", type=int, default=2560)
    parser.add_argument("--target-height", type=int, default=1600)
    args = parser.parse_args()

    directory = Path(args.directory)
    files = sorted(directory.glob("*.json"), key=lambda p: int(p.stem))
    changed = skipped = 0
    sx = args.target_width / args.source_width
    sy = args.target_height / args.source_height

    for path in files:
        data = json.loads(path.read_text(encoding="utf-8"))
        for camera in data.get("cameras", {}).values():
            if "label_image_size" in camera:
                skipped += 1
                continue
            for key in KEYS:
                camera[key] = [scale_vector(v, sx, sy) for v in camera.get(key, [])]
            camera["label_image_size"] = {
                "width": args.target_width,
                "height": args.target_height,
            }
            changed += 1
        path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"converted cameras: {changed}, skipped cameras: {skipped}, files: {len(files)}")


if __name__ == "__main__":
    main()
