using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Grade frío clínico auto-contenido del pasillo durante el "Dread peak" (F4): el hospital se filtra
/// como desaturación + viraje VERDE-CIAN + contraste + viñeta perimetral (doble exposición). NO depende
/// de DreadSystem (ausente en MainHouseTesis) — crea su propio Volume + overrides en código. weight=0 en
/// reposo (costo cero).
///
/// Los targets de cada override se setean UNA sola vez (en <see cref="CreateRuntimeVolume"/>); el fade
/// lerpea SOLO <c>_volume.weight</c> — HDRP blendea todos los overrides desde su default por weight, así
/// que con una curva se mueve todo el look junto (más barato y sin curvas redundantes). El verde-cian
/// (no azul pleno) lee "institución/enfermo" y reserva el azul-frío pleno para M3 (contención).
/// </summary>
public class CorridorDesatVolume : MonoBehaviour
{
    [Header("ColorAdjustments")]
    [SerializeField, Tooltip("Saturación objetivo HDRP -100..+100. Negativo drena color. Sutil; por debajo de -65 destruye el contraste before/after.")]
    private float saturationTarget = -45f;
    [SerializeField, Range(-100f, 100f), Tooltip("Contraste objetivo. + aplana las sombras como un fluorescente clínico.")]
    private float contrastTarget = 12f;
    [SerializeField, Tooltip("Filtro de color del grade. Cian-blanco clínico (drena rojo). NO verde-saturado (eso lee 'filtro barato'). Reservar el azul pleno para M3.")]
    private Color colorFilterTarget = new Color(0.72f, 0.90f, 0.96f, 1f);
    [SerializeField, Range(-2f, 2f), Tooltip("Post-exposure del grade. + = blowout fluorescente sobreexpuesto (clave del look clínico, mata el 'verde mediocre'). 0.4-0.6.")]
    private float postExposureTarget = 0.5f;

    [Header("White balance")]
    [SerializeField, Range(-100f, 100f), Tooltip("Temperatura. Negativo = frío.")]
    private float whiteBalanceTemperature = -40f;
    [SerializeField, Range(-100f, 100f), Tooltip("Tint. Positivo = verde (clínico).")]
    private float whiteBalanceTint = 12f;

    [Header("Viñeta perimetral")]
    [SerializeField, Range(0f, 1f), Tooltip("Intensidad de la viñeta en pico (la respiración la maneja quien llama vía SetVignetteIntensity).")]
    private float vignetteIntensity = 0.40f;
    [SerializeField] private Color vignetteColor = new Color(0.04f, 0.04f, 0.07f, 1f);
    [SerializeField, Range(0.01f, 1f)] private float vignetteSmoothness = 0.55f;
    [SerializeField, Range(0f, 1f)] private float vignetteRoundness = 0.6f;

    [Header("Volume")]
    [SerializeField, Tooltip("Volume EXTERNO (lo creás y tuneás VOS en escena, con weight 0 en reposo). Si se asigna, el script SOLO le maneja el weight (0→1 en F4 → 0 en F5). Si queda null, crea uno propio con los valores de arriba.")]
    private Volume externalVolume;
    [SerializeField, Tooltip("Prioridad del Volume propio (solo si externalVolume = null).")]
    private float volumePriority = 150f;

    private Volume _volume;
    private VolumeProfile _profile;
    private Vignette _vignette;
    private Coroutine _routine;
    private bool _ownsVolume;

    private void Awake()
    {
        if (externalVolume != null)
        {
            // Franco authorea el look del Volume; el script solo le maneja el weight.
            _volume = externalVolume;
            _volume.weight = 0f;
            _ownsVolume = false;
        }
        else
        {
            CreateRuntimeVolume();
            _ownsVolume = true;
        }
    }

    private void CreateRuntimeVolume()
    {
        _profile = ScriptableObject.CreateInstance<VolumeProfile>();

        var color = _profile.Add<ColorAdjustments>();
        color.active = true;
        color.saturation.overrideState = true;  color.saturation.value = saturationTarget;
        color.contrast.overrideState = true;    color.contrast.value = contrastTarget;
        color.colorFilter.overrideState = true; color.colorFilter.value = colorFilterTarget;
        color.postExposure.overrideState = true; color.postExposure.value = postExposureTarget;

        var wb = _profile.Add<WhiteBalance>();
        wb.active = true;
        wb.temperature.overrideState = true; wb.temperature.value = whiteBalanceTemperature;
        wb.tint.overrideState = true;        wb.tint.value = whiteBalanceTint;

        _vignette = _profile.Add<Vignette>();
        _vignette.active = true;
        _vignette.intensity.overrideState = true;  _vignette.intensity.value = vignetteIntensity;
        _vignette.color.overrideState = true;      _vignette.color.value = vignetteColor;
        _vignette.smoothness.overrideState = true; _vignette.smoothness.value = vignetteSmoothness;
        _vignette.roundness.overrideState = true;  _vignette.roundness.value = vignetteRoundness;

        _volume = gameObject.AddComponent<Volume>();
        _volume.isGlobal = true;
        _volume.priority = volumePriority;
        _volume.weight = 0f;
        _volume.profile = _profile;
    }

    /// <summary>Sube el grade (weight 0→1) en 'duration' s. Los targets ya están seteados; solo lerpea weight.</summary>
    public void FadeIn(float duration)
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(FadeRoutine(duration));
    }

    private IEnumerator FadeRoutine(float duration)
    {
        if (_volume == null) yield break;

        float startW = _volume.weight;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            _volume.weight = Mathf.Lerp(startW, 1f, t);
            yield return null;
        }
        _volume.weight = 1f;
        _routine = null;
    }

    /// <summary>Modula la intensidad base de la viñeta (respiración). La fase la conoce quien llama (SRP);
    /// no toca el weight. Se aplica sobre el target seteado, escalado por el weight del Volume.</summary>
    public void SetVignetteIntensity(float intensity)
    {
        if (_vignette != null) _vignette.intensity.value = Mathf.Clamp01(intensity);
    }

    /// <summary>Corte instantáneo a normal (parpadeo F5 / Restore). Mata la coroutine activa primero.</summary>
    public void SnapOff()
    {
        if (_routine != null) { StopCoroutine(_routine); _routine = null; }
        if (_volume != null) _volume.weight = 0f;
    }

    private void OnDestroy()
    {
        if (!_ownsVolume) return; // el Volume externo es de la escena (de Franco) — no lo destruimos
        if (_volume != null) { Destroy(_volume); _volume = null; }
        if (_profile != null) { Destroy(_profile); _profile = null; }
    }
}
