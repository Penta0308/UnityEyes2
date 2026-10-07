# 780 nm albedo texture set

Run:

```bash
python gradpupil/generate_nir_albedo.py
```

Outputs are written to:

- `Assets/Resources/NIRAlbedo/IrisTextures/`
- `Assets/Resources/NIRAlbedo/SkinTextures/`
- `gradpupil/nir_data/nir_albedo_manifest.csv`

The original visible textures are not overwritten. The generated files retain the original base filenames, dimensions, alpha, and UV layout. `gradpupil_autoload.json` selects them with `"albedo_resource_root": "NIRAlbedo"`; the controllers then load them into the existing material slots. They use the same materials and shaders and do not introduce an RGB/NIR shader branch. Omit the key or set it to an empty string to load the original visible textures.

PNG samples are sRGB-encoded representations of **linear diffuse reflectance**. Unity must import them as sRGB color textures (`linearTexture: 0` in the copied importer metadata); Unity then decodes them to linear values before the surface shader receives `o.Albedo`.

## Eye atlases

- Pupil UV radius: 0.0788; assigned linear reflectance 0.003.
- Iris UV radius: 0.1385.
- Limbus transition: 0.1385–0.1500 with smooth interpolation.
- Iris identity means are empirical quantiles from the eight directly measured 780 nm values digitized from Di Cecilia and Rovati (2020). Assignment is deliberately independent of the visible color filename because the small measured sample does not support a deterministic RGB-color-to-NIR mapping.
- Sclera identity means span 0.50–0.59, supported by Bashkatov et al. (780 nm) and Vogel et al. (804 nm).
- High-frequency atlas detail is retained after removing low-frequency visible illumination. Specular glints are not encoded in albedo.

## Skin atlases

- Each of the 20 identities receives a fixed empirical quantile from the 100-subject NIST 780 nm reflectance distribution.
- `*_color.png` and `*_color_look_down.png` share the same identity-level target.
- Low-frequency photographic luminance is removed rather than interpreted as melanin.
- Negative local detail is retained for brows, lashes, creases, and texture.
- Positive contrast is tightly capped to suppress the baked bright lid-margin/cream-band artifact.

## Limitations

- The NIST skin reflectance geometry and the ocular measurements are not identical to the Unity BRDF or the final OV9281 device geometry.
- Sclera measurements are ex-vivo bulk values and depend on thickness and hydration.
- No quantitative 780 nm conjunctiva/vessel dataset has yet been applied.
- The generated set is a literature-anchored prototype and must be calibrated against real OV9281 + 780 nm captures.
- Texture selection is explicit in the dataset JSON. The scene/material assets themselves remain unchanged, so configurations without `albedo_resource_root` retain the original RGB appearance.
