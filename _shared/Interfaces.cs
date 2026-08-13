using System.Collections;
using System.Collections.Generic;
using UnityEngine;

interface IInteractable : IInteractable3DHint
{
    public void Interact();

}

public interface IInteractable3DHint
{
    Icons GetIconType();
    Vector3 GetHintPosition();
}
interface VG_IInteractable
{
    public void Interact();
    public bool invertAngle { get; }
    public bool OneInteraction { get; }
    public Vector3 GetHintPosition();
}

interface IInteractableChangeScene
{
    public void Interact(Vector3 pos, Quaternion rot);
}
interface IInteractable3D : IInteractable
{
    Transform Transform { get; }
}

interface IKeyItem
{

}

interface Map
{

}

interface IInspectable : IInteractable3DHint
{
    public float GetCustomDistance();

    public void SetOnExitExamine();

    public Transform Pivot();

    // Per-object inspection light settings (read polymorphically by InspectObject).
    public float InspectionLightIntensity { get; }

    public bool IsMetallic { get; }

}

interface IInspectionDialogue
{
    public void PlayDialogue();
}

interface IUiHint
{
    List<HintDisplayData> GetHintDisplayDataList();
}

/// <summary>
/// Hints de la barra 2D que se muestran mientras el jugador MIRA el objeto, y se sacan al dejar
/// de mirarlo. Los dispara <see cref="InteractableRay"/> vía <c>AddAdditionalHint</c>, así se
/// suman a lo que ya haya en pantalla en vez de pisarlo.
///
/// SEPARADA de <see cref="IUiHint"/> a propósito: esa describe los controles de un modo YA ABIERTO
/// (menú de llave, examinar, leer). Reusarla acá haría que mirar una puerta con llave escupiera sus
/// tres hints de menú sin haberlo abierto.
///
/// <see cref="HasHoverHints"/> se consulta por frame y tiene que ser barata; <see cref="BuildHoverHints"/>
/// solo se llama en el flanco (cuando pasa a true o cambia el objeto mirado), porque aloca.
/// </summary>
public interface IHoverUiHint
{
    /// <summary>true si AHORA el objeto ofrece hints al mirarlo. Puede cambiar con el estado
    /// (una puerta de empuje solo ofrece "cerrar" mientras está abierta).</summary>
    bool HasHoverHints { get; }

    List<HintDisplayData> BuildHoverHints();
}

/// <summary>
/// Unified interface for objects interactable during examine mode (chain examine sub-elements).
/// Detected by InspectionRaycast. Implemented by InteractableStep and InspectableInteractableObject.
/// </summary>
public interface IExamineInteractable : IInteractable3DHint
{
    void ExamineInteract();

}

