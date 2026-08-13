namespace Memora.DirectorCore
{
    /// <summary>
    /// Racionamiento de jumpscares del director (pilar #9, §7-§B.3). Decisión PURA (sin Unity): dado el
    /// estado de sesión + config, ¿puede el director disparar un evento de tier agresivo (jumpscare sensorial)?
    /// El ESTADO (cuántos PA van, cuándo fue el último) lo lleva el caller (ZoneEventDirector); esto solo decide.
    /// Comparten el cupo con los jumpscares scripteados vía NotifyScriptedJumpscareConsumed (incrementa paFired).
    /// </summary>
    public static class JumpscareRationing
    {
        /// <param name="now">tiempo actual</param>
        /// <param name="sessionStart">inicio de sesión (Unlock)</param>
        /// <param name="unlockAfterSec">no antes de este lapso desde el inicio (~min 10)</param>
        /// <param name="paFiredThisSession">PA ya disparados (director + scripteados)</param>
        /// <param name="maxPAPerSession">cupo total por corrida (1-2)</param>
        /// <param name="currentPhase">fase narrativa actual</param>
        /// <param name="minPhase">recién elegible desde esta fase (F4+)</param>
        /// <param name="suppressUntil">ventana de beat protegida (nunca pisar)</param>
        /// <param name="lastPAFireTime">hora del último PA (muy negativo si nunca)</param>
        /// <param name="postPACooldownSec">respiro mínimo tras el PA anterior</param>
        public static bool IsAllowed(
            float now, float sessionStart, float unlockAfterSec,
            int paFiredThisSession, int maxPAPerSession,
            int currentPhase, int minPhase,
            float suppressUntil, float lastPAFireTime, float postPACooldownSec)
        {
            if (now - sessionStart < unlockAfterSec) return false;        // (a) no antes del desbloqueo
            if (paFiredThisSession >= maxPAPerSession) return false;       // (b) cupo de sesión agotado
            if (now < suppressUntil) return false;                        // (c) nunca dentro de un beat
            if (currentPhase < minPhase) return false;                    // (d) recién desde cierta fase
            if (now - lastPAFireTime < postPACooldownSec) return false;   // (e) respiro tras el PA previo
            return true;
        }
    }
}
