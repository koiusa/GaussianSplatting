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
    "com.koiusa.gaussiansplatting": "0.1.3"
  }
}
```

Alternatively, use Unity's Package Manager to install the Git URL:

```text
https://github.com/koiusa/GaussianSplatting.git?path=/Assets/com.koiusa.gaussiansplatting
```

## Contents

The package contains the renderer, PLY parser, GPU/CPU sorting, GPU frustum culling,
transform manipulator, shaders, and the default material.
