// Tests del cooldown por fuente/tier (§7-§B / R5). Lógica pura => deterministas sin Unity.
// (Escritos a ciegas con Unity cerrado; se validan al reabrir el editor — convención del proyecto.)
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class SourceTierCooldownTests
{
    [Test]
    public void FreshKey_NotOnCooldown()
    {
        var cd = new SourceTierCooldown();
        Assert.That(cd.IsOnCooldown(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f, 90f), Is.False);
    }

    [Test]
    public void AfterFire_OnCooldownWithinWindow()
    {
        var cd = new SourceTierCooldown();
        cd.RegisterFire(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f);
        Assert.That(cd.IsOnCooldown(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 50f, 90f), Is.True);
    }

    [Test]
    public void AfterCooldownElapsed_NotOnCooldown()
    {
        var cd = new SourceTierCooldown();
        cd.RegisterFire(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f);
        Assert.That(cd.IsOnCooldown(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 110f, 90f), Is.False);
    }

    [Test]
    public void DifferentSource_Independent()
    {
        var cd = new SourceTierCooldown();
        cd.RegisterFire(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f);
        Assert.That(cd.IsOnCooldown(DiegeticSource.Isabella, EventTier.PhysicalAggressive, 20f, 90f), Is.False);
    }

    [Test]
    public void DifferentTier_Independent()
    {
        var cd = new SourceTierCooldown();
        cd.RegisterFire(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f);
        Assert.That(cd.IsOnCooldown(DiegeticSource.Nicholas, EventTier.AudioDistant, 20f, 90f), Is.False);
    }

    [Test]
    public void ZeroCooldown_NeverOnCooldown()
    {
        var cd = new SourceTierCooldown();
        cd.RegisterFire(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 10f);
        Assert.That(cd.IsOnCooldown(DiegeticSource.Nicholas, EventTier.PhysicalAggressive, 11f, 0f), Is.False);
    }
}
