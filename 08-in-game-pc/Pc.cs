// Implements: refactor-input-system-memora.md — Cluster D (Puzzles)
// Migrated from UnityEngine.Input / KeyCode to InputHandler Puzzle context.
// SetContext(Puzzle) prevents player movement keys from firing while using the PC.

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

[RequireComponent(typeof(ActivarPc))]
[RequireComponent(typeof(SaveableEntity))]
public class Pc : MonoBehaviour, ISaveable
{
    [Header("CanvasPC")]
    [SerializeField] private GameObject canvasPC;

    [Header("Sound")]
    [SerializeField] private AudioClip bootingSound;
    [SerializeField] private AudioClip errorSound;

    [Header("ScreenRenderer")]
    [SerializeField] private Renderer screenRenderer;
    [SerializeField] private Material newScreenMaterial;
    private Material originalMaterial;
    [SerializeField] private GameObject screenLight;

    [Header("Password")]
    [SerializeField] private GameObject passwordPanel;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private GameObject wrongPasswordText;
    [SerializeField] private Button okButton;
    private const string correctPassword = "sofia";
    private bool passwordCorrect = false;

    [Header("Desk")]
    [SerializeField] private GameObject deskPanel;
    [SerializeField] private Button mailButton;
    [SerializeField] private Button camerasButton;


    [Header("Mail Section")]
    [SerializeField] private GameObject mailPanel;

    [Header("Camera Section")]
    [SerializeField] private GameObject cameraPanel;

    [Header("Print")]
    [SerializeField] private Button printImageButton;
    [SerializeField] private Printer printer;
    [SerializeField] private GameObject warningPaper;
    [SerializeField] private Button okButtonWarning;
    [SerializeField] private GameObject triggerEvent;
    private bool firstTimeNoPaper;

    private bool interacting;
    private bool fuseSubscribed;
    private void Awake()
    {
        originalMaterial = screenRenderer.material;

        passwordInput.onValueChanged.AddListener(delegate { ToUppercase(); });

        if(printImageButton != null && printer != null)
            printImageButton.onClick.AddListener(Print);

        if(okButton != null)
        {
            okButton.onClick.AddListener(CheckPassword);
        }

        TrySubscribeFuse();
    }

    private void TrySubscribeFuse()
    {
        // FuseManager.instance puede ser null si Pc.Awake corre antes que FuseManager.Awake
        // (orden de Awake entre objetos de escena indefinido) → antes NREaba y abortaba el resto
        // del Awake (dejando los botones sin cablear). Reintenta en OnEnable (1ª activación del PC:
        // ya corrieron todos los Awake). Idempotente vía fuseSubscribed.
        if (fuseSubscribed || FuseManager.instance == null) return;
        FuseManager.instance.OnZonesToggled += TurnOffPc;
        fuseSubscribed = true;
    }

    // Snapshot del PC creado en runtime; lo guardamos para liberarlo (evita fuga de VRAM).
    private Texture2D currentScreenTexture;

    // Resolución a la que se RENDERIZA el escritorio, en la orientación en que el jugador lo ve
    // (apaisado). El canvas se dibuja directo a este tamaño, así que su layout se recalcula al aspecto
    // del monitor: entra todo, sin recortar ni deformar. Después se rota 90° porque los UV de la malla
    // "Monitor" están rotados (medido: U corre a lo ALTO de la pantalla y V a lo ANCHO), y esa rotación
    // es la que endereza la imagen.
    //
    // El aspecto NO es el de la malla pelada (0.439 × 0.384 = 1.144): hay que corregirlo por el tiling
    // del material, que no es 1:1 (2.65 × 1.91 sobre spans UV de 0.393 × 0.522 -> cubre 1.042 × 0.997).
    //   ancho/alto = (v_span·tilingV · alto_malla) / (u_span·tilingU · ancho_malla) invertido = ~1.195
    // Si cambia el modelo del monitor o el tiling del material, se re-mide y se ajusta acá.
    [SerializeField, Tooltip("Resolución del render del escritorio, APAISADA (como se ve en el monitor). " +
        "Se rota 90° después, para compensar los UV rotados de la malla. 306×256 = 1.195, que es el " +
        "aspecto de la pantalla ya corregido por el tiling del material.")]
    private Vector2Int captureSize = new Vector2Int(306, 256);

    private void OnDestroy()
    {
        if (FuseManager.instance != null)
            FuseManager.instance.OnZonesToggled -= TurnOffPc;

        if (currentScreenTexture != null)
            Destroy(currentScreenTexture);
    }


    private void Start()
    {
        if(mailButton == null || camerasButton == null)
            return;

        okButtonWarning.onClick.AddListener(FirstTimeNoPaperEvent);
    }

    private void OnEnable()
    {
        TrySubscribeFuse();
        StartCoroutine(Init());
    }

    private IEnumerator Init()
    {
        yield return StartCoroutine(FadeManager.instance.FadeIn());

        canvasPC.SetActive(true);
        PlayerManager.instance.CanMove(false);
        GameManager.instance.ShowMouse(true);
        // Disable player movement keys while using the PC so WASD doesn't fire gameplay actions
        InputHandler.instance.SetContext(InputHandler.InputContext.Puzzle);

        if (passwordCorrect)
        {
            ShowDesk();
        }
        else
        {
            deskPanel.SetActive(false);
            passwordPanel.SetActive(true);
            passwordInput.text = "";
            passwordInput.ActivateInputField();
        }

        if (!screenLight.activeSelf)
            screenLight.SetActive(true);

        yield return StartCoroutine(FadeManager.instance.FadeOut());
    }

    private void Update()
    {
        if (InputHandler.instance.PuzzleConfirmPressed)
        {
            CheckPassword();
        }

        if (InputHandler.instance.PuzzleCancelPressed)
        {
            ExitPC();
        }
    }

    /// <summary>Latch de salida: `interacting` NO servía como tal (se prende en NoPaper y se apaga en
    /// FirstTimeNoPaperEvent), y el `enabled = false` recién llega en la última línea de Exit(), así que
    /// el Update seguía vivo toda la corrutina: cancelar dos veces arrancaba DOS Exit() encimadas, con
    /// dos fades concurrentes sobre el mismo CanvasGroup.</summary>
    private bool exiting;

    private void ExitPC()
    {
        if (interacting || exiting)
            return;

        exiting = true;
        StartCoroutine(Exit());
    }

    private void ToUppercase()
    {
        passwordInput.text = passwordInput.text.ToUpper();
    }

    private void FirstTimeNoPaperEvent()
    {
        interacting = false;

        if (!firstTimeNoPaper)
        {
            firstTimeNoPaper = true;
            triggerEvent.SetActive(true);
        }
    }

    private void CheckPassword()
    {
        if (passwordCorrect || string.IsNullOrEmpty(passwordInput.text))
            return;

        string correctPasswordUpper = correctPassword.ToUpper();

        if (passwordInput.text == correctPasswordUpper)
        {
            CorrectPassword();
        }
        else
        {
            StartCoroutine(WrongPassword());
        }

        passwordInput.text = "";
        passwordInput.ActivateInputField();
    }

    private void CorrectPassword()
    {
        passwordCorrect = true;
        AudioManager2D.instance.PlayOneShot(bootingSound);
        ShowDesk();
    }

    private IEnumerator WrongPassword()
    {
        float delayMessage = 3f;
        AudioManager2D.instance.PlayOneShot(errorSound);
        wrongPasswordText.SetActive(true);
        yield return new WaitForSeconds(delayMessage);
        wrongPasswordText.SetActive(false);
    }

    private void OnDisable()
    {
        // Safety: always restore Player context when the PC is deactivated (e.g. power cut)
        if (InputHandler.instance != null)
            InputHandler.instance.SetContext(InputHandler.InputContext.Player);
    }

    private IEnumerator Exit()
    {
        // El snapshot va DESPUÉS del fundido a negro: para renderizarlo hay que pasar el canvas a
        // ScreenSpaceCamera por unos frames, y ese cambio lo saca de la pantalla del jugador. Con la
        // pantalla ya en negro el ida y vuelta no se ve. (Antes se capturaba de la pantalla real, así
        // que tenía que ser antes del fundido.)
        yield return StartCoroutine(FadeManager.instance.FadeIn());

        yield return StartCoroutine(TakeSnapshotAndApplyToScreenRenderer());

        PlayerManager.instance.CanMove(true);
        GameManager.instance.SetInteracting(false);
        GameManager.instance.ShowMouse(false);
        InputHandler.instance.SetContext(InputHandler.InputContext.Player);
        canvasPC.SetActive(false);

        yield return StartCoroutine(FadeManager.instance.FadeOut());
        enabled = false;
        exiting = false;
    }

    /// <summary>
    /// Deja en la pantalla del monitor lo último que el jugador vio en el escritorio.
    ///
    /// Se RENDERIZA el canvas a una RenderTexture con el aspecto de la pantalla, en vez de capturar la
    /// pantalla del juego con ReadPixels. Capturando la pantalla, la imagen venía en 16:9 y había que
    /// meterla en una pantalla de ~1.14:1: como se recortaba centrado para no deformar, sobrevivía
    /// menos de la mitad del ancho y el escritorio quedaba con un zoom del doble. Renderizando al
    /// tamaño destino, el CanvasScaler recalcula el layout a ese aspecto: entra todo, sin recorte,
    /// sin barras y sin deformar.
    ///
    /// El canvas del jugador NO se toca de forma permanente: se le cambia el modo por unos frames y se
    /// restaura en el finally, pase lo que pase.
    /// </summary>
    private IEnumerator TakeSnapshotAndApplyToScreenRenderer()
    {
        screenRenderer.material = newScreenMaterial;

        Canvas canvas = canvasPC != null ? canvasPC.GetComponent<Canvas>() : null;
        if (canvas == null)
        {
            Debug.LogError("[Pc] El canvas del PC no tiene componente Canvas: no se puede dejar la " +
                           "captura en la pantalla del monitor.", this);
            yield break;
        }

        RenderMode modoOriginal = canvas.renderMode;
        Camera camaraOriginal = canvas.worldCamera;
        float planoOriginal = canvas.planeDistance;

        RenderTexture destino = null;
        GameObject camaraSnapshot = null;
        Texture2D plana = null;

        try
        {
            destino = new RenderTexture(captureSize.x, captureSize.y, 24, RenderTextureFormat.ARGB32);

            camaraSnapshot = new GameObject("PcSnapshotCamera");
            Camera camara = camaraSnapshot.AddComponent<Camera>();
            camara.orthographic = true;
            camara.clearFlags = CameraClearFlags.SolidColor;
            camara.backgroundColor = Color.black;
            camara.cullingMask = 1 << canvasPC.layer;   // sólo la UI del PC, nada del mundo
            camara.nearClipPlane = 0.01f;
            camara.farClipPlane = 10f;
            camara.targetTexture = destino;

            // En HDRP una cámara sin configurar arrastra cielo, niebla y post del volumen global.
            // Para un render de UI plana no queremos nada de eso.
            HDAdditionalCameraData datosHD = camaraSnapshot.AddComponent<HDAdditionalCameraData>();
            datosHD.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            datosHD.backgroundColorHDR = Color.black;
            datosHD.volumeLayerMask = 0;
            datosHD.probeLayerMask = 0;

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camara;
            canvas.planeDistance = 1f;

            // El CanvasScaler recalcula el layout recién cuando el canvas se reconstruye: sin esperar,
            // el render saldría con el layout viejo (el de 16:9).
            Canvas.ForceUpdateCanvases();
            yield return new WaitForEndOfFrame();

            camara.Render();
            plana = LeerRenderTexture(destino);
        }
        finally
        {
            canvas.renderMode = modoOriginal;
            canvas.worldCamera = camaraOriginal;
            canvas.planeDistance = planoOriginal;
            Canvas.ForceUpdateCanvases();

            if (camaraSnapshot != null) Destroy(camaraSnapshot);
            if (destino != null)
            {
                RenderTexture.active = null;
                destino.Release();
                Destroy(destino);
            }
        }

        if (plana == null) yield break;

        // La malla del monitor tiene los UV rotados 90°, así que la imagen se rota para compensar.
        Texture2D rotada = RotateTexture90Left(plana);
        Destroy(plana);

        // Liberar el snapshot ANTERIOR antes de reemplazarlo. Solo destruimos texturas
        // creadas en runtime (nunca el asset serializado del material) -> evita el
        // acumulado de Texture2D en VRAM cada vez que el jugador sale/entra al PC.
        if (currentScreenTexture != null)
            Destroy(currentScreenTexture);
        currentScreenTexture = rotada;

        newScreenMaterial.SetTexture("_BaseColorMap", rotada);
        newScreenMaterial.SetTexture("_EmissiveColorMap", rotada);
        HDMaterial.ValidateMaterial(newScreenMaterial);
    }

    private Texture2D LeerRenderTexture(RenderTexture origen)
    {
        RenderTexture anterior = RenderTexture.active;
        RenderTexture.active = origen;

        Texture2D resultado = new Texture2D(origen.width, origen.height, TextureFormat.RGB24, false);
        resultado.ReadPixels(new Rect(0, 0, origen.width, origen.height), 0, 0);
        resultado.Apply();

        RenderTexture.active = anterior;
        return resultado;
    }

    private void TurnOffPc(bool state)
    {

        if (!state)
        {
            screenRenderer.material = originalMaterial;
            screenLight.SetActive(false);
        }
        else
        {
            screenRenderer.material = newScreenMaterial;
            screenLight.SetActive(true);
        }
    }


    private Texture2D RotateTexture90Left(Texture2D original)
    {
        int width = original.width;
        int height = original.height;

        Texture2D rotated = new Texture2D(height, width, original.format, false);
        Color[] originalPixels = original.GetPixels();
        Color[] rotatedPixels = new Color[originalPixels.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                rotatedPixels[(width - 1 - x) * height + y] = originalPixels[y * width + x];
            }
        }

        rotated.SetPixels(rotatedPixels);
        rotated.Apply();
        return rotated;
    }

    private void ShowDesk()
    {
        HidePasswordPanel();
        // El cursor ya lo dejó Confined+visible GameManager.ShowMouse(true) en Init(), que corre antes
        // de los dos caminos que llegan acá. Repetirlo era un segundo dueño del mismo estado.
        deskPanel.SetActive(true);
    }

    private void HidePasswordPanel()
    {
        passwordPanel.SetActive(false);
    }

    private void Print()
    {
        printer.TryPrint(NoPaper, ExitPC);
    }

    private void NoPaper()
    {
        interacting = true;
        warningPaper.SetActive(true);
        AudioManager2D.instance.PlayOneShot(errorSound);
    }

    [System.Serializable]
    private struct PcSaveData
    {
        public bool passwordCorrect;
        public bool firstTimeNoPaper;
    }

    public object CaptureState()
    {
        return new PcSaveData
        {
            passwordCorrect = passwordCorrect,
            firstTimeNoPaper = firstTimeNoPaper
        };
    }

    public void RestoreState(object state)
    {
        if (state == null) return;

        // Saves nuevos: JObject → PcSaveData. Saves viejos: bool crudo (solo passwordCorrect).
        if (state is Newtonsoft.Json.Linq.JObject)
        {
            var data = SaveUtils.Deserialize<PcSaveData>(state);
            passwordCorrect = data.passwordCorrect;
            firstTimeNoPaper = data.firstTimeNoPaper;

            // Re-armar el trigger del corte de luz. Su propio Start() se auto-desactiva, y quien lo
            // encendía (este mismo evento, una única vez) ya se consumió: sin esto, guardar entre el
            // "sin papel" de la PC y cruzar el trigger dejaba el corte de luz apagado PARA SIEMPRE,
            // y con él toda la cadena que cuelga del apagón. Si el beat ya ocurrió, el RestoreState de
            // TriggerEvents lo destruye en la pasada siguiente del SaveManager (marca `triggered`).
            if (firstTimeNoPaper && triggerEvent != null)
                triggerEvent.SetActive(true);
        }
        else
        {
            passwordCorrect = SaveUtils.Deserialize<bool>(state);
        }
    }
}
