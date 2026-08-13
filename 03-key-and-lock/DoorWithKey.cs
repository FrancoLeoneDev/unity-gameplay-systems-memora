using DG.Tweening;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(PhysicDoor))]
[RequireComponent(typeof(SaveableEntity))]
[RequireComponent(typeof(InspectionIntensity))]

public class DoorWithKey : InteractableObject, IMenuInteractable, ISaveable, IUiHint
{
    private enum AxisToRotate { X, Y, Z }

    public bool InteractionSucceeded { get; private set; }

    /// <summary>La puerta terminó de desbloquearse. Sólo esta clase lo invoca.</summary>
    public event Action OnDoorUnlocked;
    /// <summary>El jugador interactuó con la cerradura (antes de saber si tiene la llave).</summary>
    public event Action OnInteracted;

    [Header("Door Settings")]
    [SerializeField] private Transform camPos;
    [SerializeField] private Transform keySpawnPoint;
    [SerializeField] private Transform bocaCerradura;

    [Header("Key Settings")]
    [SerializeField] private KeyData keyNeeded;
    [SerializeField] private float insertKeyDuration = 1.5f;
    [SerializeField] private float rotateKeyDuration = 1f;
    [SerializeField] private Vector3 keyFinalPosition;
    [SerializeField] private float rotateKeyValue;
    [SerializeField] private AxisToRotate axisToRotate;

    [Header("Timings de la secuencia")]
    [SerializeField, Tooltip("Pausa antes de que la llave arranque a viajar hacia la cerradura. " +
        "Le da aire al corte de cámara antes de que empiece el movimiento.")]
    private float delayBeforeInsert = 0.5f;
    [SerializeField, Tooltip("Cuánto se sostiene el plano después del giro, con el sonido de destrabe, " +
        "antes de cerrar el menú y devolverle el control al jugador.")]
    private float delayBeforeExit = 1f;

    private GameObject key;
    private PhysicDoor physicDoor;
    private PhysicDoor secondPhysicDoor;
    private DoorWithKeyPartner keyPartner;

    protected override void Awake()
    {
        physicDoor = GetComponent<PhysicDoor>();
        iconType = Icons.Locked;
    }

    public override void Interact()
    {
        OnInteracted?.Invoke();
        MenuInteractionCamera.MoveTo(this, camPos);
        OpenMenu();
        // Los hints los muestra InteractionsMenu (centralizado): A/D Seleccionar · Espacio Usar · Click der. Salir.
    }

    public void OpenMenu()
    {
        InteractionsMenuManager.instance.OpenMenu(this);
    }

    /// <summary>Guard de re-entrada: "ya se usó una llave acá", distinto del veredicto del intento.</summary>
    private bool keyAlreadyUsed;

    public bool UseItem(ScriptableObject selectedItem)
    {
        if (keyAlreadyUsed)
        {
            Debug.LogWarning("Ya se ha usado una llave o hay una transición en curso.", this);
            return false;
        }

        if (!(selectedItem is KeyData keydata))
            return false;                        // no es una llave → ítem incorrecto, sin ruido

        if (keydata != keyNeeded)
            return false;                        // es una llave, pero no LA llave

        // A partir de acá la llave SE CONSUME, así que primero nos aseguramos de poder terminar.
        keyAlreadyUsed = true;
        InteractionSucceeded = true;
        PlayerManager.instance.CanMove(false);
        Inventory.instance.DeleteKeyItem(keydata);

        // Cableado incompleto (falta el punto de spawn o el prefab de la llave): NO abortamos.
        // La llave ya se consumió y esta puerta puede ser paso obligado — dejarla trabada sería un
        // soft-lock por un campo vacío en el Inspector. Se salta la animación, se loguea fuerte y
        // la puerta se abre igual: la partida siempre tiene que poder seguir.
        GameObject keyPrefab = keydata.GetPrefab();
        if (keySpawnPoint == null || keyPrefab == null)
        {
            Debug.LogError($"[DoorWithKey] '{name}' sin cablear: keySpawnPoint={(keySpawnPoint == null ? "NULL" : "ok")}, " +
                           $"prefab de {keydata.name}={(keyPrefab == null ? "NULL" : "ok")}. " +
                           "Se abre la puerta sin animación para no dejar la partida trabada.", this);
            BeginOpenSequence();
            return true;
        }

        key = Instantiate(keyPrefab, keySpawnPoint.position, keySpawnPoint.rotation);
        key.transform.SetParent(transform);
        key.transform.SetLayerRecursively(LayerMask.NameToLayer("Default"));

        // Esta llave es SÓLO el visual de la animación, no una llave del mundo: su lógica no debe correr.
        // El prefab de una llave trae su componente Key, y el Start de Key hace Destroy(gameObject) si esa
        // llave YA fue obtenida — que acá es siempre, porque la estás usando: la llave se borraba sola a
        // mitad del viaje hacia la cerradura. Los colliders tampoco corresponden: chocarían con la hoja
        // mientras entra. Se apagan ANTES del primer frame, así ningún Start alcanza a ejecutarse.
        foreach (var interactuable in key.GetComponentsInChildren<InteractableObject>(true))
            interactuable.enabled = false;
        foreach (var collider in key.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        StartCoroutine(InsertKey());
        return true;
    }
    public void SetSecondDoor(PhysicDoor door, DoorWithKeyPartner doorWithKeyPartner)
    {
        secondPhysicDoor = door;
        keyPartner = doorWithKeyPartner;
    }


    // Toda la cadena llave→cerradura→abrir tiene que terminar SÍ O SÍ en OpenAndExit: la llave ya
    // se consumió, así que cortarse a la mitad (tween matado por un cambio de escena, un DOKill de
    // otro sistema, la llave destruida) dejaría la puerta trabada para siempre y sin llave.
    // Por eso cada paso tiene su OnKill que salta al siguiente, y este latch evita abrir dos veces.
    private bool openSequenceStarted;

    IEnumerator InsertKey()
    {
        Vector3 finalPosition = keySpawnPoint.position + keyFinalPosition;
        yield return new WaitForSeconds(delayBeforeInsert);

        if (key == null) { BeginOpenSequence(); yield break; }

        bool completed = false;
        key.transform.DOMove(finalPosition, insertKeyDuration)
            .OnComplete(() =>
            {
                completed = true;
                if (bocaCerradura != null && key != null)
                    key.transform.SetParent(bocaCerradura);
                RotateKey();
            })
            .OnKill(() => { if (!completed) BeginOpenSequence(); });
    }

    void RotateKey()
    {
        Transform rotator = bocaCerradura != null ? bocaCerradura : (key != null ? key.transform : null);
        if (rotator == null) { BeginOpenSequence(); return; }

        bool completed = false;
        rotator.DORotate(RotateKeyValue(), rotateKeyDuration, RotateMode.LocalAxisAdd)
            .SetEase(Ease.OutCubic)
            .OnComplete(() => { completed = true; BeginOpenSequence(); })
            .OnKill(() => { if (!completed) BeginOpenSequence(); });
    }

    private void BeginOpenSequence()
    {
        if (openSequenceStarted) return;
        openSequenceStarted = true;
        StartCoroutine(OpenAndExit());
    }

    private Vector3 RotateKeyValue()
    {
        switch (axisToRotate)
        {
            case AxisToRotate.X:
                return new Vector3(rotateKeyValue, 0f, 0f);
            case AxisToRotate.Y:
                return new Vector3(0f, rotateKeyValue, 0f);
            case AxisToRotate.Z:
                return new Vector3(0f, 0f, rotateKeyValue);
            default:
                return Vector3.zero;
        }
    }

    IEnumerator OpenAndExit()
    {
        // Null-safe: el audio es decoración, y que falte un clip o un singleton NO puede impedir
        // que la puerta se abra — la llave ya se consumió.
        if (physicDoor != null && AudioLibrary.instance != null && AudioManager2D.instance != null)
        {
            AudioClip clip = AudioLibrary.instance.GetDoorSound(physicDoor.GetDoorType(), AudioLibrary.DoorSounds.Unlock);
            if (clip != null) AudioManager2D.instance.PlayOneShot(clip);
        }
        yield return new WaitForSeconds(delayBeforeExit);

        // Mismo criterio null-safe que el audio: el menú es UI, y que su singleton no esté no puede
        // impedir que la puerta se abra — la llave ya se consumió.
        if (InteractionsMenuManager.instance != null)
            InteractionsMenuManager.instance.CloseMenu();
        OpenDoor();
    }

    public void DestroyKeyPartner()
    {
        if(keyPartner != null)
        {
            Destroy(keyPartner);
        }
    }

    public void OpenDoor()
    {
        physicDoor.SetLocked(false);
        if (secondPhysicDoor != null && keyPartner != null)
        {
            secondPhysicDoor.SetLocked(false);
            DestroyKeyPartner();
        }
        // Guardado ad-hoc al abrir la puerta con su llave: gateado por el set-piece del clímax.
        SaveManager.Instance?.TrySave();
        OnDoorUnlocked?.Invoke();
        if (key != null)
        {
            Destroy(key);
        }
        // Strip the lock logic but keep the door mesh/GameObject. Route through DestroySaveable so
        // this ISaveable is removed from its SaveableEntity cache (a bare Destroy(this) would strand
        // a dead reference that crashes the next save/restore — same class as the door-knocker bug).
        SaveableEntity.DestroySaveable(this);
    }

    public object CaptureState()
    {
        return physicDoor.IsLocked();
    }

    public void RestoreState(object state)
    {
        bool locked = SaveUtils.Deserialize<bool>(state);
        physicDoor.SetLocked(locked);

        if (!locked)
        {
            if (secondPhysicDoor != null && keyPartner != null)
            {
                secondPhysicDoor.SetLocked(false);
                SaveableEntity.MarkForDeferredAction(() =>
                {
                    if (keyPartner != null) Destroy(keyPartner);
                });
            }
            SaveableEntity.MarkForDeferredAction(() =>
            {
                if (this != null) Destroy(this);
            });
        }
    }

    public List<HintDisplayData> GetHintDisplayDataList()
    {
        return new List<HintDisplayData>()
        {
            HintDisplayData.Make(HintType.Select, HintIconType.WASDIcon),
            HintDisplayData.Make(HintType.Use, HintIconType.EIcon),
            HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon),
        };
    }
}


