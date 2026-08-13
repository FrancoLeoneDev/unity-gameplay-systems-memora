using DG.Tweening;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pestaña de notas del inventario. Inspección en DOS etapas (estilo RE Requiem, adaptado):
///
///  1. Browsing: la nota seleccionada en la lista se instancia como objeto 3D en
///     <see cref="MenuUIBase{T}.inspectionPoint"/> (derecha), SIN panel de texto.
///  2. Lectura (Confirm/Space): la UI del inventario se oculta, la nota se desliza a
///     <see cref="readPoint"/> (izquierda) y <see cref="DocumentPanelUI"/> muestra el texto
///     a la derecha. Sale con Cancel (click derecho).
///
/// Se mueve el OBJETO-nota, no la cámara: la regla "cero lerp de cámara" del world-reading
/// no aplica acá (es el mismo principio que usaba el sistema viejo: DOMove de la nota).
/// La inspección vive en esta clase, no en DocumentReader (que es cámara-céntrico y exclusivo
/// del mundo); ambos comparten únicamente el singleton DocumentPanelUI.
/// </summary>
public class InventoryNotesUI : MenuUIBase<NoteData>
{
    [Header("Lectura (estilo RE Requiem)")]
    [Tooltip("Posición IZQUIERDA a la que se desliza la nota al leer (el panel de texto ocupa la derecha).")]
    [SerializeField] private Transform readPoint;

    [Tooltip("Duración del deslizamiento de la nota entre browsing (derecha) y lectura (izquierda).")]
    [SerializeField] private float moveDuration = 0.4f;

    /// <summary>Se entra/sale de la lectura de una nota. InventoryUI oculta/restaura la chrome.</summary>
    public static event Action OnInspectingNoteUI;
    public static event Action OnExitInspectingNoteUI;

    private Tween noteMoveTween;

    protected override List<NoteData> GetInventoryItems()
    {
        if (Inventory.instance == null) return null;
        return Inventory.instance.GetNotesList();
    }

    protected override void ShowItem(NoteData item)
    {
        // Cambiar de nota mientras se lee (sólo posible vía combinación): cortar la lectura sin
        // animación porque el objeto actual está por destruirse.
        if (inspecting) ExitReadMode(instant: true);

        if (item.notePrefab != null)
        {
            // notePrefab es Transform → pasamos .gameObject al helper unificado (usa la localRotation del prefab).
            currentItem = SpawnAndAlignPreviewItem(item.notePrefab.gameObject);
        }
        // Browsing: sin panel de texto. El texto aparece sólo al inspeccionar.
    }

    protected override void Update()
    {
        base.Update(); // navegación de lista (queda congelada mientras inspecting == true)

        if (InputHandler.instance == null) return;

        if (!inspecting)
        {
            if (InputHandler.instance.MenuConfirmPressed && currentItem != null)
                EnterReadMode();
        }
        else if (InputHandler.instance.MenuCancelPressed)
        {
            ExitReadMode();
        }
    }

    private void EnterReadMode()
    {
        if (readPoint == null)
        {
            Debug.LogError("[InventoryNotesUI] 'readPoint' sin asignar — autorar el Transform de la posición de lectura (izquierda).", this);
            return;
        }

        inspecting = true;
        myPanel.gameObject.SetActive(false); // oculta la lista
        OnInspectingNoteUI?.Invoke();        // InventoryUI oculta pestañas/label + bloquea Tab/ciclado

        ShowReadHints();

        // El texto se monta cuando la nota terminó de deslizarse a la izquierda (lectura secuencial:
        // primero se acomoda el papel, después aparece la traducción).
        KillNoteTween();
        noteMoveTween = currentItem.transform
            .DOMove(readPoint.position, moveDuration)
            .SetEase(Ease.OutQuad)
            .OnComplete(() =>
            {
                if (DocumentPanelUI.Instance != null)
                    DocumentPanelUI.Instance.ShowNote(selectedItemData);
            });
    }

    private void ExitReadMode(bool instant = false)
    {
        inspecting = false;

        if (DocumentPanelUI.Instance != null)
            DocumentPanelUI.Instance.Hide();

        KillNoteTween();
        if (currentItem != null)
        {
            if (instant)
                currentItem.transform.position = inspectionPoint.position;
            else
                noteMoveTween = currentItem.transform
                    .DOMove(inspectionPoint.position, moveDuration)
                    .SetEase(Ease.OutQuad);
        }

        myPanel.gameObject.SetActive(true);  // restaura la lista
        OnExitInspectingNoteUI?.Invoke();    // InventoryUI restaura chrome + hints de browsing
    }

    private void ShowReadHints()
    {
        if (UIButtonsManager.instance == null) return;

        UIButtonsManager.instance.ShowHints(new List<HintDisplayData>
        {
            HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon)
        });
    }

    private void KillNoteTween()
    {
        if (noteMoveTween != null && noteMoveTween.IsActive())
            noteMoveTween.Kill();
        noteMoveTween = null;
    }

    public void InspectCombinatedNote(ScriptableObject resultNote)
    {
        if (resultNote is NoteData note)
        {
            selectedItemIndex = GetInventoryItems().IndexOf(note);
            SelectItemByIndex(selectedItemIndex);
        }
    }

    protected override void OnDisable()
    {
        // Cerrar el inventario en medio de la lectura: limpiar el sub-estado antes de que la base
        // destruya el objeto-nota, para no dejar el tween huérfano (crash) ni la chrome oculta.
        if (inspecting)
        {
            inspecting = false;
            OnExitInspectingNoteUI?.Invoke();
        }

        KillNoteTween();
        base.OnDisable(); // destruye currentItem, apaga la luz de inspección, restaura contexto Player

        if (DocumentPanelUI.Instance != null)
            DocumentPanelUI.Instance.HideInstant();
    }
}
