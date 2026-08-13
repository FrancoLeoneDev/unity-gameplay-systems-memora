using UnityEngine;

public abstract class InteractableObject : MonoBehaviour, IInteractable
{
    [Header("Canvas 3d Settings")]
    [SerializeField] protected Transform uiHintPosition;
    [SerializeField] protected Icons iconType;

    [Header("Interaction Settings")]
    [Tooltip("Custom max interaction distance. -1 = use system default.")]
    [SerializeField] private float interactionRange = -1f;
    public float InteractionRange => interactionRange;

    /// <summary>
    /// Ajusta el alcance en runtime. Es para los eventos que necesitan que un objeto TODAVÍA no se
    /// pueda tomar: con un alcance mínimo, el raycast lo descarta por distancia antes de ofrecer el
    /// hint, el Interact o el examen. Sirve cuando la layer no se puede usar para bloquearlo porque
    /// está reservada para que la física funcione (el cuadro que cae en la pieza del niño).
    /// Ojo: un valor de 0 o negativo NO bloquea — el sistema lo lee como "usar el default".
    /// </summary>
    public void SetInteractionRange(float range) => interactionRange = range;

    public abstract void Interact();

    protected virtual void Awake() { }

    protected virtual void Start() { }

    protected virtual void Update() { }

   

    public Vector3 GetHintPosition()
    {
        return uiHintPosition != null ? uiHintPosition.position : transform.position; 
    }

    public Icons GetIconType()
    {
        return iconType;
    }
}
