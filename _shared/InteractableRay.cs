using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class InteractableRay : MonoBehaviour, IStateCheckable
{

    [SerializeField] private Transform cam;
    [SerializeField] private float rayLength;
    [Tooltip("Absolute max raycast distance. Must be >= rayLength. Objects beyond their own InteractionRange are filtered out.")]
    [SerializeField] private float maxRayLength = 5f;
    private bool isInteracting;
    private GameObject lastHitObject;
    private DraggableObject currentDraggableObject;
    /// <summary>Último ícono mostrado, para no re-disparar la animación del canvas cada frame.</summary>
    private Icons lastIconShown = Icons.None;

    // --- Hints 2D de hover (barra de abajo). Ver IHoverUiHint. ---
    private IHoverUiHint currentHoverHintProvider;
    private bool hoverHintsApplied;
    private readonly List<HintInstance> activeHoverHints = new List<HintInstance>();

    private InspectObject inspectObject;

    [Header("Layers")]
    [SerializeField] private LayerMask ignoreLayers;
    [SerializeField] private LayerMask inspectLayer;
    [SerializeField] private LayerMask interactableLayer;
    private LayerMask combinedLayerMask;

    private static readonly List<GameState> StatesToCheck = new List<GameState>
    {
        GameState.Interacting, GameState.Paused, GameState.InCinematic, GameState.InventoryOpen
    };

    private static readonly WaitForSeconds InteractCooldown = new WaitForSeconds(0.2f);

    private static int dragLayerMaskCached = -1;
    private static int DragLayerMask
    {
        get
        {
            if (dragLayerMaskCached == -1)
                dragLayerMaskCached = LayerMask.GetMask("Interactable") | LayerMask.GetMask("Door");
            return dragLayerMaskCached;
        }
    }

    private void Awake()
    {
        inspectObject = GetComponent<InspectObject>();
        inspectObject.enabled = false;
        combinedLayerMask = interactableLayer | inspectLayer;

        if (maxRayLength < rayLength)
        {
            maxRayLength = rayLength;
        }
    }

    private bool subscribedToCam;

    private void OnEnable()
    {
        TrySubscribeCam();
    }

    private void Start()
    {
        // Fallback anti-race: si OnEnable corrió antes del Awake de MoveCamPuzzles (ambos viven en
        // el Player.prefab spawneado/DDOL, orden Awake/OnEnable indefinido), Instance era null y la
        // sub se salteaba para siempre → el ícono de interacción no se ocultaba en puzzles con
        // cámara bloqueada. Start corre después de todos los Awake.
        TrySubscribeCam();
    }

    private void OnDisable()
    {
        if (subscribedToCam && MoveCamPuzzles.Instance != null)
        {
            MoveCamPuzzles.Instance.OnCameraMoved -= HideInteractionIcon;
            subscribedToCam = false;
        }

        HideInteractionIcon();
        ClearHoverHints();   // si no, el "[E] Cerrar" queda pegado en la barra al desactivar el ray
    }

    private void TrySubscribeCam()
    {
        if (subscribedToCam || MoveCamPuzzles.Instance == null) return;
        MoveCamPuzzles.Instance.OnCameraMoved += HideInteractionIcon;
        subscribedToCam = true;
    }

    private void HideInteractionIcon()
    {
        if (InteractionsIconsCanvas3D.instance != null)
            InteractionsIconsCanvas3D.instance.HideIcon();
    }

    private void Update()
    {
        HandleRaycast();
    }

    public float GetRayLenght()
    {
        return rayLength;
    }

    // ── Caché de componentes del objeto apuntado ──────────────────────────────
    // El raycast corre por frame y resolvía hasta 3 TryGetComponent por frame, dos de ellos por
    // INTERFAZ (bastante más caros que por clase), mientras mirás cualquier cosa. Los componentes no
    // cambian mientras sigas mirando el mismo objeto: se resuelven una vez por objeto.
    private GameObject cacheHitObject;
    private InteractableObject cacheInteractable;
    private IHoverUiHint cacheHoverHint;
    private IInteractable3DHint cacheHint3D;

    /// <summary>
    /// Un componente puede DESTRUIRSE mientras el jugador lo sigue mirando (DoorWithKey se autodestruye
    /// al desbloquear la puerta). Sobre una referencia de tipo INTERFAZ, `== null` es el operador de C#
    /// y NO el de Unity, así que un componente destruido daría "no es null": hay que castear a
    /// UnityEngine.Object para detectarlo.
    /// </summary>
    private static bool EstaVivo(object componente)
        => componente is UnityEngine.Object obj ? obj != null : componente != null;

    private void RefrescarCacheDeComponentes(GameObject hitObject)
    {
        // Se revalida también si el objeto es el mismo pero alguno de sus componentes murió.
        if (hitObject == cacheHitObject
            && (cacheHoverHint == null || EstaVivo(cacheHoverHint))
            && (cacheHint3D == null || EstaVivo(cacheHint3D)))
            return;

        cacheHitObject = hitObject;
        cacheInteractable = null;
        cacheHoverHint = null;
        cacheHint3D = null;

        if (hitObject == null) return;

        hitObject.TryGetComponent(out cacheInteractable);
        hitObject.TryGetComponent(out cacheHoverHint);
        hitObject.TryGetComponent(out cacheHint3D);
    }

    private void HandleRaycast()
    {
        if (Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, maxRayLength, ~ignoreLayers))
        {
            GameObject hitObject = hit.collider.gameObject;

            if ((combinedLayerMask & (1 << hitObject.layer)) != 0)
            {
                RefrescarCacheDeComponentes(hitObject);

                float effectiveRange = rayLength;
                if (cacheInteractable != null && cacheInteractable.InteractionRange > 0f)
                {
                    effectiveRange = cacheInteractable.InteractionRange;
                }

                if (hit.distance > effectiveRange)
                {
                    ResetInteraction();
                    return;
                }

                HandleInteractions(hit);

                if (hitObject != lastHitObject)
                {
                    HandleNewHitObject(hitObject);
                }
                else if (currentDraggableObject != null && !currentDraggableObject.enabled && currentDraggableObject.CanDisable())
                {
                    currentDraggableObject.enabled = true;
                    ShowHint(hitObject, force: true);
                }
                else
                {
                    // Mismo objeto: refrescar el hint. Hace falta porque hay objetos cuyo ícono
                    // depende de su ESTADO y no de su identidad (una PhysicDoor ofrece "cerrar"
                    // solo mientras está abierta), y porque el objeto puede moverse mientras lo
                    // mirás (una puerta que gira). ShowIcon solo se re-dispara si el ícono cambió:
                    // llamarlo cada frame reiniciaría la animación del canvas.
                    ShowHint(hitObject, force: false);
                }

                UpdateHoverHints(hitObject);
                return;
            }
        }

        ResetInteraction();
    }

    private void HandleNewHitObject(GameObject hitObject)
    {
        if (currentDraggableObject != null && !currentDraggableObject.IsDragging() && currentDraggableObject.CanDisable())
        {
            currentDraggableObject.enabled = false;
            currentDraggableObject = null;
        }

        lastHitObject = hitObject;

        if (hitObject.TryGetComponent(out DraggableObject draggable))
        {
            currentDraggableObject = draggable;
            draggable.enabled = true;
        }

        ShowHint(hitObject, force: true);
    }

    /// <summary>
    /// Sincroniza los hints 2D de hover (la barra de abajo) con lo que el jugador está mirando.
    /// Usa AddAdditionalHint/RemoveAdditionalHint y NO ShowHints, para sumarse a lo que ya haya
    /// en pantalla en vez de pisarlo.
    ///
    /// <c>HasHoverHints</c> se consulta por frame (barata); <c>BuildHoverHints</c> solo en el
    /// flanco, porque aloca — un rebuild por frame sería basura en el hot path.
    /// </summary>
    private void UpdateHoverHints(GameObject hitObject)
    {
        RefrescarCacheDeComponentes(hitObject);
        IHoverUiHint provider = cacheHoverHint;

        // Si el jugador no puede interactuar (menú, pausa, cinemática, inventario, examinar),
        // tampoco anunciamos la interacción.
        bool canInteractNow = !inspectObject.enabled
            && GameManager.instance != null
            && GameManager.instance.CanToggleState(GetStatesToCheck());

        if (!canInteractNow || provider == null || !provider.HasHoverHints)
        {
            ClearHoverHints();
            return;
        }

        if (provider == currentHoverHintProvider && hoverHintsApplied) return;   // ya está puesto

        ClearHoverHints();
        if (UIButtonsManager.instance == null) return;

        currentHoverHintProvider = provider;
        hoverHintsApplied = true;

        List<HintDisplayData> hints = provider.BuildHoverHints();
        if (hints == null) return;
        for (int i = 0; i < hints.Count; i++)
        {
            HintInstance inst = UIButtonsManager.instance.AddAdditionalHint(hints[i]);
            if (inst != null) activeHoverHints.Add(inst);
        }
    }

    private void ClearHoverHints()
    {
        if (UIButtonsManager.instance != null)
        {
            for (int i = 0; i < activeHoverHints.Count; i++)
                UIButtonsManager.instance.RemoveAdditionalHint(activeHoverHints[i]);
        }
        activeHoverHints.Clear();
        currentHoverHintProvider = null;
        hoverHintsApplied = false;
    }

    /// <summary>
    /// Muestra/actualiza el hint 3D del objeto apuntado. Con <paramref name="force"/> false solo
    /// re-dispara ShowIcon si el ícono cambió (si no, la animación del canvas se reiniciaría cada
    /// frame); la posición sí se actualiza siempre, para objetos que se mueven mientras los mirás.
    /// </summary>
    private void ShowHint(GameObject hitObject, bool force)
    {
        if (InteractionsIconsCanvas3D.instance == null) return;
        if (currentDraggableObject != null && currentDraggableObject.IsDragging()) return;
        RefrescarCacheDeComponentes(hitObject);
        IInteractable3DHint hint = cacheHint3D;
        if (hint == null) return;

        Icons icon = hint.GetIconType();

        if (force || icon != lastIconShown)
        {
            InteractionsIconsCanvas3D.instance.ShowIcon(hint.GetHintPosition(), icon);
            lastIconShown = icon;
        }
        else
        {
            InteractionsIconsCanvas3D.instance.UpdatePos(hint.GetHintPosition());
        }
    }

    private void ResetInteraction()
    {
        if (currentDraggableObject != null)
        {
            if (!currentDraggableObject.IsDragging() && currentDraggableObject.CanDisable())
            {
                currentDraggableObject.enabled = false;
                currentDraggableObject = null;
            }
        }

        if (InteractionsIconsCanvas3D.instance != null)
        {
            InteractionsIconsCanvas3D.instance.HideIcon();
        }

        lastHitObject = null;
        lastIconShown = Icons.None;
        ClearHoverHints();
    }

    private void HandleInteractions(RaycastHit hit)
    {
        // While InspectObject is active the player is in examine mode.
        // LMB in that mode drives rotation (RotateInspectHeld) via InspectObject — not ray interaction.
        if (inspectObject.enabled)
            return;

        if (InputHandler.instance.PrimaryInteractPressed)
        {
            if (InteractionsMenuManager.instance != null && InteractionsMenuManager.instance.OpenedMenu())
                return;

            if (!GameManager.instance.CanToggleState(GetStatesToCheck()))
                return;

            if (IsOnLayer(hit.transform.gameObject, inspectLayer))
            {
                inspectObject.enabled = true;
                inspectObject.ActiveExamine(hit.transform);
            }
            if (hit.collider.gameObject.TryGetComponent(out IInteractable interactableObj) && !isInteracting)
            {
                StartCoroutine(Interacting());
                interactableObj.Interact();
            }
        }
    }

    public void Clean()
    {
        lastHitObject = null;
        currentDraggableObject = null;
        lastIconShown = Icons.None;
    }

    /// <summary>
    /// Checks if the given DraggableObject is still in front of the player camera
    /// within interaction range. Used by DraggableObject to validate drag start.
    /// </summary>
    public bool CanStartDrag(DraggableObject draggableObject)
    {
        Ray ray = new Ray(cam.position, cam.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, rayLength, DragLayerMask))
        {
            if (hit.collider.TryGetComponent<DraggableObject>(out var drag))
            {
                return drag == draggableObject;
            }
        }
        return false;
    }

    private bool IsOnLayer(GameObject obj, LayerMask layer)
    {
        return (layer.value & (1 << obj.layer)) != 0;
    }

    private IEnumerator Interacting()
    {
        isInteracting = true;
        yield return InteractCooldown;
        isInteracting = false;
    }

    public List<GameState> GetStatesToCheck()
    {
        return StatesToCheck;
    }
}
