using System;
using System.Collections.Generic;
using UnityEngine;

public class CombinableItemsManager : MonoBehaviour
{
    public static CombinableItemsManager instance { get; private set; }

    public event Action OnCombinationComplete;
    public event Action<ScriptableObject> OnNoteCombinationSuccess;
    public event Action<ScriptableObject> OnItemCombinationSuccess;

    private CombinableItem itemA;
    private CombinableItem itemB;

    private bool combining = false;
    private void Awake()
    {
        instance = this;
    }

    public void SetCombinable(CombinableItem combinableItem, Dictionary<ScriptableObject, ItemButtonController> dictionary)
    {
        if(itemA == null)
        {
            itemA = combinableItem;
            DisableNotCombinableItems(combinableItem, dictionary);
            combining = true;
        }
        else
        {
            itemB = combinableItem;
            if (TryCombine())
            {
                Combine();
                StopCombination(dictionary);
            }
            else
            {
                CantCombine(dictionary);
            }
        }
    }

    private void DisableNotCombinableItems(CombinableItem combinableItem, Dictionary<ScriptableObject, ItemButtonController> dictionary)
    {
        dictionary[combinableItem].SetInteractable(false);
        foreach (var item in dictionary.Keys)
        {
            if (item is not CombinableItem)
            {
                dictionary[item].SetInteractable(false);
                continue;
            }
        }
    }

    private bool TryCombine()
    {
        if(itemA.resultItem == null || itemB.resultItem == null)
        {
            Debug.LogError("Falta asigar un resultado a uno de los objetos combinables.");
            return false;
        }
        if(itemA.resultItem == itemB.resultItem)
        {
            return true;
        }
        return false;
    }

    private void Combine()
    {
        ScriptableObject resultItem = itemA.resultItem;

        switch (resultItem)
        {
            case UniqueKeyItemData uniqueKeyItem:
                Inventory.instance.AddUniqueKeyItem(uniqueKeyItem, success => HandleSuccess(success, resultItem));
                break;

            case NoteData note:
                Inventory.instance.AddNote(note);
                HandleSuccess(true, resultItem); // ya sabemos que siempre entra
                break;

            case PhotoData photo:
                Inventory.instance.AddPhoto(photo, success => HandleSuccess(success, resultItem));
                break;

            default:
                Debug.LogWarning("Tipo de �tem no soportado.");
                HandleSuccess(false, resultItem);
                break;
        }
    }

    private void HandleSuccess(bool success, ScriptableObject resultItem)
    {
        if (success)
        {
            Inventory.instance.DeleteKeyItem(itemA);
            Inventory.instance.DeleteKeyItem(itemB);

            OnCombinationComplete?.Invoke();

            if (resultItem is NoteData note)
            {
                OnNoteCombinationSuccess?.Invoke(note);
            }
            else
            {
                OnItemCombinationSuccess?.Invoke(resultItem);
            }

            Debug.Log("Resultado a�adido al inventario.");
        }
        else
        {
            Debug.LogWarning("No se pudo a�adir el Resultado al inventario.");
        }
    }

    public void StopCombination(Dictionary<ScriptableObject, ItemButtonController> dictionary)
    {
        Debug.Log("StopCombination()");
        itemA = null;
        itemB = null;
        combining = false;

        foreach (var item in dictionary.Keys)
        {
            dictionary[item].SetInteractable(true);
        }
    }

    private void CantCombine(Dictionary<ScriptableObject, ItemButtonController> dictionary)
    {
        dictionary[itemB].PlayErrorAnimation();
        dictionary[itemB].PlayErrorFlash();

        // Una combinación FALLIDA dejaba itemA/itemB/combining pegados y los botones sin re-habilitar:
        // el inventario quedaba en modo combinación para siempre y con slots apagados. Mismo cierre
        // que usa el camino de cancelar.
        StopCombination(dictionary);
    }
    public bool IsCombining()
    {
        return combining;
    }   
}
