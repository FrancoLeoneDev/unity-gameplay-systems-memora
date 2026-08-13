# Chain Examine

Object inspection where what you're holding can have parts you interact with, without putting it down.

A frame falls off the wall. You pick it up, rotate it, and inside the broken frame there's a photo you
can take. While something is still pending inside, the container **locks the exit** — you can't drop
the object until you deal with what it holds.

### Details

- Sub-elements live on their own raycast layer, which survives the sweep that moves the whole object
  to the inspection layer. A mis-authored first step would be unreachable, so it's forced from code at
  runtime rather than trusted to the prefab.
- The background blur uses a mask the inspected object writes into its own `RenderTexture` — see
  `InspectedStencilPass.cs` in `01-memory-dive` for why the obvious stencil route doesn't work in HDRP.
- `InspectionLightService` is a static service on purpose: the light GameObject starts inactive, so a
  singleton component living on it could never run its own `Awake` to register itself.

`InspectObject.cs` is the largest file here (~790 lines). It's the single owner of "what is being
examined right now", which several other systems depend on — but the neighbouring responsibilities
(light, render layers, blur, sub-element raycast) are delegated to their own owners rather than
reimplemented.

**Entry point:** `InspectObject.cs` · **Depends on:** `_shared`, HDRP custom passes, DOTween
