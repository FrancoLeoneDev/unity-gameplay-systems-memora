using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using HDRenderingLayerMask = UnityEngine.Rendering.HighDefinition.RenderingLayerMask;

/// <summary>
/// Sits on the inspection light ("luz inspeccion"). Two knobs, both applied when the light is
/// enabled and reverted when it is disabled, so the light is ALWAYS left in its isolation default:
///
/// 1. INTENSITY — each inspected target can request its own intensity (brighter for things in dark
///    rooms) while sharing the one light.
/// 2. LIGHT LAYER MODE —
///    � <see cref="Mode.Isolation"/> (default): the light keeps its authored Light Layer (LightLayer7).
///      The inspected object's renderers are moved onto that layer elsewhere, so ONLY this light hits
///      them � the room-independent "studio" look for objects you rotate.
///    � <see cref="Mode.SceneObject"/>: the light is opened to EVERY layer so it reaches in-situ
///      close-ups (doors / puzzles / notes) that stay in the scene on their room's own light layer.
///
/// Call <see cref="SetNextIntensity"/> / <see cref="SetNextMode"/> BEFORE enabling the light GameObject.
/// On disable both the authored intensity AND the isolation Light Layer are restored � so an
/// interrupted close-up can never leave the light "open" and break the next object's isolation.
/// </summary>
[RequireComponent(typeof(HDAdditionalLightData))]
public class InspectionLight : MonoBehaviour
{
    public enum Mode { Isolation, SceneObject }

    [Tooltip("Intensidad EN EV100 (la misma unidad que ves en el inspector de la luz) para close-ups " +
             "in-situ (puertas/puzzles/notas, modo SceneObject). <= 0 usa la intensidad autorada de la luz. " +
             "Independiente de la inspeccion de objetos, que sigue usando la autorada.")]
    [SerializeField] private float sceneObjectIntensity = -1f;

    private HDAdditionalLightData hdLight;
    private Light unityLight;

    private float authoredIntensity;
    private HDRenderingLayerMask isolationLayersHD;   // authored mask = LightLayer7
    private int isolationLayersLight;                 // Light.renderingLayerMask mirror of the above

    private float nextIntensity = -1f;
    private Mode nextMode = Mode.Isolation;

    private void Awake()
    {
        hdLight = GetComponent<HDAdditionalLightData>();
        unityLight = GetComponent<Light>();
        authoredIntensity = hdLight.intensity;
        isolationLayersHD = hdLight.lightlayersMask;
        if (unityLight != null) isolationLayersLight = unityLight.renderingLayerMask;
    }

    /// <summary>Intensity for the NEXT activation. Call BEFORE enabling. &lt;= 0 keeps the authored default.</summary>
    public void SetNextIntensity(float intensity) => nextIntensity = intensity;

    /// <summary>Light-layer mode for the NEXT activation. Call BEFORE enabling. Resets to Isolation after.</summary>
    public void SetNextMode(Mode mode) => nextMode = mode;

    /// <summary>
    /// Apply an intensity RIGHT NOW instead of queuing it for the next OnEnable. Needed when the light
    /// is ALREADY on and the inspected target switches (e.g. browsing inventory items) -- OnEnable won't
    /// re-fire, so the per-object intensity has to be pushed immediately. Same resolution as OnEnable:
    /// &gt; 0 is the per-object EV value; otherwise the authored photometric value is restored.
    /// </summary>
    public void ApplyIntensityNow(float intensity)
    {
        if (hdLight == null) return;
        nextIntensity = intensity;
        if (intensity > 0f)
            hdLight.SetIntensity(intensity, hdLight.lightUnit);
        else
            hdLight.intensity = authoredIntensity;
    }

    private void OnEnable()
    {
        if (hdLight == null) return;

        // Priority: explicit per-call intensity  >  scene-object default (only in SceneObject mode)  >  authored.
        // User-facing values (per-object override / scene default) are authored in the light's UNIT
        // (this light is EV100), so they MUST be applied via SetIntensity(value, unit). The linear
        // .intensity setter would treat them as raw lumens -- that bug showed as "set 11 -> reads 6.4 EV".
        // The authored fallback restores the exact captured photometric value via .intensity: a perfect
        // round-trip that needs no unit conversion.
        float requested = nextIntensity > 0f
            ? nextIntensity
            : (nextMode == Mode.SceneObject && sceneObjectIntensity > 0f ? sceneObjectIntensity : -1f);

        if (requested > 0f)
            hdLight.SetIntensity(requested, hdLight.lightUnit);
        else
            hdLight.intensity = authoredIntensity;

        if (nextMode == Mode.SceneObject)
            ApplyLayers((HDRenderingLayerMask)(-1), -1);          // every layer: reach in-scene objects
        else
            ApplyLayers(isolationLayersHD, isolationLayersLight); // authored LightLayer7: isolate

        nextIntensity = -1f;
        nextMode = Mode.Isolation;
    }

    private void OnDisable()
    {
        if (hdLight == null) return;
        hdLight.intensity = authoredIntensity;
        ApplyLayers(isolationLayersHD, isolationLayersLight);     // leak-proof: always back to isolation
    }

    // HDRP reads HDAdditionalLightData.lightlayersMask; the base Light.renderingLayerMask is kept in
    // sync because the two are not guaranteed to mirror each other across HDRP 17.x patches.
    private void ApplyLayers(HDRenderingLayerMask hdMask, int lightMask)
    {
        hdLight.lightlayersMask = hdMask;
        if (unityLight != null) unityLight.renderingLayerMask = lightMask;
    }
}
