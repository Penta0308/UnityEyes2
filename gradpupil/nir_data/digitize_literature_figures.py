"""Digitize selected literature plots used by the GradPupil NIR model.

This script reads 300-dpi page renders in nir_data/digitization/.  Coordinates are
specific to those renders and are intentionally recorded here for reproducibility.
The output is an approximate graph digitization, not author-supplied raw data.
"""
from __future__ import annotations

import csv
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent
DIG = ROOT / "digitization"


def log_plot_value(y: float, top: float, bottom: float) -> float:
    """Map a y pixel to a four-decade axis: 10^1 at top, 10^-3 at bottom."""
    return 10.0 ** (1.0 - (y - top) / (bottom - top) * 4.0)


def median_col_y(mask: np.ndarray, x: int, top: int, bottom: int) -> float:
    ys, _ = np.where(mask[top : bottom + 1, x - 4 : x + 5])
    ys = ys + top
    ys = ys[(ys > top + 1) & (ys < bottom - 1)]
    if not len(ys):
        raise RuntimeError(f"No curve pixels near x={x}, y={top}:{bottom}")
    return float(np.median(ys))


def digitize_di_cecilia() -> None:
    """Digitize Fig. 17 (three-session mean and 95% repeatability coefficient)."""
    image = cv2.imread(str(DIG / "di_cecilia-11.png"))
    b, g, r = cv2.split(image)
    blue = (b > 130) & (b > r * 1.30) & (b > g * 1.15) & (r < 180)
    orange = (r > 150) & (r > g * 1.25) & (r > b * 1.50) & (b < 180)

    grades = [4, 10, 11, 13, 16, 17, 23, 24]
    tops = [395, 395, 694, 694, 992, 992, 1291, 1291]
    lefts = [272, 1023] * 4
    plot_width = 570
    plot_height = 193
    wavelengths = list(range(480, 901, 20))

    rows: list[dict[str, float | int]] = []
    for grade, top, left in zip(grades, tops, lefts):
        bottom = top + plot_height
        for wavelength in wavelengths:
            x = round(left + (wavelength - 480) / (900 - 480) * plot_width)
            reflectance = log_plot_value(
                median_col_y(blue, x, top, bottom), top, bottom
            )
            repeatability = log_plot_value(
                median_col_y(orange, x, top, bottom), top, bottom
            )
            rows.append(
                {
                    "franssen_grade": grade,
                    "wavelength_nm": wavelength,
                    "mean_reflectance_percent": reflectance,
                    "relative_repeatability_95": repeatability,
                    "approx_abs_95_halfwidth_percent": reflectance * repeatability,
                }
            )

    path = ROOT / "iris_di_cecilia_2020_fig17_digitized.csv"
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def write_summary() -> None:
    source = ROOT / "iris_di_cecilia_2020_fig17_digitized.csv"
    rows = list(csv.DictReader(source.open(encoding="utf-8")))
    selected = [row for row in rows if int(row["wavelength_nm"]) == 780]
    path = ROOT / "iris_780_di_cecilia_2020.csv"
    fields = [
        "franssen_grade",
        "mean_reflectance_percent",
        "mean_reflectance_fraction",
        "relative_repeatability_95",
        "approx_abs_95_halfwidth_percent",
    ]
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for row in selected:
            pct = float(row["mean_reflectance_percent"])
            writer.writerow(
                {
                    "franssen_grade": row["franssen_grade"],
                    "mean_reflectance_percent": f"{pct:.4f}",
                    "mean_reflectance_fraction": f"{pct / 100.0:.6f}",
                    "relative_repeatability_95": f"{float(row['relative_repeatability_95']):.4f}",
                    "approx_abs_95_halfwidth_percent": f"{float(row['approx_abs_95_halfwidth_percent']):.4f}",
                }
            )


if __name__ == "__main__":
    digitize_di_cecilia()
    write_summary()
