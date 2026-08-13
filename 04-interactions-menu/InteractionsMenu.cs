using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Selector de objetos para "usar un ítem sobre un objeto del mundo" (puertas con llave, printer,
/// lámpara). La cámara ya se acercó a la cerradura IN SITU; este menú abre un panel lateral DERECHO
/// (mismo shell crema/recuadro+máscara que la lectura de notas) con una GRILLA de slots — los ítems
/// se ven como foto-miniaturas B&N. Elegís con WASD, usás con Espacio, salís con click derecho.
/// Debajo de la grilla: nombre + descripción del seleccionado.
///
/// Conserva la interfaz pública (OpenMenu/CloseMenu/OpenedMenu) que usan InteractionsMenuManager
/// y los callers (DoorWithKey, etc.) — esos no cambian. NO toca el lector de notas (DocumentPanelUI):
/// solo comparte el sprite/estilo del recuadro.
/// </summary>
public class InteractionsMenu : MonoBehaviour
{
    [Header("Raíz")]
    [SerializeField] private GameObject allMenuParent;

    [Header("Grilla")]
    [Tooltip("Contenedor con GridLayoutGroup donde se instancian los slots.")]
    [SerializeField] private RectTransform gridContent;
    [SerializeField] private GameObject slotPrefab;
    [Tooltip("Columnas de la grilla (debe coincidir con el GridLayoutGroup).")]
    [SerializeField] private int columns = 3;
    [Tooltip("Mínimo de slots SIEMPRE visibles (3×3 = 9). Los que sobran sobre los items reales se dibujan vacíos.")]
    [SerializeField] private int minSlots = 9;

    [Header("Texto (bajo la grilla)")]
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI descriptionLabel;
    [Tooltip("Texto que se muestra en nameLabel cuando no hay objetos usables.")]
    [SerializeField] private string emptyText = "—  sin objetos  —";

    [Header("Sonido")]
    [SerializeField] private AudioSource selectionAudioSource;
    [SerializeField] private AudioClip selectionClip;
    [Tooltip("Suena al usar un ítem incorrecto sobre el objeto (acompaña al flash rojo del recuadro).")]
    [SerializeField] private AudioClip wrongItemClip;

    private IMenuInteractable currentInteractable;
    private readonly List<ScriptableObject> items = new List<ScriptableObject>();
    private readonly List<ItemButtonController> cells = new List<ItemButtonController>();
    private int selectedIndex;
    private bool openedMenu;
    private bool itemConsumed;
    public bool OpenedMenu => openedMenu;

    private int textRequestId;

    private void Awake()
    {
        if (allMenuParent != null) allMenuParent.SetActive(false);
    }

    private void OnDisable()
    {
        KillCellTweens();
    }

    private void Update()
    {
        if (!openedMenu || InputHandler.instance == null) return;

        if (items.Count > 0 && !itemConsumed)
        {
            HandleNavigation();
            if (InputHandler.instance.MenuConfirmPressed) TryUseSelected();
        }

        // Acción secundaria (F): el objeto decide si está disponible (p. ej. lámpara con bulbo puesto).
        // F llega por FlipNote, que vive en el mapa Menu y sigue activo mientras el menú maneja el input.
        if (!itemConsumed
            && currentInteractable is IMenuSecondaryAction secondary
            && secondary.CanInvokeSecondary
            && InputHandler.instance.FlipNotePressed)
        {
            itemConsumed = true;
            if (UIButtonsManager.instance != null) UIButtonsManager.instance.HideHints();
            secondary.InvokeSecondary();
        }

        if (InputHandler.instance.MenuCancelPressed && (MoveCamPuzzles.Instance == null || !MoveCamPuzzles.Instance.IsMoving))
            StartCoroutine(CloseMenu(true));
    }

    #region API pública

    public void OpenMenu(IMenuInteractable menuInteractable)
    {
        currentInteractable = menuInteractable;
        BuildGrid();
        selectedIndex = 0;
        itemConsumed = false;

        allMenuParent.SetActive(true);
        openedMenu = true;
        GameManager.instance.SetInteracting(true);
        GameManager.instance.DesactiveInteractions(true);

        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Menu);

        if (UIButtonsManager.instance != null)
            UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());

        UpdateSelectionVisuals();
        UpdateText();
    }

    public IEnumerator CloseMenu(bool fade)
    {
        // Guard de re-entrada: cerrar el estado antes del fade evita una segunda coroutine de cierre.
        if (!openedMenu) yield break;
        openedMenu = false;

        bool success = currentInteractable != null && currentInteractable.InteractionSucceeded;

        GameManager.instance.ShowMouse(false);
        if (UIButtonsManager.instance != null) UIButtonsManager.instance.HideHints();

        // yield return del IEnumerator directo (no StartCoroutine): el fade corre en el host de la
        // coroutine (que puede ser el manager), sin depender de que ESTE componente esté enabled.
        if (fade && MoveCamPuzzles.Instance != null)
            yield return MoveCamPuzzles.Instance.BackToOriginPosInstant();

        allMenuParent.SetActive(false);
        ClearGrid();

        if (!success)
            PlayerManager.instance.CanMove(true);

        GameManager.instance.SetInteracting(false);
        GameManager.instance.DesactiveInteractions(false);

        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);

        if (InteractionsMenuManager.instance != null)
            InteractionsMenuManager.instance.NotifyMenuClosed(currentInteractable, success);

        enabled = false;
    }

    #endregion

    #region Grilla

    private void BuildGrid()
    {
        ClearGrid();
        items.Clear();
        items.AddRange(Inventory.instance.GetKeyItemsList());

        if (gridContent == null || slotPrefab == null) return;

        // 9 slots fijos (3×3): primeros N = items reales, el resto vacíos. La navegación
        // (HandleNavigation) ya clampa a items.Count, así que los vacíos no se seleccionan.
        int rowsTarget = Mathf.CeilToInt(items.Count / (float)columns) * columns;
        int target = Mathf.Max(minSlots, rowsTarget);
        for (int i = 0; i < target; i++)
        {
            GameObject go = Instantiate(slotPrefab, gridContent);
            var cell = go.GetComponent<ItemButtonController>();
            if (i < items.Count) cell.Setup(items[i]);
            else cell.SetEmpty();
            cells.Add(cell);
        }
    }

    /// <summary>
    /// Re-lee el inventario y redibuja la grilla dejando la selección lo más cerca posible de donde
    /// estaba (clampeada, porque la lista pudo achicarse). La grilla se arma UNA vez al abrir el
    /// menú: si el inventario cambia con el menú abierto, sin esto queda mostrando ítems fantasma.
    /// </summary>
    private void RebuildGridKeepingSelection()
    {
        int previousIndex = selectedIndex;
        BuildGrid();
        selectedIndex = items.Count > 0 ? Mathf.Clamp(previousIndex, 0, items.Count - 1) : 0;
        UpdateSelectionVisuals();
        UpdateText();
    }

    private void ClearGrid()
    {
        KillCellTweens();
        for (int i = 0; i < cells.Count; i++)
            if (cells[i] != null) Destroy(cells[i].gameObject);
        cells.Clear();
    }

    private void KillCellTweens()
    {
        for (int i = 0; i < cells.Count; i++)
            if (cells[i] != null) cells[i].transform.DOKill();
    }

    private void HandleNavigation()
    {
        int next = selectedIndex;
        if (InputHandler.instance.NavRightPressed) next = selectedIndex + 1;
        else if (InputHandler.instance.NavLeftPressed) next = selectedIndex - 1;
        else if (InputHandler.instance.NavDownPressed) next = selectedIndex + columns;
        else if (InputHandler.instance.NavUpPressed) next = selectedIndex - columns;
        else return;

        // Sólo se cae en ítems reales (los slots vacíos no son seleccionables).
        if (next < 0 || next >= items.Count || next == selectedIndex) return;
        selectedIndex = next;
        PlaySelectionSound();
        UpdateSelectionVisuals();
        UpdateText();
    }

    private void PlaySelectionSound() => PlayClip(selectionClip);

    private void PlayWrongItemSound() => PlayClip(wrongItemClip);

    private void PlayClip(AudioClip clip)
    {
        if (selectionAudioSource != null && clip != null)
            selectionAudioSource.PlayOneShot(clip);
    }

    private void UpdateSelectionVisuals()
    {
        for (int i = 0; i < cells.Count; i++)
            if (cells[i] != null) cells[i].SetSelected(items.Count > 0 && i == selectedIndex);
    }

    #endregion

    #region Uso del ítem

    private void TryUseSelected()
    {
        if (items.Count == 0 || currentInteractable == null) return;
        selectedIndex = Mathf.Clamp(selectedIndex, 0, items.Count - 1);
        ScriptableObject selectedItem = items[selectedIndex];

        bool itemWasCorrect;
        try
        {
            itemWasCorrect = currentInteractable.UseItem(selectedItem);
        }
        catch (System.Exception e)
        {
            // Un implementador que revienta NO puede dejar el menú zombi. Sin este catch la
            // excepción se llevaba puesto el resto de TryUseSelected: itemConsumed quedaba en false,
            // el menú no se cerraba nunca y el jugador podía seguir "usando" ítems sobre un objeto
            // ya roto. Lo tratamos como uso incorrecto: suena el error, el menú sigue vivo y
            // cerrable, y el stack queda en consola para arreglar la causa.
            Debug.LogException(e, currentInteractable as UnityEngine.Object);
            itemWasCorrect = false;

            // El implementador pudo haber alcanzado a sacar el ítem del inventario antes de reventar.
            // Reconstruir la grilla desde el inventario evita el síntoma que se reportó: seguir viendo
            // (y pudiendo "usar") una llave que ya no tenés.
            RebuildGridKeepingSelection();
        }

        if (itemWasCorrect)
        {
            // Ítem correcto: consumir y bloquear más input hasta que el caller/manager cierre el menú
            // (evita doble-uso durante la animación de la cerradura/printer).
            itemConsumed = true;

            if (selectedItem is UniqueKeyItemData uniqueKeyItem)
                Inventory.instance.UseKeyItem(uniqueKeyItem);

            if (UIButtonsManager.instance != null) UIButtonsManager.instance.HideHints();
        }
        else
        {
            // Ítem incorrecto: el fotograma se pone ROJO + mini shake + sonido de error.
            PlayWrongItemSound();

            if (selectedIndex < cells.Count && cells[selectedIndex] != null)
            {
                cells[selectedIndex].PlayErrorFlash();
                cells[selectedIndex].PlayErrorAnimation();
            }
        }
    }

    #endregion

    #region Texto localizado

    private void UpdateText()
    {
        textRequestId++;
        int req = textRequestId;

        if (items.Count == 0)
        {
            SetText(emptyText, string.Empty);
            return;
        }

        SetText(string.Empty, string.Empty);
        if (items[selectedIndex] is IDisplayItem display)
        {
            LoadLocalized(display.GetName(), req, s => { if (nameLabel != null) nameLabel.text = s; });
            LoadLocalized(display.GetDescription(), req, s => { if (descriptionLabel != null) descriptionLabel.text = s; });
        }
    }

    private void SetText(string name, string desc)
    {
        if (nameLabel != null) nameLabel.text = name;
        if (descriptionLabel != null) descriptionLabel.text = desc;
    }

    private void LoadLocalized(LocalizedString localized, int req, System.Action<string> apply)
    {
        if (!localized.IsResolvable()) return;
        localized.GetLocalizedStringAsync().Completed += (AsyncOperationHandle<string> handle) =>
        {
            if (req != textRequestId) return;
            if (handle.Status == AsyncOperationStatus.Succeeded) apply(handle.Result);
        };
    }

    #endregion

    private List<HintDisplayData> GetHintDisplayDataList()
    {
        var hints = new List<HintDisplayData>()
        {
            HintDisplayData.Make(HintType.Select, HintIconType.WASDIcon),
            HintDisplayData.Make(HintType.Use, HintIconType.SpaceIcon),
        };

        // Acción secundaria del objeto (p. ej. lámpara: sacar el bulbo con F) — solo si está disponible.
        if (currentInteractable is IMenuSecondaryAction secondary && secondary.CanInvokeSecondary)
            hints.Add(secondary.SecondaryHint);

        hints.Add(HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon));
        return hints;
    }
}
