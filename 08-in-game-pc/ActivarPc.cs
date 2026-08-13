using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ActivarPc : InteractableObject
{
    Pc pc;
    [Header("Zone")]
    [SerializeField] ZoneID zone = ZoneID.LeftWing;

    protected override void Awake()
    {
        pc = GetComponent<Pc>();
    }

    public override void Interact()
    {
        // Null-guard del singleton, como hace Pc.TrySubscribeFuse con el mismo manager: sin FuseManager
        // no se puede saber si la zona tiene luz, así que se trata como "sin energía" en vez de tirar NRE.
        if (FuseManager.instance != null && FuseManager.instance.IsZonePowered(zone))
        {
            GameManager.instance.SetInteracting(true);
            pc.enabled = true;
        }
    }
}
