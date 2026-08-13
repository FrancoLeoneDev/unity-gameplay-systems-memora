# Physics Door

Doors with no "open" button. The blade is a `Rigidbody` with a `HingeJoint`, and a collider on the
player pushes it before the body reaches it — so hitting it near the edge swings it easily and near
the hinge barely moves it. Nobody explains that to the player; it just feels right.

### The design rule

**What the player does against the blade is physics. What is authored is animation.**

Pushing is physics. So is the slam when you run into a closed door — that one is an impulse written
straight onto the live blade, not a tween. Which means it stops against whatever is actually there:
the hinge limit, a wall, a chair someone left behind the door. A tween would have passed through all
of it, and would also have forced every door in the game to declare how far it's allowed to fly open.

The deliberate keypress close is the animation, along with everything the house's event director
commands — those are authored gestures that have to read identically every time.

### Details worth reading

- The slam writes `angularVelocity` directly rather than applying torque, so the gesture doesn't
  change with each door's mass and inertia tensor. Impact is detected as a drop in angular speed
  within a single `FixedUpdate` step — one rule that covers both the hinge limit (which emits no
  collision) and a real collider.
- Its direction comes from the actual torque of the hit (`r × F` projected on the hinge axis), not
  from a field somebody could set wrong.
- Auto-close is the `HingeJoint`'s own `JointSpring`, and it ships **off by default** — a deliberate
  design call: the door stays where you leave it, and if *every* door closed itself, the house closing
  one would stop reading as a scare.
- Closing sweeps its own arc against the player layer: stand in the doorway and it refuses to close
  rather than clipping through you.
- Collision layers follow the state machine, so you don't get stopped at a distance by a frozen blade.

**Entry point:** `PhysicDoor.cs` · **Depends on:** `10-save-system`, `_shared` (audio library, interaction), DOTween
