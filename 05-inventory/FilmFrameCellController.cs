using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Celda de la grilla 3×3 de Objetos: un recuadro (frame, sprite SlotFrame) con el ícono del ítem
/// EN COLOR (~75%, centrado) sobre la máscara negra de la zona. La selección ILUMINA el recuadro
/// (sube alpha) y el ícono (brillo) — sin escala. Soporta slot VACÍO (frame tenue, sin ícono).
/// Misma celda en inventario y menú de usar-objeto (unificación). MenuUIBase/InteractionsMenu la
/// manejan por polimorfismo como ItemButtonController.
/// </summary>
public class FilmFrameCellController : ItemButtonController
{
    [Header("Recuadro + ícono")]
    [SerializeField] private Image frame;   // SlotFrame (recuadro)
    [SerializeField] private Image icon;    // ícono color

    [Header("Alphas del recuadro por estado")]
    [SerializeField, Range(0f, 1f)] private float emptyFrameAlpha = 0.16f;
    [SerializeField, Range(0f, 1f)] private float unselectedFrameAlpha = 0.45f;
    [SerializeField, Range(0f, 1f)] private float selectedFrameAlpha = 1f;
    [Header("Brillo del ícono")]
    [SerializeField, Range(0f, 1f)] private float iconUnselectedBrightness = 0.78f;
    [SerializeField] private float tweenDuration = 0.12f;

    private bool hasItem;
    private bool isSelected;

    // RGB autorado del recuadro, capturado UNA sola vez. No se puede leer frame.color cuando haga
    // falta restaurar: a mitad del flash de error ese valor es ROJO (ver PlayErrorFlash).
    private Color frameBaseRGB = Color.white;
    private bool frameBaseCaptured;

    private void CaptureFrameBase()
    {
        if (frameBaseCaptured || frame == null) return;
        frameBaseRGB = frame.color;
        frameBaseCaptured = true;
    }

    public override void Setup(ScriptableObject item)
    {
        if (item is IDisplayItem display && display.GetIconSprite() != null)
        {
            if (icon != null)
            {
                icon.sprite = display.GetIconSprite();
                icon.enabled = true;
                icon.preserveAspect = true;
            }
            hasItem = true;
            if (button != null) button.interactable = true;
            ApplyState(false, true);
        }
        else
        {
            SetEmpty();
        }
    }

    public override void SetEmpty()
    {
        hasItem = false;
        if (icon != null) { icon.DOKill(); icon.sprite = null; icon.enabled = false; }
        if (button != null) button.interactable = false;
        if (newBadge != null) newBadge.SetVisible(false); // slots vacíos nunca tienen badge
        ApplyState(false, true);
    }

    public override void SetSelected(bool isSelected) => ApplyState(isSelected, false);

    private void ApplyState(bool selected, bool instant)
    {
        isSelected = selected;
        CaptureFrameBase();

        if (frame != null)
        {
            float a = FrameAlphaForState();
            frame.DOKill();
            if (instant) { Color c = frame.color; c.a = a; frame.color = c; }
            else frame.DOFade(a, tweenDuration).SetEase(Ease.InOutSine);
        }
        if (icon != null && hasItem)
        {
            float b = selected ? 1f : iconUnselectedBrightness;
            icon.DOKill();
            Color target = new Color(b, b, b, 1f);
            if (instant) icon.color = target;
            else icon.DOColor(target, tweenDuration).SetEase(Ease.InOutSine);
        }
    }

    public override void SetInteractable(bool interactable)
    {
        if (button != null) button.interactable = interactable;
        float a = interactable ? 1f : 0.3f;
        if (icon != null) { Color ci = icon.color; ci.a = a; icon.color = ci; }
    }

    [Header("Flash de error (ítem incorrecto)")]
    [SerializeField] private Color errorFlashColor = new Color(0.6f, 0.1f, 0.08f, 1f);
    [SerializeField] private float errorFlashDuration = 0.12f;

    /// <summary>Alpha del recuadro que le corresponde al estado actual (vacío / sin selección / seleccionado).</summary>
    private float FrameAlphaForState()
        => hasItem ? (isSelected ? selectedFrameAlpha : unselectedFrameAlpha) : emptyFrameAlpha;

    /// <summary>
    /// Parpadeo rojo del recuadro al usar un ítem incorrecto.
    ///
    /// OJO: el color de restauración se DERIVA DEL ESTADO, no se captura de frame.color. La versión
    /// vieja hacía `Color original = frame.color` al entrar: si spameabas Espacio, la segunda llamada
    /// capturaba el color en pleno tween (o sea ROJO) y su OnComplete restauraba A ROJO — el recuadro
    /// quedaba rojo para siempre. (DOKill() además no dispara el OnComplete anterior.)
    /// La clase base ya lo hacía así; este override se había desviado.
    /// </summary>
    public override void PlayErrorFlash()
    {
        if (frame == null) return;

        CaptureFrameBase();
        Color restore = frameBaseRGB;
        restore.a = FrameAlphaForState();

        frame.DOKill();
        frame.color = restore;
        frame.DOColor(new Color(errorFlashColor.r, errorFlashColor.g, errorFlashColor.b, restore.a), errorFlashDuration)
            .SetLoops(2, LoopType.Yoyo)
            .OnComplete(() => { if (frame != null) frame.color = restore; });
    }
}
