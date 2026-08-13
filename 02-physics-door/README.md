# Physics Door

Doors with no "open" button. The blade is a `Rigidbody` with a `HingeJoint`, and a collider on the
player pushes it before the body reaches it — so hitting it near the edge swings it easily and near
the hinge barely moves it. Nobody explains that to the player; it just feels right.

### The design rule

**Continuous interaction is physics. Discrete verbs are animation.**

Pushing is continuous, so it's physics. But the slam when you run into a closed door, and the
keypress close, have to look identical every time — those are DOTween animations. As physics they
were eaten by the angular drag and the spring, which are tuned for the continuous push.

### Details worth reading

- Auto-close is the `HingeJoint`'s own `JointSpring`, and it ships **off by default** — a deliberate
  design call: the door stays where you leave it, and if *every* door closed itself, the house closing
  one would stop reading as a scare.
- Closing sweeps its own arc against the player layer: stand in the doorway and it refuses to close
  rather than clipping through you.
- The slam's direction comes from the real torque of the impact (`r × F` projected on the hinge axis),
  not from a field somebody could set wrong.
- Collision layers follow the state machine, so you don't get stopped at a distance by a frozen blade.

**Entry point:** `PhysicDoor.cs` · **Depends on:** `_shared` (audio library, save, interaction), DOTween
