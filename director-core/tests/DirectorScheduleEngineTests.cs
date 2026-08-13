// Tests del DirectorScheduleEngine — director-timing-oportuno.md §7 + plan §11.G.
// Lógica verificada por TDD red-green fuera de Unity (dotnet, 15 tests); estos son los mismos casos
// en NUnit como gate del Test Runner. Reloj/probe/RNG inyectados => sin Time.time, deterministas.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Memora.DirectorCore;

[TestFixture]
public class DirectorScheduleEngineTests
{
    private sealed class MutableProbe : IOpportunityProbe
    {
        public bool IsPlayerStill { get; set; }
        public float TimeSinceLastInteraction { get; set; } = 9999f;
        public bool IsPlayerBusy { get; set; }
    }

    private sealed class FixedRandom : IRandomSource
    {
        private readonly double _d;
        public FixedRandom(double d) { _d = d; }
        public double NextDouble() => _d;
        public int Next(int a, int b) => a;
    }

    private static DirectorScheduleSettings Small() =>
        new DirectorScheduleSettings { MinGap = 1f, MaxGap = 2f, RandomFireShare = 0f };

    private static DirectorScheduleSettings BigCeiling() =>
        new DirectorScheduleSettings { MinGap = 1f, MaxGap = 100f, RandomFireShare = 0f };

    // Tupla nombrada en TODA la declaración: en C# los nombres de elementos viven en el tipo de la
    // variable; si la lista local se declara sin nombres, fires[0].r no existe (CS1061).
    private static DirectorScheduleEngine Build(IDirectorClock clock, IOpportunityProbe probe,
        IRandomSource rng, DirectorScheduleSettings s, List<(FireReason r, OpportunitySignal s)> fires,
        bool poolHasEvent = true)
    {
        return new DirectorScheduleEngine(clock, probe, rng, s, (r, sig) =>
        {
            if (!poolHasEvent) return false;
            fires.Add((r, sig));
            return true;
        });
    }

    private static void Advance(FakeDirectorClock c, DirectorScheduleEngine e, float seconds, float step = 0.25f)
    {
        float acc = 0f;
        while (acc < seconds - 1e-4f)
        {
            float s = Math.Min(step, seconds - acc);
            c.Advance(s); e.Tick(); acc += s;
        }
    }

    [Test]
    public void Director_StartsLockedUntilBirthday()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires);

        Advance(clock, eng, 100f);
        Assert.That(fires.Count, Is.EqualTo(0), "No debe disparar antes de Unlock");
        Assert.That(eng.State, Is.EqualTo(DirectorState.Idle));

        eng.Unlock();
        Assert.That(eng.CeilingDeadline, Is.EqualTo(0f), "Unlock resetea ceiling (G17)");
        Advance(clock, eng, 100f);
        Assert.That(fires.Count, Is.GreaterThan(0), "Debe disparar tras Unlock");
    }

    [Test]
    public void ArmedState_DoesNotFireOnGapExpiry()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires);
        eng.Unlock();
        Advance(clock, eng, 1.3f);
        Assert.That(eng.State, Is.EqualTo(DirectorState.Armed));
        Assert.That(fires.Count, Is.EqualTo(0));
    }

    [Test]
    public void ArmedState_ForceFiresAtCeiling()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires);
        eng.Unlock();
        Advance(clock, eng, 6f);
        Assert.That(fires.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(fires[0].r, Is.EqualTo(FireReason.Timeout));
    }

    [Test]
    public void ArmedState_StaysArmedOnEmptyPool()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var probe = new MutableProbe { IsPlayerStill = true };
        var eng = Build(clock, probe, new FixedRandom(0.0), BigCeiling(), fires, poolHasEvent: false);
        eng.Unlock();
        Advance(clock, eng, 6f);
        Assert.That(eng.State, Is.EqualTo(DirectorState.Armed));
        Assert.That(fires.Count, Is.EqualTo(0));
    }

    [Test]
    public void ArmedState_FiresOnOpportunityWhenRollMet()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var probe = new MutableProbe { IsPlayerStill = true };
        var eng = Build(clock, probe, new FixedRandom(0.0), BigCeiling(), fires);
        eng.Unlock();
        Advance(clock, eng, 6f);
        Assert.That(fires.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(fires[0].r, Is.EqualTo(FireReason.Opportunity));
        Assert.That(fires[0].s, Is.EqualTo(OpportunitySignal.PlayerStill));
    }

    [Test]
    public void OpportunityAcceptance_GrowsOverTime()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var probe = new MutableProbe { IsPlayerStill = true };
        var eng = Build(clock, probe, new FixedRandom(0.99), BigCeiling(), fires); // umbral alto => no dispara
        eng.Unlock();
        Advance(clock, eng, 2.4f);
        float rollEarly = eng.OpportunityRoll;
        Advance(clock, eng, 1.5f);
        float rollLate = eng.OpportunityRoll;
        Assert.That(rollLate, Is.GreaterThan(rollEarly), "El roll crece con señal sostenida");
        Assert.That(fires.Count, Is.EqualTo(0));
    }

    [Test]
    public void GapClamp_NeverBelowMinGap()
    {
        var s = new DirectorScheduleSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(123), s,
            new List<(FireReason r, OpportunitySignal s)>());
        for (int i = 0; i < 5000; i++)
        {
            float g = eng.DrawGap(float.NaN, float.NaN, float.NaN);
            Assert.That(g, Is.InRange(s.MinGap, s.MaxGap));
        }
    }

    [Test]
    public void GapDistribution_Skewed()
    {
        var s = new DirectorScheduleSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(7), s,
            new List<(FireReason r, OpportunitySignal s)>());
        var gaps = new List<float>(10000);
        for (int i = 0; i < 10000; i++) gaps.Add(eng.DrawGap(float.NaN, float.NaN, float.NaN));
        gaps.Sort();
        float median = gaps[gaps.Count / 2];
        float midpoint = (s.MinGap + s.MaxGap) / 2f;
        Assert.That(median, Is.LessThan(midpoint - 5f), "La distribución debe ser skew-izquierda (mediana baja)");
    }

    [Test]
    public void VarianceGuard_BreaksCluster()
    {
        var s = new DirectorScheduleSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new FixedRandom(0.0), s,
            new List<(FireReason r, OpportunitySignal s)>());
        float clustered = eng.DrawGap(float.NaN, float.NaN, float.NaN);
        float broken = eng.DrawGap(45f, 45f, 45f);
        Assert.That(clustered, Is.EqualTo(s.MinGap).Within(1e-2f));
        Assert.That(broken, Is.GreaterThan(60f), "Tras 3 gaps iguales, el 4to sale del extremo opuesto");
    }

    // ── Modelo Poisson ────────────────────────────────────────────────────────
    // El modelo Acotado de arriba tiende al centro del rango y rompe rachas a propósito, y por eso
    // el jugador termina estimando la frecuencia. Poisson existe para lo contrario: que no se pueda.

    private static DirectorScheduleSettings PoissonSettings() => new DirectorScheduleSettings
    {
        Model = GapModel.Poisson,
        GapMeanSec = 150f,
        GapFloorSec = 20f,
    };

    [Test]
    public void PoissonGap_AnyDraw_NeverBelowFloor()
    {
        // Arrange
        var s = PoissonSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(123), s,
            new List<(FireReason r, OpportunitySignal s)>());

        // Act + Assert
        for (int i = 0; i < 5000; i++)
        {
            float g = eng.DrawGap(float.NaN, float.NaN, float.NaN);
            Assert.That(g, Is.GreaterThanOrEqualTo(s.GapFloorSec));
        }
    }

    [Test]
    public void PoissonGap_ManyDraws_MeanMatchesConfigured()
    {
        // Arrange
        var s = PoissonSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(7), s,
            new List<(FireReason r, OpportunitySignal s)>());

        // Act
        double suma = 0d;
        const int N = 40000;
        for (int i = 0; i < N; i++) suma += eng.DrawGap(float.NaN, float.NaN, float.NaN);
        double media = suma / N;

        // Assert — el corte de cola a 4x baja la media ~7%, así que la tolerancia lo contempla.
        Assert.That(media, Is.EqualTo(s.GapMeanSec).Within(s.GapMeanSec * 0.15f),
            "La media empírica debe acercarse a GapMeanSec");
    }

    [Test]
    public void PoissonGap_ManyDraws_ProducesBothBurstsAndDroughts()
    {
        // Arrange
        var s = PoissonSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(99), s,
            new List<(FireReason r, OpportunitySignal s)>());

        // Act
        int cortos = 0, largos = 0;
        for (int i = 0; i < 10000; i++)
        {
            float g = eng.DrawGap(float.NaN, float.NaN, float.NaN);
            if (g < s.GapMeanSec * 0.25f) cortos++;      // racimo: la casa vuelve a estar lista enseguida
            if (g > s.GapMeanSec * 2f) largos++;         // sequía
        }

        // Assert — es LA razón de ser del modelo: sin las dos colas, es el acotado con otro nombre.
        Assert.That(cortos, Is.GreaterThan(1000), "Deben salir esperas muy cortas (racimos)");
        Assert.That(largos, Is.GreaterThan(500), "Deben salir esperas muy largas (sequías)");
    }

    [Test]
    public void PoissonGap_AfterThreeShortGaps_StillAllowsShortGap()
    {
        // Arrange — el modelo acotado, ante 3 gaps iguales, FUERZA el 4to al extremo opuesto.
        // Poisson es memoryless: el historial no debe cambiar nada.
        var s = PoissonSettings();
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new SystemRandomSource(31), s,
            new List<(FireReason r, OpportunitySignal s)>());

        // Act — se pasa un historial de racimo bajo, que en Acotado dispararía el rompe-rachas.
        int cortosTrasRacimo = 0;
        for (int i = 0; i < 5000; i++)
            if (eng.DrawGap(25f, 25f, 25f) < s.GapMeanSec * 0.25f) cortosTrasRacimo++;

        // Assert
        Assert.That(cortosTrasRacimo, Is.GreaterThan(500),
            "Tras un racimo el siguiente gap puede seguir siendo corto: no hay guard que lo corrija");
    }

    [Test]
    public void Suppression_TakesMax()
    {
        var clock = new FakeDirectorClock();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(),
            new List<(FireReason r, OpportunitySignal s)>());
        eng.SuppressFor(30f);
        eng.SuppressFor(10f);
        Assert.That(eng.SuppressUntil, Is.EqualTo(30f).Within(1e-2f));
    }

    [Test]
    public void Suppression_PreservesArmedStateAndExtendsCeiling()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        // S2 desactivada para aislar la supresión (si no, dwell acumula >20s en el Advance y dispara).
        var s = new DirectorScheduleSettings { MinGap = 1f, MaxGap = 100f, RandomFireShare = 0f, S2LingerSec = 99999f };
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), s, fires);
        eng.Unlock();
        Advance(clock, eng, 1.3f);
        float ceilBefore = eng.CeilingDeadline;
        eng.SuppressFor(30f);
        Assert.That(eng.CeilingDeadline, Is.EqualTo(ceilBefore + 30f).Within(1e-2f), "ceiling += supresión neta");
        Assert.That(eng.State, Is.EqualTo(DirectorState.Armed));
        Advance(clock, eng, 35f);
        Assert.That(eng.State, Is.EqualTo(DirectorState.Armed), "Sigue Armed tras la supresión");
        Assert.That(fires.Count, Is.EqualTo(0));
    }

    [Test]
    public void Reactivation_ClearsArmedAndResetsCeiling()
    {
        var clock = new FakeDirectorClock();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), BigCeiling(),
            new List<(FireReason r, OpportunitySignal s)>());
        eng.Unlock();
        Advance(clock, eng, 1.3f);
        eng.SetDirectingEnabled(false);
        eng.SetDirectingEnabled(true);
        Assert.That(eng.State, Is.EqualTo(DirectorState.Idle));
        Assert.That(eng.NextArmTime, Is.EqualTo(clock.Now + 1f).Within(1e-2f), "nextArm = Now + minGap");
        Assert.That(eng.CeilingDeadline, Is.EqualTo(0f));
    }

    [Test]
    public void RandomFireShare_AsFractionOfFires()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var probe = new MutableProbe(); // sin señales
        var s = new DirectorScheduleSettings { MinGap = 1f, MaxGap = 100f, RandomFireShare = 0.12f };
        var eng = Build(clock, probe, new SystemRandomSource(2024), s, fires);
        eng.Unlock();

        int guard = 0;
        while (fires.Count < 500 && guard < 5_000_000)
        {
            clock.Advance(0.25f); eng.Tick(); guard++;
        }
        int random = 0;
        foreach (var f in fires) if (f.r == FireReason.Random) random++;
        double frac = (double)random / fires.Count;
        Assert.That(frac, Is.InRange(0.08, 0.16), "random/total ~ randomFireShare");
    }

    [Test]
    public void PhaseGapMultiplier_HigherPhase_ShortensGap()
    {
        // Escalada por fase: una fase más alta (multiplicador < 1) debe acortar el gap; una más baja (>1) alargarlo.
        var s = new DirectorScheduleSettings
        {
            MinGap = 10f, MaxGap = 20f, GapExponent = 1f, RandomFireShare = 0f,
            PhaseGapMultipliers = new[] { 2f, 1f, 0.5f }
        };
        var eng = Build(new FakeDirectorClock(), new MutableProbe(), new FixedRandom(0.5), s,
            new List<(FireReason r, OpportunitySignal s)>());

        float gapPhase0 = eng.DrawGap(float.NaN, float.NaN, float.NaN); // mult 2 => eff[20,40], u=0.5 => 30
        eng.AdvancePhase(2);
        float gapPhase2 = eng.DrawGap(float.NaN, float.NaN, float.NaN); // mult 0.5 => eff[5,10], u=0.5 => 7.5

        Assert.That(gapPhase0, Is.EqualTo(30f).Within(0.1f), "fase 0 (mult 2): gap escalado hacia arriba");
        Assert.That(gapPhase2, Is.EqualTo(7.5f).Within(0.1f), "fase 2 (mult 0.5): gap escalado hacia abajo");
        Assert.That(gapPhase2, Is.LessThan(gapPhase0), "fase más alta => gap más corto (escalada)");
    }

    [Test]
    public void BudgetStretch_OnPace_IsOne()
    {
        // esperado = 20*0.5 = 10; disparados = 10 => deficit 0 => sin corrección.
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 500f, 10, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(1f).Within(0.01f));
    }

    [Test]
    public void BudgetStretch_BehindPace_ShortensGap()
    {
        // esperado 10, disparados 0 => deficit +10 => acelerar (stretch < 1).
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 500f, 0, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(0.5f).Within(0.01f));
        Assert.That(stretch, Is.LessThan(1f));
    }

    [Test]
    public void BudgetStretch_AheadPace_LengthensGap()
    {
        // esperado 10, disparados 20 => deficit -10 => frenar (stretch > 1).
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 500f, 20, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(1.5f).Within(0.01f));
        Assert.That(stretch, Is.GreaterThan(1f));
    }

    [Test]
    public void BudgetStretch_FarAhead_ClampedToMax()
    {
        // disparados enormes => stretch topa a maxStretch.
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 500f, 1000, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(2.5f).Within(0.01f));
    }

    [Test]
    public void BudgetStretch_ZeroTarget_IsOne()
    {
        // guard: target 0 no divide por cero.
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(0f, 1000f, 500f, 5, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(1f).Within(0.01f));
    }

    [Test]
    public void FiredThisSession_IncrementsOnFire()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires);
        eng.Unlock();
        Assert.That(eng.FiredThisSession, Is.EqualTo(0), "Unlock arranca la sesión en 0");
        Advance(clock, eng, 6f); // fuerza al menos un disparo por techo
        Assert.That(eng.FiredThisSession, Is.GreaterThanOrEqualTo(1), "cada disparo incrementa el contador de sesión");
    }

    [Test]
    public void BudgetStretch_FarBehind_ClampedToMin()
    {
        // esperado 20, disparados 0 => deficit +20 => stretch=1-1*(20/20)=0 => clamp a 1/maxStretch.
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 1000f, 0, 1f, 2.5f);
        Assert.That(stretch, Is.EqualTo(1f / 2.5f).Within(0.01f));
    }

    [Test]
    public void BudgetStretch_ZeroGain_IsOne()
    {
        // gain=0 (presupuesto apagado, default del SO) => sin corrección pase lo que pase.
        float stretch = DirectorScheduleEngine.ComputeBudgetStretch(20f, 1000f, 500f, 0, 0f, 2.5f);
        Assert.That(stretch, Is.EqualTo(1f).Within(0.01f));
    }

    [Test]
    public void Unlock_SecondCall_DoesNotResetSession()
    {
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires);
        eng.Unlock();
        Advance(clock, eng, 6f);
        int firedBefore = eng.FiredThisSession;
        Assert.That(firedBefore, Is.GreaterThanOrEqualTo(1));
        eng.Unlock(); // 2da llamada: debe ser no-op
        Assert.That(eng.FiredThisSession, Is.EqualTo(firedBefore), "el 2do Unlock NO resetea la sesión");
    }

    [Test]
    public void Timeout_EmptyPool_DoesNotCountAsFired()
    {
        // Fix "contador fantasma": un timeout con pool vacío NO es un evento — si contara,
        // el presupuesto de sesión creería que sonaron eventos que nunca pasaron.
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), Small(), fires, poolHasEvent: false);
        eng.Unlock();

        Advance(clock, eng, 30f); // varios ciclos: todos mueren en el techo con pool vacío

        Assert.That(fires.Count, Is.EqualTo(0), "pool vacío: nada suena");
        Assert.That(eng.FiredThisSession, Is.EqualTo(0), "un timeout sin evento NO incrementa la sesión");
    }

    [Test]
    public void ResumeWithGap_OverridesMinGapOnResume()
    {
        // Spec Franco: al volver de un recuerdo el primer gap es corto (20s), no el MinGap normal.
        var s = new DirectorScheduleSettings { MinGap = 45f, MaxGap = 120f, RandomFireShare = 0f };
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0.5), s, fires);
        eng.Unlock();
        eng.SetDirectingEnabled(false);   // entró a un recuerdo
        clock.Advance(100f);

        eng.ResumeWithGap(20f);           // volvió: gap corto de regreso

        Assert.That(eng.DirectingEnabled, Is.True);
        Assert.That(eng.NextArmTime, Is.EqualTo(clock.Now + 20f).Within(0.001f),
            "el primer arm post-recuerdo debe ser now+20, no now+MinGap(45)");
    }

    [Test]
    public void S3Window_WithinDrawnWindow_FiresOpportunity()
    {
        // Ventana S3 sorteada por ciclo: interacción hace 3s, ventana [5,5] => señal activa => dispara.
        var s = new DirectorScheduleSettings
        {
            MinGap = 1f, MaxGap = 2f, RandomFireShare = 0f, CeilingJitter = 0f,
            S3WindowMinSec = 5f, S3WindowMaxSec = 5f,
            WeightStill = 0f, WeightDwell = 0f, WeightPostInteraction = 0.5f
        };
        var clock = new FakeDirectorClock();
        var probe = new MutableProbe { TimeSinceLastInteraction = 3f };
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, probe, new FixedRandom(0.0), s, fires);
        eng.Unlock();

        Advance(clock, eng, 3f);

        Assert.That(fires.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(fires[0].r, Is.EqualTo(FireReason.Opportunity), "dentro de la ventana => oportunidad");
        Assert.That(fires[0].s, Is.EqualTo(OpportunitySignal.PostInteraction));
    }

    [Test]
    public void S3Window_OutsideDrawnWindow_NoOpportunitySignal()
    {
        // Misma interacción (3s atrás) pero ventana [2,2] => señal INACTIVA => el 1er disparo es por techo.
        var s = new DirectorScheduleSettings
        {
            MinGap = 1f, MaxGap = 2f, RandomFireShare = 0f, CeilingJitter = 0f,
            S3WindowMinSec = 2f, S3WindowMaxSec = 2f,
            WeightStill = 0f, WeightDwell = 0f, WeightPostInteraction = 0.5f
        };
        var clock = new FakeDirectorClock();
        var probe = new MutableProbe { TimeSinceLastInteraction = 3f };
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, probe, new FixedRandom(0.0), s, fires);
        eng.Unlock();

        Advance(clock, eng, 8f);

        Assert.That(fires.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(fires[0].r, Is.EqualTo(FireReason.Timeout), "fuera de la ventana => solo el techo dispara");
    }

    [Test]
    public void AdvancePhase_InvalidatesGapHistory_NoCrossPhaseClusterBreak()
    {
        // En modo timer (determinista) construimos 3 gaps iguales (cluster) y cambiamos de fase ANTES
        // del disparo que rompería por varianza. Con el historial invalidado, el gap post-cambio es un
        // draw normal (=MinGap), no el extremo "roto por cluster".
        var s = new DirectorScheduleSettings
        {
            MinGap = 10f, MaxGap = 100f, GapExponent = 1f, RandomFireShare = 0f,
            OpportunityMode = false, BudgetGain = 0f,
            PhaseGapMultipliers = new[] { 1f, 1f } // mismas cotas => aísla el efecto del historial
        };
        var clock = new FakeDirectorClock();
        var fires = new List<(FireReason r, OpportunitySignal s)>();
        var eng = Build(clock, new MutableProbe(), new FixedRandom(0f), s, fires);
        eng.Unlock();
        Advance(clock, eng, 31f);            // disparos en t=10,20,30 => gapCount=2, nextArm=40
        Assert.That(fires.Count, Is.EqualTo(3));
        eng.AdvancePhase(1);                 // cambio de fase => debe invalidar el historial
        Advance(clock, eng, 11f);            // disparo en t=40
        Assert.That(eng.NextArmTime, Is.EqualTo(50f).Within(1f),
            "gap post-cambio = MinGap (historial invalidado); SIN el fix saldría ~94 por ruptura de cluster");
    }
}
