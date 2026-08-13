# Interactions Menu

A generic "use an item on a world object" selector. The camera has already moved to the object; this
opens a side panel with a fixed 3×3 grid of the inventory as black-and-white thumbnails. WASD to
choose, Space to use, right click to leave.

Any object in the world opts in by implementing `IMenuInteractable` — doors, the printer, the lamp.
`UseItem` returns a verdict `bool`, so the object itself decides whether the item was right, and a
wrong item flashes red without kicking you out of the menu.

### The part to read

`TryUseSelected` wraps the `UseItem` call in try/catch. The menu does not trust implementers not to
throw: a failure is treated as "wrong item" instead of leaving a zombie menu, and the grid is rebuilt
in case the implementer already mutated the inventory before blowing up.

Optional secondary actions ride on a separate interface (`IMenuSecondaryAction`), so objects that
don't have one are unaffected — Open/Closed rather than an enum and a switch.

**Entry point:** `InteractionsMenu.cs` · **Depends on:** `_shared` (input, inventory, managers), DOTween, uGUI
