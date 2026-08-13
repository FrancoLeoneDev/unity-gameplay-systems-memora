namespace Memora.DirectorCore
{
    public enum GateState { Locked, Released, Armed }

    /// <summary>
    /// Máquina de estados PURA del gating de objetos scripteados (Tanda 2, §7-§A.2). Locked→Released→Armed.
    /// - Locked: el director NUNCA lo toca (antes de su beat scripteado).
    /// - Released: su beat ya pasó (NotifyBeatFired / NotifyUnlocked).
    /// - Armed: elegible — el director recién lo toca cuando el jugador ENTRA a la zona de armado tras liberarse
    ///   (diegético "pasó mientras no estabas", NO un timer aprendible).
    /// Sin UnityEngine ni DirectorZone: el MonoBehaviour LiberationGate computa el bool "entrando a la zona de
    /// armado" y lo pasa acá ⇒ testeable en EditMode sin escena.
    /// Simplificación sobre el draft §7-§A.2: "arm-on-enter" NO necesita `_hasLeftSinceRelease` (un Enter solo
    /// puede ocurrir tras un Exit, así que un Enter post-liberación ya es un "volviste"). Elimina el riesgo R1.
    /// </summary>
    public sealed class LiberationGateMachine
    {
        private readonly bool _requireReentry;
        private GateState _state = GateState.Locked;

        public LiberationGateMachine(bool requireReentryAfterRelease) => _requireReentry = requireReentryAfterRelease;

        public GateState State => _state;
        public bool IsArmed => _state == GateState.Armed;

        /// <summary>El beat scripteado liberó el objeto. Locked→Released (o →Armed si no requiere reentry).</summary>
        public void Release()
        {
            if (_state != GateState.Locked) return;
            _state = _requireReentry ? GateState.Released : GateState.Armed;
        }

        /// <summary>Transición de zona del jugador. Released + ENTRAR a la zona de armado ⇒ Armed.</summary>
        public void OnZoneTransition(bool enteringArmingZone)
        {
            // Armed es TERMINAL: una vez armado no vuelve atrás (re-lockeo necesitaría una transición nueva explícita).
            if (_state == GateState.Released && enteringArmingZone) _state = GateState.Armed;
        }

        /// <summary>
        /// Restaura el estado persistido (save/load). VENTANA CRÍTICA: si se guarda en `Released` y se restaura
        /// sin esto, la máquina nace en `Locked` y el beat one-shot no se repite ⇒ gate atascado (ver F-02 / L-01).
        /// </summary>
        public void Restore(GateState state) => _state = state;
    }
}
