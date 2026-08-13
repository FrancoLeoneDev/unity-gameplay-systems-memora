using System.Collections;
using UnityEngine;

/// <summary>
/// La lámpara colgante FLICKEA (titila luchando) y se APAGA. Maneja TODAS las luces de la lámpara (array)
/// + el/los material(es) EMISIVO(s) del bulbo, en sync → flickean y mueren juntos (sin calidez filtrada
/// cuando aparece el techo de hospital encima).
///
/// Uso desde el evento:
///   yield return lamp.PlayFlicker();   // titila (sigue viva)
///   yield return lamp.PlayDeath();     // se apaga (todo → 0)
///   lamp.RestoreWarmInstant();         // vuelve (F5/Restore)
///
/// HDRP: Light.intensity DIRECTO; emisiva vía renderer.material (instancia) + "_EmissiveColor"
/// (MaterialPropertyBlock NO sirve para _EmissiveColor en HDRP). Frame-rate-independiente.
/// </summary>
public sealed class PendantLampDeathSequence : MonoBehaviour
{
    private static readonly int EmissiveColorID = Shader.PropertyToID("_EmissiveColor");

    [Header("Qué controla (todas las luces + el material del bulbo, en sync)")]
    [SerializeField, Tooltip("Las 2 (o N) Light de la lámpara.")]
    private Light[] lights;
    [SerializeField, Tooltip("Renderers con material emisivo del bulbo. Su _EmissiveColor sigue la intensidad → se apaga al morir.")]
    private Renderer[] emissiveRenderers;

    [Header("Flicker (titila luchando)")]
    [SerializeField, Min(1), Tooltip("Cuántos parpadeos/dips antes de morir.")]
    private int dipCount = 6;
    [SerializeField, Range(0f, 0.6f), Tooltip("Qué tan oscuro cae cada dip (0 = casi negro).")]
    private float dipFloor = 0.08f;
    [SerializeField, Range(0.3f, 1f), Tooltip("Techo del ÚLTIMO dip: la luz PIERDE la pelea (no recupera a full). 1 = sin decay.")]
    private float monotonicFloor = 0.5f;
    [SerializeField, Min(0.02f)] private float dipDownDuration = 0.10f;
    [SerializeField, Min(0.02f)] private float dipUpDuration = 0.16f;
    [SerializeField, Min(0f)] private float dipHold = 0.04f;
    [SerializeField, Min(0f)] private float dipGap = 0.20f;
    [SerializeField, Min(0f)] private float dipGapVariance = 0.08f;

    [Header("Muerte")]
    [SerializeField, Min(0.05f), Tooltip("Fade final a apagado.")]
    private float deathFadeDuration = 0.6f;

    [Header("Audio")]
    [SerializeField, Tooltip("Sonido de flicker (3D, EN la lámpara). Suena DURANTE el flicker (fase A) y corta al morir. Loop o one-shot según el clip. Null = sin sonido.")]
    private AudioSource flickerAudioSource;

    private struct LightCache { public Light light; public float authored; }
    private struct EmissiveCache { public Material mat; public Color original; }

    private LightCache[] _lights;
    private EmissiveCache[] _emissives;
    private float _current;
    private bool _initialized;

    private void Awake()
    {
        int n = lights != null ? lights.Length : 0;
        _lights = new LightCache[n];
        for (int i = 0; i < n; i++)
            if (lights[i] != null) _lights[i] = new LightCache { light = lights[i], authored = lights[i].intensity };

        int m = emissiveRenderers != null ? emissiveRenderers.Length : 0;
        _emissives = new EmissiveCache[m];
        for (int i = 0; i < m; i++)
            if (emissiveRenderers[i] != null)
            {
                var mat = emissiveRenderers[i].material; // instancia (no toca el shared)
                _emissives[i] = new EmissiveCache { mat = mat, original = mat.HasProperty(EmissiveColorID) ? mat.GetColor(EmissiveColorID) : Color.black };
            }

        _initialized = n > 0;
        if (!_initialized) { Debug.LogError("[PendantLampDeathSequence] sin 'lights' asignadas.", this); enabled = false; }
        _current = 1f;
    }

    /// <summary>Flicker seguido de muerte (atajo).</summary>
    public IEnumerator PlayWarmDeath() { yield return PlayFlicker(); yield return PlayDeath(); }

    /// <summary>A — titila luchando (dips con decay monótono). Sigue viva al terminar.</summary>
    public IEnumerator PlayFlicker()
    {
        if (!_initialized) yield break;
        if (flickerAudioSource != null) flickerAudioSource.Play();
        _current = 1f;
        for (int i = 0; i < dipCount; i++)
        {
            float ceiling = Mathf.Lerp(1f, monotonicFloor, (float)(i + 1) / dipCount); // pierde la pelea
            yield return Ramp(Random.Range(dipFloor, dipFloor + 0.2f), dipDownDuration); // cae
            if (dipHold > 0f) yield return DeltaWait(dipHold);
            yield return Ramp(ceiling, dipUpDuration);                                   // recupera (cada vez menos)
            if (i < dipCount - 1) yield return DeltaWait(Mathf.Max(0.04f, dipGap + Random.Range(-dipGapVariance, dipGapVariance)));
        }
    }

    /// <summary>B — se apaga (todas las luces + emisiva → 0).</summary>
    public IEnumerator PlayDeath()
    {
        if (!_initialized) yield break;
        yield return Ramp(0f, deathFadeDuration);
        Apply(0f);
        if (flickerAudioSource != null) flickerAudioSource.Stop(); // el zumbido muere CON la luz
    }

    /// <summary>Vuelve al estado encendido autorado (instantáneo). F5/Restore.</summary>
    public void RestoreWarmInstant()
    {
        if (!_initialized) return;
        if (flickerAudioSource != null) flickerAudioSource.Stop();
        _current = 1f;
        Apply(1f);
    }

    // Red de seguridad: si el owner se desactiva/destruye a mitad de secuencia, cortar el zumbido de flicker.
    private void OnDisable()
    {
        if (flickerAudioSource != null) flickerAudioSource.Stop();
    }

    // ── Internos ───────────────────────────────────────────────────────────────

    private IEnumerator Ramp(float to, float duration)
    {
        float from = _current;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            _current = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            Apply(_current);
            yield return null;
        }
        _current = to;
        Apply(to);
    }

    private static IEnumerator DeltaWait(float seconds)
    {
        float r = seconds;
        while (r > 0f) { r -= Time.deltaTime; yield return null; }
    }

    private void Apply(float normalized)
    {
        float n = Mathf.Max(0f, normalized);
        for (int i = 0; i < _lights.Length; i++)
            if (_lights[i].light != null) _lights[i].light.intensity = _lights[i].authored * n;
        for (int i = 0; i < _emissives.Length; i++)
            if (_emissives[i].mat != null) _emissives[i].mat.SetColor(EmissiveColorID, _emissives[i].original * n);
    }
}
