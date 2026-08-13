# Inventory

Opens at any time to review what you've collected and inspect it again: every object renders in 3D and
rotates with the mouse over a blurred background, with its description alongside.

The categories are the game's, not the system's — here they're usable objects, notes and photos,
because that's what the story needs.

- **Notes** read full-screen, and reading is modal: the tab strip hides for immersion and the
  inventory won't close until you exit.
- **Photos** aren't just items — they're the entrance to a memory. Holding on one closes the inventory
  by itself to hand the screen to the transition.
- **Objects combine**: pick a combinable one and the grid greys out everything that doesn't pair with
  it; if both point at the same result they fuse into a new item. That's how a torn poem is reassembled.
- An amber dot marks what you haven't seen yet, backed by a persisted set of seen IDs.

### Known rough edge

`InventoryUI` reads `HospitalClimaxDirector` directly to block input during one late set-piece, instead
of publishing a `GameState` through the mechanism the class already consults two lines below. It's one
line and it works, but it's the kind of shortcut worth naming rather than hiding.

**Entry point:** `InventoryUI.cs` (view) / `Inventory.cs` (data) · **Depends on:** `10-save-system`, `_shared`, `06-chain-examine`, DOTween, uGUI
