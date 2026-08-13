using System.Collections;
using UnityEngine;

/// <summary>
/// El Doctor del pasillo (F3-01) — figura texturizada en penumbra ("El Protocolo"). Set-piece de 6 fases.
/// Regla de oro: el HOSPITAL (techo/piso/paredes que aparecen) = el sustantivo, el grade = el adjetivo
/// (dread), el daze = el verbo (te agarra), y TODO converge en UN downbeat (F4).
///   F2  flicker corto (decay monótono) + las 4 cálidas mueren en paralelo → oscuridad.
///   F3  approach = duración del riser (su pico cae clavado en F4).
///   F4  EL HOSPITAL INVADE + DISOCIACIÓN, todo en el downbeat: riser CORTA + sub-bass + heartbeat
///       + daze + grade SNAP + el techo de hospital APARECE en la oscuridad + el Doctor
///       (modelo texturizado, Lit oscuro) se enciende + hum. El control se va ~90% + cabeceo pesado.
///   F5  el PARPADEO de la INTRO (Animator BlackBlinkingBackground / "blackscreenanim") enmascara el swap:
///       bajo el negro TODOS los resets; al abrir, la casa cálida vuelve LENTA sin Doctor → "¿qué fue eso?".
///   F6  rastro + aftermath.
/// </summary>
public class DoctorSightEvent : SightEvent
{
    [Header("Doctor")]
    [SerializeField, Tooltip("El modelo del Doctor (texturizado, Lit oscuro). Arranca SetActive(false); el reveal lo enciende bajo el negro.")]
    private GameObject doctorSilhouetteRoot;

    [Header("Luces cálidas")]
    [SerializeField] private WarmLightGroupFader warmFader;

    [Header("Lámpara colgante (muere cálida → re-enciende fría)")]
    [SerializeField, Tooltip("La lámpara héroe: agoniza realista en cálido, muere, y re-enciende FRÍA revelando al doctor.")]
    private PendantLampDeathSequence pendantLamp;

    [Header("Hospital filtrado (F4)")]
    [SerializeField] private CorridorDesatVolume corridorDesat;
    [SerializeField, Tooltip("Tubo fluorescente fantasma (techo). SetActive en F4, off en F5. Marcador icónico + ancla del hum. Null = skip.")]
    private GameObject fluorStripGhost;
    [SerializeField, Tooltip("Micro-movimiento sub-umbral del Doctor (presencia viva). Enable en F4 (reveal), off en F5/Restore. Null = skip.")]
    private DoctorSubtleSway doctorSway;

    [Header("Audio")]
    [SerializeField] private AudioSource heartbeatSource;
    [SerializeField, Tooltip("Clip de latido DIRECTO. One-shot @ F4, vol ~0.4.")]
    private AudioClip heartbeatClip;
    [SerializeField] private AudioSource fluorHumSource;
    [SerializeField, Tooltip("Solo ruta fail-open. Con el preview de RecallDaze activo, el monitor lo trae RecallDaze.")]
    private MonitorBeepEvent monitorBeep;
    [SerializeField, Tooltip("Opcional. Sub-bass DRONE de presión (bus HospitalPressure). Play @F4, Stop @F5.")]
    private AudioSource subBassSource;
    [SerializeField, Tooltip("Opcional. Riser/whine que construye en F3 y CORTA en F4. Null = skip.")]
    private AudioSource riserSource;
    [SerializeField] private AudioSource distantSoundSource;
    [SerializeField, Tooltip("Sonido lejano del aftermath (papel/cajón). Ej: PapelDeslizado.mp3.")]
    private AudioClip distantSoundClip;

    [Header("Disociación (RecallDaze)")]
    [SerializeField, Tooltip("Daze FUERTE, activado en F4. Asignar DoctorPreviewDaze. Fail-open: sin RecallDazeController, no pasa nada.")]
    private RecallDazeProfile doctorPreviewDaze;

    [Header("Parpadeo F5 (Animator — el MISMO que la intro)")]
    [SerializeField, Tooltip("Animator de BlackBlinkingBackground (el parpadeo de la intro). Si null, se busca por nombre.")]
    private Animator blackBlinkAnimator;
    [SerializeField, Tooltip("Segundos hasta que el parpadeo está NEGRO (= IntroPlayer: 0.525). Los resets ocurren ahí.")]
    private float blinkCloseToBlackSeconds = 0.525f;
    private const string BlinkAnimState = "blackscreenanim";

    [Header("Timing")]
    [SerializeField, Tooltip("F2: las 4 cálidas mueren EN PARALELO a la agonía de la colgante (no snap).")]
    private float warmFadeOutDuration = 1.0f;
    [SerializeField, Tooltip("Beat de OSCURIDAD entre la muerte de la cálida y el encendido del techo (el dread del negro). 0 = sin beat.")]
    private float darkBeatDuration = 0.8f;
    [SerializeField] private float dreadPeakDesatFadeIn = 0.35f;     // F4: SNAP del grade (golpe)
    [SerializeField] private float dreadPeakHoldDuration = 8f;       // F4: el hold (daze largo)
    [SerializeField] private float warmRestoreDuration = 2.5f;       // F5: vuelta LENTA de la cálida (uncanny)
    [SerializeField, Tooltip("F5: recuperación GRACEFUL del cuerpo (el visual del daze se desvanece mientras el control vuelve YA = 'nadar de vuelta'). Requiere visualDuration del SO > duración del beat.")]
    private float dazeRecoveryDuration = 3.0f;
    [SerializeField, Tooltip("F5: fade-out del AUDIO del daze (el monitor/beep). Corto → el beep NO queda sonando tras el corte del mundo (el visual sí sigue graceful).")]
    private float dazeAudioFadeOut = 0.5f;
    [SerializeField, Tooltip("F5: fade-out del audio de AMBIENTE (subBass/fluorHum) — se van con el mundo, casi instantáneo.")]
    private float environmentAudioFadeOut = 0.35f;
    [SerializeField, Tooltip("F5: fade-out del latido — se va con el cuerpo (más lento que el ambiente).")]
    private float heartbeatFadeOutDuration = 2.0f;
    [SerializeField] private float aftermathSilenceDuration = 8f;    // F6
    [SerializeField] private float distantSoundDelay = 2f;           // F6

    [Header("Volúmenes de hum")]
    [SerializeField, Range(0f, 1f), Tooltip("F4. Textura del entorno, NO protagonista (no debe tapar el heartbeat).")]
    private float fluorHumVolumeDreadPeak = 0.38f;

    private void Awake()
    {
        // R-04: el sight-collider arranca disabled (defensa doble con TriggerActivarDoctorSight).
        if (TryGetComponent<Collider>(out var col)) col.enabled = false;
        // Fallback: el Doctor arranca DESACTIVADO (por si quedó activo en el editor mientras se lo tuneaba).
        if (doctorSilhouetteRoot != null) doctorSilhouetteRoot.SetActive(false);
    }

    protected override void Event()
    {
        StartCoroutine(DoctorScareSequence());
    }

    private IEnumerator DoctorScareSequence()
    {
        // Contrato de beats (spec Franco): el director se mutea durante TODO el set-piece del Doctor.
        ZoneEventDirector.Instance?.NotifyBeatStarted();

        // ── A — FLICKER (la cálida TITILA, struggling) + el DAZE arranca ACÁ ──
        // El daze arranca ACÁ (con el flicker) — donde siempre arrancó. El blur entra gateado a un parpadeo
        // (config del SO: blurOnsetGatedToBlink), pero el DAZE como tal (clamp + cabeceo + audio) arranca acá.
        RecallDazeController.instance?.Activate(doctorPreviewDaze);
        if (pendantLamp != null) yield return pendantLamp.PlayFlicker();

        // ── B — TRANSFORMACIÓN (riser-paced): muere cálida + nace fría + grade vira + silueta ──
        // El riser sube y el GRADE vira frío AL MISMO TIEMPO que la luz cambia (lo que faltaba: sincronía).
        if (riserSource != null) riserSource.Play();
        if (warmFader != null) warmFader.FadeOut(warmFadeOutDuration);                 // las 4 cálidas mueren
        if (corridorDesat != null) corridorDesat.FadeIn(dreadPeakDesatFadeIn);         // grade vira CON la luz
        SelectiveSilenceController.Instance?.EnterSilence();
        if (subBassSource != null) subBassSource.Play();
        if (fluorHumSource != null) { fluorHumSource.volume = fluorHumVolumeDreadPeak; fluorHumSource.Play(); }

        if (pendantLamp != null) yield return pendantLamp.PlayDeath();                 // la cálida MUERE (mientras el grade vira)
        if (warmFader != null) warmFader.SnapToZero();                                 // cleanup exacto

        // BEAT DE OSCURIDAD: todo apagado un instante (el dread del negro) antes de que el hospital encienda.
        if (darkBeatDuration > 0f) yield return new WaitForSeconds(darkBeatDuration);

        // El TECHO de hospital se enciende ACÁ — en plena OSCURIDAD (recién murió la cálida, todo apagado)
        // para que NO se note el pop del mesh: aparece junto con su luz cuando está oscuro = el fluorescente igniendo.
        if (fluorStripGhost != null) fluorStripGhost.SetActive(true);

        // nace la FRÍA + revela la silueta = el downbeat (el golpe de audio cae acá)
        // El Doctor consume cupo del racionamiento de jumpscares (compartido con el director, §B.3):
        // que el director NO meta otro susto fuerte encima de ESTE. [Decisión flaggeada: solo el Doctor
        // consume cupo — el retrato de la mirilla no, para dejarle 1 PA al director en F4+.]
        ZoneEventDirector.Instance?.NotifyScriptedJumpscareConsumed();
        if (doctorSilhouetteRoot != null) doctorSilhouetteRoot.SetActive(true);
        if (doctorSway != null) doctorSway.enabled = true;                              // micro-movimiento sub-umbral (presencia viva)
        if (heartbeatSource != null)
        {
            if (heartbeatClip != null) heartbeatSource.clip = heartbeatClip;
            heartbeatSource.Play();
        }
        // La lámpara colgante NO re-enciende: la luz fría la da el TECHO de hospital (fluorStripGhost).
        // La cálida murió (PlayDeath) → breve oscuridad → el fluorescente del techo se enciende = la transformación.
        if (riserSource != null) riserSource.Stop();                                  // el riser CORTA cuando el frío+silueta aterrizan
        if (monitorBeep != null && RecallDazeController.instance == null) monitorBeep.Play();

        // ── C — sostiene: clampeado de frente, avanzás/quedás quieto hacia la silueta (con los monitores) ──
        yield return new WaitForSeconds(dreadPeakHoldDuration);

        // ── D — el parpadeo de la INTRO borra todo ──
        yield return StartCoroutine(PlayBlinkAndReset());

        // ── F6 — rastro + aftermath ──
        yield return new WaitForSeconds(distantSoundDelay);
        if (distantSoundSource != null && distantSoundClip != null)
            distantSoundSource.PlayOneShot(distantSoundClip);

        yield return new WaitForSeconds(Mathf.Max(0f, aftermathSilenceDuration - distantSoundDelay));
        // base.triggered ya quedó true (SightEvent.Trigger lo setea antes de Event). Fin de coroutine.

        // Fin del beat: libera el mute + 60s de silencio (el aftermath ya dio su propio respiro).
        ZoneEventDirector.Instance?.NotifyBeatEnded();
    }

    // ── Parpadeo F5 (mismo Animator que la intro; ver IntroPlayer.PlayBlinkSafe) ──

    private bool EnsureBlinkAnimator()
    {
        if (blackBlinkAnimator == null)
        {
            var f = GameObject.Find("BlackBlinkingBackground");
            if (f != null) blackBlinkAnimator = f.GetComponent<Animator>();
        }
        if (blackBlinkAnimator == null)
        {
            Debug.LogError("[DoctorSightEvent] BlackBlinkingBackground no encontrado — sin parpadeo, corte seco.", this);
            return false;
        }
        if (!blackBlinkAnimator.gameObject.activeInHierarchy) blackBlinkAnimator.gameObject.SetActive(true);
        if (!blackBlinkAnimator.enabled) blackBlinkAnimator.enabled = true;
        blackBlinkAnimator.Rebind();
        blackBlinkAnimator.Update(0f);
        return true;
    }

    private IEnumerator PlayBlinkAndReset()
    {
        bool ready = EnsureBlinkAnimator();
        if (ready)
        {
            blackBlinkAnimator.Play(BlinkAnimState, 0, 0f);
            blackBlinkAnimator.Update(0f);
            yield return new WaitForSeconds(blinkCloseToBlackSeconds); // esperar el NEGRO
        }

        F5Resets(); // TODOS los resets bajo el negro

        if (ready)
        {
            yield return null;
            float len = blackBlinkAnimator.GetCurrentAnimatorStateInfo(0).length;
            float rem = Mathf.Max(0f, len - blinkCloseToBlackSeconds);
            if (rem > 0f) yield return new WaitForSeconds(rem); // dejar que el parpadeo ABRA
        }
    }

    /// <summary>Bajo el negro del parpadeo: TODOS los resets en el mismo frame (fades graceful).</summary>
    private void F5Resets() => TeardownAll(instant: false);

    /// <summary>
    /// Fuente ÚNICA de teardown del evento (evita divergencia entre F5 y Restore). Desarma TODO lo que la
    /// secuencia tocó. instant=false (F5, bajo el negro) = fades graceful; instant=true (Restore, post-reload)
    /// = snaps. Si sumás un subsistema a la secuencia, agregá su teardown ACÁ (un solo lugar).
    /// </summary>
    private void TeardownAll(bool instant)
    {
        // Escena / Doctor (igual en ambos casos)
        if (doctorSilhouetteRoot != null) doctorSilhouetteRoot.SetActive(false);
        if (doctorSway != null) doctorSway.enabled = false;
        if (fluorStripGhost != null) fluorStripGhost.SetActive(false);
        if (pendantLamp != null) pendantLamp.RestoreWarmInstant();
        if (corridorDesat != null) corridorDesat.SnapOff();
        if (riserSource != null) riserSource.Stop();

        if (instant)
        {
            // Reset terminal (Restore, post-reload): snaps, sin fades.
            RecallDazeController.instance?.ForceOff();
            if (warmFader != null) warmFader.RestoreToOriginal();
            if (subBassSource != null) subBassSource.Stop();
            if (fluorHumSource != null) fluorHumSource.Stop();
            if (heartbeatSource != null) heartbeatSource.Stop();
            SelectiveSilenceController.Instance?.ForceNormal(); // incondicional: no dejar el mixer mudo tras reload
        }
        else
        {
            // Cuerpo: recuperación GRACEFUL — el control vuelve YA, el visual del daze se DESVANECE ('nadar de vuelta').
            RecallDazeController.instance?.ForceFadeOut(dazeRecoveryDuration, dazeAudioFadeOut);
            // Audio: fades en vez de cortes. Ambiente se va rápido (con el mundo); el latido lento (con el cuerpo).
            FadeOutAudio(subBassSource, environmentAudioFadeOut);
            FadeOutAudio(fluorHumSource, environmentAudioFadeOut);
            FadeOutAudio(heartbeatSource, heartbeatFadeOutDuration);
            if (warmFader != null) warmFader.FadeIn(warmRestoreDuration); // la casa vuelve cálida LENTA (uncanny)
            SelectiveSilenceController.Instance?.ExitSilence();
        }
    }

    /// <summary>Desvanece el volumen de un AudioSource a 0 y lo detiene (en vez de cortar seco). Restaura el volumen para el próximo Play.</summary>
    private void FadeOutAudio(AudioSource src, float duration)
    {
        if (src == null || !src.isPlaying) return;
        if (duration <= 0f) { src.Stop(); return; }
        StartCoroutine(FadeOutAudioRoutine(src, duration));
    }

    private IEnumerator FadeOutAudioRoutine(AudioSource src, float duration)
    {
        float startVol = src.volume;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            src.volume = Mathf.Lerp(startVol, 0f, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        src.Stop();
        src.volume = startVol; // restaurar para el próximo Play
    }

    /// <summary>
    /// Estado terminal tras save+reload con triggered=true. Null-guard total: corre en CADA load.
    /// Espejo de F5Resets vía TeardownAll (incluye ForceNormal del mixer → no queda mudo tras reload).
    /// </summary>
    protected override void Restore()
    {
        TeardownAll(instant: true);
        base.Restore(); // MarkForDestroy del TriggerVision GO
    }
}
