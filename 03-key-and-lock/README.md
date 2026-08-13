# Key & Lock

Interacting with a locked door frames the camera on the keyhole in situ and opens the item selector.
Pick the right key and it's consumed, travels to the lock, and turns.

### The tool half

The key's destination is a world-space offset added to a spawn point — an invisible point that was
three floats in the Inspector. The only way to know whether the key entered the lock or stabbed the
wood was to enter Play, look, exit, adjust, repeat, for every door in the game.

`DoorWithKeyEditor.cs` turns that point into a draggable Scene-view handle, with the key drawn at
origin and destination, the trajectory between them, the rotation arc, and a button that plays the
whole animation **without entering Play mode**.

### One decision worth calling out

If a door is mis-wired in the Inspector, the system does **not** abort. The key has already been
consumed and that door may be the only way forward — leaving it locked would be a soft-lock caused by
an empty field. It logs a loud error, skips the animation, and opens anyway. The run always has to be
able to continue.

**Entry point:** `DoorWithKey.cs` · **Editor:** `DoorWithKeyEditor.cs` · **Depends on:** `02-physics-door`, `04-interactions-menu`, `_shared`
