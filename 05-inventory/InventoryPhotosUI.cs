using System;
using System.Collections.Generic;
using UnityEngine;

public class InventoryPhotosUI : MenuUIBase<PhotoData>
{
    public static event Action OnInspectingPhotoUI;
    public static event Action OnExitInspectingPhotoUI;
    private GameObject itemInstance;

    protected override List<PhotoData> GetInventoryItems()
    {
        if (Inventory.instance == null) return null;
        return Inventory.instance.GetPhotosList();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        inspecting = false;
    }

    protected override void Update()
    {
        if (!myPanel.gameObject.activeSelf) return;

        base.Update();
        HandleInspection();
    }

    private void HandleInspection()
    {
        if (InputHandler.instance == null) return;

        if (InputHandler.instance.MenuConfirmPressed)
        {
            if (itemInstance == null) return;
            // Ver InventoryItemsUI: el guard va antes de tocar `inspecting` para no dejar el estado colgado.
            if (InspectObject.instance == null) return;

            inspecting = true;
            OnInspectingPhotoUI?.Invoke();
            InspectObject.instance.enabled = true;
            InspectObject.instance.ActiveExamineUI(itemInstance.transform, OnInspectionUIFinished);

            if (selectedItemData != null)
            {
                InspectObject.instance.SetPhotoContext(selectedItemData, true);
            }

            myPanel.gameObject.SetActive(false);
        }
    }

    protected override void ShowItem(PhotoData item)
    {
        bool isCompleted = Inventory.instance != null && Inventory.instance.IsPhotoCompleted(item.ID);
        GameObject prefab = item.GetActivePrefab(isCompleted);
        if (prefab == null) return;

        // FIX (Q-03): antes instanciaba con Quaternion.identity (ignoraba la rotación) → fotos torcidas.
        // Ahora usa la rotación autorada del prefab (lo orientás rotando el prefab en el editor).
        itemInstance = SpawnAndAlignPreviewItem(prefab);
        currentItem = itemInstance;
    }

    private void OnInspectionUIFinished()
    {
        myPanel.gameObject.SetActive(true);

        // Re-activate inspection light (ExitExamine turned it off during exit animation)
        InspectionLightService.SetActive(true);

        OnExitInspectingPhotoUI?.Invoke();
        inspecting = false;
    }
}
