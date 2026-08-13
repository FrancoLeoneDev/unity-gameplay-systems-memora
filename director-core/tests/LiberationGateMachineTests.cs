// Tests de la máquina de gating de objetos scripteados (Tanda 2, §7-§A.2). Pura ⇒ deterministas sin Unity.
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class LiberationGateMachineTests
{
    [Test]
    public void New_IsLocked_NotArmed()
    {
        var g = new LiberationGateMachine(requireReentryAfterRelease: true);
        Assert.That(g.State, Is.EqualTo(GateState.Locked));
        Assert.That(g.IsArmed, Is.False);
    }

    [Test]
    public void Release_WhenReentryRequired_GoesReleasedNotArmed()
    {
        var g = new LiberationGateMachine(true);
        g.Release();
        Assert.That(g.State, Is.EqualTo(GateState.Released));
        Assert.That(g.IsArmed, Is.False, "liberado no alcanza: falta que el jugador vuelva a la zona");
    }

    [Test]
    public void Release_WhenNoReentry_GoesArmedImmediately()
    {
        var g = new LiberationGateMachine(false);
        g.Release();
        Assert.That(g.State, Is.EqualTo(GateState.Armed));
    }

    [Test]
    public void Released_EnterArmingZone_BecomesArmed()
    {
        var g = new LiberationGateMachine(true);
        g.Release();
        g.OnZoneTransition(enteringArmingZone: true);
        Assert.That(g.IsArmed, Is.True, "liberado + el jugador vuelve a la zona ⇒ armado");
    }

    [Test]
    public void Released_NonEnterTransition_StaysReleased()
    {
        var g = new LiberationGateMachine(true);
        g.Release();
        g.OnZoneTransition(enteringArmingZone: false); // salir / transición a otra zona
        Assert.That(g.State, Is.EqualTo(GateState.Released));
    }

    [Test]
    public void Locked_EnterArmingZone_StaysLocked()
    {
        var g = new LiberationGateMachine(true);
        g.OnZoneTransition(enteringArmingZone: true); // antes de liberarse: nunca arma
        Assert.That(g.State, Is.EqualTo(GateState.Locked));
    }

    [Test]
    public void Release_Idempotent()
    {
        var g = new LiberationGateMachine(true);
        g.Release();
        g.Release(); // la 2da no reabre nada
        Assert.That(g.State, Is.EqualTo(GateState.Released));
    }

    [Test]
    public void Release_WithoutSubsequentEnter_StaysReleased()
    {
        // Jugador IN armingZone al momento de Release: el tracker NO notifica (mismo-zona) ⇒ la máquina no
        // recibe OnZoneTransition. Invariante: debe quedar Released, NO armar sola. Guardia de regresión del
        // invariante "arm-on-enter" (depende del guard `if (newZone == _currentZone) return` de PlayerZoneTracker).
        var g = new LiberationGateMachine(requireReentryAfterRelease: true);
        g.Release();
        Assert.That(g.State, Is.EqualTo(GateState.Released));
        Assert.That(g.IsArmed, Is.False, "Release sin Enter posterior: debe permanecer Released");
    }

    [Test]
    public void Armed_StaysArmed_OnFurtherTransitions()
    {
        var g = new LiberationGateMachine(true);
        g.Release();
        g.OnZoneTransition(true); // armado
        g.OnZoneTransition(false);
        g.OnZoneTransition(true);
        Assert.That(g.IsArmed, Is.True);
    }
}
