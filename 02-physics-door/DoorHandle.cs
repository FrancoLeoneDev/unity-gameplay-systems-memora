using DG.Tweening;
using UnityEngine;

public class DoorHandle : DraggableObjectEventsBase
{
    [SerializeField] private float rotationAngle = -40f; // Grados de rotación al arrastrar

    public override void OnStartDrag()
    {
        transform.DOLocalRotate(new Vector3(0, 0, rotationAngle), 0.5f, RotateMode.LocalAxisAdd).SetEase(Ease.OutBack);
    }

    public override void OnStopDrag()
    {
        transform.DOLocalRotate(Vector3.zero, 0.5f).SetEase(Ease.OutBack);
        
    }

    

    
}
