using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InspectableObject : MonoBehaviour, IInspectable
{
    [Range(-0.5f, 0.5f)][SerializeField] float customDistance;
    [SerializeField] private Transform hintPosition;

    
    [SerializeField] private Transform pivot;

    [Header("Inspection Light")]
    [SerializeField, Tooltip("Inspection light intensity for THIS object. 0 = use the light's default. Raise it for objects that live in dark rooms.")]
    private float inspectionLightIntensity = 0f;

    [SerializeField, Tooltip("Metallic objects look BLACK when isolated to the inspection light layer. Tick this so the object keeps receiving scene reflection probes + lighting during inspection.")]
    private bool isMetallic = false;

    public float InspectionLightIntensity => inspectionLightIntensity;
    public bool IsMetallic => isMetallic;


    private void Awake()
    {
        gameObject.layer = LayerMask.NameToLayer("Inspectable");
    }

    public Transform Pivot()
    {
        return pivot? pivot : transform;
    }

    public float GetCustomDistance()
    {
        return customDistance;
    }

    public Vector3 GetHintPosition()
    {
        return hintPosition != null ? hintPosition.position : transform.position;
    }

    public Icons GetIconType()
    {
        return Icons.Loupe;
    }

    public void SetOnExitExamine()
    {
        return;
    }
}
