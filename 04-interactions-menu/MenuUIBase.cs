using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public abstract class MenuUIBase<T> : MonoBehaviour where T : ScriptableObject
{
    // Layer del sistema de blur single-camera: InspectedStencilPass la escribe en _InspectedMaskTex
    // y BackgroundBlur la mantiene nítida. (Antes "RenderAbove" = layer del sistema viejo de 2 cámaras,
    // que PlayerCam no renderiza → el preview no se veía en Game view.)
    private const string InspectedObjectLayer = "InspectedObject";

    [SerializeField] protected Transform inspectionPoint;
    [SerializeField] protected GameObject itemButtonPrefab;
    [SerializeField] protected Transform itemListParent;

    protected List<T> inventoryItems = new List<T>();
    protected Dictionary<T, ItemButtonController> itemButtonsDictionary = new Dictionary<T, ItemButtonController>();

    protected int selectedItemIndex = 0;
    protected GameObject currentItem;
    protected bool itemsLoaded = false;

    protected T selectedItemData;
    protected ItemButtonController selectedButton;

    protected bool inspecting;

    [SerializeField] protected Transform myPanel;

    [Header("Sonido de selección")]
    [SerializeField] private AudioSource selectionAudioSource;
    [SerializeField] private AudioClip selectionClip;
    private bool skipNextSelectionSound;

    protected abstract List<T> GetInventoryItems();
    protected abstract void ShowItem(T item);

    protected virtual void OnEnable()
    {
        myPanel.gameObject.SetActive(true);
        LoadInventoryItems();

        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Menu);
    }

    protected virtual void OnDisable()
    {
        if (currentItem != null)
        {
            Destroy(currentItem);
        }

        // Turn off inspection light (item no longer displayed)
        InspectionLightService.SetActive(false);

        // Safety: restore Player context so movement is never left locked
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);

        itemsLoaded = false;
    }

    protected virtual void Start()
    {
        // Null-guard: en escenas sin CombinableItemsManager (tests, hotreload) esto crasheaba.
        if (CombinableItemsManager.instance != null)
            CombinableItemsManager.instance.OnCombinationComplete += LoadInventoryItems;
    }

    /// <summary>
    /// CombinableItemsManager es DDOL y sobrevive al cambio de escena: sin desuscribir, cada UI de menú
    /// destruida le deja un delegado colgado apuntando a un objeto muerto, y el manager se los acarrea
    /// toda la partida (se van acumulando escena a escena).
    /// </summary>
    protected virtual void OnDestroy()
    {
        if (CombinableItemsManager.instance != null)
            CombinableItemsManager.instance.OnCombinationComplete -= LoadInventoryItems;
    }

    protected virtual void Update()
    {
        if (inventoryItems == null || inventoryItems.Count == 0 || inspecting) return;

        if (InputHandler.instance == null) return;

        HandleNavigation();
    }

    /// <summary>
    /// Navegación por defecto = LISTA vertical (NavUp/NavDown, ±1 con wraparound, saltando ítems
    /// no-interactuables). Las subclases con otro layout (ej: grilla) la sobreescriben.
    /// </summary>
    protected virtual void HandleNavigation()
    {
        if (InputHandler.instance.NavUpPressed)
        {
            SelectNextInteractable(-1);
        }
        else if (InputHandler.instance.NavDownPressed)
        {
            SelectNextInteractable(1);
        }
    }

    private void SelectNextInteractable(int direction)
    {
        int originalIndex = selectedItemIndex;

        for (int i = 0; i < inventoryItems.Count; i++)
        {
            selectedItemIndex = (selectedItemIndex + direction + inventoryItems.Count) % inventoryItems.Count;
            if (IsInteractable(inventoryItems[selectedItemIndex]))
            {
                OnItemClicked(inventoryItems[selectedItemIndex]);
                return;
            }
        }

        selectedItemIndex = originalIndex;
    }

    private bool IsInteractable(ScriptableObject item)
    {
        if (item is T typedItem && itemButtonsDictionary.TryGetValue(typedItem, out var buttonController))
        {
            return buttonController.IsInteractable();
        }
        return false;
    }

    protected virtual void LoadInventoryItems()
    {
        if (itemsLoaded) return;

        ClearItemList();
        inventoryItems = GetInventoryItems();

        if (inventoryItems == null) return;

        foreach (var item in inventoryItems)
        {
            CreateItemButton(item);
        }

        PadSlots();

        if (inventoryItems.Count > 0)
        {
            selectedItemIndex = 0;
            skipNextSelectionSound = true; // no sonar en la auto-selección inicial al abrir
            OnItemClicked(inventoryItems[selectedItemIndex]);
        }

        itemsLoaded = true;
    }

    /// <summary>Hook para rellenar la grilla con slots vacíos hasta un mínimo fijo. Default: no-op
    /// (las listas de Notas/Fotos NO deben tener celdas vacías). Lo sobreescribe la grilla de Objetos.</summary>
    protected virtual void PadSlots() { }

    protected virtual void CreateItemButton(T item)
    {
        GameObject buttonObject = Instantiate(itemButtonPrefab, itemListParent);
        ItemButtonController buttonController = buttonObject.GetComponent<ItemButtonController>();
        if (buttonController != null)
        {
            buttonController.Setup(item);
            itemButtonsDictionary[item] = buttonController;
            ApplyNewBadge(item, buttonController);
        }

        buttonObject.GetComponent<Button>().onClick.AddListener(() => OnItemClicked(item));
    }

    protected virtual void OnItemClicked(T item)
    {
        if (item == null)
        {
            Debug.LogWarning("OnItemClicked fue llamado con un item nulo.");
            return;
        }

        selectedItemData = item;

        if (currentItem != null)
        {
            Destroy(currentItem);
        }
        if (selectedButton != null)
        {
            selectedButton.SetSelected(false);
        }

        if (itemButtonsDictionary.TryGetValue(item, out selectedButton))
        {
            selectedButton.SetSelected(true);
        }

        MarkSelectedSeen(item);
        PlaySelectionSound();
        ShowItem(item);
    }

    protected void PlaySelectionSound()
    {
        if (skipNextSelectionSound) { skipNextSelectionSound = false; return; }
        if (selectionAudioSource != null && selectionClip != null)
            selectionAudioSource.PlayOneShot(selectionClip);
    }

    protected virtual void SelectItemByIndex(int index)
    {
        var items = GetInventoryItems();
        if (index < 0 || index >= items.Count)
        {
            Debug.LogWarning($"SelectItemByIndex: \u00edndice {index} fuera de rango.");
            return;
        }

        var item = items[index];
        if (item == null)
        {
            Debug.LogWarning("SelectItemByIndex fue llamado con un item nulo.");
            return;
        }

        selectedItemData = item;

        if (currentItem != null)
        {
            Destroy(currentItem);
        }

        if (selectedButton != null)
        {
            selectedButton.SetSelected(false);
        }

        if (itemButtonsDictionary.TryGetValue(item, out selectedButton))
        {
            selectedButton.SetSelected(true);
        }

        MarkSelectedSeen(item);
        ShowItem(item);
    }

    public void ClearItemList()
    {
        foreach (Transform child in itemListParent)
        {
            Destroy(child.gameObject);
        }

        itemButtonsDictionary.Clear();
        itemsLoaded = false;
    }

    // ── Indicador "item nuevo" (badge no-leído) ──────────────────────────────────

    /// <summary>Enciende el indicador "nuevo" de la celda si el item aún no fue visto por el jugador.</summary>
    private void ApplyNewBadge(T item, ItemButtonController buttonController)
    {
        if (Inventory.instance == null || buttonController == null) return;
        if (item is IIdentificable identifiable)
            buttonController.SetNewBadge(!Inventory.instance.IsItemSeen(identifiable.ID));
    }

    /// <summary>Marca el item seleccionado como visto y apaga su badge de inmediato (sin esperar al evento).</summary>
    private void MarkSelectedSeen(T item)
    {
        if (Inventory.instance == null) return;
        if (item is IIdentificable identifiable)
        {
            Inventory.instance.MarkItemSeen(identifiable.ID);
            if (selectedButton != null) selectedButton.SetNewBadge(false);
        }
    }

    // ── Preview 3D: spawn + luz ───────────────────────────────────────────────────

    /// <summary>
    /// Instancia el prefab de preview en <see cref="inspectionPoint"/> con la rotación AUTORADA en el prefab
    /// (lo orientás rotando el prefab en el editor), reproducida en el frame del inspectionPoint; lo pone en la
    /// layer del blur de inspección, aplica la cercanía (el customDistance del IInspectable, igual que el examine)
    /// y activa la luz. Unifica los tres <c>ShowItem</c>.
    /// </summary>
    protected GameObject SpawnAndAlignPreviewItem(GameObject prefab)
    {
        GameObject instance = Instantiate(prefab, inspectionPoint.position, Quaternion.identity, inspectionPoint);

        // La preview es una MAQUETA, no el objeto del mundo: su lógica de interacción NO debe correr.
        // El prefab de un item lleva su componente de mundo, y el Start de Key se AUTODESTRUYE cuando la
        // llave ya fue obtenida — que es exactamente la situación en la que estás mirando su preview:
        // el modelo aparecía un frame y desaparecía, dejando el panel vacío y sin nada que inspeccionar.
        // Se deshabilitan ANTES del primer frame (un MonoBehaviour deshabilitado no corre Start); los
        // campos serializados siguen legibles, así que la luz y la cercanía de más abajo no se pierden.
        InteractableObject[] logicaDeMundo = instance.GetComponentsInChildren<InteractableObject>(true);
        for (int i = 0; i < logicaDeMundo.Length; i++)
            logicaDeMundo[i].enabled = false;

        // Orientación = rotación autorada del prefab, reproducida en el frame del inspectionPoint.
        instance.transform.localRotation = prefab.transform.localRotation;

        instance.transform.SetLayerRecursively(LayerMask.NameToLayer(InspectedObjectLayer));

        // Luz + cercanía: ambas del MISMO IInspectable del prefab (igual que el examine de cerca).
        float ev = 0f; bool metallic = false;
        if (instance.TryGetComponent<IInspectable>(out var inspectable))
        {
            ev = inspectable.InspectionLightIntensity;
            metallic = inspectable.IsMetallic;
            // customDistance: el mismo valor que el examine. Positivo = más cerca (hacia cámara = -Z local del anchor).
            instance.transform.localPosition = new Vector3(0f, 0f, -inspectable.GetCustomDistance());
        }
        InspectionLightService.ActivateTransient(instance.transform, ev, metallic);

        return instance;
    }
}
