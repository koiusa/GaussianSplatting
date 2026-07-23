# PLY File Selector

Open `GaussianSplatFileSelector.unity` and enter Play Mode. In the Unity Editor,
use **Browse...** to select a binary Gaussian Splat PLY and then press **Load**.

In a Player build, enter an absolute file path and press **Load**. The sample deliberately
uses no platform-specific native file-picker dependency.

The sample uses the normal camera path and does not install optional Off-Axis or
animated-mesh shadow components. Add `GaussianSplatOffAxisController` or
`GaussianMeshShadowRenderer` to the renderer GameObject when those integrations
are required.
