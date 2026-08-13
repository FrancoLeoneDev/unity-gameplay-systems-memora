# Save System

JSON persistence over Newtonsoft, behind an `ISaveSerializer` so the format is swappable. Objects opt
in by implementing `ISaveable`; a `SaveableEntity` component per GameObject owns a stable GUID and
collects the state of every `ISaveable` on it.

Nothing here is a tutorial save system. Each of the three pieces below exists because something broke
in the actual game.

---

## Atomic writes with automatic backup recovery

`SaveManager.WriteFile` / `ReadFile`

Writing straight over the save file means a crash or a power cut mid-write leaves a truncated JSON and
a dead run. So a write goes to a `.tmp` first and then uses `File.Replace`, which is atomic on NTFS
**and** produces the `.bak` in the same operation — one syscall, no window where neither file is valid.

Reading is the mirror image: if the primary JSON is missing or fails to parse, it recovers from the
backup on its own, with a second best-effort fallback if even that fails.

This is also why `NewGame` deletes through `Delete()` rather than removing the primary file by hand —
wiping only `slot_N.json` left the previous run alive in the `.bak`, and the first read of the *new*
run silently resurrected it.

## Multi-pass restore with a hard stop

`SaveManager.RestoreState`

Restoring one entity can bring another into existence: the printer turns on a photo, the lamp
instantiates a bulb. Those newcomers register themselves *during* the restore, so a single pass over
the registry misses them — and iterating the live registry directly throws
`InvalidOperationException` the moment one of them calls `Add`, aborting the entire load.

So the restore takes a defensive copy, runs a pass, collects whatever appeared, and runs again — up to
a hard cap (`MaxPasadasRestauracion`) so a pathological chain can't hang the game instead of failing.

## Unity's fake null, and why destruction has to be queued

`SaveableEntity`

Two Unity-specific traps, both documented in the file with the bug that motivated them:

- A destroyed `MonoBehaviour` is not really `null`. Unity overloads `==` to *pretend* it is, but that
  overload is bypassed when the reference is held as an interface (`ISaveable`), so the usual null
  check silently lies. `DestroySaveable` exists so a component can remove itself from its entity's
  cache instead of leaving a stranded reference that crashes the next save.
- Destroying a GameObject *during* a `RestoreState` mutates the collection being iterated. Those
  destructions are queued (`MarkForDeferredAction`) and flushed after the pass — this is the fix for a
  real production soft-lock the comments call "the door-knocker bug".

---

## Also here

- **Suppression gate** — a scripted set-piece can turn saving off while it runs (`SetSavingSuppressed`),
  so a checkpoint can't land mid-sequence and freeze the run in an inconsistent state. `TrySave()` is
  the entry point for gameplay saves that must respect it.
- **Lifecycle autosave** with guards: never from the main menu (the manager is `DontDestroyOnLoad` and
  survives going back there — without the guard, quitting from the menu wrote the *menu's* scene index
  into the slot and every future Continue loaded the menu), and never mid-transition.
- **Hot cache** so a checkpoint doesn't re-read and re-deserialize the file every time, while inactive
  entities keep their previous state from that cache instead of being wiped.
- **Duplicate-GUID detection** at capture time, which catches a GUID copied by duplicating an object
  across scenes — `OnValidate` can't see it because memories load additively over the house, so the
  collision only exists at runtime.

**Entry point:** `SaveManager.cs` · **Per-object:** `SaveableEntity.cs` · **Depends on:** Newtonsoft.Json, Unity
