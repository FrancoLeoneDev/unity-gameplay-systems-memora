# Shared Infrastructure

Project-wide services every system above leans on. They're here so the other folders are readable, not
because they're part of any one system.

### Save system

JSON over Newtonsoft behind an `ISaveSerializer`, with `ISaveable` + `SaveableEntity` for per-object
state. Three things worth reading:

- **Atomic writes with automatic backup recovery** (`SaveManager.WriteFile` / `ReadFile`) — writes to a
  `.tmp` and uses `File.Replace`, which is atomic on NTFS and produces the `.bak` in the same step. If
  the primary JSON is missing or corrupt, it falls back to the backup on its own.
- **Multi-pass restore with a hard stop** — restoring one entity can activate another (a printer turns
  on a photo, a lamp instantiates a bulb). Those newcomers get their own pass, up to a cap so a
  pathological cycle can't hang the game.
- **`SaveableEntity`** — Unity's "fake null" on destroyed components bypasses the operator overload, and
  destroying a GameObject *during* a restore has to be queued rather than run in place. The comments
  name the production soft-lock that motivated each.

### Input

`InputHandler` wraps the Input System with named contexts (gameplay, menu, puzzle), so a system can
take over input without every caller knowing which action map is live.

### Audio

`AudioLibrary` is the single entry point for audio, with nested enums per family (doors, drawers,
footsteps). The newer director sounds moved to a `ScriptableObject` + dictionary — both patterns are
visible, which is honest about how the file grew.
