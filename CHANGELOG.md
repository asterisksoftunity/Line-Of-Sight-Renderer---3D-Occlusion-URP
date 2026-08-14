# Changelog

## 1.0.1
- Added a URP render graph implementation, so the package works on Unity 6.0
  through 6.5 and later, including versions where Compatibility Mode has been
  removed.
- Compatibility Mode is still supported on the Unity versions that have it. The
  correct path is selected automatically at compile time.
- Removed deprecation warnings on newer Unity versions.

## 1.0.0
- Initial release.
- Line of sight visibility rendering for URP: FOV cone,
  proximity radius, exact 3D occlusion via height map ray marching.
- Occluder selection by Rendering Layer Mask, terrain occlusion.
- Temporal reveal/hide response, mask blur, tint/desaturation composite.
- CPU gameplay visibility queries and per object visibility trackers driven
  by the same data as the visuals.
