// BlurManager.cs
// ============================================================================
// Memora — Controlador central del blur de fondo.
//
// PROPIEDAD COMPARTIDA (refcount por dueño)
//   El blur lo piden VARIOS sistemas a la vez y se solapan entre sí: pausar durante una
//   inspección, inspeccionar un ítem desde el inventario, previsualizar dentro del inventario,
//   entrar a una foto desde una inspección. Con un booleano crudo el último en hablar ganaba
//   y el blur quedaba encendido (o apagado) en el momento equivocado.
//
//   Ahora cada sistema registra su propio token con Acquire(this) y lo suelta con Release(this).
//   El blur está encendido si y sólo si queda AL MENOS UN dueño. Un sistema no puede pisar a
//   otro, y anidar (inventario → preview → pausa → despausa) se comporta solo.
//
//   Auto-sanación: los dueños destruidos se podan en cada operación, así un sistema que muere
//   sin soltar su token (escena que se descarga, GameObject destruido a mitad de una
//   transición) no deja el blur pegado para siempre.
//
// PATH DE RENDER (single-camera):
//   El objeto inspeccionado se queda en PlayerCam, movido a la capa "InspectedObject".
//   InspectedStencilPass (DrawRenderers CustomPass en AfterOpaqueAndSky sobre esa capa) escribe
//   esos píxeles en una RenderTexture-máscara, bindeada global como _InspectedMaskTex. El
//   BackgroundBlur de AfterPostProcess lee _InspectedMaskTex.r > 0.5 y devuelve el source sin
//   blurrear para esos píxeles; todo el resto se blurrea. No hay segunda cámara.
//
// VOLUME:
//   m_BlurVolume — un Volume (isGlobal) cuyo profile tiene un override de BackgroundBlur.
//   Se maneja el WEIGHT del Volume (0 = off, 1 = full) para no mutar el asset del profile.
//
// Historia: refactor a cámara única (2026-06-16) · propiedad con refcount (2026-08-03,
//           por el bug del blur que quedaba pegado al salir de una inspección).
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class BlurManager : MonoBehaviour
{
    public static BlurManager instance { get; private set; }

    [Header("Background Blur")]
    [Tooltip("Volume (isGlobal) cuyo profile tiene un override de BackgroundBlur. " +
             "BlurManager maneja el weight de este Volume (0 = off, 1 = full).")]
    [SerializeField] private Volume m_BlurVolume;

    [Tooltip("Intensidad aplicada cuando hay al menos un dueño y nadie llamó a SetBlurIntensity.")]
    [SerializeField] private float m_DefaultBlurIntensity = 1f;

    // Dueños vivos del blur. Lista (no HashSet) a propósito: son ≤ 7 y hay que poder comparar
    // con el operador == de UnityEngine.Object, que detecta objetos destruidos — cosa que
    // Equals/GetHashCode de un HashSet NO hacen.
    private readonly List<Object> m_Owners = new List<Object>();

    private float m_CurrentIntensity;

    // Supresión temporal: oculta el blur SIN cambiar de dueño. La usa PhotoMemoryPortal, que
    // necesita bajar el blur mientras dibuja la grieta de luz (el blur taparía la grieta) y
    // devolverlo después, con la inspección todavía activa y todavía dueña de su token.
    private bool m_Suppressed;

    public float DefaultBlurIntensity => m_DefaultBlurIntensity;

    /// <summary>True si algún sistema tiene el blur tomado y no está suprimido.</summary>
    public bool IsActive => m_Owners.Count > 0 && !m_Suppressed;

    private void Awake()
    {
        if (instance != null)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        m_CurrentIntensity = m_DefaultBlurIntensity;

        // Arranca apagado (weight 0 → IsActive() false → costo GPU cero).
        m_Owners.Clear();
        ApplyWeight();
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    #region API pública

    /// <summary>
    /// Pide el blur a nombre de <paramref name="owner"/>. Idempotente: pedirlo dos veces desde el
    /// mismo dueño cuenta una sola vez, así que no hace falta llevar la cuenta del lado del caller.
    /// </summary>
    public void Acquire(Object owner)
    {
        if (owner == null) return;

        PruneDeadOwners();

        if (IndexOfOwner(owner) < 0)
            m_Owners.Add(owner);

        ApplyWeight();
    }

    /// <summary>
    /// Suelta el blur a nombre de <paramref name="owner"/>. Se apaga sólo cuando no queda ningún
    /// dueño. Soltar sin haber pedido es un no-op seguro.
    /// </summary>
    public void Release(Object owner)
    {
        PruneDeadOwners();

        if (owner != null)
        {
            int i = IndexOfOwner(owner);
            if (i >= 0) m_Owners.RemoveAt(i);
        }

        ApplyWeight();
    }

    /// <summary>
    /// Suelta TODOS los dueños. Para cortes duros donde el estado anterior ya no aplica:
    /// cambio de escena, vuelta de un recuerdo, reinicio del clímax.
    /// </summary>
    public void ReleaseAll()
    {
        m_Owners.Clear();
        m_Suppressed = false;
        m_CurrentIntensity = m_DefaultBlurIntensity;
        ApplyWeight();
    }

    /// <summary>
    /// Oculta/devuelve el blur SIN tocar quién es dueño. Para efectos que necesitan la pantalla
    /// limpia un rato y después devolverla al estado que había (grieta de luz sobre la foto).
    /// </summary>
    public void Suppress(bool suppressed)
    {
        m_Suppressed = suppressed;
        ApplyWeight();
    }

    /// <summary>
    /// Override de intensidad (p. ej. animar la entrada del blur). Sólo se ve si hay algún dueño.
    /// </summary>
    public void SetBlurIntensity(float intensity)
    {
        m_CurrentIntensity = Mathf.Clamp01(intensity);
        ApplyWeight();
    }

    /// <summary>Vuelve la intensidad al default autorado.</summary>
    public void ResetBlurIntensity()
    {
        m_CurrentIntensity = m_DefaultBlurIntensity;
        ApplyWeight();
    }

    #endregion

    #region Interno

    private int IndexOfOwner(Object owner)
    {
        for (int i = 0; i < m_Owners.Count; i++)
            if (ReferenceEquals(m_Owners[i], owner)) return i;
        return -1;
    }

    /// <summary>
    /// Saca los dueños que ya fueron destruidos. Es lo que evita que el blur quede pegado para
    /// siempre cuando un sistema muere sin soltar su token.
    /// </summary>
    private void PruneDeadOwners()
    {
        for (int i = m_Owners.Count - 1; i >= 0; i--)
            if (m_Owners[i] == null) m_Owners.RemoveAt(i);
    }

    /// <summary>
    /// Maneja el blur de AfterPostProcess vía el weight del Volume (0 = off, 1 = full).
    /// Usar el weight (y no el parámetro del profile) evita mutar el asset compartido.
    /// No-opea limpio si m_BlurVolume no está cableado en el Editor.
    /// </summary>
    private void ApplyWeight()
    {
        if (m_BlurVolume == null) return;
        m_BlurVolume.weight = IsActive ? Mathf.Clamp01(m_CurrentIntensity) : 0f;
    }

    #endregion
}
