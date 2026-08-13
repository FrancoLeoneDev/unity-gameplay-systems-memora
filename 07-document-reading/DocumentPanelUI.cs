using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

/// <summary>
/// Panel lateral de documentos (estilo RE Requiem). La UI se ARMA EN EDITOR y se asignan
/// las referencias acá (mismo patrón que el resto de los canvas del juego); este script
/// solo la muestra/oculta con fade+slide y rellena los textos localizados.
///
/// Setup en editor:
/// - Este componente va en un GameObject SIEMPRE ACTIVO (ej: el Canvas raíz, idealmente
///   dentro de PersistentObjects para que exista en todas las escenas).
/// - panelRoot   = contenedor visual del panel (hijo, anclado a la derecha). Se activa/desactiva.
/// - canvasGroup = CanvasGroup sobre ese contenedor (para el fade).
/// - titleText / bodyText = los dos TMP que rellena el script.
/// - Header "DOCUMENTOS", divisores, fondo, etc. son estáticos del canvas — el script no los toca.
///
/// Anti-race: las cargas async de localización se descartan si llega una respuesta de un
/// pedido viejo (requestId), para que un cambio rápido de documento no pise el texto actual.
/// </summary>
public class DocumentPanelUI : MonoBehaviour
{
    public static DocumentPanelUI Instance { get; private set; }

    [Header("Referencias (armar el panel en editor y asignar)")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private CanvasGroup canvasGroup;
    [Tooltip("Opcional: título del documento (nombre de la nota). Dejar vacío si el panel no muestra título.")]
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI bodyText;

    [Header("Animación")]
    [SerializeField] private float fadeInDuration = 0.22f;
    [SerializeField] private float fadeOutDuration = 0.15f;
    [Tooltip("Desplazamiento horizontal del slide de entrada. 0 = solo fade.")]
    [SerializeField] private float slideDistance = 40f;

    [Header("Scroll (opcional, para textos largos)")]
    [Tooltip("ScrollRect que envuelve al cuerpo (opcional). El scroll lo maneja Unity nativamente — el cursor está visible durante la lectura. La referencia es solo para resetear al tope con cada documento nuevo.")]
    [SerializeField] private ScrollRect scrollRect;

    private RectTransform panelRect;
    private float panelBaseX;
    private Tween fadeTween;
    private Tween slideTween;
    private int requestId;
    private bool configured;

    private void Awake()
    {
        Instance = this;

        // titleText es OPCIONAL (el panel puede no mostrar título).
        configured = panelRoot != null && canvasGroup != null && bodyText != null;
        if (!configured)
        {
            Debug.LogError("[DocumentPanelUI] Faltan referencias (panelRoot/canvasGroup/bodyText) — armar el panel en editor y asignarlas.", this);
            return;
        }

        panelRect = panelRoot.GetComponent<RectTransform>();
        panelBaseX = panelRect.anchoredPosition.x;
        panelRoot.SetActive(false);
    }

    private void OnDestroy()
    {
        KillTweens();
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Vuelve el scroll al tope (cada documento arranca desde arriba).</summary>
    private void ResetScroll()
    {
        if (scrollRect == null) return;
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 1f;
    }

    #region API

    public void ShowNote(NoteData data)
    {
        if (!configured || data == null) return;

        requestId++;
        SetTitle(string.Empty);
        SetBody(string.Empty);
        LoadLocalized(data.noteName, requestId, SetTitle);
        LoadLocalized(data.content, requestId, SetBody);
        Open();
    }

    public void ShowPlain(string body, string title = null)
    {
        if (!configured) return;

        requestId++;
        SetTitle(title ?? string.Empty);
        SetBody(body ?? string.Empty);
        Open();
    }

    public void Hide()
    {
        if (!configured) return;

        requestId++;
        if (!panelRoot.activeSelf) return;

        KillTweens();
        fadeTween = DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 0f, fadeOutDuration)
            .OnComplete(() => panelRoot.SetActive(false));
    }

    public void HideInstant()
    {
        if (!configured) return;

        requestId++;
        KillTweens();
        canvasGroup.alpha = 0f;
        panelRoot.SetActive(false);
    }

    #endregion

    private void Open()
    {
        KillTweens();
        panelRoot.SetActive(true);
        canvasGroup.alpha = 0f;
        panelRect.anchoredPosition = new Vector2(panelBaseX + slideDistance, panelRect.anchoredPosition.y);

        fadeTween = DOTween.To(() => canvasGroup.alpha, a => canvasGroup.alpha = a, 1f, fadeInDuration);
        slideTween = DOTween.To(() => panelRect.anchoredPosition.x, x =>
                panelRect.anchoredPosition = new Vector2(x, panelRect.anchoredPosition.y),
                panelBaseX, fadeInDuration)
            .SetEase(Ease.OutQuad);
    }

    private void KillTweens()
    {
        if (fadeTween != null && fadeTween.IsActive()) fadeTween.Kill();
        if (slideTween != null && slideTween.IsActive()) slideTween.Kill();
        fadeTween = null;
        slideTween = null;
    }

    private void SetTitle(string value)
    {
        if (titleText != null) titleText.text = value;
    }

    private void SetBody(string value)
    {
        bodyText.text = value;
        ResetScroll();
    }

    private void LoadLocalized(LocalizedString localized, int request, System.Action<string> apply)
    {
        if (!localized.IsResolvable()) return;

        localized.GetLocalizedStringAsync().Completed += (AsyncOperationHandle<string> handle) =>
        {
            // Descarta respuestas de pedidos viejos (cambio rápido de documento).
            if (request != requestId) return;
            if (handle.Status == AsyncOperationStatus.Succeeded)
                apply(handle.Result);
        };
    }
}
