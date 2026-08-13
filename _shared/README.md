# Shared Infrastructure

Project-wide services the systems above lean on. They're here so the other folders are readable, not
because they're part of any one system.

> Persistence used to live here too. It earned its own folder: see **[`10-save-system`](../10-save-system)**.

### Input

`InputHandler` wraps the Input System with named contexts (gameplay, menu, puzzle), so a system can
take over input without every caller knowing which action map is live. `SetContext(Puzzle)` is what
stops movement keys from firing while you're using the in-game PC, for example.

### Audio

`AudioLibrary` is the single entry point for audio, with nested enums per family (doors, drawers,
footsteps). The newer director sounds moved to a `ScriptableObject` + dictionary — both patterns are
visible in the file, which is honest about how it grew rather than pretending it was designed that way
from the start.

### Interaction

`InteractableObject` is the abstract base every interactable in the game derives from (80+ of them),
and `InteractableRay` is the continuous camera raycast that detects them, drives the 3D hint icons,
and routes `Interact()`. `Interfaces.cs` holds the small contracts they share.

### Managers

`GameManager` owns the coarse game state (paused, interacting, inventory open, in cinematic) and
exposes it both as properties and as a `GameState` dictionary that systems query before reacting to
input. `UiButtonsManager` renders the contextual hint bar. `MoveCamPuzzles` is the shared camera rig
that puzzles, document reading and close-ups borrow — and give back.
