"""Generate drop-in 780 nm diffuse-albedo textures for UnityEyes2.

The renderer and shader are unchanged.  Output textures use the same UVs and base
filenames as the visible assets, but live under Resources/NIRAlbedo so the RGB
assets are never overwritten. Pixel values are sRGB-encoded PNG samples of
linear diffuse reflectance.

Only high-frequency spatial detail is retained from the photographic visible
textures. Low-frequency visible luminance is deliberately removed so baked
illumination is not interpreted as melanin or NIR reflectance.
"""
from __future__ import annotations

import csv
import hashlib
import re
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets" / "Resources"
DATA = ROOT / "gradpupil" / "nir_data"
OUT = ASSETS / "NIRAlbedo"
IRIS_OUT = OUT / "IrisTextures"
SKIN_OUT = OUT / "SkinTextures"

PUPIL_RADIUS_UV = 0.0788
IRIS_RADIUS_UV = 0.1385
LIMBUS_OUTER_RADIUS_UV = 0.1500
PUPIL_REFLECTANCE = 0.003


def srgb_to_linear(x: np.ndarray) -> np.ndarray:
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(x: np.ndarray) -> np.ndarray:
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * x ** (1 / 2.4) - 0.055)


def load_rgba(path: Path) -> tuple[np.ndarray, np.ndarray]:
    rgba = np.asarray(Image.open(path).convert("RGBA"), dtype=np.float32) / 255.0
    linear = srgb_to_linear(rgba[..., :3])
    luminance = linear @ np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    return luminance, rgba[..., 3]


def detail_factor(luminance: np.ndarray, sigma: float, lo: float, hi: float) -> np.ndarray:
    # Work in log space. This removes multiplicative low-frequency illumination
    # while retaining wrinkles, crypts, vessels, lashes and hair boundaries.
    eps = 1e-4
    log_lum = np.log(np.maximum(luminance, eps))
    low = gaussian_filter(log_lum, sigma=sigma, mode="reflect")
    return np.clip(np.exp(log_lum - low), lo, hi)


def normalize_region(values: np.ndarray, mask: np.ndarray, target: float) -> np.ndarray:
    mean = float(values[mask].mean())
    return values * (target / max(mean, 1e-8))


def unity_guid(path: Path) -> str:
    return hashlib.md5(path.relative_to(ROOT).as_posix().encode("utf-8")).hexdigest()


def ensure_folder_meta(path: Path) -> None:
    meta = Path(str(path) + ".meta")
    meta.write_text(
        "fileFormatVersion: 2\n"
        f"guid: {unity_guid(path)}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
        encoding="utf-8",
    )


def write_texture_meta(source: Path, destination: Path) -> None:
    source_meta = Path(str(source) + ".meta")
    text = source_meta.read_text(encoding="utf-8")
    text = re.sub(r"(?m)^guid: [0-9a-f]+$", f"guid: {unity_guid(destination)}", text)
    Path(str(destination) + ".meta").write_text(text, encoding="utf-8")


def save_gray_rgba(
    path: Path, linear_gray: np.ndarray, alpha: np.ndarray, source: Path
) -> None:
    encoded = linear_to_srgb(linear_gray)
    rgb = np.repeat(encoded[..., None], 3, axis=2)
    rgba = np.concatenate([rgb, alpha[..., None]], axis=2)
    Image.fromarray(np.round(np.clip(rgba, 0, 1) * 255).astype(np.uint8), "RGBA").save(path)
    write_texture_meta(source, path)


def iris_assignments() -> list[tuple[Path, float, int]]:
    measurements = []
    with (DATA / "iris_780_di_cecilia_2020.csv").open(encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            measurements.append(
                (float(row["mean_reflectance_fraction"]), int(row["franssen_grade"]))
            )
    measurements.sort()
    files = sorted((ASSETS / "IrisTextures").glob("eyeball_*.png"))
    # Empirical quantiles; intentionally not inferred from visible color names.
    indexes = np.round(np.linspace(0, len(measurements) - 1, len(files))).astype(int)
    return [(path, *measurements[idx]) for path, idx in zip(files, indexes)]


def generate_irises(rows: list[dict[str, object]]) -> None:
    IRIS_OUT.mkdir(parents=True, exist_ok=True)
    ensure_folder_meta(OUT)
    ensure_folder_meta(IRIS_OUT)
    assignments = iris_assignments()
    sclera_targets = np.linspace(0.50, 0.59, len(assignments))

    for (source, iris_target, grade), sclera_target in zip(assignments, sclera_targets):
        lum, alpha = load_rgba(source)
        h, w = lum.shape
        yy, xx = np.mgrid[0:h, 0:w]
        # Pixel-center UV coordinates match the radial convention in EyeShader.
        u = (xx + 0.5) / w
        v = (yy + 0.5) / h
        radius = np.sqrt((u - 0.5) ** 2 + (v - 0.5) ** 2)
        pupil = radius <= PUPIL_RADIUS_UV
        iris = (radius > PUPIL_RADIUS_UV) & (radius <= IRIS_RADIUS_UV)
        sclera = radius >= LIMBUS_OUTER_RADIUS_UV
        limbus = (radius > IRIS_RADIUS_UV) & (radius < LIMBUS_OUTER_RADIUS_UV)

        detail = detail_factor(lum, sigma=max(h, w) / 64.0, lo=0.65, hi=1.35)
        result = np.full_like(lum, sclera_target)

        iris_values = normalize_region(iris_target * detail, iris, iris_target)
        sclera_values = normalize_region(sclera_target * detail, sclera, sclera_target)
        result[iris] = iris_values[iris]
        result[sclera] = sclera_values[sclera]
        result[pupil] = PUPIL_REFLECTANCE

        t = np.clip(
            (radius - IRIS_RADIUS_UV)
            / (LIMBUS_OUTER_RADIUS_UV - IRIS_RADIUS_UV),
            0.0,
            1.0,
        )
        smooth = t * t * (3.0 - 2.0 * t)
        blended = iris_values * (1.0 - smooth) + sclera_values * smooth
        result[limbus] = blended[limbus]
        result = np.clip(result, 0.0, 0.95)

        destination = IRIS_OUT / source.name
        save_gray_rgba(destination, result, alpha, source)
        rows.append(
            {
                "asset_type": "iris",
                "output": destination.relative_to(ROOT).as_posix(),
                "source": source.relative_to(ROOT).as_posix(),
                "identity": source.stem,
                "target_reflectance_780": f"{iris_target:.6f}",
                "secondary_target": f"{sclera_target:.6f}",
                "measurement_assignment": f"DiCecilia2020_Franssen_{grade}",
                "sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
            }
        )


def skin_targets() -> dict[str, tuple[float, int]]:
    measured = []
    with (DATA / "skin_780_subjects.csv").open(encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            measured.append((float(row["reflectance_780nm_linear"]), int(row["subject"])))
    measured.sort()
    identities = sorted({p.name.split("_")[0] for p in (ASSETS / "SkinTextures").glob("*_color*.png")})
    indexes = np.round(np.linspace(0, len(measured) - 1, len(identities))).astype(int)
    return {identity: measured[idx] for identity, idx in zip(identities, indexes)}


def generate_skins(rows: list[dict[str, object]]) -> None:
    SKIN_OUT.mkdir(parents=True, exist_ok=True)
    ensure_folder_meta(SKIN_OUT)
    targets = skin_targets()
    for source in sorted((ASSETS / "SkinTextures").glob("*_color*.png")):
        identity = source.name.split("_")[0]
        target, nist_subject = targets[identity]
        lum, alpha = load_rgba(source)
        # Positive visible contrast is capped tightly: broad bright lid-margin
        # bands are baked illumination, not evidence of high NIR reflectance.
        # Negative detail remains stronger so brows, lashes and creases survive.
        detail = detail_factor(lum, sigma=max(lum.shape) / 64.0, lo=0.55, hi=1.12)
        # Median normalization is less sensitive to hair, eyelashes and atlas padding.
        detail /= max(float(np.median(detail[alpha > 0.5])), 1e-8)
        result = np.clip(target * detail, 0.0, 0.95)
        destination = SKIN_OUT / source.name
        save_gray_rgba(destination, result, alpha, source)
        rows.append(
            {
                "asset_type": "skin",
                "output": destination.relative_to(ROOT).as_posix(),
                "source": source.relative_to(ROOT).as_posix(),
                "identity": identity,
                "target_reflectance_780": f"{target:.6f}",
                "secondary_target": "",
                "measurement_assignment": f"NIST_skin_subject_{nist_subject}",
                "sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
            }
        )


def main() -> None:
    rows: list[dict[str, object]] = []
    generate_irises(rows)
    generate_skins(rows)
    manifest = DATA / "nir_albedo_manifest.csv"
    fields = list(rows[0])
    with manifest.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    print(f"Generated {len(rows)} textures under {OUT}")


if __name__ == "__main__":
    main()
