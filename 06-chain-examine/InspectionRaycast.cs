using UnityEngine;

/// <summary>
/// Secondary raycast active during examine mode.
/// Detects IExamineInteractable components on the "InspectionRaycast" layer
/// to enable interaction with sub-elements of inspected objects (chain examine).
///
/// Front-face detection is collider-based: the parent object's body is kept as a trigger
/// during inspection, so it blocks the ray from behind. Under the single-camera path the body
/// is on "InspectedObject" ("RenderAbove" kept for any legacy inspected objects).
/// If the ray hits InspectionRaycast layer → interact. If it hits the body layer → blocked.
/// </summary>
public class InspectionRaycast : MonoBehaviour
{
    [SerializeField] private float rayLength = 2f;
    [SerializeField] private Transform inspectionCamera;

    private int inspectionLayer;
    private int combinedMask;

    private void Awake()
    {
        inspectionLayer = LayerMask.NameToLayer("InspectionRaycast");

        // The inspected body must be in the mask so it blocks the ray from behind. Guard each layer:
        // a missing layer → NameToLayer returns -1 → `1 << -1` would corrupt the mask (shifts to bit 31).
        combinedMask = LayerToBit(inspectionLayer)
                     | LayerToBit(LayerMask.NameToLayer("RenderAbove"))
                     | LayerToBit(LayerMask.NameToLayer("InspectedObject"));
    }

    private static int LayerToBit(int layer) => layer >= 0 ? (1 << layer) : 0;

    private void Start()
    {
        enabled = false;
    }

    private void Update()
    {
        // Antes este Update SÓLO sabía mostrar el ícono: no lo escondía nunca — ni cuando el rayo
        // fallaba, ni cuando el cuerpo del objeto lo bloqueaba desde atrás, ni al apagarse el raycast.
        // El hint quedaba flotando sobre un sub-elemento que ya no estabas mirando.
        Ray ray = new Ray(inspectionCamera.position, inspectionCamera.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, rayLength, combinedMask, QueryTriggerInteraction.Collide))
        {
            OcultarIcono();
            return;
        }

        // Parent body (on the inspected-object layer) blocks the ray from behind
        if (hit.collider.gameObject.layer != inspectionLayer)
        {
            OcultarIcono();
            return;
        }

        var element = hit.collider.GetComponentInParent<IExamineInteractable>();
        if (element == null)
        {
            OcultarIcono();
            return;
        }

        // Null-check del singleton: en escenas sin el canvas de íconos esto tiraba NullReference por frame.
        if (InteractionsIconsCanvas3D.instance != null)
            InteractionsIconsCanvas3D.instance.ShowIcon(element.GetHintPosition(), element.GetIconType(), true);

        if (InputHandler.instance != null && InputHandler.instance.PrimaryInteractPressed)
            element.ExamineInteract();
    }

    /// <summary>Suelta el hint del chain examine. También al apagarse el raycast (salida del examine).</summary>
    private void OnDisable() => OcultarIcono();

    private void OcultarIcono()
    {
        if (InteractionsIconsCanvas3D.instance != null)
            InteractionsIconsCanvas3D.instance.HideIcon();
    }
}
