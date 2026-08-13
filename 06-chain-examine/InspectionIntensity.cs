using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Optional per-object override for the shared inspection light's intensity, by COMPOSITION:
/// drop this on any in-scene close-up object (door, painting/Dibujo, note, music box, puzzle) that
/// needs a custom brightness -- e.g. stronger in a dark room. Objects WITHOUT this component fall back
/// to the light's shared scene-object default (InspectionLight.sceneObjectIntensity).
///
/// Value is in EV100 (the same unit as the light's inspector). The choke points that drive the
/// inspection light (MenuInteractionCamera, DocumentReader, MusicBoxEvent) read it through
/// <see cref="Resolve"/>, so NO gameplay class needs to declare its own intensity field.
///
/// Sentinel matches the IInspectable path on purpose: 0 = use the light's default (same meaning as
/// InspectableObject.inspectionLightIntensity). Anything &gt; 0 is this object's EV100 override.
///
/// NOTE: only for the scene-object light path (camera close-ups). Objects you inspect-and-rotate
/// (IInspectable) keep their own per-object intensity on the InspectableObject base.
/// </summary>
[AddComponentMenu("Memora/Inspection Intensity")]
public class InspectionIntensity : MonoBehaviour
{
    [Tooltip("Intensidad EN EV100 para el close-up de inspeccion de ESTE objeto. 0 = usa la sceneObjectIntensity compartida de la luz.")]
    [FormerlySerializedAs("ev")]
    [SerializeField] private float inspectionLightIntensity = 0f;

    public float InspectionLightIntensity => inspectionLightIntensity;

    /// <summary>The owner's override EV, or <paramref name="fallback"/> if it has no InspectionIntensity attached.</summary>
    public static float Resolve(Component owner, float fallback = 0f)
    {
        return owner != null && owner.TryGetComponent<InspectionIntensity>(out var i) ? i.inspectionLightIntensity : fallback;
    }
}
