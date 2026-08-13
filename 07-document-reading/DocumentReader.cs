// Sistema de lectura de documentos estilo RE Requiem (plans/sistema-lectura-documentos.md):
// fade a negro → la cámara aparece frente al documento IN SITU (el documento nunca se mueve)
// → fade de vuelta con el panel lateral mostrando la traducción. Sin lerps de cámara: todo
// el movimiento es vía MoveCamPuzzles.MoveToPoseInstant / BackToOriginPosInstant (fades).
// Reemplaza a InspectNotes, LookObjectManager y ActiveLookObject (tres sistemas unificados).

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DocumentReader : MonoBehaviour, IUiHint
{
    public static DocumentReader instance { get; private set; }

    private enum ReaderState { Idle, Approaching, Reading, Returning }

    [Header("Lectura")]
    [Tooltip("Encender la luz de inspección del rig de cámara al llegar al documento (ayuda en cuartos oscuros).")]
    [SerializeField] private bool useInspectionLight = true;

    private ReaderState state = ReaderState.Idle;
    private Note currentNote;

    private void Awake()
    {
        instance = this;
        enabled = false;
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    private void Update()
    {
        if (InputHandler.instance == null) return;

        if (state == ReaderState.Reading && InputHandler.instance.CancelPressed)
        {
            BeginExit();
        }
    }

    public List<HintDisplayData> GetHintDisplayDataList()
    {
        return new List<HintDisplayData>()
        {
            HintDisplayData.Make(HintType.Exit, HintIconType.RightClickIcon),
        };
    }

    #region API

    /// <summary>Lee una nota in situ: fade → cámara al camPos autorado → panel con el contenido localizado.</summary>
    public void Read(Note note)
    {
        if (!CanStart() || note == null || note.CamPos == null) return;

        NoteData data = note.GetNoteData();
        if (data == null)
        {
            Debug.LogWarning($"[DocumentReader] La nota '{note.name}' no tiene NoteData asignado.", note);
            return;
        }

        currentNote = note;
        if (data.isPickable)
        {
            AddToInventoryOnce(data);
        }
        note.OnReadStarted();

        BeginApproach(new Pose(note.CamPos.position, note.CamPos.rotation), () =>
        {
            if (DocumentPanelUI.Instance != null)
                DocumentPanelUI.Instance.ShowNote(data);
            else
                Debug.LogWarning("[DocumentReader] No hay DocumentPanelUI en escena — armar el canvas del panel y agregar el componente.");
        }, InspectionIntensity.Resolve(note));
    }

    /// <summary>Lectura con pose de cámara autorada y texto plano (placas, carteles — ReadableObject).</summary>
    public void ReadPlain(Transform cameraPose, string body, string title = null, float intensity = -1f)
    {
        if (!CanStart() || cameraPose == null) return;

        BeginApproach(new Pose(cameraPose.position, cameraPose.rotation), () =>
        {
            if (DocumentPanelUI.Instance != null)
                DocumentPanelUI.Instance.ShowPlain(body, title);
            else
                Debug.LogWarning("[DocumentReader] No hay DocumentPanelUI en escena — armar el canvas del panel y agregar el componente.");
        }, intensity);
    }

    /// <summary>Acercamiento de cámara SIN panel de texto (mirar algo de cerca: calendario, manual, etc.).</summary>
    public void Look(Transform cameraPose, float intensity = -1f)
    {
        if (!CanStart() || cameraPose == null) return;

        BeginApproach(new Pose(cameraPose.position, cameraPose.rotation), null, intensity);
    }

    #endregion

    #region Flujo interno

    private bool CanStart()
    {
        if (state != ReaderState.Idle) return false;
        if (MoveCamPuzzles.Instance == null || MoveCamPuzzles.Instance.IsMoving) return false;
        return true;
    }

    private void BeginApproach(Pose pose, System.Action showContent, float intensity = -1f)
    {
        state = ReaderState.Approaching;
        enabled = true;

        if (GameManager.instance != null)
        {
            GameManager.instance.SetInteracting(true);
            GameManager.instance.DesactiveInteractions(true);
        }

        MoveCamPuzzles.Instance.MoveToPoseInstant(
            pose.position,
            pose.rotation,
            useInspectionLight,
            whileBlack: () =>
            {
                // El panel, los hints y el cursor se montan en negro: al abrir los ojos ya están ahí.
                // Mouse visible durante la lectura (igual que el sistema viejo): el ScrollRect del
                // panel scrollea nativo con la rueda. BackToOriginPosInstant lo oculta al salir.
                showContent?.Invoke();

                if (UIButtonsManager.instance != null)
                    UIButtonsManager.instance.ShowHints(GetHintDisplayDataList());

                if (GameManager.instance != null)
                    GameManager.instance.ShowMouse(true);
            },
            onArrived: () =>
            {
                state = ReaderState.Reading;
            },
            intensity: intensity);
    }

    private void BeginExit()
    {
        state = ReaderState.Returning;

        if (UIButtonsManager.instance != null)
            UIButtonsManager.instance.HideHints();

        if (DocumentPanelUI.Instance != null)
            DocumentPanelUI.Instance.Hide();

        Note finished = currentNote;
        currentNote = null;

        if (finished != null)
            finished.OnReadEnded();

        StartCoroutine(ExitRoutine(finished));
    }

    private IEnumerator ExitRoutine(Note finished)
    {
        yield return StartCoroutine(MoveCamPuzzles.Instance.BackToOriginPosInstant(whileBlack: () =>
        {
            // La nota pickable desaparece del mundo con la pantalla en negro (ya está en el inventario).
            if (finished != null)
            {
                NoteData data = finished.GetNoteData();
                if (data != null && data.isPickable)
                    Destroy(finished.gameObject);
            }
        }));

        FinishExit();
    }

    private void FinishExit()
    {
        if (GameManager.instance != null)
        {
            GameManager.instance.SetInteracting(false);
            GameManager.instance.DesactiveInteractions(false);
        }

        state = ReaderState.Idle;
        enabled = false;
    }

    private void AddToInventoryOnce(NoteData data)
    {
        if (Inventory.instance == null) return;

        Inventory.instance.AddNote(data);
    }

    #endregion
}
