# Memory Dive

The game's core mechanic: look at a photograph, hold, and you end up inside the memory.

The hold runs 3 seconds with a point of no return at 96%. Over that window an HDRP custom pass runs a
four-stage grade — peripheral vision opens, the world desaturates until the photo is the only living
thing left, a void, then a white flood — while a custom HLSL shader opens a jagged crack of light
across the print. The light coming out of that crack is not a screen effect: it's a real volumetric
point light (~10,000 lumens) that lights the room and pulses at 1.5 Hz, synced to the crack's core.
Crossing the commit threshold hands off to an additive scene load with its own arrival grade.

### The interesting parts

- **`InspectedStencilPass.cs`** — the shader needs to know which pixels are the photo. The canonical
  route (write stencil, read `_StencilTexture`) does not work: HDRP doesn't rebind that texture for
  passes injected at `AfterPostProcess`, so it read zero everywhere. The fix is a script-owned R8
  `RenderTexture` published as a global texture, which survives to the end of the frame because HDRP
  never touches it. The file header is the full postmortem.
- **The crack centers on the mesh's `bounds.center` in object space**, not UV (0.5, 0.5) — the photo
  mesh has curled UVs and the UV center isn't the visual center.
- **`MotionAccessibility.cs`** — three seconds of sustained vestibular stimulus on a *forced* tutorial
  hold. A persisted reduced-motion flag kills the Z-breathing, micro-tilt, pulsation, UV warp and FOV
  drop, leaving the rest of the sequence intact.

**Entry point:** `PhotoMemoryPortal.cs` · **Depends on:** `10-save-system`, `_shared` (input, managers), `SceneControllerManager`, DOTween, HDRP
