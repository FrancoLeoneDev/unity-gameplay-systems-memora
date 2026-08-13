// Tests del presupuesto de ausencia de Vida Lejana (Sistema 2, §F). Pura ⇒ deterministas sin Unity.
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class AbsenceBudgetTests
{
    [Test]
    public void BelowFloor_IsZero()
        => Assert.That(AbsenceBudget.Compute(50f, 90f, 90f, 2), Is.EqualTo(0));

    [Test]
    public void AtFloor_IsZero()
        => Assert.That(AbsenceBudget.Compute(90f, 90f, 90f, 2), Is.EqualTo(0));

    [Test]
    public void FirstStep_IsOne()
        => Assert.That(AbsenceBudget.Compute(180f, 90f, 90f, 2), Is.EqualTo(1));

    [Test]
    public void SecondStep_IsTwo()
        => Assert.That(AbsenceBudget.Compute(270f, 90f, 90f, 2), Is.EqualTo(2));

    [Test]
    public void FarPastMax_ClampedToMax()
        => Assert.That(AbsenceBudget.Compute(9999f, 90f, 90f, 2), Is.EqualTo(2));

    [Test]
    public void ZeroStep_GuardedToZero()
        => Assert.That(AbsenceBudget.Compute(200f, 90f, 0f, 2), Is.EqualTo(0));

    [Test]
    public void ZeroMax_GuardedToZero()
        => Assert.That(AbsenceBudget.Compute(200f, 90f, 90f, 0), Is.EqualTo(0));
}
