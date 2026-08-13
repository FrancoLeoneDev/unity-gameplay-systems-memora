using System.Collections;
using UnityEngine;

/// <summary>
/// Funde a 0 (y restaura) la intensidad de un GRUPO de luces cálidas vía <c>Light.intensity</c> DIRECTO
/// (nunca SetIntensity ni SetActive — convención del proyecto en HDRP 17). El actuador de grupo no
/// existía: <c>LightFlickerEvent.FadeTo</c> es private y single-light.
///
/// Fade duration-bounded y frame-rate-independiente (copia el patrón de <c>LightFlickerEvent.FadeTo</c>).
/// <see cref="SnapToZero"/>/<see cref="SnapToOriginal"/> matan la coroutine activa ANTES de setear, para
/// que un fade en curso no sobreescriba el snap en el frame siguiente. Reusable.
/// </summary>
public class WarmLightGroupFader : MonoBehaviour
{
    [Header("Luces cálidas a fundir")]
    [SerializeField, Tooltip("Las cálidas CLASIFICADAS del recto (NO incluir la luz de ventana fría 8500K).")]
    private Light[] warmLights;

    private float[] _original;
    private Coroutine _routine;

    private void Awake() => CacheOriginals();

    private void CacheOriginals()
    {
        int n = warmLights != null ? warmLights.Length : 0;
        _original = new float[n];
        for (int i = 0; i < n; i++)
            if (warmLights[i] != null) _original[i] = warmLights[i].intensity;
    }

    /// <summary>Funde las cálidas a 0 en 'duration' segundos.</summary>
    public void FadeOut(float duration) => StartFade(true, duration);

    /// <summary>Restaura las cálidas a su intensidad autorada en 'duration' segundos.</summary>
    public void FadeIn(float duration) => StartFade(false, duration);

    private void StartFade(bool toZero, float duration)
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(FadeRoutine(toZero, duration));
    }

    private IEnumerator FadeRoutine(bool toZero, float duration)
    {
        if (warmLights == null) yield break;
        int n = warmLights.Length;

        // capturar el inicio una sola vez (no en el loop)
        var start = new float[n];
        for (int i = 0; i < n; i++) start[i] = warmLights[i] != null ? warmLights[i].intensity : 0f;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            for (int i = 0; i < n; i++)
            {
                if (warmLights[i] == null) continue;
                float target = toZero ? 0f : _original[i];
                warmLights[i].intensity = Mathf.Lerp(start[i], target, t);
            }
            yield return null;
        }

        for (int i = 0; i < n; i++) // exacto al final
            if (warmLights[i] != null) warmLights[i].intensity = toZero ? 0f : _original[i];

        _routine = null;
    }

    /// <summary>Corte instantáneo a 0 (parpadeo F5). Mata la coroutine activa primero.</summary>
    public void SnapToZero() => Snap(true);

    /// <summary>Corte instantáneo a la intensidad autorada. Mata la coroutine activa primero.</summary>
    public void SnapToOriginal() => Snap(false);

    /// <summary>Alias de <see cref="SnapToOriginal"/> para DoctorSightEvent.Restore() (leak-proof).</summary>
    public void RestoreToOriginal() => SnapToOriginal();

    private void Snap(bool toZero)
    {
        if (_routine != null) { StopCoroutine(_routine); _routine = null; }
        if (warmLights == null) return;
        for (int i = 0; i < warmLights.Length; i++)
            if (warmLights[i] != null) warmLights[i].intensity = toZero ? 0f : _original[i];
    }
}
