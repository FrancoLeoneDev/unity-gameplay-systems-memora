# Document Reading

Note and document reading in the style of Resident Evil Requiem: the paper never moves and never
teleports into your hand. The camera fades to black, reappears in front of the document in place, and
fades back with a side panel showing the transcription.

### The central decision

**No camera interpolation, anywhere.** Every move is a fade over an instant teleport. A lerped camera
the player doesn't control reads as sloppy in first person and makes people motion-sick; a cut hidden
under a fade doesn't.

The state machine is four states with a single re-entry gate, and `Update` does nothing but watch for
the cancel input — and only while the reader is enabled.

`DocumentPanelUI` guards against a real async race: switching documents quickly could let a late
localization callback overwrite the newer text. Each request carries an id and stale responses are
dropped.

**Entry point:** `DocumentReader.cs` · **Depends on:** `_shared` (input, camera rig, localization), uGUI
