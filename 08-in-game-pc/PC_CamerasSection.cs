using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(SaveableEntity))]
public class PC_CamerasSection : MonoBehaviour, ISaveable
{
    [Serializable]
    public class SecurityCamera
    {
        public Camera camera;
        public bool hasSignal;
    }

    [Header("Buttons")]
    [SerializeField] Button backButton;
    [SerializeField] Button previousCameraButton;
    [SerializeField] Button nextCameraButton;

    [Header("Raw Image Display")]
    [SerializeField] RawImage uiDisplay; // UI element to display the camera feed
    [SerializeField] Texture2D noSignalImage; // Texture to show when there's no signal

    [Header("Security Cameras")]
    [SerializeField] private SecurityCamera[] securityCamerasLeft;
    [SerializeField] private SecurityCamera[] securityCamerasRight;
    private List<SecurityCamera> securityCameras = new List<SecurityCamera>();
    private Camera currentCameraActivated;
    private int currentCameraIndex;

    void Awake()
    {
        backButton.onClick.AddListener(Back);
        previousCameraButton.onClick.AddListener(PreviousCamera);
        nextCameraButton.onClick.AddListener(NextCamera);

        foreach(var c in securityCamerasLeft)
        {
            securityCameras.Add(c); // Add left cameras to the list
            c.camera.enabled = false; // Disable all cameras initially
        }

        foreach (var c in securityCamerasRight)
        {
            securityCameras.Add(c); // Add right cameras to the list
            c.camera.enabled = false; // Disable all cameras initially
        }

        gameObject.SetActive(false); // Deactivate the camera section initially
    }

    private void OnEnable()
    {
        if (securityCameras.Count == 0) return; // Ensure there are cameras to display
        ShowCamera(0); // Show the first camera by default
    }

    /// <summary>
    /// Apagar la cámara activa al salir de la sección. Sin esto seguía renderizando a su RenderTexture
    /// indefinidamente después de cerrar el circuito — coste de render fantasma. Va en OnDisable y no en
    /// Back() para cubrir TODOS los caminos de salida (Back, cierre del PC, cambio de escena).
    /// </summary>
    private void OnDisable()
    {
        if (currentCameraActivated != null)
            currentCameraActivated.enabled = false;
    }
    private void Back()
    {
        gameObject.SetActive(false); // Deactivate the camera section
    }

    private void ShowCamera(int index)
    {
        if (index < 0 || index >= securityCameras.Count) return;
        
        if(currentCameraActivated != null)
        {
            currentCameraActivated.enabled = false; // Disable the current camera feed if it exists
        }

        currentCameraIndex = index;
        var cameraToShow = securityCameras[currentCameraIndex];

        if (cameraToShow.hasSignal)
        {
            cameraToShow.camera.enabled = true;
            uiDisplay.texture = cameraToShow.camera.targetTexture;
            currentCameraActivated = cameraToShow.camera;
        }
        else
        {
            uiDisplay.texture = noSignalImage;
        }

    }

    private void NextCamera() => ShowCamera((currentCameraIndex + 1) % securityCameras.Count);
    private void PreviousCamera() => ShowCamera((currentCameraIndex - 1 + securityCameras.Count) % securityCameras.Count);

    public object CaptureState()
    {
        // Captura solo los flags de hasSignal en orden
        bool[] cameraSignals = new bool[securityCameras.Count];
        for (int i = 0; i < securityCameras.Count; i++)
        {
            cameraSignals[i] = securityCameras[i].hasSignal;
        }

        return cameraSignals;
    }

    public void RestoreState(object state)
    {
        bool[] cameraSignals = SaveUtils.Deserialize<bool[]>(state);
        if (cameraSignals == null || cameraSignals.Length != securityCameras.Count)
        {
            Debug.LogWarning("Invalid camera state data.");
            return;
        }

        for (int i = 0; i < securityCameras.Count; i++)
        {
            securityCameras[i].hasSignal = cameraSignals[i];
        }
    }
}
