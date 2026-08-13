# Unity Gameplay Systems — Memora

Gameplay systems extracted from **[Memora](https://memoraoficial.itch.io/memora)**, a first-person
psychological horror game built in **Unity 6 / HDRP**.

> **These are reading copies, not an installable package.** Each system references project-wide
> services (save, input, audio, managers) that ship in `_shared/`, plus Unity/HDRP and DOTween.
> Dropping a folder into a fresh project will not compile — the goal here is to let you *read* real
> production code from a game that actually shipped, not to hand you a plugin.
>
> The one exception is [`director-core/`](director-core), which is pure C# with no `UnityEngine`
> dependency and **does** compile and run its test suite standalone.

---

## Why this repo exists

The game's own repository can't be public: it contains purchased Asset Store content, and it's a
commercial project. But "trust me, I wrote a save system" is worth nothing without the code. So the
systems live here, on their own, in the state they actually ship in.

Nothing here was written for a portfolio. Every file runs in the game.

---

## Systems

| | System | What it is |
|---|---|---|
| 01 | [Memory Dive](01-memory-dive) | Hold on a photograph to enter the memory: a four-stage HDRP custom-pass grade, a crack-of-light shader on the print, and a real volumetric light pouring out of it |
| 02 | [Physics Door](02-physics-door) | Doors opened by walking into them — `Rigidbody` + `HingeJoint`, torque depending on where you hit the blade. Run-in slam, sweep-checked closing, optional `JointSpring` auto-close |
| 03 | [Key & Lock](03-key-and-lock) | In-situ camera framing on the lock and a full key-into-lock animation, plus the **custom editor** that made the lock position authorable |
| 04 | [Interactions Menu](04-interactions-menu) | Generic item selector any world object can open by implementing `IMenuInteractable` — doors, printer, lamp |
| 05 | [Inventory](05-inventory) | Three-tab inventory with 3D preview, note reading, item combination, and unseen-item badges |
| 06 | [Chain Examine](06-chain-examine) | Object inspection with nested sub-elements: pick up a frame, interact with the photo inside it without letting go |
| 07 | [Document Reading](07-document-reading) | Resident Evil-style note reading — the document never moves; the camera fades and teleports to it |
| 08 | [In-Game PC](08-in-game-pc) | A working computer inside the game: boot sequence, password, mail, security-camera circuit, and a printer that prints a physical photo |
| 09 | [Scripted Set-Piece](09-scripted-set-piece) | Template-Method framework for authored horror moments, composed from single-responsibility actuators |
| — | [`director-core`](director-core) | The scheduling engine behind the ambient-event director. **Pure C#, compiles and tests standalone** |
| — | [`editor-tools`](editor-tools) | Editor tooling built to iterate on a 33-minute demo without playing it end to end |
| — | [`_shared`](_shared) | Save system, input, audio library and managers that every system above leans on |

---

## Where to look first

If you only open three files:

- **[`_shared/SaveableEntity.cs`](_shared/SaveableEntity.cs)** — Unity's "fake null" on destroyed
  components, and why destroying a GameObject *during* a restore has to be queued instead of run
  in place. The comment names the production soft-lock that motivated it.
- **[`01-memory-dive/InspectedStencilPass.cs`](01-memory-dive/InspectedStencilPass.cs)** — HDRP does
  not rebind `_StencilTexture` for passes at `AfterPostProcess`, so the mask came back zero
  everywhere. The fix is a script-owned `RenderTexture` published as a global. The header is the
  postmortem.
- **[`02-physics-door/PhysicDoor.cs`](02-physics-door/PhysicDoor.cs)** — the header explains why an
  earlier impulse-based attempt physically could not work (with the math), and the rule the whole
  system follows: *continuous interaction is physics, discrete verbs are animation*.

---

## Notes for the reader

**Mixed-language naming.** The project is written by a Spanish-speaking team and uses Spanish for
domain nouns (`PhysicDoor.bocaCerradura`, `MaderaSalida`) alongside English for engine-facing code.
That's a deliberate project convention, not drift. Comments are in Spanish where they explain
domain decisions.

**Comments explain decisions, not syntax.** Where a comment is long, it's usually documenting a bug
that was actually hit and why the fix looks the way it does.

**Tests.** The full project runs 145 EditMode tests. The subset covering `director-core` is included
here and runs without Unity.

---

## Stack

Unity 6 · HDRP · C# · HLSL · DOTween · Newtonsoft.Json · Unity Test Framework (NUnit)

## Author

Franco Leone — [portfolio](https://www.francoleone.com.ar/) · [LinkedIn](https://www.linkedin.com/in/franco-leone-294511242/)

## License

MIT — see [LICENSE](LICENSE).
