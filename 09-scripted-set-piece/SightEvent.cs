using UnityEngine;

[RequireComponent(typeof(SaveableEntity))]
public abstract class SightEvent : MonoBehaviour, ISaveable
{
    protected bool triggered;

    public void Trigger()
    {
        if(triggered)
            return;
        triggered = true;

        Event();
    }

    protected abstract void Event();

    public object CaptureState()
    {
        return triggered;
    }

    public void RestoreState(object state)
    {
        triggered = SaveUtils.Deserialize<bool>(state);
        if (triggered)
        {
            Restore();
        }
    }

    protected virtual void Restore()
    {
        SaveableEntity.MarkForDestroy(gameObject);
    }
}
