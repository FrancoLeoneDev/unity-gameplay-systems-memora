using System;
using System.Collections;
using DG.Tweening;
using UnityEngine;

/// <summary>
/// Puerta de EMPUJE físico (reemplaza el drag-con-mouse de <see cref="DraggableDoor"/>).
/// El jugador la abre chocándola con el cuerpo (su Rigidbody empuja la hoja → el
/// <see cref="HingeJoint"/> gira). La casa / el Director de Eventos Ambientales la comandan
/// por script para sustos (abrir, cerrar, portazo).
///
/// LA LÍNEA DE DISEÑO: lo que hace EL JUGADOR contra la hoja es FÍSICO; lo que se AUTORA
/// (el cierre deliberado con la tecla y todo lo del Director) es ANIMACIÓN.
///
/// Empujar la hoja con el cuerpo es físico, y el portazo al entrar corriendo
/// (<see cref="TryBurstOpen"/>) TAMBIÉN: es un impulso sobre la hoja viva, no un tween. Así frena
/// contra lo que haya —el tope del hinge, la pared, una silla detrás de la puerta— en vez de
/// atravesarlo, y no hace falta declararle a cada puerta hasta qué ángulo abrir.
///
/// Esto ya se había intentado antes y se descartó ("un impulso creíble recorría 30-40° y se
/// plantaba"). El motivo NO era la física: con amortiguación viscosa el ángulo total que cubre un
/// impulso es ω/λ, y con los valores de empuje (<c>doorMaxAngularVelocity</c> 6 sobre
/// <c>doorAngularDrag</c> 3) el techo absoluto era 2 rad = 114°, inalcanzable en la práctica. El
/// resorte al que también se culpaba hoy ni siquiera corre (<c>autoCloseEnabled</c> está OFF).
/// La solución es no compartir tuning: el portazo corre su propia ventana con
/// <c>burstAngularDamping</c> / <c>burstMaxAngularVelocity</c>, y al frenarse devuelve los valores
/// pesados con los que el empuje continuo se siente bien. NO volver a subir doorAngularDrag
/// pensando en el portazo: son dos gestos distintos y por eso tienen dos pares de valores.
///
/// El cierre con la tecla (<see cref="ClosePlayerDriven"/>) sí es un tween: es el único verbo del
/// jugador que trae la hoja HACIA el vano donde puede estar parado, y necesita poder abortarse a
/// mitad de camino (ver <see cref="IsCloseSweepBlocked"/>).
///
/// El componente vive en el DoorGO; el <c>pivot</c> (hoja con Rigidbody + HingeJoint + Collider)
/// puede ser un hijo. El feedback de puerta trabada lo reenvía <see cref="DoorContactRelay"/>
/// (en la hoja) a <see cref="OnLockedBump"/>.
///
/// State-machine (LA regla): solo <c>locked</c> o <c>_heldOpen</c> congelan la puerta
/// (kinematic + resorte off). Una puerta cerrada-normal queda DINÁMICA y empujable.
///
/// La CAPA física de los colliders sigue el estado (D5): empujable → capa "Door" (el
/// 'colisionador puertas' del player la abre ANTES del contacto directo del cuerpo);
/// congelada (trabada / held-open / animándose) → capa que el pusher ignora, para que el
/// jugador la alcance normal (usar la llave) sin frenarse a distancia contra una hoja inmóvil.
///
/// AUTO-CIERRE (decisión de diseño): apagado por default. La puerta queda DONDE LA DEJÁS
/// (estilo Visage) y el jugador la cierra con la tecla de interacción. Motivo: un resorte
/// siempre-activo peleaba con el portazo al correr y le comía la lectura a los eventos del
/// Director (si TODAS las puertas se cierran solas, que la casa cierre una deja de leerse como
/// susto). <c>autoCloseEnabled</c> reactiva el resorte por puerta si alguna lo necesita.
/// </summary>
[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(SaveableEntity))]
public class PhysicDoor : MonoBehaviour, ISaveable, IInteractable, IHoverUiHint
{
    /// <summary>Se dispara cuando la hoja cruza el umbral de "abierta". Sólo la puerta lo invoca.</summary>
    public event Action onDoorOpen;
    /// <summary>Se dispara cuando la hoja vuelve a la posición cerrada. Sólo la puerta lo invoca.</summary>
    public event Action onDoorClose;

    [Header("Door")]
    [SerializeField] private DoorType doorType = DoorType.Wooden;
    [SerializeField, Tooltip("GameObject de la hoja (Rigidbody + HingeJoint). Cae a transform si es null.")]
    private Transform pivot;

    [Header("Transform Settings Door")]
    [SerializeField] private Transform hintPosition;
    [SerializeField] private Transform invertHintPosition;

    [SerializeField, Tooltip("Ángulo local Y del pivot con la puerta CERRADA. Es el cero del que cuelgan " +
        "TODOS los ángulos del sistema: 'abrir 90°' significa 90° DESDE acá, y cerrar significa volver acá.\n\n" +
        "No se puede asumir 0 para todas: en la escena hay puertas autoradas con la hoja a -90° y a 180°, y " +
        "tratarlas como si cerradas fueran 0 las hace girar 175° al cerrar o al reventar.\n\n" +
        "Es un campo y no la pose autorada justamente para poder AUTORAR LA HOJA ABIERTA: una puerta que " +
        "arranca de par en par se deja así en la escena y se le declara acá su cerrado, y el cierre " +
        "scripteado tiene recorrido de verdad. Mientras esto se deducía de la pose, dejar la hoja abierta " +
        "en el Editor le enseñaba que ESO era estar cerrada.")]
    private float closedAngleY;

    [Header("Lock")]
    [SerializeField] private bool locked;

    [Header("Player-pusher layers (D5)")]
    [SerializeField, Tooltip("Capa cuando la puerta es EMPUJABLE: el 'colisionador puertas' del player " +
        "la abre antes del contacto directo del cuerpo.")]
    private string pushableLayerName = "Door";
    [SerializeField, Tooltip("Capa cuando la puerta NO es empujable (trabada / held-open / animándose): " +
        "el pusher la ignora para no frenar al jugador lejos de la hoja.")]
    private string frozenLayerName = "Interactable";

    [Header("Physics / Cierre")]
    [SerializeField, Tooltip("Ángulo de apertura canónico (grados) usado por los eventos del Director.")]
    private float openDoorAngle;
    [SerializeField, Tooltip("OFF por default: la puerta queda donde la dejás y el jugador la cierra " +
        "con la tecla de interacción. Activar SOLO en puertas que deban cerrarse solas siempre.")]
    private bool autoCloseEnabled;
    [SerializeField, Tooltip("Fuerza del resorte de cierre del HingeJoint. La usan el auto-cierre " +
        "(si está activo) y el cierre por interacción.")]
    private float autoCloseSpring = 5f;
    [SerializeField, Tooltip("Amortiguación del resorte de cierre del HingeJoint.")]
    private float autoCloseDamper = 5f;
    [SerializeField] private float doorAngularDrag = 3f;
    [SerializeField] private float doorMaxAngularVelocity = 6f;

    [Header("Cierre por interacción")]
    [SerializeField, Tooltip("Ángulo mínimo (grados) para que la puerta ofrezca 'cerrar' al interactuar. " +
        "Por debajo se considera ya cerrada y la interacción no hace nada.")]
    private float minAngleToOfferClose = 5f;
    [SerializeField, Tooltip("Cuánto tarda el cierre con la tecla de interacción. Es una ANIMACIÓN, " +
        "no un resorte: determinista y siempre igual.")]
    private float closeDuration = 1.0f;
    [SerializeField, Tooltip("Capa que bloquea el cierre si está dentro del barrido de la hoja. " +
        "Si el jugador está en el vano, la puerta NO cierra en vez de cerrarse a través suyo. " +
        "Se resuelve por NOMBRE en Awake (mismo patrón que las capas del pusher), así las puertas " +
        "ya existentes lo toman sin recablear nada.")]
    private string closeBlockerLayerName = "Player";
    [SerializeField, Tooltip("Margen (grados) que se le suma al barrido al chequear si está bloqueado.")]
    private float closeBlockAngleMargin = 8f;

    [Header("Portazo al ABRIR (corriendo) — FÍSICO")]
    [SerializeField, Tooltip("Velocidad angular (rad/s) que se le imprime a la hoja al entrar corriendo. " +
        "Se ESCRIBE directo sobre el Rigidbody (equivale a ForceMode.VelocityChange) en vez de aplicar un " +
        "torque: así el gesto no depende de la masa ni del tensor de inercia de cada hoja, y todas las " +
        "puertas de la casa responden igual sin tunear una por una.\n\n" +
        "NO hay campo de 'ángulo de portazo' y es a propósito: hasta dónde llega lo decide el mundo " +
        "(el tope del hinge, la pared, lo que haya detrás), que es justamente el punto de que sea físico.")]
    private float burstAngularVelocity = 12f;
    [SerializeField, Tooltip("Amortiguación angular MIENTRAS dura el portazo. Es el campo que hace que " +
        "esto funcione: con doorAngularDrag (tuneado para que empujar con el cuerpo se sienta pesado) el " +
        "ángulo total que puede cubrir un impulso es doorMaxAngularVelocity/doorAngularDrag radianes, y con " +
        "los valores de la casa eso da 114° en el mejor caso — por eso el intento físico anterior 'se " +
        "plantaba a los 30-40°'. Subir esto acá vuelve a romper el portazo.")]
    private float burstAngularDamping = 0.15f;
    [SerializeField, Tooltip("Techo de velocidad angular durante el portazo. El normal " +
        "(doorMaxAngularVelocity) recorta el impulso antes de que la hoja arranque.")]
    private float burstMaxAngularVelocity = 20f;
    [SerializeField, Tooltip("Velocidad angular (rad/s) debajo de la cual el portazo se considera " +
        "terminado y la hoja recupera su amortiguación pesada normal.")]
    private float burstSettleAngularSpeed = 0.5f;
    [SerializeField, Tooltip("Corte de seguridad: si el portazo no se frenó solo en este tiempo, se " +
        "devuelven los valores normales igual. Evita que una hoja quede liviana para siempre.")]
    private float burstMaxDuration = 3f;
    [SerializeField, Tooltip("Caída de velocidad angular (rad/s) en UN paso de física que cuenta como " +
        "impacto y dispara el golpe. Una sola regla cubre los dos casos: pegar contra el tope del " +
        "HingeJoint (que no emite colisión) y chocar un collider del mundo (que sí).")]
    private float burstImpactSpeedDrop = 4f;
    [SerializeField, Tooltip("Iteraciones de velocidad del solver mientras dura el portazo. El default " +
        "del proyecto es 1, y a esta velocidad el joint penetra su propio límite y la hoja pega un salto. " +
        "0 = no tocar el valor del Rigidbody.")]
    private int burstSolverVelocityIterations = 4;
    [SerializeField, Tooltip("Cooldown entre portazos de la misma puerta.")]
    private float slamCooldown = 1f;
    [SerializeField, Tooltip("DIAGNÓSTICO: loguea cada contacto del jugador y por qué el portazo " +
        "disparó o no. Prendelo en UNA puerta para saber si el problema es que no salta o que salta " +
        "y se ve poco. Apagar después.")]
    private bool logBurstOpenDecisions;

    [Header("Audio")]
    [SerializeField] private float creakMaxVolume = 0.4f;

    [Header("Locked feedback")]
    [SerializeField] private float lockedBumpCooldown = 1.5f;
    [SerializeField, Tooltip("Cuánto queda bajada la manija al forcejear una puerta trabada.")]
    private float lockedRattleDuration = 0.3f;

    [Header("Handle (opcional)")]
    [SerializeField, Tooltip("Manija que baja mientras el jugador empuja la puerta y al forcejear una " +
        "puerta trabada (DraggableObjectEventsBase). NO baja cuando la puerta se mueve sola.")]
    private DraggableObjectEventsBase draggableObjectEvents;

    [SerializeField, Tooltip("MANIJA FIJA: tildar en las puertas cuya manija NO gira (tirador fijo, " +
        "pomo soldado, barral). Con esto tildado la manija nunca se anima aunque haya un " +
        "DraggableObjectEventsBase asignado — el resto del feedback de puerta trabada (traqueteo, " +
        "sonido) sigue funcionando igual.")]
    private bool fixedHandle;

    // --- Cached refs ---
    private Rigidbody rb;
    private HingeJoint _hinge;
    private Collider[] doorColliders;
    private AudioSource audioSource;
    private AudioSource creakAudioSource;
    private CapsuleCollider playerCollider;
    /// <summary>
    /// Cacheado en Awake, pero consultado SIEMPRE vía <see cref="HasDoorWithKey"/>: DoorWithKey se
    /// autodestruye al desbloquear la puerta (DestroySaveable), y un bool cacheado se quedaba en true
    /// para siempre → la puerta silenciaba sus clips de abrir/cerrar el resto de la partida.
    /// El operador != null de Unity devuelve false para un componente destruido, así que esto se
    /// entera solo, sin GetComponent por frame.
    /// </summary>
    private DoorWithKey _doorWithKey;
    private bool HasDoorWithKey => _doorWithKey != null;
    /// <summary>Relays de la hoja, cacheados para poder resetear sus contactos al congelar.</summary>
    private DoorContactRelay[] _relays;
    private int _pushableLayer = -1;   // capa "Door" resuelta en Awake
    private int _frozenLayer = -1;     // capa que el pusher ignora, resuelta en Awake
    /// <summary>Buffer del chequeo de barrido al cerrar. Reutilizado para no alocar por interacción.</summary>
    private readonly Collider[] _sweepHits = new Collider[8];
    private int _closeBlockerMask;     // máscara resuelta desde closeBlockerLayerName en Awake
    /// <summary>Iteraciones de velocidad del solver de la hoja fuera del portazo, para restaurarlas.</summary>
    private int _defaultSolverVelocityIterations;

    // --- State ---
    private bool isOpen;
    private bool doorOpenedBeyondThreshold;
    private bool _heldOpen;              // congelada-abierta (susto / save) — la limpian Revive() y SetOpenState(false)
    private bool _directorControlled;    // mutex: la casa/Director comanda la puerta
    private bool collisionIgnored;       // estado actual del ignore jugador↔hoja (idempotencia)
    private float _lastBump;             // cooldown del feedback de puerta trabada
    private bool _playerAnimating;       // lock de animación del jugador (portazo / cierre con E)
    private bool _handleHeld;            // la manija está bajada (idempotencia del tween)
    private float _lastSlam;             // cooldown del portazo
    private Coroutine _rattleRoutine;

    // --- Ventana del portazo físico ---
    /// <summary>La hoja está corriendo un portazo: tiene los valores livianos puestos y hay que devolverlos.</summary>
    private bool _bursting;
    private float _burstStartTime;
    /// <summary>Velocidad angular del paso de física anterior, para detectar el frenazo del impacto.</summary>
    private float _lastBurstSpeed;

    // --- Creak (loop-based) ---
    private bool isCreaking;
    private float creakVolumeVelocity;

    /// <summary>
    /// Esta puerta ya preguntó por su clip de chirrido y la AudioLibrary no tiene ninguno para su tipo.
    /// Sin este latch, <see cref="StartCreakAudio"/> vuelve a pedirlo EN CADA FRAME (se llama desde Update
    /// mientras <c>isCreaking</c> siga en false, y sin clip nunca pasa a true): eso son miles de warnings por
    /// segundo por puerta, que inflan el Editor.log a decenas de GB y se llevan puesta la RAM del Editor.
    /// Solo se latchea si la AudioLibrary EXISTE y aun asi no devolvio clip — si el singleton todavía no
    /// despertó no contamos ese intento, así la puerta sigue sonando cuando la librería cargue.
    /// </summary>
    private bool _sinClipDeChirrido;

    // --- Constantes de ángulo/creak ---
    private const float DoorOpenedThreshold = 3f;
    private const float CloseAngleThreshold = 1.5f;
    private const float OpenSoundAngleThreshold = 2f;
    private const float CreakMinAngularSpeed = 0.3f;
    private const float CreakVolumeSmoothTime = 0.1f;
    /// <summary>Brazo mínimo (m) del choque respecto de la bisagra para saber hacia qué lado abrir.</summary>
    private const float MinBurstTorqueLever = 0.02f;
    /// <summary>Velocidad angular (rad/s) debajo de la cual la hoja se considera DETENIDA en 0°,
    /// y no simplemente cruzando el 0° de camino al otro lado.</summary>
    private const float ClosedSettleAngularSpeed = 0.25f;

    // ── Getters / eventos ─────────────────────────────────────────────────────
    public Transform Pivot => pivot;
    public bool IsOpen() => isOpen;

    /// <summary>
    /// Apertura actual en grados DESDE LA POSICIÓN CERRADA (0 = cerrada), misma unidad que reciben
    /// <see cref="AnimateToAngle"/> y <see cref="SetOpenStateSilent"/>. La consultan los eventos del
    /// Director para decidir el gesto según cómo esté la puerta: leer el euler crudo daría cualquier
    /// cosa en las puertas autoradas con la hoja a -90° o 180°.
    /// </summary>
    public float CurrentAngleFromClosed() => AngleFromClosed();
    public bool IsDirectorControlled => _directorControlled;
    public DoorType GetDoorType() => doorType;
    public float OpenDoorAngle() => openDoorAngle;

    // ──────────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (pivot == null) pivot = transform;

        audioSource = GetComponent<AudioSource>();
        rb = pivot.GetComponent<Rigidbody>();
        _hinge = pivot.GetComponent<HingeJoint>();
        doorColliders = pivot.GetComponentsInChildren<Collider>();
        _doorWithKey = GetComponent<DoorWithKey>();
        _relays = pivot.GetComponentsInChildren<DoorContactRelay>(true);

        if (rb != null) _defaultSolverVelocityIterations = rb.solverVelocityIterations;

        _pushableLayer = LayerMask.NameToLayer(pushableLayerName);
        _frozenLayer = LayerMask.NameToLayer(frozenLayerName);

        int blockerLayer = LayerMask.NameToLayer(closeBlockerLayerName);
        if (blockerLayer < 0)
            Debug.LogWarning($"[PhysicDoor] Capa '{closeBlockerLayerName}' no encontrada en '{name}'. " +
                "El cierre NO va a chequear si el jugador está en el vano.", this);
        else
            _closeBlockerMask = 1 << blockerLayer;
        if (_pushableLayer < 0 || _frozenLayer < 0)
            Debug.LogWarning($"[PhysicDoor] Capa no encontrada (pushable='{pushableLayerName}', " +
                $"frozen='{frozenLayerName}') en '{name}'. El asistente de empuje no cambiará de capa.", this);

        // Evita apilar un AudioSource nuevo por cada domain reload en el editor.
        if (creakAudioSource == null)
            creakAudioSource = gameObject.AddComponent<AudioSource>();
        creakAudioSource.playOnAwake = false;
        creakAudioSource.loop = true;
        creakAudioSource.spatialBlend = audioSource.spatialBlend;
        creakAudioSource.outputAudioMixerGroup = audioSource.outputAudioMixerGroup;
        creakAudioSource.minDistance = audioSource.minDistance;
        creakAudioSource.maxDistance = audioSource.maxDistance;
        creakAudioSource.rolloffMode = audioSource.rolloffMode;

        // NO se toca isKinematic acá: Revive() lo pone en false y Freeze() en true, así que cualquier
        // asignación previa quedaba pisada. Una puerta cerrada-normal queda DINÁMICA (empujable).
        if (!locked) Revive();
        else Freeze();                 // trabada: congela + aplica la capa que el pusher ignora

        if (rb != null)
        {
            rb.angularDamping = doorAngularDrag;
            rb.maxAngularVelocity = doorMaxAngularVelocity;
        }

        enabled = true;
        // El creak loop se arma perezosamente en Update (robusto ante el orden de init de
        // AudioLibrary singleton y auto-re-armado tras un evento del Director que lo detiene).

        // El portazo físico necesita el hinge para dos cosas: su eje (sobre el que se escribe la
        // velocidad angular) y su límite (lo que la frena si no hay nada más). Sin hinge la hoja no
        // gira sola de ninguna manera y solo responde a los tweens del Director.
        if (_hinge == null)
            Debug.LogWarning($"[PhysicDoor] '{name}' no tiene HingeJoint: no se puede empujar con el " +
                "cuerpo ni dar portazo al entrar corriendo. Solo responde a los eventos animados del " +
                "Director.", this);
        else if (Mathf.Approximately(openDoorAngle, 0f))
            Debug.LogWarning($"[PhysicDoor] openDoorAngle es ~0 en '{name}': los eventos del Director " +
                "que usan OpenDoorAngle() no abrirán esta puerta. (El portazo del jugador ya NO depende " +
                "de este campo — hasta dónde llega lo decide el mundo.)", this);
    }

    private void OnDisable()
    {
        // DOTween NO muere con OnDisable (regla del proyecto): matar tweens del pivot para no dejar zombis.
        if (pivot != null) pivot.DOKill(false);
        SetPlayerCollisionIgnored(false);
        StopCreakAudio();

        // Las corrutinas mueren con el componente: soltar la manija a mano, o la puerta se reactiva
        // con la manija baja. EndBurst devuelve los valores pesados si se desactivó en pleno portazo.
        _rattleRoutine = null;
        EndBurst();
        SetHandleHeld(false);
    }

    // ── State-machine ─────────────────────────────────────────────────────────
    /// <summary>
    /// Puerta viva: kinematic off + capa empujable. El resorte queda armado SOLO si
    /// <c>autoCloseEnabled</c>; con el default (off) la puerta se queda donde la dejen y el
    /// cierre con la tecla es una animación aparte (ClosePlayerDriven), no el resorte.
    /// </summary>
    private void Revive()
    {
        _heldOpen = false;
        if (rb != null) rb.isKinematic = false;
        ConfigureSpring(autoCloseEnabled, autoCloseSpring, autoCloseDamper);
        ApplyColliderLayer(pushable: true);
    }

    /// <summary>Puerta congelada: resorte off + kinematic on + capa que el pusher ignora.</summary>
    private void Freeze()
    {
        // Cortar el portazo ANTES de volverla cinemática. Congelar es la vía por la que el Director,
        // el cierre con E y SetLocked se quedan con la puerta, y cualquiera de las tres puede caer en
        // medio de un portazo: sin esto la hoja se despertaría después con la amortiguación liviana
        // puesta y quedaría flotando con cualquier roce el resto de la partida.
        EndBurst();

        if (_hinge != null) _hinge.useSpring = false;
        if (rb != null) rb.isKinematic = true;
        ApplyColliderLayer(pushable: false);

        // Soltar la manija: congelar cambia la capa y puede activar IgnoreCollision, y en ese caso
        // el OnCollisionExit del relay puede no llegar nunca. Sin esto, la manija se quedaba bajada
        // mientras el Director hace su animación de susto. Se resetean también los contactos del
        // relay, porque un contacto cortado por cambio de capa dejaría su contador en >0 para siempre.
        SetHandleHeld(false);
        if (_relays != null)
        {
            for (int i = 0; i < _relays.Length; i++)
                if (_relays[i] != null) _relays[i].ResetContacts();
        }

        // El creak se apaga ACÁ y no en cada llamador. Es un AudioSource en LOOP cuyo volumen lo
        // maneja Update(), y Update() arranca con `if (rb.isKinematic) return`. O sea que congelar
        // mata al único que puede bajarle el volumen: si el loop quedaba sonando, sonaba PARA
        // SIEMPRE. Pasaba al trabar una puerta (SetLocked -> Freeze), que era el único de los tres
        // caminos de congelado que no lo apagaba por su cuenta.
        StopCreakAudio();
    }

    /// <summary>Escribe los valores del resorte de cierre y lo enciende/apaga. Único lugar que
    /// toca <c>_hinge.spring</c>, para que el auto-cierre y el cierre por interacción no diverjan
    /// (cada uno pasa su propio par spring/damper: la deriva lenta y el gesto rápido no comparten tuning).</summary>
    /// <summary>
    /// Prende o apaga el AUTO-CIERRE en runtime (el resorte que devuelve la hoja a cerrada).
    ///
    /// Existe para las puertas que tienen que quedarse abiertas un rato y recién después cerrarse
    /// solas: con el auto-cierre prendido desde el arranque, cualquier apertura scripteada se revierte
    /// sola apenas termina (OpenTo llama Revive, y Revive reconfigura el resorte). Es lo que pasaba con
    /// la puerta doble del hospital: se abría con el acta y el resorte la cerraba en el acto.
    ///
    /// El resorte sólo actúa sobre una puerta VIVA: si está congelada (kinematic) no hace nada hasta
    /// que la reviva SetOpenState/SetLocked.
    /// </summary>
    public void SetAutoClose(bool enabled)
    {
        autoCloseEnabled = enabled;
        ConfigureSpring(autoCloseEnabled, autoCloseSpring, autoCloseDamper);
    }

    private void ConfigureSpring(bool useSpring, float spring, float damper)
    {
        if (_hinge == null) return;
        JointSpring s = _hinge.spring;
        s.spring = spring;
        s.damper = damper;
        s.targetPosition = 0f;
        _hinge.spring = s;
        _hinge.useSpring = useSpring;
    }

    /// <summary>
    /// Mueve los colliders de la hoja a la capa según el estado (D5). Empujable → capa "Door"
    /// (el pusher del player la abre antes del contacto); congelada → capa que el pusher ignora,
    /// para que el cuerpo del jugador alcance la hoja inmóvil de cerca (usar la llave) sin frenarse
    /// a distancia. Solo colliders no-trigger (los de física); el raycast de interacción detecta
    /// ambas capas, así que la llave/hint siguen funcionando en cualquier estado.
    /// </summary>
    private void ApplyColliderLayer(bool pushable)
    {
        int layer = pushable ? _pushableLayer : _frozenLayer;
        if (layer < 0 || doorColliders == null) return;
        for (int i = 0; i < doorColliders.Length; i++)
        {
            Collider c = doorColliders[i];
            if (c != null && !c.isTrigger)
                c.gameObject.layer = layer;
        }
    }

    // ── Update (gate por !rb.isKinematic, no por flags de drag) ────────────────
    private void Update()
    {
        if (rb == null || rb.isKinematic) return;

        CheckIfDoorOpened();

        // El loop se arma sólo cuando la puerta SE MUEVE de verdad. Antes se armaba en el primer frame
        // de toda puerta viva y no se paraba nunca: el volumen bajaba a 0 pero el AudioSource seguía
        // reproduciendo en loop igual. Eran 13 loops mudos al arrancar la casa y hasta 31 con todas las
        // puertas revividas, gastando voces de audio toda la partida.
        if (!isCreaking && Mathf.Abs(rb.angularVelocity.y) > CreakMinAngularSpeed)
            StartCreakAudio();

        UpdateCreakAudio();

        float angle = AngleFromClosed();

        if (Mathf.Abs(angle) > DoorOpenedThreshold)
            doorOpenedBeyondThreshold = true;

        // Latch de cierre (B2): solo cierra si estuvo abierta y no la comanda el Director.
        // La hoja tiene que estar DETENIDA en 0°, no pasando por 0°: empujar la puerta de un lado
        // y cruzar al otro la hace atravesar el cero a toda velocidad, y sin este chequeo eso se
        // registraba como un cierre completo (con su sonido) e inmediatamente como una apertura
        // nueva del otro lado (con el suyo).
        bool detenida = rb.angularVelocity.sqrMagnitude
            <= ClosedSettleAngularSpeed * ClosedSettleAngularSpeed;

        if (Mathf.Abs(angle) < CloseAngleThreshold && doorOpenedBeyondThreshold
            && !_directorControlled && detenida)
        {
            OnDoorClose();
        }
    }

    /// <summary>
    /// Suena al EMPEZAR a abrirse estando cerrada, una sola vez. No se re-dispara al cruzar el
    /// cero empujando de un lado al otro porque el latch de cierre exige que la hoja esté
    /// detenida: mientras cruza en movimiento, <c>isOpen</c> nunca vuelve a false.
    /// </summary>
    private void CheckIfDoorOpened()
    {
        if (isOpen) return;

        float currentAngle = Mathf.Abs(AngleFromClosed());
        if (currentAngle <= OpenSoundAngleThreshold) return;

        if (!HasDoorWithKey)   // el one-shot de una puerta con llave lo maneja DoorWithKey
        {
            AudioClip clip = AudioLibrary.instance != null
                ? AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Open)
                : null;
            if (clip != null) audioSource.PlayOneShot(clip);
        }

        isOpen = true;
        onDoorOpen?.Invoke();
    }

    /// <summary>
    /// La puerta quedó cerrada. NO congela: sigue viva/empujable.
    ///
    /// El clip NO suena por default. Cerrar es un GESTO (la tecla, o la casa), no un estado al que
    /// la hoja llega de casualidad: una puerta que vuelve sola a 0° tras un empujón no debería
    /// anunciarse. Lo pide explícitamente quien cierra a propósito.
    /// </summary>
    /// <summary>
    /// La puerta llegó al marco. Suena SIEMPRE, se haya cerrado con la tecla, de un empujón o sola
    /// por el resorte: cerrarse es cerrarse.
    ///
    /// Antes esto tenía un flag para que solo sonara el cierre deliberado (la tecla). Era para tapar
    /// el doble sonido de cruzar la puerta de un lado al otro — pero esa causa ya la resuelve el
    /// chequeo <c>detenida</c> del latch (atravesar el 0° a toda velocidad no cuenta como cierre).
    /// El flag quedó arreglando un bug que ya no existía, y lo único que hacía era dejar mudos
    /// todos los cierres físicos. NO reponerlo sin sacar antes el otro chequeo.
    /// </summary>
    private void OnDoorClose()
    {
        onDoorClose?.Invoke();
        PlayCloseClip();

        isOpen = false;
        doorOpenedBeyondThreshold = false;
        // El creak loop sigue armado (UpdateCreakAudio lo lleva a volumen 0 al frenar).
        // NO rb.isKinematic=true, NO enabled=false.
    }

    private void PlayCloseClip()
    {
        if (HasDoorWithKey) return;   // el one-shot de una puerta con llave lo maneja DoorWithKey

        AudioClip clip = AudioLibrary.instance != null
            ? AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Close)
            : null;
        if (clip != null) audioSource.PlayOneShot(clip);
    }

    // ── Director control API ───────────────────────────────────────────────────
    public void BeginDirectorControl(bool ignorePlayerCollision = true)
    {
        _directorControlled = true;
        Freeze();   // Freeze ya apaga el creak
        if (ignorePlayerCollision) SetPlayerCollisionIgnored(true);
    }

    public void EndDirectorControl()
    {
        _directorControlled = false;
        SetPlayerCollisionIgnored(false);       // clear simétrico obligatorio
        if (!isOpen && !locked) Revive();        // si quedó abierta, sigue congelada (susto)
    }

    // ── SetOpenState / Silent (save + Director) ────────────────────────────────
    /// <summary>
    /// Fija el estado abierto/cerrado.
    ///
    /// <paramref name="holdFrozen"/> decide QUÉ clase de puerta abierta queda:
    /// <list type="bullet">
    /// <item><b>true</b> (default, compat) — congelada: kinematic + capa no empujable. El jugador NO la puede
    /// empujar ni cerrar con E. Es para el susto donde la puerta tiene que ser inamovible, y para restaurar
    /// un save que la guardó así.</item>
    /// <item><b>false</b> — abierta pero VIVA: se queda en su ángulo igual (el auto-cierre está off por
    /// default) y el jugador la puede empujar y cerrar con E. Es lo que corresponde a los eventos ambientales
    /// desde que existe el cierre por interacción: congelarla le sacaba al jugador esa acción sin motivo.</item>
    /// </list>
    /// </summary>
    public void SetOpenState(bool open, bool holdFrozen = true)
    {
        StopCreakAudio();
        // `locked` MANDA sobre holdFrozen, en las dos ramas. Es la regla de la state-machine de esta
        // clase ("solo locked o _heldOpen congelan la puerta"), y esta era la única de las tres rutas
        // que reviven que se la salteaba: EndDirectorControl y EndPlayerAnimation ya consultan locked.
        //
        // El agujero se veía así: el portazo scripteado de una puerta TRABADA terminaba en
        // SetOpenState(false) -> Revive(), que le saca isKinematic y la devuelve a la capa "Door" — la
        // capa sobre la que actúa el colisionador de puertas del player. La puerta quedaba con el
        // candado puesto y empujable igual, así que el jugador entraba sin la llave.
        //
        // Quien necesite abrir una puerta trabada Y dejarla empujable tiene que destrabarla primero
        // (SetLocked(false)), que es lo que ya hacen TriggerAbrirLoop y ActaNacimiento.
        if (open)
        {
            isOpen = true;
            doorOpenedBeyondThreshold = true;

            if (holdFrozen || locked)
            {
                Freeze();
                _heldOpen = holdFrozen;   // trabada-y-no-held sigue congelada, pero no es "held open"
            }
            else
            {
                Revive();   // limpia _heldOpen, saca kinematic y devuelve la capa empujable
            }
        }
        else
        {
            isOpen = false;
            doorOpenedBeyondThreshold = false;
            _heldOpen = false;
            if (locked) Freeze();
            else Revive();
        }
    }

    /// <summary>
    /// Vida Lejana: fija estado + euler de una, sin audio ni tween.
    /// <paramref name="angleY"/> es RELATIVO a la posicion cerrada (0 = cerrada), igual que el resto
    /// del sistema. Ver <see cref="AngleFromClosed"/>.
    /// </summary>
    public void SetOpenStateSilent(bool open, float angleY, bool holdFrozen = true)
    {
        pivot.localEulerAngles = EulerForAngle(angleY);
        SetOpenState(open, holdFrozen);
    }

    // ── Helpers de animación del Director (DOTween sobre el pivot) ──────────────
    /// <summary>
    /// Anima el pivot hasta <paramref name="angleY"/> grados DESDE LA POSICION CERRADA (0 = cerrada).
    /// Es relativo, no absoluto: en la escena hay puertas autoradas con la hoja a -90° y a 180°, y
    /// tratarlo como euler absoluto las mandaba a girar 175°. Los callers ya pasaban valores con
    /// intencion relativa (0 para cerrar, 85/110 para abrir), asi que esto es lo que siempre quisieron.
    /// La animación corre con la puerta CONGELADA
    /// (kinematic + resorte off): un Rigidbody kinematic deja de ser gobernado por el HingeJoint, así que el
    /// tween escribe el ángulo sin pelearse con PhysX. El ángulo se clampea a los límites del hinge para que
    /// el joint no lo rebote al soltar.
    ///
    /// <paramref name="holdFrozen"/> decide cómo queda DESPUÉS: congelada (susto inamovible) o viva
    /// (se queda en su ángulo pero el jugador la puede empujar y cerrar con E). Ver <see cref="SetOpenState"/>.
    /// </summary>
    public void AnimateToAngle(float angleY, float dur, Ease ease = Ease.OutCubic,
        bool? endOpen = null, Action onComplete = null, bool holdFrozen = true)
    {
        pivot.DOKill(false);
        BeginDirectorControl(true);   // Freeze + StopCreak + ignore-collision

        Vector3 target = EulerForAngle(ClampToHingeLimits(angleY));

        bool completed = false;
        pivot.DOLocalRotate(target, dur, RotateMode.Fast).SetEase(ease)
            .OnComplete(() =>
            {
                completed = true;
                // Orden: primero el estado (que puede revivir la puerta), después soltar el control del
                // director — si no, EndDirectorControl vería isOpen viejo y decidiría mal.
                SetOpenState(endOpen ?? !Mathf.Approximately(angleY, 0f), holdFrozen);
                EndDirectorControl();
                onComplete?.Invoke();
            })
            .OnKill(() =>
            {
                if (!completed) EndDirectorControl();
            });
    }

    public void OpenTo(float angleY, float dur, Action onComplete = null, bool holdFrozen = true)
        => AnimateToAngle(angleY, dur, Ease.OutCubic, true, onComplete, holdFrozen);

    public void CloseTo(float dur, Action onComplete = null)
        => AnimateToAngle(0f, dur, Ease.InOutCubic, false, onComplete);

    /// <summary>
    /// CIERRA la puerta de un portazo (verbo opuesto al portazo de apertura del jugador, que es
    /// <see cref="TryBurstOpen"/>). <paramref name="angleY"/> es relativo a la posición cerrada.
    ///
    /// <paramref name="onComplete"/> corre SIEMPRE: al terminar la animación o si la interrumpen.
    /// Es a propósito — sus llamadores lo usan para confirmar estado (trabar la puerta), no para
    /// reaccionar a la animación, y perderlo por una interrupción deja la puerta en un estado
    /// intermedio sin que nadie se entere.
    /// </summary>
    public void Slam(float angleY = 0f, float dur = 0.1f, AudioClip clip = null, Action onComplete = null)
    {
        pivot.DOKill(false);
        BeginDirectorControl(true);

        Vector3 target = EulerForAngle(ClampToHingeLimits(angleY));

        bool c = false;
        pivot.DOLocalRotate(target, dur, RotateMode.Fast).SetEase(Ease.InQuart)
            .OnComplete(() =>
            {
                c = true;
                PlaySlamSound(clip);
                SetOpenState(false);
                EndDirectorControl();
                onComplete?.Invoke();
            })
            .OnKill(() =>
            {
                if (c) return;
                EndDirectorControl();
                // El callback corre TAMBIÉN si interrumpieron el portazo. Sus llamadores lo usan
                // para CONFIRMAR estado (trabar la puerta), no para festejar la animación: si solo
                // sonara al completar, un evento del Director que mate el tween dejaba una puerta
                // de un solo sentido destrabada para siempre, en silencio.
                // Va después de EndDirectorControl a propósito: con el mutex ya liberado,
                // SetLocked() puede congelar la hoja de verdad en vez de salirse por su early-return.
                onComplete?.Invoke();
            });
    }

    /// <summary>Clampea el ángulo objetivo a los límites del HingeJoint (si los usa). Avisa si excede.</summary>
    private float ClampToHingeLimits(float angleY)
    {
        if (_hinge == null || !_hinge.useLimits) return angleY;

        JointLimits limits = _hinge.limits;
        float clamped = Mathf.Clamp(angleY, limits.min, limits.max);
        if (!Mathf.Approximately(clamped, angleY))
            Debug.LogWarning($"[PhysicDoor] ángulo objetivo {angleY} fuera de los límites del " +
                $"HingeJoint [{limits.min}, {limits.max}] en '{name}'. Clampeado a {clamped}.", this);
        return clamped;
    }

    // ── Lock state + feedback de bump (D4, sin relay: el script está en la hoja) ──
    public void SetLocked(bool v)
    {
        locked = v;
        if (_directorControlled) return;
        if (v) Freeze();
        else Revive();
    }

    public bool IsLocked() => locked;

    // Llamado por DoorContactRelay (en el GO del Rigidbody/collider de la hoja) cuando el jugador
    // choca una puerta trabada. El relay existe porque PhysicDoor vive en el DoorGO y el collider
    // puede estar en un pivot hijo (layouts ≥3), donde OnCollisionEnter no llegaría a este componente.
    public void OnLockedBump()
    {
        if (!locked) return;
        if (Time.time - _lastBump < lockedBumpCooldown) return;
        _lastBump = Time.time;

        AudioClip clip = AudioLibrary.instance != null
            ? AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Locked)
            : null;
        if (clip != null) audioSource.PlayOneShot(clip);

        SetHandleHeld(true);                 // la manija baja…
        if (_rattleRoutine != null) StopCoroutine(_rattleRoutine);
        _rattleRoutine = StartCoroutine(RattleReset());   // …y vuelve
    }

    private IEnumerator RattleReset()
    {
        yield return new WaitForSeconds(lockedRattleDuration);
        SetHandleHeld(false);
        _rattleRoutine = null;
    }

    /// <summary>
    /// Baja la manija y la suelta tras <paramref name="holdSeconds"/>, SIN tocar la hoja: alguien del otro
    /// lado probando el picaporte.
    ///
    /// Existe aparte de <see cref="OnLockedBump"/> porque son dos cosas distintas: aquel es el feedback de UN
    /// choque del jugador y tiene cooldown (1,5 s) para no spamear; este es un gesto deliberado del Director,
    /// que necesita encadenar varios apretones seguidos y rápidos. Con el cooldown de OnLockedBump el gesto
    /// salía lento y perdía el nervio.
    /// </summary>
    public void PressHandle(float holdSeconds)
    {
        if (_rattleRoutine != null) StopCoroutine(_rattleRoutine);
        _rattleRoutine = StartCoroutine(PressHandleRoutine(holdSeconds));
    }

    private IEnumerator PressHandleRoutine(float holdSeconds)
    {
        SetHandleHeld(true);
        yield return new WaitForSeconds(holdSeconds);
        SetHandleHeld(false);
        _rattleRoutine = null;
    }

    // ── Contacto del jugador con la hoja (lo reenvía DoorContactRelay) ─────────
    /// <summary>
    /// El jugador tocó la hoja. Concentra las tres respuestas al contacto: forcejeo si está
    /// trabada, manija bajada mientras empuja, y portazo si venía corriendo. También cancela
    /// un cierre por interacción en curso — volver a chocar la puerta la "agarra" de nuevo.
    /// </summary>
    /// <param name="contactPoint">Punto del choque, en mundo.</param>
    /// <param name="pushVelocity">Velocidad del jugador en mundo (horizontal). Solo se usa como
    /// DIRECCIÓN, para saber hacia qué lado abre el portazo.</param>
    /// <param name="playerIsSprinting">Si el jugador viene corriendo. Es la condición del portazo.</param>
    public void OnPlayerContactEnter(Vector3 contactPoint, Vector3 pushVelocity, bool playerIsSprinting)
    {
        if (locked) { OnLockedBump(); return; }
        if (_directorControlled) return;

        SetHandleHeld(true);
        TryBurstOpen(contactPoint, pushVelocity, playerIsSprinting);
    }

    /// <summary>El jugador dejó de tocar la hoja: la manija vuelve.</summary>
    public void OnPlayerContactExit()
    {
        if (_rattleRoutine != null) return;   // un forcejeo en curso maneja su propio reset
        SetHandleHeld(false);
    }

    /// <summary>
    /// Dispara el golpe de portazo de esta puerta (<c>DoorSounds.Slam</c> según su DoorType).
    /// Existe para los cierres que resuelve la FÍSICA y no una animación: ahí nadie pasa por
    /// <see cref="Slam"/>, así que el sonido lo pide quien detecta el impacto. Sigue saliendo de
    /// AudioLibrary y de la AudioSource de la puerta — un solo dueño del audio.
    /// </summary>
    public void PlaySlam() => PlaySlamSound(null);

    /// <summary>
    /// Baja/sube la manija. Idempotente (el tween de DoorHandle es de 0.5s y re-dispararlo cada
    /// frame de contacto lo dejaría trabado). NO se llama cuando la puerta se mueve sola: una
    /// puerta que se cierra con la manija quieta es justamente lo que queremos que inquiete.
    /// </summary>
    private void SetHandleHeld(bool held)
    {
        // Manija fija (tirador, pomo soldado, barral): no gira nunca. Se sale acá y no en el caller
        // para que TODOS los caminos que bajan la manija (empuje, forcejeo de trabada, Freeze,
        // OnDisable) queden cubiertos por el mismo chequeo.
        if (fixedHandle) return;

        if (draggableObjectEvents == null || _handleHeld == held) return;
        _handleHeld = held;
        if (held) draggableObjectEvents.OnStartDrag();
        else draggableObjectEvents.OnStopDrag();
    }

    // ── Portazo al ABRIR (FÍSICO: impulso sobre la hoja viva) ──────────────────
    /// <summary>
    /// Entrar corriendo revienta la puerta para ABRIRLA. Es un IMPULSO sobre la hoja dinámica, no
    /// una animación: la hoja nunca se vuelve cinemática, así que frena contra lo que haya (el tope
    /// del hinge, la pared, algo apoyado detrás) y no hace falta declararle un ángulo de destino.
    ///
    /// De acá salieron los guards que existían solo por ser un tween: el de "solo si está casi
    /// cerrada" (una hoja cinemática barría al jugador; un Rigidbody no puede), el de "ya está
    /// abierta hacia ese lado" y el de "los límites no dejan recorrido" (ahora los resuelve el
    /// joint por su cuenta, sin fantasmas). Tampoco marca <c>isOpen</c> a mano: la puerta se mueve
    /// de verdad, así que <see cref="CheckIfDoorOpened"/> se entera solo.
    ///
    /// OJO: es el verbo OPUESTO al <see cref="Slam"/> del Director, que CIERRA de un portazo y sigue
    /// siendo un tween a propósito (un susto autorado tiene que verse igual siempre).
    /// </summary>
    private void TryBurstOpen(Vector3 contactPoint, Vector3 pushVelocity, bool playerIsSprinting)
    {
        if (rb == null || rb.isKinematic || _playerAnimating)
        { LogBurst($"descartado: puerta congelada (kinematic={rb == null || rb.isKinematic}, animando={_playerAnimating})"); return; }

        // La condición es "¿viene corriendo?", preguntada directamente a PlayerMovement. Antes se
        // deducía de la magnitud de la velocidad, que además de indirecto era frágil: dependía de
        // que sprintSpeed > moveSpeed, y hoy en el prefab del player es al revés (3 caminando vs
        // 2.4 corriendo), así que ningún umbral podía separarlos.
        if (!playerIsSprinting)
        { LogBurst("descartado: el jugador no venía corriendo"); return; }

        if (Time.time - _lastSlam < slamCooldown)
        { LogBurst($"descartado: cooldown ({Time.time - _lastSlam:0.00}s de {slamCooldown:0.00}s)"); return; }

        // Sin hinge no hay eje sobre el que imprimir la velocidad angular.
        if (_hinge == null)
        { LogBurst("descartado: la puerta no tiene HingeJoint"); return; }

        float sign = BurstOpenSign(contactPoint, pushVelocity);
        if (Mathf.Approximately(sign, 0f))
        { LogBurst("descartado: entraste casi en línea con la bisagra, el lado es ambiguo"); return; }

        LogBurst($"PORTAZO (corriendo): {burstAngularVelocity:0.0} rad/s hacia {(sign > 0f ? "+" : "-")} " +
            $"desde {AngleFromClosed():0.0}°; la frena lo que encuentre"
            + (AudioLibrary.instance != null && AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Slam) != null
                ? "" : "  ⚠ SIN CLIP DE SLAM: el golpe es MUDO"));

        _lastSlam = Time.time;
        BeginBurst(sign);
    }

    /// <summary>
    /// Abre la ventana de portazo: pone los valores livianos y le imprime la velocidad angular.
    /// La hoja sigue DINÁMICA todo el tiempo — no hay Freeze, no hay IgnoreCollision con el jugador.
    /// </summary>
    private void BeginBurst(float sign)
    {
        _bursting = true;
        _burstStartTime = Time.time;

        rb.angularDamping = burstAngularDamping;
        rb.maxAngularVelocity = burstMaxAngularVelocity;
        if (burstSolverVelocityIterations > 0)
            rb.solverVelocityIterations = burstSolverVelocityIterations;

        // Se ESCRIBE la velocidad angular (equivalente a ForceMode.VelocityChange) en vez de aplicar
        // un torque: un torque depende de la masa y del tensor de inercia de cada hoja, y las de la
        // casa usan tensor implícito (Unity lo calcula del collider), así que la misma fuerza daba
        // portazos distintos por puerta. Escribiéndola, el gesto es idéntico en todas.
        rb.angularVelocity = HingeAxisWorld() * (sign * burstAngularVelocity);
        _lastBurstSpeed = burstAngularVelocity;
    }

    /// <summary>
    /// Cierra la ventana de portazo y devuelve la amortiguación pesada del empuje continuo.
    /// Idempotente: la llaman el settle, el impacto, el timeout, <see cref="Freeze"/> y OnDisable.
    /// </summary>
    private void EndBurst()
    {
        if (!_bursting) return;
        _bursting = false;

        if (rb == null) return;
        rb.angularDamping = doorAngularDrag;
        rb.maxAngularVelocity = doorMaxAngularVelocity;
        if (burstSolverVelocityIterations > 0)
            rb.solverVelocityIterations = _defaultSolverVelocityIterations;
    }

    /// <summary>
    /// Administra la ventana del portazo. Corre en FixedUpdate porque mide una diferencia de
    /// velocidad angular ENTRE PASOS DE FÍSICA: leerla desde Update daría el delta de un número
    /// variable de pasos y el umbral de impacto no significaría nada estable.
    /// </summary>
    private void FixedUpdate()
    {
        if (!_bursting) return;
        if (rb == null || rb.isKinematic) { EndBurst(); return; }

        float speed = rb.angularVelocity.magnitude;

        // Un frenazo en UN solo paso = la hoja pegó contra algo. Detectarlo así cubre con una sola
        // regla los dos finales posibles del portazo: el tope del HingeJoint (que NO emite
        // OnCollisionEnter, es una restricción del solver) y un collider real del mundo (que sí).
        // Se corta acá y no se espera al reposo: restaurar la amortiguación pesada en el impacto es
        // lo que hace que la hoja quede clavada donde pegó en vez de quedar temblando.
        if (_lastBurstSpeed - speed >= burstImpactSpeedDrop)
        {
            LogBurst($"impacto a {AngleFromClosed():0.0}° (de {_lastBurstSpeed:0.0} a {speed:0.0} rad/s)");
            PlaySlamSound(null);
            EndBurst();
            return;
        }
        _lastBurstSpeed = speed;

        if (speed <= burstSettleAngularSpeed)
        { LogBurst($"portazo terminado sin impacto: frenó sola a {AngleFromClosed():0.0}°"); EndBurst(); return; }

        if (Time.time - _burstStartTime >= burstMaxDuration)
        { LogBurst($"portazo cortado por timeout ({burstMaxDuration:0.0}s) a {AngleFromClosed():0.0}°"); EndBurst(); }
    }

    private void LogBurst(string mensaje)
    {
        if (!logBurstOpenDecisions) return;
        Debug.Log($"[PhysicDoor:{name}] {mensaje}", this);
    }

    // ── Ángulos: TODO es relativo a la posición cerrada (closedAngleY) ─────────
    /// <summary>Cuánto está abierta la hoja ahora, en grados con signo desde su posición cerrada.</summary>
    private float AngleFromClosed()
        => Mathf.DeltaAngle(closedAngleY, pivot.localEulerAngles.y);

    /// <summary>Euler local que corresponde a estar abierta <paramref name="angleFromClosed"/> grados.</summary>
    private Vector3 EulerForAngle(float angleFromClosed)
    {
        Vector3 e = pivot.localEulerAngles;
        e.y = closedAngleY + angleFromClosed;
        return e;
    }

    /// <summary>
    /// Eje del hinge en mundo. Lo usan el impulso del portazo, el signo del torque y el chequeo de
    /// barrido al cerrar: un solo lugar que sepa de dónde sale, en vez de tres TransformDirection
    /// sueltos que puedan divergir. Se recalcula en vez de cachearse porque el pivot puede colgar de
    /// una jerarquía que rote (las puertas del hospital cuelgan del prefab de pared).
    /// </summary>
    private Vector3 HingeAxisWorld() => pivot.TransformDirection(_hinge.axis).normalized;

    /// <summary>
    /// Hacia qué lado revienta la puerta: +1, -1, o <b>0 si no se puede determinar</b>. Sale del
    /// torque que ESE empuje produciría (r × F proyectado sobre el eje del hinge), así el lado lo
    /// decide la física real del choque y entrar desde cualquiera de las dos caras abre para el
    /// lado correcto, sin campo que alguien pueda dejar mal.
    ///
    /// Devuelve 0 cuando el choque cae casi en línea con la bisagra (torque ≈ 0): ahí el lado es
    /// genuinamente ambiguo y elegir uno fijo podría reventar la puerta HACIA el jugador. Preferimos
    /// no dar el portazo y dejar que el empuje físico normal resuelva.
    /// </summary>
    private float BurstOpenSign(Vector3 contactPoint, Vector3 pushVelocity)
    {
        if (_hinge == null) return 0f;

        Vector3 axisWorld = HingeAxisWorld();
        Vector3 lever = contactPoint - pivot.TransformPoint(_hinge.anchor);
        float torque = Vector3.Dot(Vector3.Cross(lever, pushVelocity.normalized), axisWorld);

        // El torque acá está en metros (el push va normalizado), así que el umbral se lee como
        // "el contacto cayó a menos de 2 cm de la línea de la bisagra".
        return Mathf.Abs(torque) < MinBurstTorqueLever ? 0f : Mathf.Sign(torque);
    }

    // ── Cierre por interacción (IInteractable) ─────────────────────────────────
    public void Interact()
    {
        // DoorWithKey vive en ESTE GameObject y también es IInteractable, así que InteractableRay
        // puede quedarse con cualquiera de los dos. Delegamos para que ambos caminos terminen igual.
        // (DoorWithKey se auto-destruye al desbloquear, así que después ya no hay ambigüedad.)
        // TryGetComponent devuelve el componente AUNQUE esté deshabilitado: las puertas de adorno
        // llevan un DoorWithKey con m_Enabled: 0 y sin llave asignada, así que sin chequear enabled
        // delegaban igual y abrían el menú de llaves — el jugador probaba llaves contra una puerta
        // que nunca se abre. Deshabilitado ⇒ cae al OnLockedBump de abajo (traqueteo de "está trabada").
        if (locked && TryGetComponent(out DoorWithKey doorWithKey) && doorWithKey.enabled)
        {
            doorWithKey.Interact();
            return;
        }

        if (locked) { OnLockedBump(); return; }
        if (!CanCloseNow) return;

        // Guard de ocupación: es LA diferencia entre cerrar y abrir. El portazo aleja la hoja del
        // jugador, pero el cierre la trae hacia el vano donde puede estar parado, y una animación
        // (hoja cinemática) se le cerraría ENCIMA. Si está en el barrido, no cerramos.
        if (IsCloseSweepBlocked())
        {
            OnCloseBlocked();
            return;
        }

        ClosePlayerDriven();
    }

    /// <summary>
    /// "No pude cerrarla porque estás en el medio". Feedback PROPIO, distinto del de puerta
    /// trabada: son dos significados diferentes y en un juego que se apoya en que el jugador
    /// interprete lo que oye, darles el mismo sonido enseña mal. Si no hay clip de Blocked
    /// asignado NO cae al de Locked — se queda mudo y habla solo el gesto de la manija.
    /// </summary>
    private void OnCloseBlocked()
    {
        if (Time.time - _lastBump < lockedBumpCooldown) return;
        _lastBump = Time.time;

        AudioClip clip = AudioLibrary.instance != null
            ? AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Blocked)
            : null;
        if (clip != null) audioSource.PlayOneShot(clip);

        SetHandleHeld(true);
        if (_rattleRoutine != null) StopCoroutine(_rattleRoutine);
        _rattleRoutine = StartCoroutine(RattleReset());
    }

    /// <summary>
    /// Cierra la hoja con una animación determinista. Reemplaza al resorte del HingeJoint: el
    /// resorte peleaba contra <c>doorAngularDrag</c> y quedaba sobreamortiguado, y este es un
    /// gesto puntual del jugador que conviene que sea siempre igual.
    /// </summary>
    private void ClosePlayerDriven()
    {
        pivot.DOKill(false);
        BeginPlayerAnimation();

        Vector3 euler = EulerForAngle(0f);   // 0 RELATIVO = la posicion cerrada de esta puerta

        bool completed = false;
        Tween tween = null;
        tween = pivot.DOLocalRotate(euler, closeDuration, RotateMode.Fast)
            .SetEase(Ease.InOutQuad)
            // El chequeo de ocupación se repite DURANTE el cierre, no solo al arrancar: la hoja
            // es cinemática estos 0.35s, así que si el jugador se mete en el vano a mitad de la
            // animación le pasaría por encima (con física eso no podía pasar). Se aborta y la
            // puerta queda donde está.
            .OnUpdate(() =>
            {
                if (completed || !IsCloseSweepBlocked()) return;
                OnCloseBlocked();
                tween.Kill();           // dispara el OnKill de abajo → suelta el lock
            })
            .OnComplete(() =>
            {
                completed = true;
                OnDoorClose();
                EndPlayerAnimation();
            })
            .OnKill(() => { if (!completed) EndPlayerAnimation(); });
    }

    /// <summary>
    /// ¿Hay algo de <c>closeBlockedBy</c> dentro del arco que va a barrer la hoja al cerrarse?
    /// Se resuelve en ángulos alrededor de la bisagra en vez de con volúmenes barridos: se mide
    /// dónde está el obstáculo respecto de la hoja y si el barrido hacia 0° lo cruza.
    /// </summary>
    private bool IsCloseSweepBlocked()
    {
        if (_closeBlockerMask == 0 || _hinge == null) return false;

        float doorAngle = AngleFromClosed();
        if (Mathf.Approximately(doorAngle, 0f)) return false;

        Vector3 hingePos = pivot.TransformPoint(_hinge.anchor);
        Vector3 axis = HingeAxisWorld();

        // Dirección y largo de la hoja: del eje al centro del collider, así sirve con cualquier
        // orientación de modelo sin hardcodear un eje local.
        Collider leaf = null;
        for (int i = 0; i < doorColliders.Length; i++)
            if (doorColliders[i] != null && !doorColliders[i].isTrigger) { leaf = doorColliders[i]; break; }
        if (leaf == null) return false;

        Vector3 leafVec = Vector3.ProjectOnPlane(leaf.bounds.center - hingePos, axis);
        float leafLength = leafVec.magnitude * 2f;   // el centro está a media hoja
        if (leafLength <= 0.01f) return false;

        int count = Physics.OverlapSphereNonAlloc(hingePos, leafLength, _sweepHits, _closeBlockerMask,
            QueryTriggerInteraction.Ignore);

        float sweepSign = -Mathf.Sign(doorAngle);   // el cierre va hacia 0°
        for (int i = 0; i < count; i++)
        {
            Vector3 toObstacle = Vector3.ProjectOnPlane(_sweepHits[i].bounds.center - hingePos, axis);
            if (toObstacle.sqrMagnitude < 0.0001f) return true;   // pegado a la bisagra

            float rel = Vector3.SignedAngle(leafVec, toObstacle, axis);
            // Está del lado hacia el que barre la hoja y dentro del recorrido que le falta.
            if (Mathf.Sign(rel) == sweepSign && Mathf.Abs(rel) <= Mathf.Abs(doorAngle) + closeBlockAngleMargin)
                return true;
        }
        return false;
    }

    /// <summary>
    /// La puerta está abierta lo suficiente y libre como para ofrecer "cerrar". Único criterio,
    /// compartido por <see cref="Interact"/> y por el hint 2D, para que lo que se ofrece y lo que
    /// hace la tecla nunca se desincronicen.
    /// </summary>
    private bool CanCloseNow =>
        !locked && !_directorControlled && !_heldOpen && rb != null && !rb.isKinematic
        && pivot != null
        && Mathf.Abs(AngleFromClosed()) >= minAngleToOfferClose;

    // ── IHoverUiHint (barra 2D: "[E] Cerrar" mientras mirás una puerta abierta) ──
    public bool HasHoverHints => CanCloseNow;

    public System.Collections.Generic.List<HintDisplayData> BuildHoverHints()
    {
        return new System.Collections.Generic.List<HintDisplayData>(1)
        {
            HintDisplayData.Make(HintType.Close, HintIconType.EIcon)
        };
    }

    // ── Lock de animación del JUGADOR (separado del mutex del Director) ────────
    /// <summary>
    /// Congela la hoja para animarla por acción del jugador. Deliberadamente NO usa
    /// <see cref="BeginDirectorControl"/>: ese toma <c>_directorControlled</c>, que es el mutex
    /// con el que la casa se reserva la puerta para un susto. Si el portazo del jugador lo
    /// agarrara, un evento del Director que entre en el medio se pelearía con él.
    ///
    /// Prioridad: el Director gana. Sus animaciones hacen <c>pivot.DOKill</c> primero, lo que
    /// dispara el OnKill de la del jugador y suelta este lock antes de que el Director congele.
    /// </summary>
    private void BeginPlayerAnimation()
    {
        _playerAnimating = true;
        Freeze();   // Freeze ya apaga el creak
        SetPlayerCollisionIgnored(true);
    }

    private void EndPlayerAnimation()
    {
        _playerAnimating = false;
        SetPlayerCollisionIgnored(false);
        if (!locked && !_directorControlled) Revive();
    }

    // ── Player collision ignore (jugador↔hoja) ─────────────────────────────────
    private void SetPlayerCollisionIgnored(bool ignore)
    {
        if (collisionIgnored == ignore) return;

        if (playerCollider == null)
        {
            playerCollider = PlayerManager.instance != null
                ? PlayerManager.instance.GetComponentInChildren<CapsuleCollider>()
                : null;
            if (playerCollider == null) return;
        }

        for (int i = 0; i < doorColliders.Length; i++)
        {
            if (doorColliders[i] != null)
                Physics.IgnoreCollision(doorColliders[i], playerCollider, ignore);
        }
        collisionIgnored = ignore;
    }

    // ── Creak audio (loop-based, portado de DraggableDoor) ─────────────────────
    private void StartCreakAudio()
    {
        if (_sinClipDeChirrido) return;   // ya sabemos que no hay clip: no volver a preguntar cada frame

        if (AudioLibrary.instance == null) return;   // el singleton todavía no despertó: reintentar más adelante

        AudioClip clip = AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Creak);
        if (clip == null)
        {
            _sinClipDeChirrido = true;
            Debug.LogWarning($"[PhysicDoor] '{name}' no tiene clip de chirrido para el tipo '{doorType}' en la " +
                             "AudioLibrary. Esta puerta va a quedar muda (una sola vez este aviso).", this);
            return;
        }

        isCreaking = true;
        creakVolumeVelocity = 0f;
        creakAudioSource.clip = clip;
        creakAudioSource.volume = 0f;
        creakAudioSource.pitch = 1f;
        creakAudioSource.Play();
    }

    private void StopCreakAudio()
    {
        isCreaking = false;
        if (creakAudioSource != null && creakAudioSource.isPlaying)
            creakAudioSource.Stop();
    }

    private void UpdateCreakAudio()
    {
        if (!isCreaking) return;

        float angularSpeed = Mathf.Abs(rb.angularVelocity.y);
        float targetVolume = angularSpeed > CreakMinAngularSpeed
            ? Mathf.Clamp01(angularSpeed * 0.3f) * creakMaxVolume
            : 0f;

        creakAudioSource.volume = Mathf.SmoothDamp(
            creakAudioSource.volume, targetVolume, ref creakVolumeVelocity, CreakVolumeSmoothTime);

        // Variación sutil de pitch por velocidad (rango angosto para evitar sonido metálico).
        float normalizedSpeed = Mathf.Clamp01(angularSpeed / doorMaxAngularVelocity);
        creakAudioSource.pitch = Mathf.Lerp(0.95f, 1.05f, normalizedSpeed);

        // Quieta y ya en silencio: soltar la voz de audio en vez de dejar el loop girando para siempre.
        if (angularSpeed <= CreakMinAngularSpeed && creakAudioSource.volume <= 0.001f)
            StopCreakAudio();
    }

    private void PlaySlamSound(AudioClip clip)
    {
        if (clip == null)
        {
            clip = AudioLibrary.instance != null
                ? AudioLibrary.instance.GetDoorSound(doorType, AudioLibrary.DoorSounds.Slam)
                : null;
        }
        if (clip != null) audioSource.PlayOneShot(clip);
    }

    // ── IInteractable3DHint ────────────────────────────────────────────────────
    public Icons GetIconType()
    {
        // Trabada sí muestra el candado: es información que el jugador necesita. Para "cerrar" NO
        // usamos ícono 3D — va a la barra de hints 2D (ver IHoverUiHint). Una casa llena de puertas
        // con un ícono flotando encima sería demasiado ruido para un juego de gestos mínimos.
        return locked ? Icons.Locked : Icons.None;
    }

    public Vector3 GetHintPosition() => SetHintPosition();

    private Vector3 SetHintPosition()
    {
        if (invertHintPosition == null)
            return hintPosition != null ? hintPosition.position : transform.position;
        if (hintPosition == null)
            return invertHintPosition.position;

        Vector3 playerPos = PlayerManager.instance.transform.position;
        float distToMain = (playerPos - hintPosition.position).sqrMagnitude;
        float distToInvert = (playerPos - invertHintPosition.position).sqrMagnitude;
        return distToInvert < distToMain ? invertHintPosition.position : hintPosition.position;
    }

    // ── Save System ────────────────────────────────────────────────────────────
    [Serializable]
    struct PhysicDoorData
    {
        public bool locked;
        public bool isOpen;
        public bool heldOpen;
        public bool doorOpenedBeyondThreshold;
        public Vector3 pivotEuler;
    }

    public object CaptureState()
    {
        return new PhysicDoorData
        {
            locked = locked,
            isOpen = isOpen,
            heldOpen = _heldOpen,
            doorOpenedBeyondThreshold = doorOpenedBeyondThreshold,
            pivotEuler = pivot != null ? pivot.localEulerAngles : Vector3.zero
        };
    }

    public void RestoreState(object state)
    {
        var data = SaveUtils.Deserialize<PhysicDoorData>(state);

        locked = data.locked;
        isOpen = data.isOpen;
        _heldOpen = data.heldOpen;

        if (rb != null) rb.isKinematic = true;           // congelar PRIMERO
        if (pivot != null) pivot.localEulerAngles = data.pivotEuler;

        doorOpenedBeyondThreshold = data.doorOpenedBeyondThreshold;
        // Contra closedAngleY, no contra 0: una puerta cuyo cerrado es -90 o 180 se restauraba SIEMPRE
        // marcada como "estuvo abierta", y con eso armado el latch le hacía sonar un cierre fantasma.
        if (Mathf.Abs(Mathf.DeltaAngle(closedAngleY, data.pivotEuler.y)) > CloseAngleThreshold)
            doorOpenedBeyondThreshold = true;

        if (locked) Freeze();
        else if (_heldOpen) Freeze();
        else Revive();                                    // limpia _heldOpen y re-arma el resorte
    }
}
