using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Single entry point for the ONE shared inspection light ("luz inspeccion").
///
/// The light GameObject is registered once at startup (by InspectObject.Awake). After that,
/// ANY system — object inspection, note inspection, inventory display, door/puzzle close-ups —
/// turns the light on/off, sets its intensity and isolates the inspected target THROUGH HERE,
/// instead of holding its own reference to the light and calling SetActive directly.
///
/// It is a static class (not a MonoBehaviour) on purpose: the light GameObject starts INACTIVE,
/// so a singleton component living on it could never run its own Awake to register itself.
/// Intensity is delegated to the <see cref="InspectionLight"/> component on the light GameObject
/// (it applies the value on enable and restores the authored default on disable).
/// </summary>
public static class InspectionLightService
{
    private static GameObject _lightGO;
    private static InspectionLight _light;
    private static Dictionary<Renderer, uint> _restore;

    /// <summary>True once the shared light has been registered.</summary>
    public static bool Ready => _lightGO != null;

    /// <summary>Wire the service to the shared inspection light. Call once at startup.</summary>
    public static void Register(GameObject lightGO)
    {
        _lightGO = lightGO;
        _light = lightGO != null ? lightGO.GetComponent<InspectionLight>() : null;
    }

    /// <summary>
    /// Isolate <paramref name="target"/> to the inspection light layer (so ONLY this light hits it),
    /// set the intensity (0 = the light's authored default), and turn the light on. Captures the
    /// target's original rendering-layer masks so <see cref="Deactivate"/> can restore them.
    /// Use for inspected objects/notes that are returned to the world on exit.
    /// </summary>
    public static void Activate(Transform target, float intensity = 0f, bool metallic = false)
    {
        if (_lightGO == null)
        {
            Debug.LogError("[InspectionLightService] Activate sin luz registrada (_lightGO null). InspectObject.Awake debe registrarla.");
            return;
        }
        if (_light != null)
        {
            _light.SetNextIntensity(intensity);
            _light.SetNextMode(InspectionLight.Mode.Isolation);
        }
        _restore = InspectionRenderLayers.Apply(target, metallic);
        _lightGO.SetActive(true);
    }

    /// <summary>Restore the renderers captured by <see cref="Activate"/> and turn the light off.</summary>
    public static void Deactivate()
    {
        InspectionRenderLayers.Restore(_restore);
        _restore = null;
        if (_lightGO != null) _lightGO.SetActive(false);
    }

    /// <summary>
    /// Isolate a target WITHOUT capturing a restore map — for transient objects that are
    /// destroyed rather than returned to the world (inventory display, chain-examine sub-elements).
    /// Ensures the light is on at the given intensity.
    /// </summary>
    public static void ActivateTransient(Transform target, float intensity = 0f, bool metallic = false)
    {
        if (_lightGO == null) return;
        InspectionRenderLayers.ApplyWithoutCapture(target, metallic);
        if (!_lightGO.activeSelf)
        {
            if (_light != null) _light.SetNextIntensity(intensity);  // OnEnable applies it
            _lightGO.SetActive(true);
        }
        else if (_light != null)
        {
            _light.ApplyIntensityNow(intensity);  // light already on (switching targets) -> apply now
        }
    }

    /// <summary>
    /// Re-isolate a NEW target while the light is already on (chain-examine: a sub-element
    /// "becomes" the inspected object). Replaces the captured restore map with the new target's,
    /// so <see cref="Deactivate"/> restores the right renderers. Does not toggle the light.
    /// </summary>
    public static void Reisolate(Transform target, bool metallic = false)
    {
        _restore = InspectionRenderLayers.Apply(target, metallic);
    }

    /// <summary>
    /// Turn the light on/off WITHOUT isolating anything — for contextual close-ups (doors, puzzles)
    /// where the target is a large in-scene object that should keep its own scene lighting.
    /// </summary>
    public static void SetActive(bool active, float intensity = 0f)
    {
        if (_lightGO == null) return;
        if (active && _light != null)
        {
            _light.SetNextIntensity(intensity);
            _light.SetNextMode(InspectionLight.Mode.SceneObject);
        }
        _lightGO.SetActive(active);
    }
}
