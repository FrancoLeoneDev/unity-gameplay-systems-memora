# In-Game PC

A working computer inside the game. Sit down and it boots with its own sound, the screen swaps
material and lights the room, and it asks for a password you deduce by exploring the house. Inside
there's mail, photos, and a security-camera circuit that renders real cameras from the scene, each
with its own signal state. It's wired to a printer that first needs paper — only then does it spit out
a physical photo you pick up off the desk.

### The part to read

`TakeSnapshotAndApplyToScreenRenderer` renders the canvas to a `RenderTexture` and applies it to the
monitor. `try/finally` guarantees the temporary camera and texture are released and the canvas's
original render mode restored even if something throws halfway; the previous snapshot texture is freed
before being replaced so VRAM doesn't accumulate; and the auxiliary camera zeroes its volume and probe
layer masks so it doesn't drag the global post-processing into the screen.

`TrySubscribeFuse` documents and solves the `Awake`-order race with the `FuseManager` singleton — the
project's systemic bug class, handled explicitly here.

Save/restore reads both the old format (a raw bool) and the new one (a struct), and re-arms dependent
triggers to avoid soft-locks on load.

**Entry point:** `Pc.cs` · **Depends on:** `_shared`, `FuseManager`, uGUI, HDRP
