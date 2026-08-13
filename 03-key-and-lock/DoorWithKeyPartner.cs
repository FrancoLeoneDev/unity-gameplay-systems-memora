using UnityEngine;

public class DoorWithKeyPartner : InteractableObject
{
    [SerializeField] private DoorWithKey doorWithKey;
    private PhysicDoor draggableDoor;

    protected override void Awake()
    {
        draggableDoor = GetComponent<PhysicDoor>();
        if(doorWithKey != null)
        {
            doorWithKey.SetSecondDoor(draggableDoor, this);
        }
    }
    public override void Interact()
    {
        if(doorWithKey != null)
        {
            doorWithKey.Interact();
        }
    }
}
