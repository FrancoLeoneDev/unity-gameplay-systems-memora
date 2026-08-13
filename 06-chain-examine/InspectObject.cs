using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class InspectObject : MonoBehaviour, IUiHint
{
    public static InspectObject instance { get; private set; }

    public event Action OnExamineEnded;
    public event Action<PhotoData, bool> OnPhotoContextSet;

    private bool pauseRotation = false;
    private bool examine;
    private bool movingObject = false;

    private InspectionRaycast inspectionRaycast;

    private IKeyItem keyItem;
    private Action OnExitExamine;

    private Coroutine moveCoroutine;
    private Tween moveTween;

    [SerializeField] private Transform placeObject;
    [SerializeField] private float rotateSpeed;
    [SerializeField, Tooltip("Roll speed (deg/sec) for A/D keyboard rotation around the view axis. Negative flips direction.")]
    private float rollSpeed = 120f;
    // ── ZOOM DE INSPECCIÓN ────────────────────────────────────────────────────────────────
    // El zoom mueve el placeObject sobre su Z LOCAL = distancia del objeto a la cámara, en METROS.
    // El rig autora el placeObject DELANTE de la cámara, o sea Z POSITIVA (~0.5 m).
    //
    // Los campos se llamaban minZoom/maxZoom y guardaban GRADOS DE FOV del sistema viejo de zoom
    // por cámara (40 y 80). El refactor a una sola cámara los reinterpretó como Z sin re-tunearlos,
    // así que el primer notch de rueda clampeaba el objeto a Z=-40 → 40 metros DETRÁS de la cámara
    // → desaparecía, y R (que sólo resetea rotación) no lo traía de vuelta.
    // Renombrados a propósito: el dato viejo no debe sobrevivir al cambio de unidad.
    [SerializeField, Tooltip("Metros por notch de rueda. El rango útil es de centímetros: 0.5 mueve " +
        "~5 cm por notch.")]
    private float zoomSpeed = 0.5f;
    [SerializeField, Tooltip("Distancia MÍNIMA del objeto a la cámara, en metros (lo más cerca que " +
        "se puede acercar con la rueda).")]
    private float minZoomDistance = 0.25f;
    [SerializeField, Tooltip("Distancia MÁXIMA del objeto a la cámara, en metros (lo más lejos que " +
        "se puede alejar con la rueda).")]
    private float maxZoomDistance = 1.2f;

    // Z local del placeObject al empezar la inspección. Se restaura al salir y al apretar R.
    private float originalZoomZ;

    private Transform inspectedObjectTransform;
    private Vector3 originalPlaceObjectPosition;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private LayerMask originalLayer;
    private Collider[] objectColliders;
    private bool[] originalTriggerStates;

    private bool chainExamine = false;
    private bool exitLocked;

    [SerializeField] private GameObject inspectLight;

    [Header("InspectionUI")]
    private bool displayUI;
    private Action onInspectionUIFinished;
    private bool isUIMode;

    private PhotoData currentPhotoData;

    public bool ExamineProp => examine;
    public PhotoData CurrentPhotoData => currentPhotoData;
    public bool IsUIMode => isUIMode;
    public Transform InspectedObjectTransform => inspectedObjectTransform;

    private void Awake()
    {
        instance = this;
        originalPlaceObjectPosition = placeObject.localPosition;
        inspectionRaycast = GetComponent<InspectionRaycast>();

        // Register the shared inspection light so every system (objects, notes, inventory,
        // doors/puzzles) drives it through InspectionLightService instead of its own ref.
        InspectionLightService.Register(inspectLight);
    }

    private void OnEnable()
    {
        GameManager.instance.SetInteracting(true);
    }

    public List<HintDisplayData> GetHintDisplayDataList()
    {
        var list = new List<HintDisplayData>()
        {
            HintDisplayData.Make(HintType.Rotate, HintIconType.LeftClickIcon),
            HintDisplayData.Make(HintType.Reset, HintIconType.RIcon)
        };

        if (exitLocked)
            return list;

        if (keyItem != null)
        {
            list.Add(HintDisplayData.Make(HintType.Save, HintIconType.RightClickIcon));
        }
        else
        {
            list.Add(HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon));
        }

        return list;
    }

    private void OnDisable()
    {
        pauseRotation = false;
        CleanupMoveAnimation();
    }

    private void Update()
    {
        if (!pauseRotation)
        {
            HandleObjectRotation();
        }
        HandleExamine();
    }

    /// <summary>
    /// Kills any active move tween/coroutine and resets movingObject flag.
    /// Must be called in ALL exit/disable paths to prevent stale state.
    /// </summary>
    private void CleanupMoveAnimation()
    {
        if (moveCoroutine != null)
        {
            StopCoroutine(moveCoroutine);
            moveCoroutine = null;
        }

        if (moveTween != null && moveTween.IsActive())
        {
            moveTween.Kill();
            moveTween = null;
        }

        movingObject = false;
    }

    private IEnumerator MoveObjectToInspectPosition(Transform reference)
    {
        movingObject = true;
        yield return new WaitForSeconds(0.1f);

        if (reference == null)
        {
            movingObject = false;
            moveCoroutine = null;
            yield break;
        }

        moveTween = reference.DOMove(placeObject.position, 0.5f)
            .SetEase(Ease.OutQuad)
            .OnComplete(() => {
                examine = true;
                movingObject = false;
                moveTween = null;
                moveCoroutine = null;
            });
    }

    private IEnumerator MovePlaceObjectPositionUI(Vector3 targetPos)
    {
        movingObject = true;

        moveTween = placeObject.DOLocalMove(targetPos, 0.5f).SetEase(Ease.InOutSine).OnComplete(() =>
        {
            examine = true;
            movingObject = false;
            moveTween = null;
            moveCoroutine = null;
        });

        yield break;
    }

    public void ActiveExamine(Transform obj)
    {
        // Safety reset — clean any stale state from interrupted previous inspection
        CleanupMoveAnimation();
        examine = false;

        isUIMode = false;
        inspectedObjectTransform = obj;

        float inspectionIntensity = 0f;
        bool isMetallic = false;
        if (obj.TryGetComponent<IInspectable>(out var inspectable))
        {
            float customDistance = -inspectable.GetCustomDistance();
            placeObject.localPosition = new Vector3(placeObject.localPosition.x, placeObject.localPosition.y, placeObject.localPosition.z + customDistance);
            OnExitExamine = inspectable.SetOnExitExamine;
            inspectedObjectTransform = inspectable.Pivot();
            inspectionIntensity = inspectable.InspectionLightIntensity;
            isMetallic = inspectable.IsMetallic;
        }
        if (obj.TryGetComponent<IInspectionDialogue>(out var dialogue))
        {
            dialogue.PlayDialogue();
        }

        // Chain examine is specific to InspectableInteractableObject
        if (obj.TryGetComponent<InspectableInteractableObject>(out var inspObj) && inspObj.StartsChainExamine)
        {
            // Sólo trabar la salida si de verdad hay un sub-elemento para interactuar. El chain examine
            // bloquea ExitExamine hasta que elegís uno: si no hay ninguno disponible (el cuadro caído
            // cuya foto todavía no se reveló), el jugador quedaba encerrado en el examine PARA SIEMPRE,
            // sin ninguna acción posible y sin forma de soltar la cámara.
            chainExamine = HaySubElementoDisponible(obj);
        }

        if (ActiveInspectionRaycast())
        {
            inspectionRaycast.enabled = true;
        }
        DesactiveCollider(obj);
        SaveOriginalTransform(inspectedObjectTransform);

        // SINGLE-CAMERA: the object is rendered by PlayerCam (no RenderAbove / second camera).
        // Lighting isolation (LightLayer7 renderingLayerMask) is handled by InspectionLightService —
        // a SEPARATE mechanism, untouched here.
        InspectionLightService.Activate(inspectedObjectTransform, inspectionIntensity, isMetallic);

        // Move the object to the "InspectedObject" layer + enable the stencil stamp pass.
        // InspectedStencilPass (DrawRenderers CustomPass) stamps HDRP UserBit0 (stencil bit 6) on
        // its pixels; BackgroundBlur.shader reads that stencil to keep it sharp while the rest blurs.
        // Restored to the original layer on exit (ResetObjectTransform).
        if (s_InspectedObjectLayer < 0)
            s_InspectedObjectLayer = LayerMask.NameToLayer("InspectedObject");
        if (s_InspectedObjectLayer >= 0)
            ChangeObjectLayer(s_InspectedObjectLayer);
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(true);

        keyItem = obj.GetComponent<IKeyItem>();
        UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());
        SetBlur();
        moveCoroutine = StartCoroutine(MoveObjectToInspectPosition(inspectedObjectTransform));
        PlayerManager.instance.CanMove(false);
    }

    private void SetBlur()
    {
        if (BlurManager.instance != null)
        {
            // Save current placeObject local Z as the zoom baseline (single-camera zoom path).
            originalZoomZ = placeObject.localPosition.z;
            // La inspección toma el blur a su nombre. Si el inventario o la pausa ya lo tenían,
            // ambos tokens conviven y el blur se apaga recién cuando lo suelta el último.
            BlurManager.instance.Acquire(this);
        }
    }

    private static int examineLayer = -1;

    // Cached "InspectedObject" GameObject layer. The inspected object is moved here during
    // inspection so InspectedStencilPass writes its pixels into the _InspectedMaskTex mask
    // texture for the single-camera blur to keep it sharp. Restored to the original layer on exit.
    private static int s_InspectedObjectLayer = -1;

    private void DesactiveCollider(Transform obj)
    {
        if (examineLayer < 0)
            examineLayer = LayerMask.NameToLayer("InspectionRaycast");

        objectColliders = obj.GetComponentsInChildren<Collider>();
        originalTriggerStates = new bool[objectColliders.Length];

        for (int i = 0; i < objectColliders.Length; i++)
        {
            originalTriggerStates[i] = objectColliders[i].isTrigger;
            objectColliders[i].isTrigger = true;
        }

        var rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
            Destroy(rb);
    }

    /// <summary>
    /// ¿Hay algún sub-elemento que el InspectionRaycast pueda realmente tocar ahora mismo? Espeja su
    /// condición de hit: un IExamineInteractable activo y en la layer "InspectionRaycast". Los
    /// sub-elementos se habilitan en runtime (ActivateForExamine les pone la layer y el collider), así
    /// que al entrar al examine puede no haber ninguno todavía.
    /// </summary>
    private bool HaySubElementoDisponible(Transform obj)
    {
        if (examineLayer < 0)
            examineLayer = LayerMask.NameToLayer("InspectionRaycast");

        var elementos = obj.GetComponentsInChildren<IExamineInteractable>(true);
        for (int i = 0; i < elementos.Length; i++)
        {
            if (elementos[i] is not UnityEngine.Component componente || componente == null) continue;
            if (!componente.gameObject.activeInHierarchy) continue;
            if (componente.gameObject.layer != examineLayer) continue;
            return true;
        }
        return false;
    }

    private void RestoreColliders()
    {
        if (objectColliders == null) return;

        for (int i = 0; i < objectColliders.Length; i++)
        {
            if (objectColliders[i] == null) continue;
            objectColliders[i].isTrigger = i < originalTriggerStates.Length
                ? originalTriggerStates[i]
                : false;
        }

        objectColliders = null;
        originalTriggerStates = null;
    }

    public void ActiveExamineUI(Transform obj, Action onInspectionUIFinished = null)
    {
        if (examine)
            return;

        isUIMode = true;
        UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());

        Vector3 targetPos;
        float uiInspectionIntensity = 0f;
        if (obj.TryGetComponent<IInspectable>(out var inspectable))
        {
            float customDistance = -inspectable.GetCustomDistance();
            targetPos = new(placeObject.localPosition.x, placeObject.localPosition.y, placeObject.localPosition.z + customDistance);
            inspectedObjectTransform = inspectable.Pivot();
            uiInspectionIntensity = inspectable.InspectionLightIntensity;
        }
        else
        {
            targetPos = new(placeObject.localPosition.x, placeObject.localPosition.y, placeObject.localPosition.z);
        }
        CleanupMoveAnimation();
        examine = false;
        moveCoroutine = StartCoroutine(MovePlaceObjectPositionUI(targetPos));
        this.onInspectionUIFinished = onInspectionUIFinished;
        displayUI = true;
        inspectedObjectTransform = obj;
        SaveOriginalTransform(inspectedObjectTransform);
        InspectionLightService.Activate(inspectedObjectTransform, uiInspectionIntensity);

        // Single-camera: stamp the previewed item so BackgroundBlur keeps it SHARP, exactly like
        // world inspection (otherwise the inventory 3D preview blurs with the background). The layer
        // is restored in ExitInspectUI; the stencil pass is disabled in ExitExamine's shared tail.
        if (s_InspectedObjectLayer < 0)
            s_InspectedObjectLayer = LayerMask.NameToLayer("InspectedObject");
        if (s_InspectedObjectLayer >= 0)
            ChangeObjectLayer(s_InspectedObjectLayer);
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(true);

        if (BlurManager.instance != null)
        {
            originalZoomZ = placeObject.localPosition.z;
        }
    }

    private void HandleObjectRotation()
    {
        if (!examine || inspectedObjectTransform == null)
            return;

        // Mouse (hold LMB): yaw around world up + pitch around the camera's right axis.
        // MouseDelta es el delta CRUDO en píxeles — la sensibilidad se ajusta con rotateSpeed.
        if (InputHandler.instance.RotateInspectHeld)
        {
            float deltaX = InputHandler.instance.MouseDelta.x * rotateSpeed * Time.deltaTime;
            float deltaY = InputHandler.instance.MouseDelta.y * rotateSpeed * Time.deltaTime;
            Transform orientationTransform = PlayerManager.instance.GetOrientation();

            Quaternion yRotation = Quaternion.AngleAxis(-deltaX, Vector3.up);
            Quaternion xRotation = Quaternion.AngleAxis(deltaY, orientationTransform.right);

            inspectedObjectTransform.rotation = yRotation * xRotation * inspectedObjectTransform.rotation;
        }

        // Keyboard A/D: ROLL around the camera (view) axis — the third axis the mouse can't reach.
        float roll = InputHandler.instance.RollInput;
        if (!Mathf.Approximately(roll, 0f))
        {
            Quaternion zRotation = Quaternion.AngleAxis(roll * rollSpeed * Time.deltaTime, GetCameraForward());
            inspectedObjectTransform.rotation = zRotation * inspectedObjectTransform.rotation;
        }
    }

    private bool ActiveInspectionRaycast()
    {
        if (inspectedObjectTransform == null)
            return false;

        if (inspectedObjectTransform.gameObject.layer == LayerMask.NameToLayer("InspectionRaycast"))
        {
            inspectionRaycast.enabled = true;
            return true;
        }
        foreach (Transform child in inspectedObjectTransform)
        {
            if (child.gameObject.layer == LayerMask.NameToLayer("InspectionRaycast"))
            {
                return true;
            }
        }
        return false;
    }

    private void HandleExamine()
    {
        if (InputHandler.instance.CancelPressed && examine)
        {
            ExitExamine();
        }

        if (examine)
        {
            AdjustZoom();

            if (InputHandler.instance.ResetInspectPressed && !pauseRotation)
            {
                StartCoroutine(ResetObjectRotationWithAnimation());
            }
        }
    }

    private void AdjustZoom()
    {
        if (pauseRotation) return;

        // El legacy Input.GetAxis("Mouse ScrollWheel") devolvía ~±0.1 por notch.
        // <Mouse>/scroll/y devuelve ~±120 por notch: dividir por 120 lo normaliza de vuelta a ±0.1.
        float scrollNormalized = InputHandler.instance.ScrollDelta / 120f;
        if (Mathf.Approximately(scrollNormalized, 0f)) return;

        Vector3 localPos = placeObject.localPosition;

        // Rueda hacia ARRIBA (scroll positivo) = acercar = MENOS distancia, por eso el signo menos.
        float newZ = localPos.z - scrollNormalized * zoomSpeed;
        newZ = Mathf.Clamp(newZ, minZoomDistance, maxZoomDistance);

        placeObject.localPosition = new Vector3(localPos.x, localPos.y, newZ);
    }

    /// <summary>
    /// Resetea la VISTA de inspección: rotación Y zoom vuelven a como arrancó.
    /// Antes sólo reseteaba la rotación, así que si el zoom te dejaba el objeto fuera de cuadro,
    /// R no lo recuperaba y había que salir y volver a inspeccionar.
    /// </summary>
    public IEnumerator ResetObjectRotationWithAnimation()
    {
        RestorePlaceObjectZoom();

        yield return inspectedObjectTransform
            .DORotateQuaternion(originalRotation, 1f)
            .WaitForCompletion();
    }

    public void SetPhotoContext(PhotoData photoData, bool isUI)
    {
        currentPhotoData = photoData;
        OnPhotoContextSet?.Invoke(photoData, isUI);
    }

    public void LockExit(bool locked)
    {
        exitLocked = locked;
    }

    public void ForceExitForMemoryEntry()
    {
        // Kill active move animation and reset movingObject flag
        CleanupMoveAnimation();

        // Kill any other DOTween animations on these targets
        if (inspectedObjectTransform != null)
        {
            DOTween.Kill(inspectedObjectTransform);
        }
        DOTween.Kill(placeObject);

        if (!displayUI)
        {
            // World mode cleanup
            if (keyItem != null)
            {
                // Guardado ad-hoc al extraer un item-llave: gateado para respetar el set-piece
                // (durante el loop del hospital no se guarda) — ver SaveManager.TrySave/CanSaveNow.
                SaveManager.Instance?.TrySave();
                if (inspectedObjectTransform != null)
                    Destroy(inspectedObjectTransform.gameObject);
                keyItem = null;
            }

            // Do NOT invoke OnExitExamine — memory entry should not trigger exit side effects
            OnExitExamine = null;
            InspectionLightService.Deactivate();
            RestoreColliders();

            // Restore placeObject Z to the saved baseline.
            // Do NOT deactivate blur — TransitionToPhotoScene handles it after FadeIn.
            RestorePlaceObjectZoom();
        }
        else
        {
            // UI mode cleanup
            displayUI = false;
            InspectionLightService.Deactivate();
            RestorePlaceObjectZoom();
        }

        // Common cleanup for both modes
        examine = false;
        chainExamine = false;

        if (UIButtonsManager.instance != null)
        {
            UIButtonsManager.instance.HideHints();
        }

        currentPhotoData = null;
        exitLocked = false;
        onInspectionUIFinished = null;
        placeObject.localPosition = originalPlaceObjectPosition;
        inspectionRaycast.enabled = false;

        inspectedObjectTransform = null;
        OnExamineEnded?.Invoke();
        enabled = false;
    }

    private void ExitExamine()
    {
        if (exitLocked)
            return;

        if (chainExamine)
            return;

        CleanupMoveAnimation();
        examine = false;
        GameManager.instance.SetInteracting(false);

        if (UIButtonsManager.instance != null)
        {
            UIButtonsManager.instance.HideHints();
        }

        if (!displayUI)
        {
            PlayerManager.instance.CanMove(true);
            ResetObjectTransform();

            RestoreColliders();

            bool extrajoItemLlave = keyItem != null;
            if (extrajoItemLlave)
            {
                if (inspectedObjectTransform != null)
                    Destroy(inspectedObjectTransform.gameObject);
                keyItem = null;
            }

            RestorePlaceObjectZoom();

            OnExitExamine?.Invoke();
            OnExitExamine = null;
            InspectionLightService.Deactivate();

            // Guardado ad-hoc al extraer un item-llave: gateado para respetar el set-piece
            // (durante el loop del hospital no se guarda) — ver SaveManager.TrySave/CanSaveNow.
            // Va DESPUÉS de OnExitExamine a propósito: ese callback es el que aplica las consecuencias
            // de haber sacado la llave (la caja de música abre la puerta blanca, se destraba la salida).
            // Guardando antes, el slot quedaba con la llave ya tomada pero la puerta todavía cerrada, y
            // como la caja no se puede volver a jugar, el Continue dejaba al jugador encerrado.
            if (extrajoItemLlave)
                SaveManager.Instance?.TrySave();
        }
        else
        {
            InspectionLightService.Deactivate();
            ExitInspectUI();
        }

        // Soltar el blur en LAS DOS ramas. Antes vivía dentro del if (!displayUI), así que salir de
        // una inspección abierta desde el inventario no lo soltaba nunca y quedaba pegado.
        // Con propiedad por dueño esto es seguro: si el inventario sigue abierto, su propio token
        // mantiene el blur encendido y no se apaga de más.
        if (BlurManager.instance != null)
            BlurManager.instance.Release(this);

        // Normal exit: disable the stencil stamp so the object blurs like everything else next time.
        // (Deliberately NOT done in ForceExitForMemoryEntry — the photo transition keeps it ON;
        //  PhotoMemoryPortal turns it off after the cut to black.)
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(false);

        currentPhotoData = null;
        inspectedObjectTransform = null;

        // SOLO la rama mundo snapea. En la rama UI, ExitInspectUI() ya arrancó un DOLocalMove de 0.8s
        // hacia esta misma posición: snapear acá corría en el MISMO frame, así que el tween capturaba
        // su valor inicial ya en el destino y la animación de cierre no se veía nunca. displayUI sigue
        // en true hasta el OnComplete del tween, así que sirve como discriminante acá.
        if (!displayUI)
            placeObject.localPosition = originalPlaceObjectPosition;

        inspectionRaycast.enabled = false;
        OnExamineEnded?.Invoke();
        enabled = false;
    }

    private void ExitInspectUI()
    {
        Sequence returnSequence = DOTween.Sequence();

        // Capture the preview object + its original layer locally: ExitExamine's shared tail nulls
        // inspectedObjectTransform before this async tween's OnComplete fires, so we must not read
        // the field there. Restores the layer the single-camera path moved to "InspectedObject".
        Transform uiObj = inspectedObjectTransform;
        int restoreLayer = originalLayer;

        returnSequence
            .Join(inspectedObjectTransform.DORotateQuaternion(originalRotation, 1f))
            .Join(placeObject.DOLocalMove(originalPlaceObjectPosition, 0.8f))
            .OnComplete(() =>
            {
                displayUI = false;
                if (uiObj != null)
                    uiObj.SetLayerRecursively(restoreLayer);
                onInspectionUIFinished?.Invoke();
            });
    }

    /// <summary>
    /// Restores placeObject local Z to the value saved at inspection start,
    /// undoing any scroll-wheel zoom applied during examination.
    /// Called on all exit paths (ExitExamine, ForceExitForMemoryEntry).
    /// </summary>
    private void RestorePlaceObjectZoom()
    {
        Vector3 localPos = placeObject.localPosition;
        placeObject.localPosition = new Vector3(localPos.x, localPos.y, originalZoomZ);
    }

    private void SaveOriginalTransform(Transform obj)
    {
        originalPosition = obj.position;
        originalRotation = obj.rotation;
        originalLayer = obj.gameObject.layer;
    }

    private void ResetObjectTransform()
    {
        if (inspectedObjectTransform == null) return;
        inspectedObjectTransform.position = originalPosition;
        inspectedObjectTransform.rotation = originalRotation;
        // SINGLE-CAMERA: restore layer to what it was (no longer changed to RenderAbove,
        // but chain-examine may have changed it so restore is still correct here).
        ChangeObjectLayer(originalLayer);
    }

    private void ChangeObjectLayer(int newLayer)
    {
        if (inspectedObjectTransform == null) return;
        inspectedObjectTransform.SetLayerRecursively(newLayer);
    }

    public void PauseRotation(bool _bool)
    {
        pauseRotation = _bool;
    }

    /// <summary>
    /// Ends the current chain examine sequence, allowing the player to exit inspection.
    /// Call from the terminal element of a chain (e.g., after picking up a key).
    /// </summary>
    public void EndChainExamine()
    {
        chainExamine = false;
    }

    /// <summary>
    /// Transitions inspection from the current container to a sub-element.
    /// Reparents the element out of the container, destroys the container,
    /// and makes the element the new inspected object.
    /// Used when a chain examine element needs to "become" the main inspection target
    /// (e.g., photo inside a broken frame).
    /// </summary>
    public void TransitionToChainElement(Transform element)
    {
        // Detach from container before destroying it
        Transform oldContainer = inspectedObjectTransform;
        element.SetParent(placeObject, worldPositionStays: true);

        // Destroy the old container
        if (oldContainer != null)
            Destroy(oldContainer.gameObject);

        // Clear stale references (container colliders are gone)
        objectColliders = null;

        // The element is now the inspected object.
        // SINGLE-CAMERA: Reisolate sets the lighting renderingLayerMask, and the element moves to
        // the "InspectedObject" layer so the stencil pass stamps it for the blur's sharp-exclusion
        // (same as ActiveExamine). The stencil pass is already enabled from ActiveExamine.
        inspectedObjectTransform = element;
        InspectionLightService.Reisolate(element);
        if (s_InspectedObjectLayer < 0)
            s_InspectedObjectLayer = LayerMask.NameToLayer("InspectedObject");
        if (s_InspectedObjectLayer >= 0)
            element.SetLayerRecursively(s_InspectedObjectLayer);
        keyItem = element.GetComponent<IKeyItem>();

        // End chain examine and disable sub-element raycast
        chainExamine = false;
        inspectionRaycast.enabled = false;

        // Update hints to reflect new state
        if (UIButtonsManager.instance != null)
            UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());
    }

    #region Front Face Alignment

    /// <summary>
    /// Returns the camera forward direction used for face-detection calculations.
    /// </summary>
    public Vector3 GetCameraForward()
    {
        if (PlayerManager.instance != null)
            return PlayerManager.instance.GetPlayerCam().transform.forward;
        return Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
    }

    /// <summary>
    /// Dot product between worldFaceNormal and camera direction.
    /// 1 = perfectly facing camera, -1 = facing away.
    /// </summary>
    public float GetFaceDot(Vector3 worldFaceNormal)
    {
        return Vector3.Dot(worldFaceNormal, -GetCameraForward());
    }

    /// <summary>
    /// Smoothly rotates the inspected object so that worldFaceNormal ends up
    /// pointing toward the camera. Calls onComplete when finished.
    /// If already facing closely enough, invokes onComplete immediately.
    /// Does NOT manage PauseRotation — caller handles that.
    /// </summary>
    public void AlignFaceToCamera(Vector3 worldFaceNormal, float duration, Action onComplete = null)
    {
        if (inspectedObjectTransform == null)
        {
            onComplete?.Invoke();
            return;
        }

        Vector3 targetDir = -GetCameraForward();

        if (Vector3.Dot(worldFaceNormal, targetDir) > 0.95f)
        {
            onComplete?.Invoke();
            return;
        }

        Quaternion delta = Quaternion.FromToRotation(worldFaceNormal, targetDir);
        Quaternion targetRotation = delta * inspectedObjectTransform.rotation;

        inspectedObjectTransform.DORotateQuaternion(targetRotation, duration)
            .SetEase(Ease.OutQuad)
            .OnComplete(() => onComplete?.Invoke());
    }

    #endregion
}
