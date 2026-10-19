# Video shader packages

Light Player video shader packages are ordinary folders. They are not archives and do not contain executable managed or native code. One package describes one file-loaded SkSL pre-scale stage.

## Folder layout

```text
my-filter/
|- package.json
|- stage.json
|- LICENSE.txt
|- 000-first-pass.sksl
`- 001-final-pass.sksl
```

The user imports `my-filter/` itself. Files in nested child folders are allowed. Symbolic links and paths escaping the package folder are rejected.

The folder import button is available on desktop and Android. Desktop paths are copied directly;
Android Storage Access Framework folders are streamed recursively into app storage before the same
atomic validation/installation step. Browser File System Access handles are not imported in this
version.

Formal schemas:

- [Package schema v1](schemas/video-shader-package-v1.schema.json)
- [Stage schema v1](schemas/video-scale-stage-v1.schema.json)

Version 1 is immutable. Future schema versions must use a new version number while the runtime
continues to load v1 packages; existing package folders do not need regeneration merely because a
new schema is introduced.

## Package manifest

`package.json` supplies distributable identity and attribution:

```json
{
  "schemaVersion": 1,
  "id": "org.example.anime-upscale-x2",
  "name": "Anime Upscale x2",
  "description": "A compact two-times anime upscaler.",
  "version": "1.0.0",
  "author": "Example Author",
  "license": "MIT",
  "licenseFile": "LICENSE.txt",
  "stage": "stage.json"
}
```

- `id` is the stable update identity. Reimporting the same id replaces package files while preserving priority, enablement, and the user's maximum-resolution setting.
- `id` uses 3-128 lowercase ASCII letters, digits, dots, underscores, or hyphens. It must begin and end with a letter or digit.
- `version` uses semantic version form. It is displayed to the user; package replacement is governed by `id`.
- `licenseFile` and `stage` are paths relative to the package root. Both files are required.
- A package may contain at most 128 directories, 512 files, and 64 MiB. Each SkSL source is limited
  to 4 MiB.

## Stage manifest

`stage.json` declares an ordered render graph:

```json
{
  "version": 1,
  "name": "Anime Upscale x2",
  "inputDomain": "signal-rgb",
  "minimumScale": 1.2,
  "output": "MAIN",
  "passes": [
    {
      "name": "first pass",
      "shader": "000-first-pass.sksl",
      "inputs": ["MAIN"],
      "output": "features",
      "width": {
        "reference": "source",
        "numerator": 1,
        "denominator": 1
      },
      "height": {
        "reference": "source",
        "numerator": 1,
        "denominator": 1
      },
      "sizeUniforms": true
    },
    {
      "name": "two-times output",
      "shader": "001-final-pass.sksl",
      "inputs": ["features", "MAIN"],
      "output": "MAIN",
      "width": {
        "reference": "source",
        "numerator": 2,
        "denominator": 1
      },
      "height": {
        "reference": "source",
        "numerator": 2,
        "denominator": 1
      },
      "sizeUniforms": true
    }
  ]
}
```

### Graph rules

- `MAIN` is the normalized nonlinear RGB input and must be produced as the final `output`.
- `NATIVE` is an immutable alias for the original input. Use it when an earlier pass replaces `MAIN`.
- A pass can read only `MAIN`, `NATIVE`, or outputs produced by an earlier pass.
- Writing an existing output name replaces that logical image for subsequent passes.
- The runtime supports 1-256 passes and 1-64 input images per pass.
- Intermediates are opaque half-float RGBA surfaces. Keep arbitrary signed data in RGB and set alpha to 1. If a model needs four arbitrary signed channels, split the fourth channel into another image.
- Images are released after the last read of that generation. Packages should still avoid unnecessary full-resolution intermediates.

### Conditions

`minimumScale` and `maximumScale` are optional exclusive bounds applied to both physical output axes:

```text
sourceWidth * minimumScale < outputWidth < sourceWidth * maximumScale
sourceHeight * minimumScale < outputHeight < sourceHeight * maximumScale
```

These intrinsic conditions participate in automatic selection. A per-video manual package choice bypasses package enablement, the user maximum-source-resolution limit, and these scale bounds. Signal-domain compatibility is never bypassed.

The user-configurable maximum supported resolution is the source's shorter edge in pixels. `0` means unlimited.

### Dimensions

Each pass dimension is a positive rational multiple of either:

- `source`: decoded video dimensions, or
- `destination`: physical destination dimensions after display scaling.

Legacy positive integer `widthScale` and `heightScale` properties remain accepted and are source-relative.

A dimension cannot exceed 32768 pixels, and one intermediate cannot exceed 134217728 pixels.

## SkSL pass ABI

Every `.sksl` file is a complete `SKRuntimeEffect` shader:

```c
uniform shader input0;
uniform float2 input0Size;
uniform float2 outputSize;

half4 main(float2 position) {
  float2 normalized = position / outputSize;
  float4 source = float4(input0.eval(normalized * input0Size));
  return half4(source.rgb, 1.0);
}
```

- `position` and child `.eval()` coordinates are local pixel coordinates, with texel centers at half-integers.
- Child images use clamp tile modes and linear sampling.
- Inputs are named `input0`, `input1`, and so on in manifest order.
- Set `sizeUniforms` when the shader declares `outputSize` and one `inputNSize` per input.
- The stage runs before transfer decoding because Anime4K-style models are trained on normalized nonlinear RGB. PQ, HLG, and explicitly linear sources currently skip `signal-rgb` packages.
- The final `MAIN` image is transfer-decoded and handed to the user's ordinary Bilinear, Mitchell, Lanczos, or other display scaler.

## Selection behavior

Settings > Playback > Video lists installed packages in priority order. Automatic selection chooses the first package that:

1. is enabled,
2. accepts the source's shorter-edge resolution,
3. accepts the current physical scale window, and
4. supports the input signal domain.

The video-sheet settings menu can choose Automatic, Off, or any installed package. Manual choices include globally disabled and resolution-ineligible packages and are saved per video.

## Testing

A folder can be imported through the product UI, which validates metadata, containment, license, and every stage shader before atomically installing it. Import is the supported check: it detects compilation failures, invalid dimensions, and manifest or containment errors before a package is installed.

Import validation is not a reference-image equivalence test. Rendering quality regressions - non-finite output, gross color/geometry errors, luma drift, and flat-field instability - still have to be judged by playing video through the package.
