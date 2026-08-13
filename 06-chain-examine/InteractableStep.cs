using UnityEngine;

/// <summary>
/// Base class for sequential interaction steps within an inspected object.
/// Steps form a linked list via nextStep. When interacted, each step executes
/// its logic (OnStepInteracted) then auto-advances to the next step.
/// Layer is auto-set to "InspectionRaycast" when activated.
/// </summary>
public abstract class InteractableStep : MonoBehaviour, IExamineInteractable
{
    [SerializeField] private Transform uiHintPosition;
    [SerializeField] private InteractableStep nextStep;
    [SerializeField] private bool startsActive;

    private BoxCollider boxCollider;

    protected virtual void Awake()
    {
        boxCollider = GetComponent<BoxCollider>();
        if (!startsActive)
        {
            boxCollider.enabled = false;
        }
        else
        {
            // Guarantee the first step is on "InspectionRaycast" at runtime (not just by prefab
            // authoring): SetLayerRecursively skips that layer when the root is moved to
            // "InspectedObject" at inspection start, so the first step stays detectable. Without
            // this, a mis-authored first step would be swept onto InspectedObject and become unclickable.
            int raycastLayer = LayerMask.NameToLayer("InspectionRaycast");
            if (raycastLayer >= 0)
                gameObject.layer = raycastLayer;
        }
    }

    /// <summary>
    /// Called by InspectionRaycast via IExamineInteractable.
    /// Executes step-specific logic then advances the chain.
    /// </summary>
    public void ExamineInteract()
    {
        OnStepInteracted();
        AdvanceChain();
    }

    /// <summary>
    /// Subclasses implement their specific step logic here.
    /// Examples: destroy ribbon, open lid with DOTween, play animation.
    /// </summary>
    protected abstract void OnStepInteracted();

    private void AdvanceChain()
    {
        boxCollider.enabled = false;
        if (nextStep != null)
        {
            nextStep.ActivateAsExamineTarget();
        }
    }

    /// <summary>
    /// Enables this step's collider and auto-sets layer to "InspectionRaycast"
    /// so InspectionRaycast can detect it during examine mode.
    /// </summary>
    public void ActivateAsExamineTarget()
    {
        boxCollider.enabled = true;
        gameObject.layer = LayerMask.NameToLayer("InspectionRaycast");
    }

    public Icons GetIconType()
    {
        return Icons.DraggableHand;
    }

    public Vector3 GetHintPosition()
    {
        return uiHintPosition.position;
    }
}
