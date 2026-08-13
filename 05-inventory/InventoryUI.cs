using DG.Tweening;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class InventoryUI : MonoBehaviour, IUiHint, IStateCheckable
{
    public static event Action OnInventoryOpened;
    public static event Action OnInventoryClosed;

    [SerializeField] private GameObject allMenuParent;
    private bool openedMenu;

    [Header("Items Section")]
    [SerializeField] private Button inventoryButton;
    [SerializeField] private GameObject inventoryPanel;

    [Header("Photos Section")]
    [SerializeField] private Button photosButton;
    [SerializeField] private GameObject photosPanel;

    [Header("Notes Section")]
    [SerializeField] private Button notesButton;
    [SerializeField] private GameObject notesPanel;

    private GameObject currentPanelActive;

    [Header("Pestañas")]
    [Tooltip("Escala del ícono de la pestaña seleccionada (las pestañas son íconos: mochila/libreta/cámara).")]
    [SerializeField] private float selectedTabScale = 1.15f;
    [SerializeField] private float tabScaleDuration = 0.2f;

    [Tooltip("Opacidad del ícono de pestaña NO seleccionada (la activa va a 1). Las inactivas se retiran, no desaparecen.")]
    [SerializeField, Range(0f, 1f)] private float inactiveTabAlpha = 0.35f;

    [Tooltip("Label debajo de CADA ícono de pestaña (autorado en el prefab; texto fijo, el código solo activa el de la sección activa).")]
    [SerializeField] private TMPro.TMP_Text[] perTabLabels;

    [Tooltip("Tira de íconos de pestaña (ButtonsParent). Se oculta durante la lectura de una nota para inmersión total.")]
    [SerializeField] private GameObject tabsRow;

    [Header("Indicador 'items nuevos' por sección")]
    [Tooltip("Punto ámbar (NewBadge) bajo el ícono de cada pestaña. MISMO orden que las pestañas: 0=Objetos, 1=Notas, 2=Fotos.")]
    [SerializeField] private NewBadge[] sectionDots;

    private Button[] panelButtons;
    private Button selectedButton;
    private int currentIndex = 0;
    private Vector3 baseTabScale = Vector3.one; // escala base de las pestañas (horneada en el prefab, no es 1)

    private bool someMenuInteracting = false;
    private bool itemSeenSubscribed = false;

    private InventoryItemsUI inventoryItemsUI;
    private InventoryNotesUI inventoryNotesUI;

    private static InventoryUI instance;

    private void Awake()
    {
        // Guardia de singleton: sin esto, volver al menu y relanzar (o recargar la casa)
        // creaba un segundo InventoryUI persistente que duplicaba listeners y canvas en memoria.
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        notesPanel.SetActive(false);
        inventoryPanel.SetActive(false);
        photosPanel.SetActive(false);

        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        panelButtons = new Button[] { inventoryButton, notesButton, photosButton  };

        baseTabScale = inventoryButton.transform.localScale; // base horneada en el prefab
        inventoryButton.onClick.AddListener(() => { currentIndex = 0; SelectButton(0); });
        notesButton.onClick.AddListener(() => { currentIndex = 1; SelectButton(1); });
        photosButton.onClick.AddListener(() => { currentIndex = 2; SelectButton(2); });

        allMenuParent.SetActive(false);

        InventoryItemsUI.OnInspectingItemUI += SetMenuInteracting;
        InventoryItemsUI.OnExitInspectingItemUI += OnExitInspecting;

        InventoryPhotosUI.OnInspectingPhotoUI += SetMenuInteracting;
        InventoryPhotosUI.OnExitInspectingPhotoUI += OnExitInspecting;

        // Notas: lectura de dos etapas. A diferencia de ítems/fotos, NO cambia a contexto Player
        // (no hay rotación; el panel scrollea con el cursor ya visible) y oculta la chrome completa.
        InventoryNotesUI.OnInspectingNoteUI += OnNoteInspecting;
        InventoryNotesUI.OnExitInspectingNoteUI += OnNoteExitInspecting;

        PhotoMemoryPortal.OnMemoryEntryFromUI += ForceCloseForMemoryEntry;

        inventoryItemsUI = inventoryPanel.GetComponent<InventoryItemsUI>();
        inventoryNotesUI = notesPanel.GetComponent<InventoryNotesUI>();

        CombinableItemsManager.instance.OnNoteCombinationSuccess += HandleCombinationResult;
        CombinableItemsManager.instance.OnNoteCombinationSuccess += inventoryNotesUI.InspectCombinatedNote;
        CombinableItemsManager.instance.OnItemCombinationSuccess += HandleCombinationResult;
        CombinableItemsManager.instance.OnItemCombinationSuccess += inventoryItemsUI.InspectCombinatedItem;

        GameManager.instance.SetInventoryCanvas(gameObject);
        GameManager.instance.RestartInventoryCanvas();

        if (InputHandler.instance != null)
            InputHandler.instance.OnInventoryToggle += SwitchBool;
    }

    private void OnDestroy()
    {
        // Solo el duplicado se auto-destruye en Awake: que no borre la instancia real.
        if (instance == this)
            instance = null;

        UnsubscribeItemSeen();

        InventoryItemsUI.OnInspectingItemUI -= SetMenuInteracting;
        InventoryItemsUI.OnExitInspectingItemUI -= OnExitInspecting;

        InventoryPhotosUI.OnInspectingPhotoUI -= SetMenuInteracting;
        InventoryPhotosUI.OnExitInspectingPhotoUI -= OnExitInspecting;

        InventoryNotesUI.OnInspectingNoteUI -= OnNoteInspecting;
        InventoryNotesUI.OnExitInspectingNoteUI -= OnNoteExitInspecting;

        PhotoMemoryPortal.OnMemoryEntryFromUI -= ForceCloseForMemoryEntry;

        if (InputHandler.instance != null)
            InputHandler.instance.OnInventoryToggle -= SwitchBool;

        if (CombinableItemsManager.instance != null)
        {
            CombinableItemsManager.instance.OnNoteCombinationSuccess -= HandleCombinationResult;
            CombinableItemsManager.instance.OnItemCombinationSuccess -= HandleCombinationResult;

            if (inventoryNotesUI != null)
                CombinableItemsManager.instance.OnNoteCombinationSuccess -= inventoryNotesUI.InspectCombinatedNote;
            if (inventoryItemsUI != null)
                CombinableItemsManager.instance.OnItemCombinationSuccess -= inventoryItemsUI.InspectCombinatedItem;
        }
    }

    private void SetMenuInteracting()
    {
        someMenuInteracting = true;

        // FIX: el examine de inventario (InspectObject) lee input del contexto Player
        // (RotateInspectHeld, MouseDelta, CancelPressed). El inventario está en contexto Menu, donde esas
        // acciones están deshabilitadas → no se podía rotar ni salir del examine. Pasamos a Player mientras
        // se inspecciona; OnExitInspecting() restaura Menu al volver al inventario.
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);
    }

    private void Update()
    {
        if (someMenuInteracting) return;
        if (!openedMenu) return;
        if (InputHandler.instance == null) return;

        // Tab toggle is handled via OnInventoryToggle event subscription (Start/OnDestroy).
        // E = cycle right (CycleRight), Q = cycle left (CycleLeft) — Menu action map context.
        if (InputHandler.instance.CycleRightPressed)
        {
            SwitchPanel(1);
        }
        if (InputHandler.instance.CycleLeftPressed)
        {
            SwitchPanel(-1);
        }
    }

    private void OnExitInspecting()
    {
        someMenuInteracting = false;

        // Volver al contexto Menu: seguimos dentro del inventario (Nav/Confirm vuelven a funcionar).
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Menu);

        UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());
    }

    // Lectura de notas: bloquea el ciclado de pestañas y el Tab (vía someMenuInteracting) y oculta
    // la chrome del inventario, PERO mantiene el contexto Menu (MenuCancel sale de la lectura; no
    // hay rotación que requiera Player). Por eso es un handler aparte de SetMenuInteracting.
    private void OnNoteInspecting()
    {
        someMenuInteracting = true;
        SetChromeVisible(false);
    }

    private void OnNoteExitInspecting()
    {
        someMenuInteracting = false;
        SetChromeVisible(true);
        UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());
    }

    private void SetChromeVisible(bool visible)
    {
        if (tabsRow != null) tabsRow.SetActive(visible);
        // Los labels por-ícono viven dentro de tabsRow → se ocultan/muestran con él.
    }

    private void SwitchBool()
    {
        // Una inspección/lectura activa es modal: Tab no cierra el inventario hasta salir de ella
        // (con click derecho). Evita que la nota/objeto se destruya de golpe a mitad de la lectura.
        if (someMenuInteracting)
            return;

        // Clímax del hospital (Act2/Act3): no se puede ABRIR el inventario (cerrar uno abierto sí).
        if (!openedMenu && HospitalClimaxDirector.activo != null && HospitalClimaxDirector.activo.ClimaxInputBloqueado)
            return;

        if (!GameManager.instance.CanToggleState(GetStatesToCheck()))
            return;

        openedMenu = !openedMenu;

        if (openedMenu)
        {
            OpenMenu();
        }
        else
        {
            CloseMenu();
        }
    }

    private void SwitchPanel(int direction)
    {
        if (panelButtons.Length == 0) return;

        currentIndex += direction;

        if (currentIndex >= panelButtons.Length)
            currentIndex = 0;
        else if (currentIndex < 0)
            currentIndex = panelButtons.Length - 1;

        SelectButton(currentIndex);
    }

    private void SelectButton(int index)
    {
        // Cambia el panel directamente (sin el onClick.Invoke circular que hacía que el CLICK de
        // la pestaña no actualizara la selección — bug #7).
        SwitchToPanel(index);

        // Resetea TODAS las pestañas a su escala base y agranda solo la activa. Antes solo se
        // reseteaba la previa (null en la 1ra apertura) → las otras quedaban agrandadas (bug #8).
        for (int i = 0; i < panelButtons.Length; i++)
        {
            panelButtons[i].transform.DOKill();
            panelButtons[i].transform.DOScale(baseTabScale, tabScaleDuration);

            if (panelButtons[i].image != null)
            {
                Color c = panelButtons[i].image.color;
                c.a = i == index ? 1f : inactiveTabAlpha;
                panelButtons[i].image.color = c;
            }
        }
        panelButtons[index].transform.DOScale(baseTabScale * selectedTabScale, tabScaleDuration);

        // Cada ícono tiene su propio label debajo (autorado en el prefab, bien alineado).
        // Se muestra solo el de la sección activa.
        if (perTabLabels != null)
            for (int i = 0; i < perTabLabels.Length; i++)
                if (perTabLabels[i] != null) perTabLabels[i].gameObject.SetActive(i == index);

        selectedButton = panelButtons[index];

        // Cambió la pestaña activa → recalcular dots: la nueva activa esconde el suyo (manda el label),
        // la que dejás de ver vuelve a mostrarlo si todavía tiene items sin ver.
        RefreshSectionDots();
    }

    private void SwitchToPanel(int index)
    {
        switch (index)
        {
            case 0: ShowInventoryPanel(); break;
            case 1: ShowNotesPanel(); break;
            case 2: ShowPhotosPanel(); break;
        }
    }

    private void OpenMenu()
    {
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Menu);

        Canvas3DFade.instance.Activate(true);
        OnInventoryOpened?.Invoke();
        UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());
        allMenuParent.SetActive(true);
        SubscribeItemSeen();        // escuchar "visto" mientras el inventario está abierto (evita race con el spawn del Player)
        SelectButton(currentIndex); // después de activar: anima escalas + switchea el panel + refresca los dots de sección
        BlurManager.instance.Acquire(this);
        PlayerManager.instance.CanMove(false);
        GameManager.instance.SetInventoryOpen(true);
        GameManager.instance.ShowMouse(true);
    }

    private void CloseMenu()
    {
        UnsubscribeItemSeen();

        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);

        Canvas3DFade.instance.Desactivate();
        allMenuParent.SetActive(false);
        OnInventoryClosed?.Invoke();
        BlurManager.instance.Release(this);
        PlayerManager.instance.CanMove(true);
        currentIndex = 0;
        UIButtonsManager.instance.HideHints();
        GameManager.instance.SetInventoryOpen(false);
        GameManager.instance.ShowMouse(false);
    }

    private void ForceCloseForMemoryEntry()
    {
        UnsubscribeItemSeen();

        // Restore Player context; PhotoMemoryPortal will manage its own context going forward.
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);

        Canvas3DFade.instance.Desactivate();
        allMenuParent.SetActive(false);
        OnInventoryClosed?.Invoke();
        // Do NOT deactivate blur — TransitionToPhotoScene handles it after FadeIn
        // Do NOT restore player movement — TransitionToPhotoScene handles it after spawn
        currentIndex = 0;
        openedMenu = false;
        someMenuInteracting = false;
        UIButtonsManager.instance.HideHints();
        GameManager.instance.SetInventoryOpen(false);
        GameManager.instance.ShowMouse(false);
    }

    /// <summary>Cierre duro del inventario para el clímax (Acto 2). Como CloseMenu pero forzado; restaura
    /// control/blur (a diferencia de ForceCloseForMemoryEntry, que los deja a la transición de foto). No-op si cerrado.</summary>
    public static void CerrarSiAbierto()
    {
        if (instance != null && instance.openedMenu) instance.ForceCloseClimax();
    }

    private void ForceCloseClimax()
    {
        UnsubscribeItemSeen();
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);
        Canvas3DFade.instance.Desactivate();
        allMenuParent.SetActive(false);
        OnInventoryClosed?.Invoke();
        BlurManager.instance.Release(this);
        PlayerManager.instance.CanMove(true);
        currentIndex = 0;
        openedMenu = false;
        someMenuInteracting = false;
        UIButtonsManager.instance.HideHints();
        GameManager.instance.SetInventoryOpen(false);
        GameManager.instance.ShowMouse(false);
    }

    private void ShowInventoryPanel()
    {
        if (currentPanelActive != null)
        {
            currentPanelActive.SetActive(false);
        }
        currentPanelActive = inventoryPanel;
        currentPanelActive.SetActive(true);
    }

    private void ShowPhotosPanel()
    {
        if (currentPanelActive != null)
        {
            currentPanelActive.SetActive(false);
        }
        currentPanelActive = photosPanel;
        currentPanelActive.SetActive(true);
    }

    private void ShowNotesPanel()
    {
        if (currentPanelActive != null)
        {
            currentPanelActive.SetActive(false);
        }
        currentPanelActive = notesPanel;
        currentPanelActive.SetActive(true);
    }

    private void HandleCombinationResult(ScriptableObject resultItem)
    {
        switch (resultItem)
        {
            case NoteData:
                // 1 = NOTAS. Estaba en 2, que es FOTOS (ver panelButtons en Start y SwitchToPanel):
                // combinar las dos mitades del Poema te mandaba a la pestaña equivocada.
                currentIndex = 1;
                SelectButton(currentIndex);
                break;
            case UniqueKeyItemData:
                currentIndex = 0;
                SelectButton(currentIndex);
                break;
            default:
                // Es una condición de error, no traza: llegar acá significa que hay un tipo de item
                // que la UI no sabe mostrar y el jugador va a ver un panel vacío sin explicación.
                Debug.LogWarning($"[InventoryUI] Tipo de objeto no soportado en la UI: {(resultItem != null ? resultItem.GetType().Name : "null")}", this);
                break;
        }
    }

    // ── Dots de sección ("hay items nuevos en esta pestaña") ──────────────────────
    // La suscripción se acota a "inventario abierto": evita la race con el spawn del Player
    // (Inventory.instance puede ser null en el Start de InventoryUI) y solo importa con el menú visible.

    private void SubscribeItemSeen()
    {
        if (itemSeenSubscribed || Inventory.instance == null) return;
        Inventory.instance.OnItemSeen += HandleItemSeen;
        itemSeenSubscribed = true;
    }

    private void UnsubscribeItemSeen()
    {
        if (!itemSeenSubscribed) return;
        if (Inventory.instance != null)
            Inventory.instance.OnItemSeen -= HandleItemSeen;
        itemSeenSubscribed = false;
    }

    private void HandleItemSeen(string _) => RefreshSectionDots();

    private void RefreshSectionDots()
    {
        if (sectionDots == null || Inventory.instance == null) return;
        SetSectionDot(0, Inventory.instance.GetKeyItemsList());
        SetSectionDot(1, Inventory.instance.GetNotesList());
        SetSectionDot(2, Inventory.instance.GetPhotosList());
    }

    private void SetSectionDot<TItem>(int index, List<TItem> items) where TItem : ScriptableObject
    {
        if (sectionDots == null || index < 0 || index >= sectionDots.Length || sectionDots[index] == null) return;

        // El dot comparte lugar con el label de la pestaña → en la pestaña ACTIVA manda el label
        // (y los items nuevos de esa sección ya se ven con su badge en la lista: el dot sería redundante).
        bool hasNew = index != currentIndex && HasUnseen(items);
        sectionDots[index].SetVisible(hasNew);
    }

    private bool HasUnseen<TItem>(List<TItem> items) where TItem : ScriptableObject
    {
        if (items == null || Inventory.instance == null) return false;
        for (int i = 0; i < items.Count; i++)
            if (items[i] is IIdentificable id && !Inventory.instance.IsItemSeen(id.ID))
                return true;
        return false;
    }

    public List<HintDisplayData> GetHintDisplayDataList()
    {
        return new List<HintDisplayData>()
        {
            HintDisplayData.Make(HintType.Select, HintIconType.WASDIcon),
            HintDisplayData.Make(HintType.Inspect, HintIconType.SpaceIcon),
            // E (siguiente) y Q (anterior) comparten un único hint -> [E][Q] Cambiar pestaña
            HintDisplayData.Make(HintType.ChangeTab, HintIconType.EIcon, HintIconType.QIcon),
        };
    }

    public List<GameState> GetStatesToCheck()
    {
        return new List<GameState> { GameState.Interacting, GameState.Paused, GameState.InCinematic, GameState.InWhiteRoom };
    }
}
