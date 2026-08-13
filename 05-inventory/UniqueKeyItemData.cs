using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;

[CreateAssetMenu(fileName = "NewUniqueItem", menuName = "Inventory/Unique Item")]
public class UniqueKeyItemData : ScriptableObject, IDisplayItem, IIdentificable
{
    [SerializeField] LocalizedString itemName; // Nombre del ítem
    [SerializeField] LocalizedString description; // Descripción para el inventario
    [SerializeField] Sprite iconSprite; // Sprite del ítem (opcional, si se usa en el inventario)
    [SerializeField] GameObject prefab; // Prefab asociado al ítem
    [SerializeField] bool limitedUses = true; // Indica si el ítem tiene usos limitados
    [SerializeField] int remainingUses = 1; // Usos restantes (por defecto 1)
    [SerializeField] Vector3 customDistance;

    [SerializeField, HideInInspector] private string id;
    public string ID => id;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(id))
        {
            id = System.Guid.NewGuid().ToString();
            UnityEditor.EditorUtility.SetDirty(this); // Marca como modificado para guardar
        }
    }
#endif

    public Vector3 GetCustomDistance()
    {
        return customDistance;
    }

    public Sprite GetIconSprite()
    {
        return iconSprite;
    }

    public LocalizedString GetDescription()
    {
        return description;
    }

    public LocalizedString GetName()
    {
        return itemName;
    }

    public GameObject GetPrefab()
    {
        return prefab;
    }

    public int GetRemainingUses()
    {
        return remainingUses;
    }

    public bool IsLimitedUses()
    {
        return limitedUses;
    }

   
}