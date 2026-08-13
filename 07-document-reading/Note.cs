using UnityEngine;

/// <summary>
/// Nota leíble en el mundo. Al interactuar, <see cref="DocumentReader"/> teletransporta la
/// cámara (con fade) al <see cref="camPos"/> autorado y muestra el texto en el panel de
/// documentos. La nota nunca se mueve del mundo.
///
/// Autoría: crear un Transform hijo posicionado donde va la cámara mirando a la nota
/// (tip: acomodar la Scene View y usar GameObject &gt; Align With View) y asignarlo en camPos.
///
/// Subclases (ej: ActaNacimiento, ActaDefuncion) pueden engancharse al ciclo de lectura
/// sobreescribiendo <see cref="OnReadStarted"/> / <see cref="OnReadEnded"/>.
/// </summary>
[RequireComponent(typeof(SaveableEntity))]
[RequireComponent(typeof(InspectionIntensity))]

public class Note : InteractableObject, ISaveable
{
    [Header("Nota")]
    [SerializeField] private NoteData noteData;

    [Tooltip("Pose de cámara para leer esta nota (Transform hijo posicionado mirando a la nota). Obligatorio — mismo patrón que Calendar/Manual/placas.")]
    [SerializeField] private Transform camPos;

    private bool pickedUp = false;

    public NoteData GetNoteData() => noteData;
    public Transform CamPos => camPos;

    protected override void Awake()
    {
        iconType = Icons.Loupe;
    }

    public override void Interact()
    {
        if (DocumentReader.instance == null)
        {
            Debug.LogWarning($"[Note] No hay DocumentReader en escena. '{name}' no se puede leer.", this);
            return;
        }

        if (camPos == null)
        {
            Debug.LogError($"[Note] La nota '{name}' no tiene camPos asignado — crear un Transform hijo con la pose de cámara y asignarlo.", this);
            return;
        }

        if (noteData != null && noteData.isPickable)
            pickedUp = true;

        DocumentReader.instance.Read(this);
    }

    /// <summary>Llamado por DocumentReader cuando arranca la lectura (antes del fade).</summary>
    public virtual void OnReadStarted() { }

    /// <summary>Llamado por DocumentReader al salir de la lectura (antes de que la cámara vuelva).</summary>
    public virtual void OnReadEnded() { }

    public object CaptureState()
    {
        return pickedUp;
    }

    public void RestoreState(object state)
    {
        pickedUp = SaveUtils.Deserialize<bool>(state);

        // Solo las notas pickable desaparecen del mundo. Antes este destroy corría también
        // para notas no-pickable (placas, carteles) y las hacía desaparecer al cargar partida.
        if (pickedUp && noteData != null && noteData.isPickable)
        {
            SaveableEntity.MarkForDestroy(gameObject);
        }
    }
}
