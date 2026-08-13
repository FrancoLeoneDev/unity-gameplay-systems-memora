using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

public class InventoryItemsUI : MenuUIBase<ScriptableObject>
{
    [Header("Grilla")]
    [Tooltip("Columnas de la grilla del selector de objetos. Debe coincidir con el GridLayoutGroup del panel.")]
    [SerializeField] private int columns = 3;
    [Tooltip("Mínimo de slots SIEMPRE visibles (3×3 = 9). Los que sobran sobre los items reales se dibujan vacíos.")]
    [SerializeField] private int minSlots = 9;

    public static event Action OnInspectingItemUI;
    public static event Action OnExitInspectingItemUI;
    private GameObject itemInstance;
    private HintInstance combinarHint;

    [Header("Panel nombre + descripción (del ítem seleccionado)")]
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI descriptionLabel;
    private int textRequestId;

    protected override List<ScriptableObject> GetInventoryItems()
    {
        if (Inventory.instance != null)
        {
            return Inventory.instance.GetKeyItemsList();
        }
        return null;
    }

    protected override void Update()
    {
        base.Update();

        // El guard de la clase base (lista vacía / `inspecting`) protege sólo SU lógica: estas dos
        // seguían corriendo igual mientras el jugador tenía un item en inspección, así que se podía
        // disparar una combinación desde adentro del examine.
        if (inventoryItems == null || inventoryItems.Count == 0 || inspecting) return;

        HandleCombination();
        HandleInspection();
    }

    // Navegación en GRILLA (override del default lista de MenuUIBase): izq/der ±1, arriba/abajo ±columns,
    // con clamp (sin wraparound — confunde en grilla). El modelo 3D del seleccionado se instancia vía
    // OnItemClicked → ShowItem, exactamente como en la lista (lo que el usuario quiere conservar).
    protected override void HandleNavigation()
    {
        if (InputHandler.instance.NavRightPressed) MoveSelection(1);
        else if (InputHandler.instance.NavLeftPressed) MoveSelection(-1);
        else if (InputHandler.instance.NavDownPressed) MoveSelection(columns);
        else if (InputHandler.instance.NavUpPressed) MoveSelection(-columns);
    }

    private void MoveSelection(int delta)
    {
        int target = selectedItemIndex + delta;
        if (target < 0 || target >= inventoryItems.Count || target == selectedItemIndex) return;
        selectedItemIndex = target;
        OnItemClicked(inventoryItems[selectedItemIndex]);
    }

    // 9 slots fijos: rellena con celdas vacías hasta max(minSlots, múltiplo de columns >= count).
    // Las vacías no entran al diccionario (no navegables); la navegación recorre solo items reales.
    protected override void PadSlots()
    {
        int rowsTarget = Mathf.CeilToInt(inventoryItems.Count / (float)columns) * columns;
        int target = Mathf.Max(minSlots, rowsTarget);
        for (int i = inventoryItems.Count; i < target; i++)
        {
            GameObject go = Instantiate(itemButtonPrefab, itemListParent);
            var cell = go.GetComponent<ItemButtonController>();
            if (cell != null) cell.SetEmpty();
        }
    }

    private void HandleCombination()
    {
        if (InputHandler.instance == null) return;

        if (selectedItemData is CombinableItem combinableItem)
        {
            if (InputHandler.instance.FlipNotePressed)
            {
                CombinableItemsManager.instance.SetCombinable(combinableItem, itemButtonsDictionary);
            }

            if (InputHandler.instance.MenuCancelPressed && CombinableItemsManager.instance.IsCombining())
            {
                CombinableItemsManager.instance.StopCombination(itemButtonsDictionary);
            }
        }
    }

    private void HandleInspection()
    {
        if (InputHandler.instance == null) return;

        if (InputHandler.instance.MenuConfirmPressed)
        {
            if (itemInstance == null) return;
            InspectItemUI(itemInstance.transform);
        }
    }

    private void InspectItemUI(Transform itemTransform)
    {
        if (itemTransform == null)
            return;
        // Guard antes de tocar `inspecting`: si el singleton no está, salir acá deja el estado
        // consistente en vez de quedar marcado como inspeccionando algo que nunca abrió.
        if (InspectObject.instance == null)
            return;

        inspecting = true;
        InspectObject.instance.enabled = true;
        InspectObject.instance.ActiveExamineUI(itemTransform, OnInspectionUIFinished);
        myPanel.gameObject.SetActive(false);
        OnInspectingItemUI?.Invoke();
    }

    public void InspectCombinatedItem(ScriptableObject resultItem)
    {
        selectedItemIndex = GetInventoryItems().Count - 1;
        InspectItemUI(itemInstance.transform);
    }

    protected override void OnItemClicked(ScriptableObject item)
    {
        base.OnItemClicked(item);

        if (item is CombinableItem)
        {
            if (combinarHint == null)
            {
                HintDisplayData combinarHintData = HintDisplayData.Make(HintType.Combine, HintIconType.FIcon);
                combinarHint = UIButtonsManager.instance.AddAdditionalHint(combinarHintData);
            }
        }
        else
        {
            if (combinarHint != null)
            {
                UIButtonsManager.instance.RemoveAdditionalHint(combinarHint);
                combinarHint = null;
            }
        }
    }

    protected override void ShowItem(ScriptableObject item)
    {
        LoadItemText(item);

        if (item is IDisplayItem displayable)
        {
            GameObject prefab = displayable.GetPrefab();
            if (prefab == null) return;

            // Orientación = la rotación autorada del prefab (la orientás rotando el prefab en el editor).
            itemInstance = SpawnAndAlignPreviewItem(prefab);
            currentItem = itemInstance;
        }
    }

    private void LoadItemText(ScriptableObject item)
    {
        textRequestId++;
        int req = textRequestId;
        if (nameLabel != null) nameLabel.text = string.Empty;
        if (descriptionLabel != null) descriptionLabel.text = string.Empty;

        if (item is IDisplayItem display)
        {
            LoadLocalized(display.GetName(), req, s => { if (nameLabel != null) nameLabel.text = s; });
            LoadLocalized(display.GetDescription(), req, s => { if (descriptionLabel != null) descriptionLabel.text = s; });
        }
    }

    private void LoadLocalized(LocalizedString localized, int req, Action<string> apply)
    {
        if (!localized.IsResolvable()) return;
        localized.GetLocalizedStringAsync().Completed += (AsyncOperationHandle<string> handle) =>
        {
            if (req != textRequestId) return;
            if (handle.Status == AsyncOperationStatus.Succeeded) apply(handle.Result);
        };
    }

    private void OnInspectionUIFinished()
    {
        inspecting = false;
        myPanel.gameObject.SetActive(true);

        // Re-activate inspection light (ExitExamine turned it off during exit animation)
        InspectionLightService.SetActive(true);

        OnExitInspectingItemUI?.Invoke();
    }
}
