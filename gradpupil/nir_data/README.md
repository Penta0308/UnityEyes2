# 780 nm albedo data

## NIST human-skin reflectance

- Source: NIST open data, `1832_Data_JResNIST_skinrefl v3`
- URL: https://opendata.nist.gov/1832_Data_JResNIST_skinrefl%20v3.txt
- Measurand: reflectance factor
- Coverage: 250–2500 nm at 3 nm intervals
- Population: 100 subjects; `Average` is the mean of R1, R2 and R3

Files:

- `nist_skin_reflectance_v3.txt`: unmodified downloaded source table.
- `skin_780_subjects.csv`: each subject's Average at 778 and 781 nm, with 780 nm obtained by linear interpolation.

The resulting 780 nm subject distribution is:

| statistic | reflectance factor |
|---|---:|
| minimum | 0.4345 |
| 5th percentile | 0.5259 |
| median | 0.6173 |
| mean | 0.6113 |
| 95th percentile | 0.6650 |
| maximum | 0.6817 |

These values are measured skin reflectance factors, not display-gray values. They must remain linear until explicitly encoded into a texture format. No iris or sclera values are inferred from this dataset.

## Human-iris reflectance: Di Cecilia and Rovati (2020)

- Source: L. Di Cecilia and L. Rovati, “Design and performance of a hyperspectral imaging system: Preliminary in vivo spectral reflectance measurements of the human iris,” *Review of Scientific Instruments* 91, 014104 (2020).
- DOI: https://doi.org/10.1063/1.5125575
- Measurement: in vivo right eyes of eight healthy volunteers.
- Pigmentation coverage: Franssen grades 4, 10, 11, 13, 16, 17, 23 and 24.
- Spectral range: 480–900 nm in 20 nm steps; 780 nm is a directly measured band.
- Calibration: pixel-wise dark correction and a 99% Labsphere/NIST reflectance standard.
- Reported ROI: annulus covering the entire iris.
- Figure 17: mean across three sessions and relative intersession repeatability coefficient, defined as `1.96 * SD / mean` (95% level).

Files:

- `iris_di_cecilia_2020_fig17_digitized.csv`: graph-digitized three-session spectrum for all eight volunteers.
- `iris_780_di_cecilia_2020.csv`: 780 nm subset.
- `digitize_literature_figures.py`: reproducible color-curve extraction from the 300-dpi page render.
- `digitization/di_cecilia-11.png`: 300-dpi source page containing Figure 17.

Approximate Figure 17 values at 780 nm are:

| Franssen grade | mean reflectance factor | relative 95% repeatability | approximate absolute 95% half-width |
|---:|---:|---:|---:|
| 4 | 0.0296 | 0.136 | 0.0040 |
| 10 | 0.0667 | 0.266 | 0.0177 |
| 11 | 0.0376 | 0.293 | 0.0110 |
| 13 | 0.0358 | 0.093 | 0.0033 |
| 16 | 0.0385 | 0.136 | 0.0053 |
| 17 | 0.0424 | 0.354 | 0.0150 |
| 23 | 0.0592 | 0.519 | 0.0307 |
| 24 | 0.0513 | 0.372 | 0.0190 |

Across these eight subjects, the digitized 780 nm mean reflectance factors range from 0.0296 to 0.0667, with median 0.0404 and mean 0.0451. They are not monotonic with Franssen grade in this small sample. Do not infer a deterministic pigmentation-grade mapping from these eight points. The listed half-width combines the digitized mean with the paper’s plotted repeatability coefficient; it is not a population confidence interval. Approximate graph-reading uncertainty is additional (roughly 1–2 plot pixels, about 5–10% relative on the logarithmic axis).

For Unity, these measurements are defensible identity-level anchors for mean 780 nm iris diffuse reflectance. They should not be copied pixel-for-pixel: the measurement includes the real iris morphology, optical path through the cornea/anterior chamber, the instrument geometry and an entire-iris spatial average. Glints/specular reflection must remain outside the albedo texture.

## Human-sclera optical data

### Bashkatov et al. (2010)

- Source: A. N. Bashkatov et al., “Optical properties of human sclera in spectral range 370–2500 nm,” *Optics and Spectroscopy* 109, 197–204 (2010).
- DOI: https://doi.org/10.1134/S0030400X10080084
- Samples: ten ex-vivo human sclera samples, 1–3 mm thick, stored in 0.9% NaCl.
- Instrument: Cary-2415 spectrophotometer with integrating-sphere measurements.
- Direct measurements: diffuse reflection and total transmission.
- Derived by inverse adding–doubling: absorption coefficient and transport/reduced scattering coefficient, with anisotropy fixed to 0.8.
- Published fit: `mu_s_prime(lambda_nm) = 2.411e5 * lambda_nm^-1.325 cm^-1`, reported to agree well over 400–1300 nm. This gives approximately 35.50 cm⁻¹ at 780 nm.
- Approximate Figure 1 reading for the illustrated 1.0 ± 0.05 mm sample at 780 nm: diffuse reflectance 0.503 and total transmittance 0.316.
- Approximate Figure 2 reading at 780 nm: absorption coefficient about 1.0 cm⁻¹; this is a low-precision graph reading.

### Vogel et al. (1991)

- Source: A. Vogel et al., “Optical properties of human sclera, and their consequences for transscleral laser applications,” *Lasers in Surgery and Medicine* 11, 331–340 (1991).
- DOI: https://doi.org/10.1002/lsm.1900110404
- Samples: 15 human sclera specimens adjacent to the limbus for noncontact measurements; assumed thickness 0.8 mm.
- At 804 nm, the text reports total transmission 0.35, total absorption 0.06 and absorption coefficient about 0.6 cm⁻¹.
- Since the authors report `R + T + A` closure within 1%, total reflection inferred from the reported means is approximately 0.59.
- The paper states that scleral reflection is basically diffuse, while regular surface reflection is only about 2–4% at normal or near-normal incidence.

`sclera_nir_literature_summary.csv` records these values and clearly distinguishes reported, calculated and graph-digitized quantities.

### Sclera interpretation limits

The sclera values are ex-vivo bulk optical measurements and depend strongly on thickness, hydration, sample location and collection geometry. A 1-mm slab diffuse reflectance is not identical to a local BRDF diffuse-albedo parameter. For an initial Unity prototype, a mean scleral diffuse-albedo anchor around 0.50–0.59 at 780–804 nm is supported by the two studies, but it should be treated as a provisional effective range and validated against the actual OV9281/780-nm geometry. The 2–4% regular surface reflection belongs in the wet/specular layer, not in the diffuse albedo texture.

## Visible-range cross-check

`Imaging_and_Confocal_Systems_for_in_vivo_Measurements_of_Human-Iris_Spectral_Reflectance.pdf` measures gray-blue and black-brown irises over 420–700 nm at 1 nm intervals using a BaSO₄ reference. Its confocal path reduces corneal Fresnel contamination. It does not provide 780 nm values, but its overlap with Di Cecilia and Rovati can be used to check visible-range scale and spectral shape before generating paired visible/NIR textures.
