# Scripted Set-Piece

A framework for authored horror moments. `SightEvent` is a Template Method base — a one-shot trigger
guard, an abstract `Event()`, and save/restore — and each set-piece composes single-responsibility
actuators that know nothing about each other: a warm-light group fader, a pendant lamp death sequence,
a desaturation volume, a sub-threshold sway, the dissociation effect.

### The moment it was built for

Six phases in a corridor. The four warm lights die in parallel; the hanging lamp agonises warm, dies,
and relights **cold**, revealing a figure at the far end; an audio riser is tuned so its peak lands on
the exact frame the hospital ceiling appears over the house. The composition rule was: *the hospital is
the noun, the grade is the adjective, the dissociation is the verb — and everything converges on one
downbeat.* The last phase hides the reset under a blink, so when you open your eyes the house is back
to normal and the figure isn't there.

### The part to read

`TeardownAll(bool instant)` — one disarm path reused by both the interrupted-by-reload route (snaps)
and the normal end-of-sequence route (graceful fades). That's what prevents the classic bug where the
happy path cleans up one set of things and the emergency path cleans up a different set.

Every actuator kills its own running coroutine before starting a new one, and restores state in
`OnDisable`.

**Entry point:** `DoctorSightEvent.cs` · **Base:** `SightEvent.cs` · **Depends on:** `_shared`, RecallDaze, HDRP volumes
