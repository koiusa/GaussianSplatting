# Gaussian Splatting

Standalone runtime Gaussian Splatting support for Unity.

## Requirements

- Unity 6.3 or later
- Input System 1.11.2

## Installation

Install the package from npm by adding the npm registry to your project's
`Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "koiusa",
      "url": "https://registry.npmjs.org",
      "scopes": ["com.koiusa"]
    }
  ],
  "dependencies": {
    "com.koiusa.gaussiansplatting": "0.2.2"
  }
}
```

The npm package automatically installs its `com.koiusa.placement` dependency.

Alternatively, install both packages from Git by adding the following entries to
your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.koiusa.placement": "https://github.com/koiusa/GaussianSplatting.git?path=/Assets/com.koiusa.placement",
    "com.koiusa.gaussiansplatting": "https://github.com/koiusa/GaussianSplatting.git?path=/Assets/com.koiusa.gaussiansplatting"
  }
}
```

## Contents

The Gaussian Splatting package contains the renderer, PLY parser, GPU/CPU sorting,
GPU frustum culling, shaders, and the default material. Reusable transform placement
and manipulation components are provided by the separate `com.koiusa.placement`
package.

## Optional integrations

- Add `GaussianSplatOffAxisController` beside `GaussianSplatRenderer` when an
  Off-Axis system overrides the camera view/projection matrices. Without it, the
  renderer uses the normal camera transform and has no Off-Axis-specific behavior.
- Add `GaussianMeshShadowRenderer` only when MMD or another animated mesh should
  cast a shadow onto the splats. Assign its `ShadowSource` from the project-specific
  model adapter; the base renderer does not depend on LibMMD.
