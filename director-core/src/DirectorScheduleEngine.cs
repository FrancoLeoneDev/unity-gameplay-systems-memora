using System;

namespace Memora.DirectorCore
{
    /// <summary>
    /// Scheduler del Director (timing v2 "Momento Oportuno", director-timing-oportuno.md NORMATIVO).
    /// IDLE -> ARMED -> FIRE: el gap ARMA, una señal de oportunidad (o el techo, o el random) DISPARA.
    /// C# puro con IDirectorClock + IOpportunityProbe + IRandomSource inyectados => testeable en EditMode
    /// y reutilizable por el simulador (dry-run) sin drift. Lee SOLO señales del jugador, jamás historia.
    /// Lógica verificada por TDD red-green fuera de Unity (15 tests del §7) antes de integrarse.
    /// </summary>
    public sealed class DirectorScheduleEngine
    {
        private readonly IDirectorClock _clock;
        private readonly IOpportunityProbe _probe;
        private IRandomSource _rng;   // no-readonly: Reseed() lo swapea al restaurar un save (seed por partida)
        private readonly DirectorScheduleSettings _s;
        private readonly Func<FireReason, OpportunitySignal, bool> _tryFire;

        private bool _unlocked;
        private bool _directingEnabled = true;
        private DirectorState _state = DirectorState.Idle;
        private int _currentPhase;
        private int _firedThisSession;
        private float _sessionStart;

        private float _nextArmTime;
        private float _armedAt;
        private float _ceilingDeadline;
        private float _opportunityRoll;
        private float _randomFireAt = -1f;

        private float _suppressUntil;
        private bool _suppressedIndefinitely;

        private float _lastNow;
        private float _stillTimer;
        private float _zoneDwellTimer;
        private bool _dwellUsedThisVisit;
        private float _s3WindowThisCycle;   // sorteada en [S3WindowMin,Max] al armar (ventana fija = aprendible)

        private float _lastFireTime;
        private bool _hasFired;
        private readonly float[] _gapHistory = new float[3];
        private int _gapHead;
        private int _gapCount;

        private OpportunitySignal _lastTriggerSignal = OpportunitySignal.None;

        public DirectorScheduleEngine(IDirectorClock clock, IOpportunityProbe probe,
            IRandomSource rng, DirectorScheduleSettings settings,
            Func<FireReason, OpportunitySignal, bool> tryFire)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
            _s = settings ?? throw new ArgumentNullException(nameof(settings));
            _tryFire = tryFire ?? throw new ArgumentNullException(nameof(tryFire));
            _lastNow = clock.Now;
        }

        public DirectorState State => _state;
        public bool Unlocked => _unlocked;
        public bool DirectingEnabled => _directingEnabled;
        public float OpportunityRoll => _opportunityRoll;
        public float CeilingDeadline => _ceilingDeadline;
        public float SuppressUntil => _suppressUntil;
        public float NextArmTime => _nextArmTime;
        public int CurrentPhase => _currentPhase;
        public int FiredThisSession => _firedThisSession;

        /// <summary>Enciende el director al volver del Recuerdo 1 (persiste). Arranca fresco (G17: ceiling=0).</summary>
        public void Unlock()
        {
            if (_unlocked) return;        // idempotente: una 2da llamada NO resetea el presupuesto de sesión
            _unlocked = true;
            _sessionStart = _clock.Now;   // arranca el presupuesto de sesión (NO se resetea al pausar por recuerdos)
            _firedThisSession = 0;
            ResetCycleFresh();
        }

        /// <summary>Pausa/reanuda (entrada/salida de recuerdos). Reanudar resetea a IDLE con piso minGap.</summary>
        public void SetDirectingEnabled(bool on)
        {
            _directingEnabled = on;
            if (on) ResetCycleFresh();
        }

        /// <summary>
        /// Reanuda tras la vuelta de un recuerdo con un gap CORTO (spec Franco: 20s) en vez del MinGap normal:
        /// la casa "reacciona" a lo que el jugador vio, sin 45s de aire muerto. gapSec &lt;= 0 = comportamiento normal.
        /// </summary>
        public void ResumeWithGap(float gapSec)
        {
            _directingEnabled = true;
            ResetCycleFresh();
            if (gapSec > 0f) _nextArmTime = _clock.Now + gapSec;
        }

        /// <summary>Pulso explícito de fase (escalada): lo llaman los hitos narrativos (EventsOnBackManager). Escala el gap.</summary>
        public void AdvancePhase(int phase)
        {
            if (_currentPhase == phase) return;   // idempotente
            _currentPhase = phase < 0 ? 0 : phase;
            _gapCount = 0;                         // invalida el historial de gaps: pertenece a la fase anterior
            _gapHead = 0;                          // (sus cotas eran de otra fase => el variance-guard malclasificaría)
        }

        private float PhaseGapMultiplier()
        {
            var m = _s.PhaseGapMultipliers;
            if (m == null || m.Length == 0) return 1f;
            int i = _currentPhase >= m.Length ? m.Length - 1 : (_currentPhase < 0 ? 0 : _currentPhase);
            return m[i] > 0f ? m[i] : 1f;
        }

        /// <summary>
        /// Presupuesto blando: factor que estira/encoge el gap para acercar el TOTAL de sesión al target,
        /// sin volver el timing predecible. deficit = esperado-disparados; behind (>0) => acelera (&lt;1),
        /// ahead (&lt;0) => frena (&gt;1). Acotado a [1/maxStretch, maxStretch]. Función PURA => testeable sola.
        /// </summary>
        public static float ComputeBudgetStretch(float target, float sessionLengthSec, float elapsedSec,
            int fired, float gain, float maxStretch)
        {
            if (target <= 0f || sessionLengthSec <= 0f || maxStretch < 1f) return 1f;
            float frac = elapsedSec / sessionLengthSec;
            if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
            float expected = target * frac;
            float deficit = expected - fired;                 // >0 atrasado (acelerar), <0 adelantado (frenar)
            float stretch = 1f - gain * (deficit / target);
            float lo = 1f / maxStretch;
            if (stretch < lo) stretch = lo;
            else if (stretch > maxStretch) stretch = maxStretch;
            return stretch;
        }

        private void ResetCycleFresh()
        {
            _nextArmTime = _clock.Now + _s.MinGap;
            _state = DirectorState.Idle;
            _ceilingDeadline = 0f;
            _opportunityRoll = 0f;
            _randomFireAt = -1f;
            _stillTimer = 0f;
            _zoneDwellTimer = 0f;
            _dwellUsedThisVisit = false;
            _s3WindowThisCycle = _s.S3WindowMinSec;
        }

        public void SuppressFor(float seconds)
        {
            if (seconds <= 0f) return;
            float now = _clock.Now;
            float oldUntil = _suppressUntil;
            _suppressUntil = Math.Max(_suppressUntil, now + seconds);
            // Extender el techo SOLO por la supresión NETA nueva (preserva take-max sin doble-contar).
            float added = _suppressUntil - Math.Max(oldUntil, now);
            if (_state == DirectorState.Armed && added > 0f)
            {
                _ceilingDeadline += added;
                if (_randomFireAt > 0f) _randomFireAt += added;
            }
        }

        public void SuppressIndefinitely() => _suppressedIndefinitely = true;
        public void ClearIndefiniteSuppression() => _suppressedIndefinitely = false;

        /// <summary>
        /// Swapea la fuente de azar SIN tocar el estado de sesión (unlock/fase/presupuesto/historial).
        /// Usado al restaurar un save: la partida cargada recupera SU seed (seed-por-partida, anti-clones).
        /// </summary>
        public void Reseed(IRandomSource rng)
        {
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        public void OnZoneChanged()
        {
            float grace = _s.ZoneGraceMin + (float)_rng.NextDouble() * (_s.ZoneGraceMax - _s.ZoneGraceMin);
            _nextArmTime = Math.Max(_nextArmTime, _clock.Now + grace);
            _zoneDwellTimer = 0f;
            _dwellUsedThisVisit = false;
        }

        public void Tick()
        {
            float now = _clock.Now;
            float dt = now - _lastNow;
            if (dt < 0f) dt = 0f;
            _lastNow = now;

            // Timers del jugador (reflejan realidad; se acumulan siempre).
            if (_probe.IsPlayerStill) _stillTimer += dt; else _stillTimer = 0f;
            _zoneDwellTimer += dt;

            // Compuertas.
            if (!_unlocked || !_directingEnabled) return;
            if (_probe.IsPlayerBusy) return;
            if (_suppressedIndefinitely) return;
            if (now < _suppressUntil) return;

            // Kill-switch: timer simple (comportamiento original).
            if (!_s.OpportunityMode)
            {
                if (_state == DirectorState.Idle && now >= _nextArmTime)
                {
                    bool firedT = _tryFire(FireReason.Timeout, OpportunitySignal.None);
                    ScheduleNextArm(now, firedT);
                }
                return;
            }

            // IDLE -> ARMED
            if (_state == DirectorState.Idle && now >= _nextArmTime)
            {
                _armedAt = now;
                _ceilingDeadline = now + DrawArmedCeiling();
                _opportunityRoll = 0f;
                _randomFireAt = (_rng.NextDouble() < _s.RandomFireShare)
                    ? _armedAt + (float)_rng.NextDouble() * (_ceilingDeadline - _armedAt)
                    : -1f;
                // Ventana S3 sorteada por ciclo (fija = patrón aprendible).
                _s3WindowThisCycle = _s.S3WindowMinSec
                    + (float)_rng.NextDouble() * (_s.S3WindowMaxSec - _s.S3WindowMinSec);
                _state = DirectorState.Armed;
            }

            if (_state != DirectorState.Armed) return;

            // Techo anti-sequía (incondicional, primero).
            if (now >= _ceilingDeadline)
            {
                bool firedC = _tryFire(FireReason.Timeout, OpportunitySignal.None);
                ScheduleNextArm(now, firedC);
                return;
            }

            // Random anti-condicionamiento (posicionado al armar).
            if (_randomFireAt > 0f && now >= _randomFireAt)
            {
                bool firedR = _tryFire(FireReason.Random, OpportunitySignal.None);
                _randomFireAt = -1f;
                if (firedR) ScheduleNextArm(now, true);
                return;
            }

            // Oportunidad con aceptación probabilística creciente.
            OpportunitySignal sig = EvaluateOpportunitySignals();
            if (sig != OpportunitySignal.None)
            {
                _opportunityRoll += OpportunityWeight(sig) * dt;
                if (_rng.NextDouble() < _opportunityRoll)
                {
                    bool fired = _tryFire(FireReason.Opportunity, sig);
                    if (fired)
                    {
                        _lastTriggerSignal = sig;
                        if (sig == OpportunitySignal.Dwell) _dwellUsedThisVisit = true;
                        ScheduleNextArm(now, true);
                    }
                    // pool vacío => sigue Armed, el roll persiste
                }
            }
        }

        private OpportunitySignal EvaluateOpportunitySignals()
        {
            // Activas, excluyendo la última que disparó (guard anti-consecutivo), por peso.
            OpportunitySignal best = OpportunitySignal.None;
            float bestW = 0f;

            void Consider(OpportunitySignal sig, bool active, float w)
            {
                if (active && sig != _lastTriggerSignal && w > bestW) { best = sig; bestW = w; }
            }

            Consider(OpportunitySignal.PlayerStill, _stillTimer >= _s.S1StillSec, _s.WeightStill);
            Consider(OpportunitySignal.PostInteraction, _probe.TimeSinceLastInteraction <= _s3WindowThisCycle, _s.WeightPostInteraction);
            Consider(OpportunitySignal.Dwell, _zoneDwellTimer >= _s.S2LingerSec && !_dwellUsedThisVisit, _s.WeightDwell);
            return best;
        }

        private float OpportunityWeight(OpportunitySignal sig)
        {
            switch (sig)
            {
                case OpportunitySignal.PlayerStill: return _s.WeightStill;
                case OpportunitySignal.PostInteraction: return _s.WeightPostInteraction;
                case OpportunitySignal.Dwell: return _s.WeightDwell;
                default: return 0f;
            }
        }

        private float DrawArmedCeiling()
        {
            // maxGap*ceilFactor + jitter(±CeilingJitter)
            return _s.MaxGap * _s.CeilFactor + ((float)_rng.NextDouble() * 2f - 1f) * _s.CeilingJitter;
        }

        private void ScheduleNextArm(float now, bool fired)
        {
            // Solo un disparo REAL cuenta para historial/presupuesto: un timeout con pool vacío no es un evento
            // (contarlo infla _firedThisSession y el presupuesto creería que sonaron eventos fantasma).
            if (fired)
            {
                if (_hasFired)
                {
                    _gapHistory[_gapHead] = now - _lastFireTime;
                    _gapHead = (_gapHead + 1) % 3;
                    if (_gapCount < 3) _gapCount++;
                }
                _lastFireTime = now;
                _hasFired = true;
                _firedThisSession++; // post-incremento intencional: el budget del próximo gap ya cuenta el disparo actual
            }

            float p1 = _gapCount >= 3 ? _gapHistory[0] : float.NaN;
            float p2 = _gapCount >= 3 ? _gapHistory[1] : float.NaN;
            float p3 = _gapCount >= 3 ? _gapHistory[2] : float.NaN;

            // Presupuesto blando: ajusta el gap para acercar el total de sesión al target (SRP: DrawGap=forma, esto=total).
            float budget = ComputeBudgetStretch(_s.SessionTargetEvents, _s.SessionLengthSec,
                now - _sessionStart, _firedThisSession, _s.BudgetGain, _s.BudgetMaxStretch);
            _nextArmTime = now + DrawGap(p1, p2, p3) * budget;
            _state = DirectorState.Idle;
            _lastTriggerSignal = OpportunitySignal.None; // el guard anti-consecutivo es por ventana armada
        }

        /// <summary>
        /// Sortea el próximo gap: power-law (skew derecho) + variance guard sobre los 3 gaps previos.
        /// prevN = NaN si no hay historia suficiente (no se evalúa el cluster).
        /// </summary>
        public float DrawGap(float prev1, float prev2, float prev3)
        {
            // Escalada por fase (Tanda 1): el multiplicador escala las cotas efectivas del gap.
            // Default (mult=1) => idéntico al comportamiento original.
            float mult = PhaseGapMultiplier();

            if (_s.Model == GapModel.Poisson) return DrawGapPoisson(mult);

            float effMin = _s.MinGap * mult;
            float effMax = _s.MaxGap * mult;
            float range = effMax - effMin;
            double u = _rng.NextDouble();
            float raw = effMin + range * (float)Math.Pow(u, _s.GapExponent);

            if (IsCluster(prev1, prev2, prev3, effMin, effMax, out bool low))
            {
                float frac = low
                    ? 0.6f + (float)_rng.NextDouble() * 0.4f   // clustereó bajo => tirar alto
                    : (float)_rng.NextDouble() * 0.4f;          // clustereó alto => tirar bajo
                float jitter = 0.85f + (float)_rng.NextDouble() * 0.3f;
                raw = (effMin + range * frac) * jitter;
            }

            if (raw < effMin) raw = effMin;
            if (raw > effMax) raw = effMax;
            return raw;
        }

        /// <summary>
        /// Espera exponencial (proceso de Poisson). NO se le pasa el historial a propósito: el modelo es
        /// memoryless, y mirar los gaps previos para "corregir" es exactamente lo que lo volvía estimable.
        ///
        /// -ln(u) * media da la exponencial; el piso evita que dos eventos se pisen. Sin tope superior:
        /// la cola larga es lo que produce las sequías, y de que no se vaya de mambo ya se ocupa el techo
        /// anti-sequía de <see cref="DrawArmedCeiling"/>, que dispara igual si nadie apareció.
        /// </summary>
        /// <summary>Múltiplo de la media donde se corta la cola de la exponencial (~1,8% de los sorteos).</summary>
        private const float TailCutoffFactor = 4f;

        private float DrawGapPoisson(float mult)
        {
            double u = _rng.NextDouble();
            if (u <= 0d) u = 1e-6d;                  // ln(0) = -infinito

            float media = Math.Max(1f, _s.GapMeanSec * mult);
            float piso = Math.Max(0f, _s.GapFloorSec * mult);

            // La media se mide sobre el total, así que se descuenta el piso antes de sortear.
            float mediaSobrePiso = Math.Max(1f, media - piso);
            float gap = piso + (float)(-Math.Log(u) * mediaSobrePiso);

            // Corte de cola: la exponencial no tiene tope y una tirada mala daría veinte minutos muertos.
            // A 4x la media el corte pega en ~1,8% de los sorteos, así que la forma no se distorsiona.
            float tope = media * TailCutoffFactor;
            return gap > tope ? tope : gap;
        }

        private bool IsCluster(float g1, float g2, float g3, float effMin, float effMax, out bool low)
        {
            low = false;
            if (float.IsNaN(g1) || float.IsNaN(g2) || float.IsNaN(g3)) return false;
            float eps = _s.VarianceEpsilon;
            bool clustered = Math.Abs(g1 - g2) < eps * g2 && Math.Abs(g2 - g3) < eps * g3;
            if (!clustered) return false;
            float avg = (g1 + g2 + g3) / 3f;
            low = avg < (effMin + effMax) / 2f;
            return true;
        }
    }
}
