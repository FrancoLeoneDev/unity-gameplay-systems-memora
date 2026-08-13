using UnityEngine;

/// <summary>
/// Concrete InspectableInteractableObject for objects that are inspectable
/// but have no direct interaction logic. Used as chain examine roots
/// where sub-elements (on "InspectionRaycast" layer) handle the actual interaction.
/// Always locks exit (startsChainExamine = true) — that's the whole point of a container.
/// Example: broken frame containing a photo — inspect the frame, click the photo inside.
/// </summary>
public class ExaminableContainer : InspectableInteractableObject
{
    // A container always starts chain examine — sub-elements end it.
    // No checkbox needed in Inspector.
    public override bool StartsChainExamine => true;

    public override void Interact() { }

    public override void SetOnExitExamine() { }
}
