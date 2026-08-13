// InputHandler.cs
// Implements: refactor-input-system-memora.md — CONTRATO FINAL (Fase 1)
// Single source of truth for all input. No other script reads Input.* or KeyCode directly.
// Context switching: PlayerMovement always active; Player/Menu/Puzzle maps swap per context.

using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

public class InputHandler : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------
    public static InputHandler instance { get; private set; }

    // -------------------------------------------------------------------------
    // Inspector-tunable fields
    // -------------------------------------------------------------------------
    [Header("Movement Smoothing")]
    [SerializeField] private float inputSmoothTime = 0.02f;

    // -------------------------------------------------------------------------
    // Context
    // -------------------------------------------------------------------------
    public enum InputContext { Player, Menu, Puzzle }

    // -------------------------------------------------------------------------
    // Backward-compatible read-only properties (replaces public fields)
    // External scripts that read these will still compile unchanged.
    // -------------------------------------------------------------------------
    public float horizontalInput { get; private set; }
    public float verticalInput   { get; private set; }
    public float moveAmount      { get; private set; }
    public float cameraInputX    { get; private set; }
    public float cameraInputY    { get; private set; }

    /// <summary>Raw horizontal movement axis (A/D). Reused for inspection ROLL while player movement is locked.</summary>
    public float RollInput       { get; private set; }

    /// <summary>True while the Sprint action is held. Sourced from the PlayerMovement map.</summary>
    public bool SprintHeld       { get; private set; }

    // Legacy button states — kept for backward compat with un-migrated scripts.
    // Cluster B–F scripts will stop reading these and use the new properties below.
    public bool e_Input     { get; private set; }
    public bool tab_Input   { get; private set; }
    public bool esc_Input   { get; private set; }
    public bool enter_Input { get; private set; }

    // -------------------------------------------------------------------------
    // Player context — polled properties (set once per Update)
    // -------------------------------------------------------------------------
    public bool    PrimaryInteractPressed    { get; private set; }
    public bool    CancelPressed             { get; private set; }
    public bool    RotateInspectHeld         { get; private set; }
    public Vector2 MouseDelta               { get; private set; }
    public float   ScrollDelta              { get; private set; }
    public bool    ResetInspectPressed      { get; private set; }
    public bool    FlipOrFlashlightPressed  { get; private set; }
    public bool    ToggleTranslationPressed { get; private set; }
    /// <summary>
    /// True while Space is held. No Hold interaction — PhotoMemoryPortal runs its own holdTimer.
    /// </summary>
    public bool    EnterMemoryHeld          { get; private set; }
    public bool    ConfirmPressed           { get; private set; }
    /// <summary>
    /// True the frame Space (Confirm action) is released.
    /// Backed by WasReleasedThisFrame() — used by TriggerDoctorCerrarPuertas (GetKeyUp(Space) migration).
    /// </summary>
    public bool    ConfirmReleased          { get; private set; }
    /// <summary>Enter key (PC puzzle confirm — same physical key as Enter action, different semantic map slot).</summary>
    public bool    EnterPressed             { get; private set; }

    // -------------------------------------------------------------------------
    // Pointer — raw left-mouse-button only (no E, no Hold).
    // Used exclusively by DraggableObject and DragWithLerp to detect drag
    // start/continue/end without the 0.05s Hold delay of RotateInspect.
    // -------------------------------------------------------------------------
    /// <summary>Raw LMB pressed this frame (no E binding, no Hold interaction). Use for drag-start detection.</summary>
    public bool    PointerPressed           { get; private set; }
    /// <summary>Raw LMB held (no E binding). Use for drag-continue detection.</summary>
    public bool    PointerHeld              { get; private set; }
    /// <summary>Raw LMB released this frame. Use for drag-end detection.</summary>
    public bool    PointerReleased          { get; private set; }

    // -------------------------------------------------------------------------
    // Menu context — polled properties
    // -------------------------------------------------------------------------
    public bool NavUpPressed        { get; private set; }
    public bool NavDownPressed      { get; private set; }
    public bool NavLeftPressed      { get; private set; }
    public bool NavRightPressed     { get; private set; }
    public bool MenuConfirmPressed  { get; private set; }
    public bool MenuCancelPressed   { get; private set; }
    public bool CycleLeftPressed    { get; private set; }
    public bool CycleRightPressed   { get; private set; }
    public bool FlipNotePressed     { get; private set; }
    /// <summary>
    /// LMB held past 0.05s threshold while in Menu context (rotación 3D del preview en el menú).
    /// Backed by the Menu/RotateInspect action — same LMB+Hold binding as Player/RotateInspect
    /// but active only when the Menu map is enabled.
    /// </summary>
    public bool    MenuRotateInspectHeld { get; private set; }
    /// <summary>
    /// Raw mouse delta while in Menu context (rotación 3D del preview en el menú).
    /// Equivalent in magnitude to Input.GetAxis("Mouse X/Y") at default sensitivity;
    /// no additional scaling coefficient needed — rotationSpeed * Time.deltaTime remains correct.
    /// </summary>
    public Vector2 MenuMouseDelta        { get; private set; }

    // -------------------------------------------------------------------------
    // Puzzle context — polled properties
    // -------------------------------------------------------------------------
    public float PuzzleNavHorizontal  { get; private set; }
    public float PuzzleNavVertical    { get; private set; }
    public bool  PuzzleConfirmPressed { get; private set; }
    public bool  PuzzleCancelPressed  { get; private set; }
    public bool  PuzzleCycleLeftPressed { get; private set; }

    // -------------------------------------------------------------------------
    // One-shot global events (fire from callbacks; consumed by managers)
    // -------------------------------------------------------------------------
    /// <summary>Fired when ToggleInventory is performed. Alias kept: OnInventoryOpenPressed.</summary>
    public event Action OnInventoryToggle;
    public event Action OnInventoryOpenPressed; // backward-compat alias

    /// <summary>Fired when TogglePause is performed.</summary>
    public event Action OnPauseToggle;

    /// <summary>Fired when ToggleMap is performed.</summary>
    public event Action OnMapToggle;

    /// <summary>Fired when ToggleFlashlight is performed.</summary>
    public event Action OnFlashlightToggle;

    /// <summary>Backward-compat alias: fired when Interact (E/LMB) is performed.</summary>
    public event Action OnInteractPressed;

    // -------------------------------------------------------------------------
    // Private — controls asset and cached InputAction refs
    // -------------------------------------------------------------------------
    private PlayerControls _controls;

    // PlayerMovement
    private InputAction _movement;
    private InputAction _camera;
    private InputAction _sprint; // cached via FindAction — see CacheActionRefs

    // PlayerActions — legacy buttons (preserved by name)
    private InputAction _interact;
    private InputAction _tab;
    private InputAction _esc;
    private InputAction _enter;

    // PlayerActions — new actions
    private InputAction _cancel;
    private InputAction _rotateInspect;
    private InputAction _mouseDelta;
    private InputAction _scrollDelta;
    private InputAction _resetRotation;
    private InputAction _flipOrFlashlight;
    private InputAction _toggleTranslation;
    private InputAction _enterMemory;
    private InputAction _confirm;
    private InputAction _enterKey;
    private InputAction _toggleInventory;
    private InputAction _togglePause;
    private InputAction _toggleMap;
    private InputAction _toggleFlashlight;
    private InputAction _debugScreenshot;
    private InputAction _pointer;

    // Menu actions
    private InputAction _menuNavUp;
    private InputAction _menuNavDown;
    private InputAction _menuNavLeft;
    private InputAction _menuNavRight;
    private InputAction _menuConfirm;
    private InputAction _menuCancel;
    private InputAction _menuCycleLeft;
    private InputAction _menuCycleRight;
    private InputAction _menuFlipNote;
    private InputAction _menuRotateInspect;
    private InputAction _menuMouseDelta;

    // Puzzle actions
    private InputAction _puzzleNavHorizontal;
    private InputAction _puzzleNavVertical;
    private InputAction _puzzleConfirm;
    private InputAction _puzzleCancel;
    private InputAction _puzzleCycleLeft;

    // Cached map refs for enable/disable (generated @Menu/@Puzzle don't exist until reimport)
    private InputActionMap _menuMap;
    private InputActionMap _puzzleMap;

    // Raw movement before smoothing
    private Vector2 _movementInput;
    private Vector2 _cameraInput;
    private Vector2 _smoothedMovement;
    private Vector2 _movementSmoothVelocity;

    // Stored delegate instances for safe unsubscription
    private Action<InputAction.CallbackContext> _onInteractPerformed;
    private Action<InputAction.CallbackContext> _onTabPerformed;
    private Action<InputAction.CallbackContext> _onMovementPerformed;
    private Action<InputAction.CallbackContext> _onMovementCanceled;
    private Action<InputAction.CallbackContext> _onCameraPerformed;
    private Action<InputAction.CallbackContext> _onCameraCanceled;
    private Action<InputAction.CallbackContext> _onToggleInventoryPerformed;
    private Action<InputAction.CallbackContext> _onTogglePausePerformed;
    private Action<InputAction.CallbackContext> _onToggleMapPerformed;
    private Action<InputAction.CallbackContext> _onToggleFlashlightPerformed;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        _controls = new PlayerControls();
        CacheActionRefs();
        RegisterCallbacks();
    }

    private void OnEnable()
    {
        if (_controls == null) return;
        // PlayerMovement is always active regardless of context.
        _controls.PlayerMovement.Enable();
        // Default context is Player.
        _controls.PlayerActions.Enable();
        _menuMap?.Disable();
        _puzzleMap?.Disable();
    }

    private void OnDisable()
    {
        if (_controls == null) return;
        _controls.PlayerMovement.Disable();
        _controls.PlayerActions.Disable();
        _menuMap?.Disable();
        _puzzleMap?.Disable();
    }

    private void OnDestroy()
    {
        UnregisterCallbacks();
        _controls?.Dispose();
        _controls = null;
        instance = null;
    }

    private void Update()
    {
        PollMovement();
        PollPlayerContext();
        PollMenuContext();
        PollPuzzleContext();
    }

    // -------------------------------------------------------------------------
    // Context switching
    // -------------------------------------------------------------------------
    /// <summary>
    /// Switch the active input context. PlayerMovement stays enabled in all contexts.
    /// Call SetContext(Player) in OnDisable of any Menu/Puzzle consumer as a safety reset.
    /// </summary>
    public void SetContext(InputContext ctx)
    {
        _controls.PlayerActions.Disable();
        _menuMap?.Disable();
        _puzzleMap?.Disable();

        switch (ctx)
        {
            case InputContext.Player:
                _controls.PlayerActions.Enable();
                break;
            case InputContext.Menu:
                _menuMap?.Enable();
                // FIX: ToggleInventory vive solo en PlayerActions (deshabilitado en Menu),
                // pero el inventario debe poder CERRARSE con Tab estando abierto.
                // Se habilita la acción individual aunque su mapa esté apagado; al volver
                // a Player, PlayerActions.Enable() la cubre de nuevo.
                _toggleInventory?.Enable();
                break;
            case InputContext.Puzzle:
                _puzzleMap?.Enable();
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------
    /// <summary>
    /// Registers a one-shot callback fired on any button press (title screen AnyKey).
    /// Auto-unsubscribes after first fire.
    /// </summary>
    public void RegisterAnyKeyCallback(Action callback)
    {
        // CallOnce fires for the first button press and auto-unsubscribes.
        InputSystem.onAnyButtonPress.CallOnce(_ => callback?.Invoke());
    }

    /// <summary>
    /// Returns a CustomYieldInstruction that keeps waiting until the specified action
    /// is performed. Replaces WaitUntil(()=>Input.GetKeyDown(...)) coroutine patterns.
    /// </summary>
    public CustomYieldInstruction WaitForAction(string mapName, string actionName)
    {
        return new WaitUntilActionPerformed(_controls.asset, mapName, actionName);
    }

    // -------------------------------------------------------------------------
    // Private: cache references
    // -------------------------------------------------------------------------
    private void CacheActionRefs()
    {
        // PlayerMovement (generated accessors — stable)
        _movement = _controls.PlayerMovement.Movement;
        _camera   = _controls.PlayerMovement.Camera;

        // Sprint — generated accessor not yet available (add action in .inputactions then reimport).
        // TODO: replace with _controls.PlayerMovement.Sprint once the asset is updated and re-imported.
        _sprint = _controls.PlayerMovement.Get().FindAction(ActionNames.Sprint, throwIfNotFound: false);
        if (_sprint == null)
            Debug.LogWarning("[InputHandler] 'Sprint' action not found in PlayerMovement map. SprintHeld will always be false. Add the action in the Input Action Asset editor.");

        // PlayerActions — legacy actions (generated accessors — stable)
        _interact = _controls.PlayerActions.Interact;
        _tab      = _controls.PlayerActions.Tab;
        _esc      = _controls.PlayerActions.Esc;
        _enter    = _controls.PlayerActions.Enter;

        // PlayerActions — new actions (FindAction fallback until generated class updates)
        // Using FindAction here is safe: called once in Awake, zero per-frame allocation.
        var pa = _controls.PlayerActions.Get();
        _cancel            = pa.FindAction(ActionNames.Cancel,            throwIfNotFound: true);
        _rotateInspect     = pa.FindAction(ActionNames.RotateInspect,     throwIfNotFound: true);
        _mouseDelta        = pa.FindAction(ActionNames.MouseDelta,        throwIfNotFound: true);
        _scrollDelta       = pa.FindAction(ActionNames.ScrollDelta,       throwIfNotFound: true);
        _resetRotation     = pa.FindAction(ActionNames.ResetRotation,     throwIfNotFound: true);
        _flipOrFlashlight  = pa.FindAction(ActionNames.FlipOrFlashlight,  throwIfNotFound: true);
        _toggleTranslation = pa.FindAction(ActionNames.ToggleTranslation, throwIfNotFound: true);
        _enterMemory       = pa.FindAction(ActionNames.EnterMemory,       throwIfNotFound: true);
        _confirm           = pa.FindAction(ActionNames.Confirm,           throwIfNotFound: true);
        _enterKey          = pa.FindAction(ActionNames.EnterKey,          throwIfNotFound: true);
        _toggleInventory   = pa.FindAction(ActionNames.ToggleInventory,   throwIfNotFound: true);
        _togglePause       = pa.FindAction(ActionNames.TogglePause,       throwIfNotFound: true);
        _toggleMap         = pa.FindAction(ActionNames.ToggleMap,         throwIfNotFound: true);
        _toggleFlashlight  = pa.FindAction(ActionNames.ToggleFlashlight,  throwIfNotFound: true);
        _debugScreenshot   = pa.FindAction(ActionNames.DebugScreenshot,   throwIfNotFound: true);
        _pointer           = pa.FindAction(ActionNames.Pointer,           throwIfNotFound: true);

        // Menu actions — FindActionMap used because generated @Menu/@Puzzle accessors are produced
        // only after Unity re-imports the .inputactions asset. Using asset.FindActionMap here is
        // safe: called once in Awake, zero per-frame allocation.
        _menuMap = _controls.asset.FindActionMap(MapNames.Menu, throwIfNotFound: true);
        _puzzleMap = _controls.asset.FindActionMap(MapNames.Puzzle, throwIfNotFound: true);

        var menu = _menuMap;
        _menuNavUp     = menu.FindAction(ActionNames.NavUp,       throwIfNotFound: true);
        _menuNavDown   = menu.FindAction(ActionNames.NavDown,     throwIfNotFound: true);
        _menuNavLeft   = menu.FindAction(ActionNames.NavLeft,     throwIfNotFound: true);
        _menuNavRight  = menu.FindAction(ActionNames.NavRight,    throwIfNotFound: true);
        _menuConfirm   = menu.FindAction(ActionNames.Confirm,     throwIfNotFound: true);
        _menuCancel    = menu.FindAction(ActionNames.Cancel,      throwIfNotFound: true);
        _menuCycleLeft      = menu.FindAction(ActionNames.CycleLeft,      throwIfNotFound: true);
        _menuCycleRight     = menu.FindAction(ActionNames.CycleRight,     throwIfNotFound: true);
        _menuFlipNote       = menu.FindAction(ActionNames.FlipNote,       throwIfNotFound: true);
        _menuRotateInspect  = menu.FindAction(ActionNames.RotateInspect,  throwIfNotFound: true);
        _menuMouseDelta     = menu.FindAction(ActionNames.MouseDelta,     throwIfNotFound: true);

        // Puzzle actions
        var puzzle = _puzzleMap;
        _puzzleNavHorizontal = puzzle.FindAction(ActionNames.NavHorizontal, throwIfNotFound: true);
        _puzzleNavVertical   = puzzle.FindAction(ActionNames.NavVertical,   throwIfNotFound: true);
        _puzzleConfirm       = puzzle.FindAction(ActionNames.Confirm,       throwIfNotFound: true);
        _puzzleCancel        = puzzle.FindAction(ActionNames.Cancel,        throwIfNotFound: true);
        _puzzleCycleLeft     = puzzle.FindAction(ActionNames.CycleLeft,     throwIfNotFound: true);
    }

    // -------------------------------------------------------------------------
    // Private: event callbacks (one-shot logic lives here; polling in Update)
    // -------------------------------------------------------------------------
    private void RegisterCallbacks()
    {
        // Store delegates so they can be unsubscribed exactly later (lambda identity)
        _onInteractPerformed = _ => OnInteractPressed?.Invoke();
        _onTabPerformed      = _ => OnInventoryOpenPressed?.Invoke();

        _onMovementPerformed = ctx => _movementInput = ctx.ReadValue<Vector2>();
        _onMovementCanceled  = _   => _movementInput = Vector2.zero;
        _onCameraPerformed   = ctx => _cameraInput   = ctx.ReadValue<Vector2>();
        _onCameraCanceled    = _   => _cameraInput   = Vector2.zero;

        _onToggleInventoryPerformed = _ =>
        {
            OnInventoryToggle?.Invoke();
            OnInventoryOpenPressed?.Invoke(); // fire legacy alias too
        };
        _onTogglePausePerformed     = _ => OnPauseToggle?.Invoke();
        _onToggleMapPerformed       = _ => OnMapToggle?.Invoke();
        _onToggleFlashlightPerformed = _ => OnFlashlightToggle?.Invoke();

        // Subscribe
        _interact.performed        += _onInteractPerformed;
        _tab.performed             += _onTabPerformed;
        _movement.performed        += _onMovementPerformed;
        _movement.canceled         += _onMovementCanceled;
        _camera.performed          += _onCameraPerformed;
        _camera.canceled           += _onCameraCanceled;
        _toggleInventory.performed += _onToggleInventoryPerformed;
        _togglePause.performed     += _onTogglePausePerformed;
        _toggleMap.performed       += _onToggleMapPerformed;
        _toggleFlashlight.performed += _onToggleFlashlightPerformed;
    }

    private void UnregisterCallbacks()
    {
        if (_controls == null) return;

        _interact.performed        -= _onInteractPerformed;
        _tab.performed             -= _onTabPerformed;
        _movement.performed        -= _onMovementPerformed;
        _movement.canceled         -= _onMovementCanceled;
        _camera.performed          -= _onCameraPerformed;
        _camera.canceled           -= _onCameraCanceled;
        _toggleInventory.performed -= _onToggleInventoryPerformed;
        _togglePause.performed     -= _onTogglePausePerformed;
        _toggleMap.performed       -= _onToggleMapPerformed;
        _toggleFlashlight.performed -= _onToggleFlashlightPerformed;
    }

    // -------------------------------------------------------------------------
    // Private: per-frame poll methods (zero allocation)
    // -------------------------------------------------------------------------
    private void PollMovement()
    {
        // Frame-rate independent smoothing via SmoothDamp (inherently frame-rate independent)
        _smoothedMovement = Vector2.SmoothDamp(
            _smoothedMovement, _movementInput,
            ref _movementSmoothVelocity, inputSmoothTime);

        // Snap denormalized floats to zero
        if (_smoothedMovement.sqrMagnitude < 1e-6f)
        {
            _smoothedMovement        = Vector2.zero;
            _movementSmoothVelocity  = Vector2.zero;
        }

        horizontalInput = _smoothedMovement.x;
        verticalInput   = _smoothedMovement.y;
        moveAmount      = Mathf.Clamp01(Mathf.Abs(horizontalInput) + Mathf.Abs(verticalInput));

        cameraInputX = _cameraInput.x;
        cameraInputY = _cameraInput.y;

        // Raw A/D axis — consumed by InspectObject for roll (movement is locked during inspection).
        RollInput = _movementInput.x;

        SprintHeld = _sprint != null && _sprint.IsPressed();
    }

    private void PollPlayerContext()
    {
        // Legacy button states (backward compat)
        e_Input     = _interact.WasPerformedThisFrame();
        tab_Input   = _tab.WasPerformedThisFrame();
        esc_Input   = _esc.WasPerformedThisFrame();
        enter_Input = _enter.WasPerformedThisFrame();

        // New Player properties
        PrimaryInteractPressed   = _interact.WasPerformedThisFrame();
        CancelPressed            = _cancel.WasPerformedThisFrame();
        // RotateInspect has a Hold interaction on the binding; IsPressed() = held past threshold
        RotateInspectHeld        = _rotateInspect.IsPressed();
        MouseDelta               = _mouseDelta.ReadValue<Vector2>();
        ScrollDelta              = _scrollDelta.ReadValue<float>();
        ResetInspectPressed      = _resetRotation.WasPerformedThisFrame();
        FlipOrFlashlightPressed  = _flipOrFlashlight.WasPerformedThisFrame();
        ToggleTranslationPressed = _toggleTranslation.WasPerformedThisFrame();
        // EnterMemory: NO Hold interaction — IsPressed() lets PhotoMemoryPortal manage its own timer
        EnterMemoryHeld          = _enterMemory.IsPressed();
        ConfirmPressed           = _confirm.WasPerformedThisFrame();
        ConfirmReleased          = _confirm.WasReleasedThisFrame();
        EnterPressed             = _enterKey.WasPerformedThisFrame();

        // Pointer: raw LMB state — WasPerformedThisFrame/IsPressed/WasReleasedThisFrame
        // Used by DraggableObject and DragWithLerp; distinct from Interact (no E, no Hold).
        PointerPressed  = _pointer.WasPerformedThisFrame();
        PointerHeld     = _pointer.IsPressed();
        PointerReleased = _pointer.WasReleasedThisFrame();
    }

    private void PollMenuContext()
    {
        NavUpPressed          = _menuNavUp.WasPerformedThisFrame();
        NavDownPressed        = _menuNavDown.WasPerformedThisFrame();
        NavLeftPressed        = _menuNavLeft.WasPerformedThisFrame();
        NavRightPressed       = _menuNavRight.WasPerformedThisFrame();
        MenuConfirmPressed    = _menuConfirm.WasPerformedThisFrame();
        MenuCancelPressed     = _menuCancel.WasPerformedThisFrame();
        CycleLeftPressed      = _menuCycleLeft.WasPerformedThisFrame();
        CycleRightPressed     = _menuCycleRight.WasPerformedThisFrame();
        FlipNotePressed       = _menuFlipNote.WasPerformedThisFrame();
        MenuRotateInspectHeld = _menuRotateInspect.IsPressed();
        MenuMouseDelta        = _menuMouseDelta.ReadValue<Vector2>();
    }

    private void PollPuzzleContext()
    {
        PuzzleNavHorizontal   = _puzzleNavHorizontal.ReadValue<float>();
        PuzzleNavVertical     = _puzzleNavVertical.ReadValue<float>();
        PuzzleConfirmPressed  = _puzzleConfirm.WasPerformedThisFrame();
        PuzzleCancelPressed   = _puzzleCancel.WasPerformedThisFrame();
        PuzzleCycleLeftPressed = _puzzleCycleLeft.WasPerformedThisFrame();
    }

    // -------------------------------------------------------------------------
    // Map and action name constants — no magic strings in method bodies
    // -------------------------------------------------------------------------
    private static class MapNames
    {
        public const string PlayerMovement = "PlayerMovement";
        public const string PlayerActions  = "PlayerActions";
        public const string Menu           = "Menu";
        public const string Puzzle         = "Puzzle";
    }

    private static class ActionNames
    {
        // PlayerMovement
        public const string Sprint = "Sprint";

        // PlayerActions new
        public const string Cancel            = "Cancel";
        public const string RotateInspect     = "RotateInspect";
        public const string MouseDelta        = "MouseDelta";
        public const string ScrollDelta       = "ScrollDelta";
        public const string ResetRotation     = "ResetRotation";
        public const string FlipOrFlashlight  = "FlipOrFlashlight";
        public const string ToggleTranslation = "ToggleTranslation";
        public const string EnterMemory       = "EnterMemory";
        public const string Confirm           = "Confirm";
        public const string EnterKey          = "EnterKey";
        public const string ToggleInventory   = "ToggleInventory";
        public const string TogglePause       = "TogglePause";
        public const string ToggleMap         = "ToggleMap";
        public const string ToggleFlashlight  = "ToggleFlashlight";
        public const string DebugScreenshot   = "DebugScreenshot";
        public const string Pointer           = "Pointer";

        // Menu
        public const string NavUp    = "NavUp";
        public const string NavDown  = "NavDown";
        public const string NavLeft  = "NavLeft";
        public const string NavRight = "NavRight";
        public const string CycleLeft  = "CycleLeft";
        public const string CycleRight = "CycleRight";
        public const string FlipNote   = "FlipNote";

        // Puzzle
        public const string NavHorizontal = "NavHorizontal";
        public const string NavVertical   = "NavVertical";
    }
}

// =============================================================================
// WaitUntilActionPerformed — CustomYieldInstruction for WaitForAction()
// Replaces coroutine patterns like: yield return new WaitUntil(()=>Input.GetKeyDown(...))
// =============================================================================
public sealed class WaitUntilActionPerformed : CustomYieldInstruction
{
    private bool _performed;

    public override bool keepWaiting => !_performed;

    public WaitUntilActionPerformed(InputActionAsset asset, string mapName, string actionName)
    {
        var action = asset.FindActionMap(mapName, throwIfNotFound: false)
                         ?.FindAction(actionName, throwIfNotFound: false);

        if (action == null)
        {
            Debug.LogWarning($"[WaitUntilActionPerformed] Action '{mapName}/{actionName}' not found. Will resolve immediately.");
            _performed = true;
            return;
        }

        action.performed += OnPerformed;
    }

    private void OnPerformed(InputAction.CallbackContext ctx)
    {
        _performed = true;
        ctx.action.performed -= OnPerformed;
    }
}
