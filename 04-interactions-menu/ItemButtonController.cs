using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

public class ItemButtonController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI itemName;
    [SerializeField] private GameObject selectedItem;
    [SerializeField] private Image unselectedItemImage;
    [SerializeField] protected Image itemIconImage; // Icono del �tem, si se usa en el inventario

    [Header("Indicador 'item nuevo' (no-leído)")]
    [Tooltip("Punto ámbar que late cuando el item no fue visto aún. GO hijo, DESACTIVADO por defecto. " +
        "En la lista va junto al nombre; en la grilla, en una esquina del recuadro.")]
    [SerializeField] protected NewBadge newBadge;
    private float initialOpacity;
    private string descriptionText; 

    protected Button button;
    RectTransform rectTransform;

    private void Awake()
    {
        button = GetComponent<Button>();
        rectTransform = GetComponent<RectTransform>();
    }

    /// <summary>Celda sin ítem (slot vacío de una grilla fija). Default: oculta nombre, ícono y badge.</summary>
    public virtual void SetEmpty()
    {
        if (itemName != null) itemName.text = string.Empty;
        if (itemIconImage != null) itemIconImage.enabled = false;
        if (button != null) button.interactable = false;
        if (newBadge != null) newBadge.SetVisible(false);
    }

    /// <summary>Enciende/apaga el indicador de "item nuevo" (no-leído) de esta celda.</summary>
    public virtual void SetNewBadge(bool hasNew)
    {
        if (newBadge != null) newBadge.SetVisible(hasNew);
    }

    public virtual void Setup(ScriptableObject item)
    {
        // Mismo criterio que itemName/itemIconImage dos líneas más abajo: un slot-prefab sin este
        // Image asignado tiraba NRE acá antes de llegar a los null-checks que sí tenía el método.
        if (unselectedItemImage != null)
            initialOpacity = unselectedItemImage.color.a;
        descriptionText = ""; // valor por defecto

        if (item is IDisplayItem inspectable)
        {
            if (itemName != null)
            {
                LoadLocalizedText(inspectable.GetName(), itemName);
                LoadLocalizedText(inspectable.GetDescription(), null); // guardamos la descripci�n en string
                itemIconImage.sprite = inspectable.GetIconSprite();
            }
        }
        else if (item is NoteData noteData)
        {
            if (itemName != null)
            {
                LoadLocalizedText(noteData.noteName, itemName);
                itemIconImage.gameObject.SetActive(false);
                // Nota sin ícono → el nombre usa el ancho completo del renglón (más lugar para nombres largos).
                itemName.rectTransform.offsetMax = new Vector2(-8f, itemName.rectTransform.offsetMax.y);
            }
        }
        else if (item is PhotoData photoData)
        {
            if (itemName != null)
            {
                LoadLocalizedText(photoData.PhotoTitle, itemName);
                // Fotos = IDÉNTICO a Notas: sin ícono en la fila, nombre a ancho completo.
                itemIconImage.gameObject.SetActive(false);
                itemName.rectTransform.offsetMax = new Vector2(-8f, itemName.rectTransform.offsetMax.y);
            }
        }
    }

    /// <summary>
    /// Carga un texto localizado de forma asíncrona.
    ///
    /// El guard IsEmpty NO es opcional: GetLocalizedStringAsync() sobre un LocalizedString sin tabla
    /// asignada tira ArgumentException("Empty Table Reference") de forma SÍNCRONA, y eso abortaba
    /// Setup() a mitad de camino — dejando el TMP con su placeholder de autor ("New Text") y el
    /// Image del ícono sin ocultar (cuadrado blanco). DocumentPanelUI e InteractionsMenu ya tenían
    /// este guard; esta clase era la única que no.
    /// </summary>
    private void LoadLocalizedText(LocalizedString localizedString, TextMeshProUGUI targetText)
    {
        if (localizedString == null || localizedString.IsEmpty)
        {
            if (targetText != null) targetText.text = string.Empty;
            else descriptionText = string.Empty;
            return;
        }

        localizedString.GetLocalizedStringAsync().Completed += (AsyncOperationHandle<string> handle) =>
        {
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                if (targetText != null)
                {
                    targetText.text = handle.Result; 
                }
                else
                {
                    descriptionText = handle.Result; // guardamos el string traducido
                }
            }
            else
            {
                Debug.LogError("Error al cargar el texto localizado.");
            }
        };
    }

    [Header("Highlight de selección (lista)")]
    [SerializeField] private float selectedScale = 1.04f;
    [SerializeField] private float highlightDuration = 0.18f;
    [SerializeField] private Color normalNameColor = new Color(0.9294f, 0.898f, 0.8157f, 1f);
    [SerializeField] private Color selectedNameColor = new Color(1f, 0.972f, 0.933f, 1f); // brilla sin ser blanco puro
    private bool selectedState;

    public virtual void SetSelected(bool isSelected)
    {
        selectedState = isSelected;

        // Marcador de selección (flecha) — visible solo en el item seleccionado
        if (selectedItem != null)
            selectedItem.SetActive(isSelected);

        if (unselectedItemImage != null)
        {
            Color color = unselectedItemImage.color;
            color.a = isSelected ? 1.0f : initialOpacity;
            unselectedItemImage.color = color;
        }

        // Highlight animado (OutSine = respuesta material): el nombre se agranda un poco y brilla más.
        if (itemName != null)
        {
            itemName.rectTransform.DOKill();
            itemName.rectTransform.DOScale(isSelected ? selectedScale : 1f, highlightDuration).SetEase(Ease.OutSine);
            itemName.DOKill();
            itemName.DOColor(isSelected ? selectedNameColor : normalNameColor, highlightDuration).SetEase(Ease.OutSine);
        }
    }

    public string GetDescription()
    {
        return descriptionText ?? "";
    }

    public virtual void SetInteractable(bool interactable)
    {
        if (button != null) button.interactable = interactable;

        // null-guard: las celdas sin label de nombre (ej: FilmFrameCell, el nombre va al panel) lo dejan null.
        if (itemName != null)
        {
            Color c = itemName.color;
            c.a = interactable ? 1f : 0.3f;
            itemName.color = c;
        }
    }

    public bool IsInteractable()
    {
        return button != null && button.interactable;
    }

    public virtual void PlayErrorFlash()
    {
        if (itemName == null) return;

        // Restaurar al color SEGÚN estado (no a un valor intermedio capturado a mitad de un tween).
        Color restore = selectedState ? selectedNameColor : normalNameColor;
        itemName.DOKill();
        itemName.color = restore;
        itemName.DOColor(Color.red, 0.15f)
                .SetLoops(2, LoopType.Yoyo)
                .OnComplete(() => { if (itemName != null) itemName.color = restore; });
    }

    [Header("Shake de error")]
    [SerializeField] private float errorShakeDuration = 0.4f;
    [SerializeField] private float errorShakeStrength = 10f;
    [SerializeField] private int errorShakeVibrato = 10;

    // Posición autorada de la celda, capturada UNA sola vez. Mismo motivo que el frameBaseRGB de
    // FilmFrameCellController: NO se puede leer anchoredPosition en el momento de restaurar. La
    // versión vieja hacía `Vector2 origen = rectTransform.anchoredPosition` al entrar, así que si
    // llegaba un segundo error con el shake anterior todavía corriendo, capturaba la celda A MITAD
    // de la sacudida y su OnComplete restauraba A ESA posición desplazada — la celda quedaba corrida
    // para siempre. La grilla es de 9 slots FIJOS, así que la posición base nunca cambia y alcanza
    // con leerla la primera vez.
    private Vector2 posicionBase;
    private bool posicionBaseCapturada;

    private void CapturarPosicionBase()
    {
        if (posicionBaseCapturada || rectTransform == null) return;
        posicionBase = rectTransform.anchoredPosition;
        posicionBaseCapturada = true;
    }

    /// <summary>
    /// Sacudida de la celda al usar un ítem incorrecto.
    ///
    /// DOShakeAnchorPos NO garantiza volver al punto de partida: si el tween se mata a mitad
    /// (cerrar el menú, spamear Espacio, reconstruir la grilla) la celda se queda DESPLAZADA para
    /// siempre. Por eso se restaura la posición base en OnComplete y en OnKill — OnKill cubre
    /// justamente el caso en que el tween no llega a terminar.
    /// </summary>
    public void PlayErrorAnimation()
    {
        if (rectTransform == null) return;

        CapturarPosicionBase();

        rectTransform.DOKill();
        rectTransform.anchoredPosition = posicionBase;

        rectTransform.DOShakeAnchorPos(errorShakeDuration, errorShakeStrength, errorShakeVibrato, 90, false, true)
            .OnComplete(() => { if (rectTransform != null) rectTransform.anchoredPosition = posicionBase; })
            .OnKill(() => { if (rectTransform != null) rectTransform.anchoredPosition = posicionBase; });
    }
}
