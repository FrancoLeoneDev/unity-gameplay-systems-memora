// Memory ARRIVAL beat — the memory is BORN warm amber and settles to neutral (inverse of the photo-hold drain).
// Triggered by SceneControllerManager after the memory scene loads.
// Design: plans/diseno-transicion-foto-puerta-blanca.md §B.1 — "Llegada cálida ámbar".
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public class MemoryArrivalGrade : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------
    public static MemoryArrivalGrade instance { get; private set; }

    // -------------------------------------------------------------------------
    // Tunable knobs (designer-facing)
    // -------------------------------------------------------------------------
    [Header("Arrival Grade — Color")]
    // Saturation override on the HDRP -100..+100 scale.
    // 0 = identity (no saturation change). The world arrives grey because
    // MemoryTransitionPass held satFactor=0.0 through the void; this component
    // must NOT re-add color. Keep at 0 so the memory scene's own Volumes control
    // its look without interference from this arrival beat.
    [SerializeField] private float arrivalSaturation = 0f;
    // Color filter at arrival. Identity (white) = no tint injected by this beat.
    // The warm amber value (1, 0.80, 0.55) was the re-saturation path that caused
    // color to flood back on arrival — removed per design requirement (stay grey).
    [SerializeField] private Color arrivalColorFilter = Color.white;
    [SerializeField] private float arrivalPostExposure = 0.6f;      // slightly over-bright; settles as color floods in

    [Header("Arrival Grade — Timing")]
    [SerializeField] private float defaultDuration = 0.85f;

    // -------------------------------------------------------------------------
    // Runtime HDRP Volume (created in Awake, destroyed in OnDestroy)
    // -------------------------------------------------------------------------
    private Volume runtimeVolume;
    private VolumeProfile runtimeProfile;
    private ColorAdjustments colorAdjustments;

    private Coroutine arrivalCoroutine;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        CreateRuntimeVolume();
    }

    private void OnDisable()
    {
        StopArrivalCoroutine();
        SetVolumeWeight(0f);
    }

    private void OnDestroy()
    {
        StopArrivalCoroutine();

        if (instance == this)
            instance = null;

        DestroyRuntimeVolume();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Snaps the grade to full-grey/over-bright and resolves to neutral over defaultDuration seconds.
    /// </summary>
    public void PlayArrival()
    {
        PlayArrival(defaultDuration);
    }

    /// <summary>
    /// Snaps the grade to full-grey/over-bright and resolves to neutral over the supplied duration.
    /// Call this immediately before the black curtain lifts; the snap happens under black, no pop.
    /// </summary>
    public void PlayArrival(float duration)
    {
        if (runtimeVolume == null) return;

        // MEMORY IS IN COLOR (user ruling 2026-06-07): the black & white is ONLY the
        // TRANSITION background while holding Space — NOT the memory. This arrival grade
        // therefore stays OFF so the memory shows its own natural colour. (Reverts the
        // earlier "stay grey forever" hold, which was a misread of the requirement.)
        // The transition background B&W lives entirely in MemoryTransitionPass and is
        // turned off under the black cut, so nothing grey leaks into the colour memory.
        StopArrivalCoroutine();
        colorAdjustments.saturation.value   = 0f;           // identity — no desaturation
        colorAdjustments.postExposure.value = 0f;
        colorAdjustments.colorFilter.value  = Color.white;
        SetVolumeWeight(0f);                                // volume OFF → memory natural colour
    }

    /// <summary>
    /// Immediately snaps the volume off. Safe to call from any context.
    /// </summary>
    public void ForceOff()
    {
        StopArrivalCoroutine();
        SetVolumeWeight(0f);

        if (colorAdjustments != null)
        {
            colorAdjustments.saturation.value   = 0f;
            colorAdjustments.postExposure.value = 0f;
            colorAdjustments.colorFilter.value  = Color.white;
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private IEnumerator ResolveToNeutral(float duration)
    {
        if (duration <= 0f)
        {
            ForceOff();
            yield break;
        }

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float rawT = Mathf.Clamp01(elapsed / duration);

            // Ease-out quad: fast initial settle from warm amber, gentle finish at neutral.
            float t = 1f - Mathf.Pow(1f - rawT, 2f);

            colorAdjustments.saturation.value   = Mathf.Lerp(arrivalSaturation, 0f, t);
            colorAdjustments.postExposure.value  = Mathf.Lerp(arrivalPostExposure, 0f, t);
            colorAdjustments.colorFilter.value   = Color.Lerp(arrivalColorFilter, Color.white, t);

            // Weight stays 1 while lerping values; drop to 0 only when fully resolved
            // so the override doesn't suddenly vanish mid-lerp.
            yield return null;
        }

        // Fully resolved — disable the volume so it has zero cost at rest.
        colorAdjustments.saturation.value   = 0f;
        colorAdjustments.postExposure.value = 0f;
        colorAdjustments.colorFilter.value  = Color.white;
        SetVolumeWeight(0f);

        arrivalCoroutine = null;
    }

    private void StopArrivalCoroutine()
    {
        if (arrivalCoroutine != null)
        {
            StopCoroutine(arrivalCoroutine);
            arrivalCoroutine = null;
        }
    }

    private void SetVolumeWeight(float weight)
    {
        if (runtimeVolume != null)
            runtimeVolume.weight = weight;
    }

    private void CreateRuntimeVolume()
    {
        runtimeProfile = ScriptableObject.CreateInstance<VolumeProfile>();

        colorAdjustments = runtimeProfile.Add<ColorAdjustments>();
        colorAdjustments.active = true;
        colorAdjustments.saturation.overrideState = true;
        colorAdjustments.saturation.value = 0f;
        colorAdjustments.postExposure.overrideState = true;
        colorAdjustments.postExposure.value = 0f;
        colorAdjustments.colorFilter.overrideState = true;
        colorAdjustments.colorFilter.value = Color.white;

        runtimeVolume = gameObject.AddComponent<Volume>();
        runtimeVolume.isGlobal = true;
        runtimeVolume.priority = 200f;
        runtimeVolume.weight = 0f;
        runtimeVolume.profile = runtimeProfile;
    }

    private void DestroyRuntimeVolume()
    {
        if (runtimeVolume != null)
        {
            Destroy(runtimeVolume);
            runtimeVolume = null;
        }
        if (runtimeProfile != null)
        {
            Destroy(runtimeProfile);
            runtimeProfile = null;
        }
    }
}
