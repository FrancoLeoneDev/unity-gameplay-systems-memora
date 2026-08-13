using System;

namespace Memora.DirectorCore
{
    public enum DirectorState { Idle, Armed }
    public enum FireReason { Opportunity, Timeout, Random }
    public enum OpportunitySignal { None, PlayerStill, Dwell, PostInteraction }

    /// <summary>Categoría del evento (diseño §5). NO son compuertas: definen pesos por defecto y telemetría.</summary>
    public enum EventTier { AudioDistant, Gaslight, PhysicalSubtle, PhysicalAggressive }

    /// <summary>Fuente diegética del bleedthrough (metadata para el juego de 6h; en la demo no selecciona).</summary>
    public enum DiegeticSource { House, Isabella, Nicholas, Abuelo }

    /// <summary>RNG abstraído para testear la distribución de gaps y el variance guard con secuencias fijas.</summary>
    public interface IRandomSource
    {
        double NextDouble();
        int Next(int minInclusive, int maxExclusive);
    }

    public sealed class SystemRandomSource : IRandomSource
    {
        private readonly Random _r;
        public SystemRandomSource(int seed) { _r = new Random(seed); }
        public double NextDouble() => _r.NextDouble();
        public int Next(int minInclusive, int maxExclusive) => _r.Next(minInclusive, maxExclusive);
    }

    /// <summary>
    /// Señales del jugador que lee el engine (jamás historia/fases). S4 (anchor detrás) vive en
    /// EventSelector/LookGate, no acá. director-timing-oportuno.md §2.
    /// </summary>
    /// <summary>
    /// Modelo de sorteo de la espera entre armados.
    ///
    /// Acotado — uniforme sesgada entre MinGap y MaxGap, con un guard que rompe rachas. La espera
    /// siempre cae en la misma franja, así que el jugador termina estimando la frecuencia aunque no
    /// adivine QUÉ va a pasar. Es el modelo original; se conserva para poder volver.
    ///
    /// Poisson — exponencial alrededor de GapMeanSec, con piso en GapFloorSec y sin tope duro (queda
    /// el techo anti-sequía). Es MEMORYLESS: que recién haya pasado algo no dice nada sobre cuándo
    /// viene lo próximo. De ahí salen solos los racimos y las sequías, sin programarlos.
    /// </summary>
    public enum GapModel
    {
        Acotado,
        Poisson,
    }

    public interface IOpportunityProbe
    {
        bool IsPlayerStill { get; }             // S1 crudo (SmoothedVelocity bajo umbral)
        float TimeSinceLastInteraction { get; } // S3 (seg desde OnStopInteracting; grande si nunca)
        bool IsPlayerBusy { get; }              // pausa/inventario/interactuando/cinemática (fix #14)
    }

    /// <summary>
    /// Knobs del timing v2 (director-timing-oportuno.md §6). Es C# puro (no ScriptableObject) para que
    /// el engine sea testeable sin Unity; el DirectorConfig (SO) mapea a esto en el MonoBehaviour.
    /// </summary>
    public sealed class DirectorScheduleSettings
    {
        // 5 knobs + kill-switch
        public float MinGap = 45f;
        public float MaxGap = 120f;
        public float GapExponent = 1.5f;

        /// <summary>Cómo se sortea la espera entre armados. Ver <see cref="GapModel"/>.</summary>
        public GapModel Model = GapModel.Acotado;
        /// <summary>Poisson: espera PROMEDIO en segundos. No es un tope — la mitad de las esperas son menores.</summary>
        public float GapMeanSec = 150f;
        /// <summary>Poisson: piso absoluto, para que dos eventos no se pisen. Mucho menor que MinGap a propósito.</summary>
        public float GapFloorSec = 20f;
        public float CeilFactor = 1.75f;
        public float RandomFireShare = 0.12f;
        public bool OpportunityMode = true;

        // Escalada por fase (Tanda 1): multiplica el gap efectivo por fase (índice = fase; <1 = más denso, >1 = más sparse).
        // Default todo 1f = sin efecto → no toca los 41 tests existentes.
        public float[] PhaseGapMultipliers = { 1f, 1f, 1f, 1f };

        // Presupuesto blando de sesión (Tanda 1, "punto justo"): mantiene el TOTAL por sesión cerca del target
        // sin volver el timing predecible. Estira/encoge el gap según fired-vs-esperado. Ver ComputeBudgetStretch.
        public float SessionTargetEvents = 21f;   // total objetivo por corrida (~33 min)
        public float SessionLengthSec = 1980f;    // 33 min
        public float BudgetGain = 1f;             // 0 = sin corrección (A/B); 1 = corrección plena
        public float BudgetMaxStretch = 2.5f;     // tope de estiramiento/encogimiento (stretch ∈ [1/this, this])

        // derived (tuning fino, no knobs principales)
        public float VarianceEpsilon = 0.20f;
        public float CeilingJitter = 15f;       // ± sobre maxGap*ceilFactor
        public float ZoneGraceMin = 2f;
        public float ZoneGraceMax = 8f;
        public float S1StillSec = 2f;
        public float S2LingerSec = 20f;
        // S3: la ventana post-interacción se SORTEA por ciclo armado en [min,max] (anti-condicionamiento:
        // una ventana fija de 4s era un patrón aprendible). min==max => ventana fija (tests deterministas).
        public float S3WindowMinSec = 2f;
        public float S3WindowMaxSec = 7f;

        // pesos de oportunidad (OpportunityWeights)
        public float WeightStill = 0.40f;
        public float WeightDwell = 0.20f;
        public float WeightPostInteraction = 0.30f;
    }
}
