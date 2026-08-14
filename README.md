# Line of Sight Renderer

Single agent line of sight rendering for Unity URP.

## What this is

Line of Sight Renderer illuminates the parts of the scene a single agent can see in 3D space,
and hides everything else. Vision is defined by a view cone plus a small radius around the character, and it is blocked by
geometry: walls, roofs, raised platforms and terrain.

This is not fog of war. No map memory is being revealed as you explore. 

The same visibility data is available on the CPU. The  mask used to draw the
effect is read back and shared with gameplay code, so a prop that looks hidden is
also reported as hidden to your scripts.


## Requirements

- Unity 6000.0 or newer
- Universal Render Pipeline 17 or newer
- Either render path. The render graph is used by default, and Compatibility
  Mode (Render Graph Disabled) is also supported on Unity 6.0 to 6.2, where that
  setting still exists. 


## Quick start

1. Import the package.

2. Add the LineOfSightRenderFeature to your active URP renderer, connects the shaders, checks Compatibility Mode, and prints anything still missing to the Console.

3. Add a Line Of Sight Agent component to your player GameObject, under
   Add Component > Rendering > Line Of Sight Agent. The render feature finds the
   agent on its own, so there are no references to assign.

4. Mark your occluders. Blockers are selected by Rendering Layer, not by
   GameObject Layer. The feature uses rendering layer index 1 by default, which
   leaves the Default layer free for everything else.

5. Set Eye Height on the agent to your character's eye level, then press Play.

To see it working straight away, open the included demo scene at Demo/LineOfSightDemo.



