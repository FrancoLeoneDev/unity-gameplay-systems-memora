using UnityEngine;

[RequireComponent(typeof(SaveableEntity))]
public class Photo : InspectableInteractableObject, ISaveable, IKeyItem
{
    [SerializeField] PhotoData photoData;
    private bool pickedUp = false;

    public override void Interact()
    {
        Inventory.instance.AddPhoto(photoData, success =>
        {
            if (success)
            {
                pickedUp = true;
                if (InspectObject.instance != null)
                {
                    // If inside a chain examine, transition inspection to this photo
                    // (reparents photo out of container, destroys container)
                    if (InspectObject.instance.InspectedObjectTransform != transform)
                    {
                        InspectObject.instance.TransitionToChainElement(transform);
                    }
                    else
                    {
                        InspectObject.instance.EndChainExamine();
                    }

                    InspectObject.instance.SetPhotoContext(photoData, false);
                }
            }
            else
            {
                Debug.LogWarning("No se pudo añadir la foto al inventario.");
            }
        });
    }
    public override void SetOnExitExamine()
    {
    }

    public object CaptureState()
    {
        return pickedUp;
    }

    public void RestoreState(object state)
    {
        pickedUp = SaveUtils.Deserialize<bool>(state);
        if (pickedUp)
        {
            SaveableEntity.MarkForDestroy(gameObject);
        }
    }
}
