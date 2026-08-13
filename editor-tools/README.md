# Editor Tools

Tooling built for one reason: **the demo takes 33 minutes to play, so iterating on a scare at minute 28
meant playing 28 minutes to see it once.**

### `MemoraIrA.cs` — jump to any beat

Starts the game directly at any moment of the demo. It is **not** a teleport: it replays the real
startup sequence in order — opens the house, flips the skip-intro flag the game already had, enters
Play, waits for the player and managers to exist, grants the loadout the player would have at that
point, and only then loads the memory *through the same path the game uses*. A hand-rolled additive
load would leave the internal state inconsistent, and saving inside the memory or returning to the
house would then fail — meaning the shortcut would have you chasing bugs that exist only because of the
tool.

Adding a destination is two lines: the sequence is generic and only five values differ between them.

### `DirectorSimulator.cs` — 100 playthroughs without pressing Play

A ghost player walks the real zones of the open scene with a play profile, and the **real** engine
decides what fires. Reports events per run (min/avg/max), which ones, in which zone, at which tier.
Assumptions are printed with the report rather than hidden.

### `DirectorAuthoringGizmos.cs` — see what an event will do before it happens

Draws, in the Scene view, where an object will rotate, move or fall and with how much spread, so you
don't have to play and wait for the director to pick that event. Cyan ghosts are destination poses,
orange is push/fall direction, green is a linked object, red is a missing reference — one colour means
one thing across every actuator. Drawn only for the selected object, with a mesh cap so a heavy prop
can't hang the Editor repaint.

Its companion `DirectorAuthoringValidation.cs` covers the other half: the validator tells you what's
**wrong**, the gizmos show you what's going to **happen**.
