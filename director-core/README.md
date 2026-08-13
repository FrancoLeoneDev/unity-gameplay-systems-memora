# Director Core

**The only folder here that compiles and runs on its own.**

The scheduling engine behind Memora's ambient-event director — the system that decides when the house
does something unsettling. It's written as **pure C# with no `UnityEngine` dependency**, taking its
clock, its randomness and its opportunity probe as injected interfaces.

That decision paid twice:

1. The logic is unit-testable in EditMode, where `Time.time` is frozen at zero. `tests/` has the suite.
2. The same fake clock powers an offline simulator that runs 100 playthroughs of a 33-minute demo in
   seconds, without entering Play mode — which is the only practical way to balance an event pace you'd
   otherwise have to play for hours to observe.

### What's modelled

Shuffle-bag selection (no repeats until the cycle completes, weighted, seed-reproducible), per
source-and-tier cooldowns, jumpscare rationing with quotas and suppression windows, a liberation gate
state machine, and an absence budget for the "what changed while you were away" sibling system.

### The tests read as the spec

```
ShuffleBag_NeverRepeatsUntilCycle
ShuffleBag_SameSeedSameOrder
ShuffleBag_WeightedProportional
QuotaReached_NotAllowed
DuringSuppression_NotAllowed
WithinPostPACooldown_NotAllowed
```

`src/` is the engine, `tests/` is the NUnit suite. The full project runs 145 EditMode tests; these are
the ones that don't need Unity.
