using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Centralizes the HDRP rendering-layer switch that isolates an inspected object
/// onto the inspection light's light layer. The inspected object's renderers are
/// moved to <see cref="InspectionLightLayer"/> so that ONLY the inspection light
/// (configured to the same light layer) illuminates them, while every scene light
/// is excluded.
///
/// Previously this logic was duplicated verbatim in InspectObject and InspectNotes;
/// it now lives here so the layer index has a single source of truth (DRY).
/// </summary>
public static class InspectionRenderLayers
{
    /// <summary>HDRP Light Layer index the inspection light and the inspected object share.</summary>
    public const int InspectionLightLayer = 7;

    private static readonly uint LightLayerBit = 1u << InspectionLightLayer;

    /// <summary>
    /// Moves all renderers under <paramref name="obj"/> to the inspection light layer,
    /// returning a map of their original masks so they can be restored on exit.
    /// If <paramref name="metallic"/> is true the renderers are set to ALL layers instead of
    /// being isolated, so the object keeps receiving scene reflection probes (metals look black
    /// without environment reflections) while the inspection light — which is on the inspection
    /// layer, included in "all" — still reaches it.
    /// </summary>
    public static Dictionary<Renderer, uint> Apply(Transform obj, bool metallic = false)
    {
        var original = new Dictionary<Renderer, uint>();
        if (obj == null) return original;

        uint mask = metallic ? uint.MaxValue : LightLayerBit;
        foreach (var r in obj.GetComponentsInChildren<Renderer>())
        {
            original[r] = r.renderingLayerMask;
            r.renderingLayerMask = mask;
        }

        return original;
    }

    /// <summary>
    /// Moves renderers to the inspection light layer WITHOUT capturing originals.
    /// Used for transient inventory-display objects that are destroyed rather than restored.
    /// </summary>
    public static void ApplyWithoutCapture(Transform obj, bool metallic = false)
    {
        if (obj == null) return;

        uint mask = metallic ? uint.MaxValue : LightLayerBit;
        foreach (var r in obj.GetComponentsInChildren<Renderer>())
            r.renderingLayerMask = mask;
    }

    /// <summary>Restores renderers to the masks captured by <see cref="Apply"/>.</summary>
    public static void Restore(Dictionary<Renderer, uint> original)
    {
        if (original == null) return;

        foreach (var kvp in original)
        {
            if (kvp.Key != null)
                kvp.Key.renderingLayerMask = kvp.Value;
        }
    }
}
