// Tests del racionamiento de jumpscares del director (§7-§B.3). Lógica pura => deterministas sin Unity.
// (Escritos a ciegas con Unity cerrado; se validan al reabrir el editor — convención del proyecto.)
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class JumpscareRationingTests
{
    // Helper con defaults "todo OK" => cada test varía UN solo eje para aislar la condición que niega.
    private static bool Allowed(
        float now = 700f, float sessionStart = 0f, float unlockAfterSec = 600f,
        int paFired = 0, int maxPA = 2, int phase = 3, int minPhase = 3,
        float suppressUntil = 0f, float lastPA = -9999f, float postPACd = 120f)
        => JumpscareRationing.IsAllowed(now, sessionStart, unlockAfterSec, paFired, maxPA,
            phase, minPhase, suppressUntil, lastPA, postPACd);

    [Test]
    public void AllConditionsMet_Allowed() => Assert.That(Allowed(), Is.True);

    [Test]
    public void BeforeUnlockMinute_NotAllowed() => Assert.That(Allowed(now: 300f), Is.False);

    [Test]
    public void QuotaReached_NotAllowed() => Assert.That(Allowed(paFired: 2, maxPA: 2), Is.False);

    [Test]
    public void DuringSuppression_NotAllowed() => Assert.That(Allowed(suppressUntil: 800f), Is.False);

    [Test]
    public void BelowMinPhase_NotAllowed() => Assert.That(Allowed(phase: 2, minPhase: 3), Is.False);

    [Test]
    public void WithinPostPACooldown_NotAllowed() => Assert.That(Allowed(lastPA: 650f, now: 700f, postPACd: 120f), Is.False);
}
