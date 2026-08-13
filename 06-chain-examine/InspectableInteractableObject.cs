using UnityEngine;

/// <summary>
/// Abstract base for objects that can be inspected AND interacted with during examine mode.
/// Implements IExamineInteractable so it can be detected by InspectionRaycast as a chain sub-element.
/// Set startsChainExamine in Inspector to auto-lock exit when this object begins a chain.
/// </summary>
// IKeyItem NO va acá: "es un item que te llevás" es cierto de las hojas concretas (Key, Photo,
// PapelImpresora, FlashlightPickup) y FALSO de ExaminableContainer, cuyo propósito es contener
// sub-elementos y quedarse en el mundo. Mientras el marcador vivía en esta base, InspectObject
// veía keyItem != null en un contenedor y al salir del examine lo DESTRUÍA con sus hijos adentro
// (el cuadro caído se llevaba puesta la foto de la Memoria 1, y encima guardaba). Cada subclase
// declara IKeyItem si de verdad es extraíble.
public abstract class InspectableInteractableObject : InteractableObject, IInspectable, IExamineInteractable
{
    [Range(-0.5f, 0.5f)][SerializeField] private float customDistance;
    [SerializeField] private Transform pivot;

    [Header("Chain Examine")]
    [SerializeField] private bool startsChainExamine;

    [Header("Front Face (used by PhotoMemoryPortal)")]
    [SerializeField, Tooltip("Local direction of the front face. Default (0,0,1) = Z+. Change if mesh has different orientation.")]
    private Vector3 localFaceNormal = Vector3.forward;

    [Header("Inspection Light")]
    [SerializeField, Tooltip("Inspection light intensity for THIS object. 0 = use the light's default. Raise it for objects that live in dark rooms so they read well up close.")]
    private float inspectionLightIntensity = 0f;

    [SerializeField, Tooltip("Metallic objects look BLACK when isolated to the inspection light layer (metals need environment reflections). Tick this so the object is NOT isolated during inspection: it keeps receiving scene reflection probes + lighting while the inspection light still reaches it.")]
    private bool isMetallic = false;

    public virtual bool StartsChainExamine => startsChainExamine;
    public Vector3 LocalFaceNormal => localFaceNormal;
    public float InspectionLightIntensity => inspectionLightIntensity;
    public bool IsMetallic => isMetallic;

    protected override void Awake()
    {
        base.Awake();
        transform.gameObject.layer = LayerMask.NameToLayer("Inspectable");
    }

    public Transform Pivot()
    {
        return pivot ? pivot : transform;
    }

    public float GetCustomDistance()
    {
        return customDistance;
    }

    public abstract override void Interact();

    public abstract void SetOnExitExamine();

    /// <summary>
    /// IExamineInteractable — delegates to the existing Interact() method.
    /// Used when this object is a sub-element during chain examine.
    /// </summary>
    public void ExamineInteract()
    {
        Interact();
    }

    /// <summary>
    /// Activates this object for detection by InspectionRaycast during examine mode.
    /// Sets layer to "InspectionRaycast" and enables collider.
    /// Call this when revealing a sub-element as part of a chain sequence.
    /// </summary>
    public void ActivateForExamine()
    {
        gameObject.layer = LayerMask.NameToLayer("InspectionRaycast");
        var col = GetComponent<Collider>();
        if (col != null)
            col.enabled = true;
    }
}
