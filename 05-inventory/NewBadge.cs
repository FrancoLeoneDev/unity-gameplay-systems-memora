using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Punto ámbar que late suavemente. Reutilizable en celdas de lista (junto al nombre) y en los
/// dots de sección (bajo el ícono de pestaña). Marca "item nuevo / no-leído".
///
/// El GameObject del badge arranca DESACTIVADO en el prefab; <see cref="SetVisible"/> lo enciende/apaga.
/// Con el GO inactivo el Update no corre → cero overhead cuando no hay nada nuevo.
///
/// El latido usa fase acumulada con <see cref="Time.unscaledDeltaTime"/> (frame-rate independiente y
/// robusto al timeScale=0 del inventario en pausa) en vez de <c>Time.time</c>, evitando la pérdida de
/// precisión de float en sesiones largas.
/// </summary>
[RequireComponent(typeof(Image))]
public sealed class NewBadge : MonoBehaviour
{
    [Header("Color (ámbar Memora, nunca blanco puro)")]
    [SerializeField] private Color badgeColor = new Color(0.910f, 0.784f, 0.478f, 1f); // ~#E8C87A

    [Header("Latido")]
    [SerializeField, Range(0.05f, 2f)] private float pulseFrequency = 0.30f; // Hz
    [SerializeField, Range(0f, 1f)] private float minAlpha = 0.30f;
    [SerializeField, Range(0f, 1f)] private float maxAlpha = 0.90f;

    private const float TwoPi = Mathf.PI * 2f;
    private Image image;
    private float phase;

    private void Awake() => image = GetComponent<Image>();

    private void OnEnable()
    {
        phase = 0f; // empezar siempre desde el mínimo al aparecer
        if (image != null) ApplyAlpha(minAlpha);
    }

    private void Update()
    {
        phase += Time.unscaledDeltaTime * pulseFrequency * TwoPi;
        if (phase > TwoPi) phase -= TwoPi;
        ApplyAlpha(Mathf.Lerp(minAlpha, maxAlpha, (Mathf.Sin(phase) + 1f) * 0.5f));
    }

    private void ApplyAlpha(float a)
    {
        image.color = new Color(badgeColor.r, badgeColor.g, badgeColor.b, a);
    }

    /// <summary>Muestra u oculta el punto. Activar/desactivar el GO detiene el Update por sí solo.</summary>
    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf == visible) return;
        gameObject.SetActive(visible);
    }
}
