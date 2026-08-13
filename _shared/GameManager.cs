using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public enum GameState { Paused, InventoryOpen, InCinematic, Interacting, InWhiteRoom }


public class GameManager : MonoBehaviour
{
    public event Action OnInteracting;
    public event Action OnStopInteracting;

    [Header("Canvas")]
    [SerializeField] GameObject pauseCanvas;
    private GameObject inventoryCanvas;

    public static GameManager instance { get; private set; }
    
    private bool paused;
    private bool interacting;
    private bool inventoryOpen;
    private bool inCinematic;
    // Salas blancas: el inventario es un blur con iconos BLANCOS, ilegible sobre fondo blanco.
    private bool inWhiteRoom;
    private Dictionary<GameState, bool> gameStates = new Dictionary<GameState, bool>();



    public bool Interacting { get {  return interacting; } }
    public bool IsGamePaused() => paused;

    /// <summary>
    /// Returns true when the player is in any state that should suppress ambient director events
    /// (paused, interacting with an object, inventory open, or in a cinematic).
    /// Used by IOpportunityProbe implementations in the Director system.
    /// </summary>
    public bool IsPlayerBusy() => paused || interacting || inventoryOpen || inCinematic;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else
        {
            // return obligatorio: sin él el duplicado condenado seguía ejecutando DontDestroyOnLoad y
            // SyncGameStates(), pisando el estado de juego compartido justo antes de morir.
            Destroy(gameObject);
            return;
        }

        DontDestroyOnLoad(gameObject);

        SyncGameStates();
    }

    private void OnDestroy()
    {
        // Nulear `instance`: si queda apuntando a un objeto destruido, los null-checks de otros
        // sistemas dan "no es null" sobre una referencia muerta.
        if (instance == this) instance = null;
    }

    private void SyncGameStates()
    {
        gameStates[GameState.Paused] = paused;
        gameStates[GameState.Interacting] = interacting;
        gameStates[GameState.InventoryOpen] = inventoryOpen;
        gameStates[GameState.InCinematic] = inCinematic;
        gameStates[GameState.InWhiteRoom] = inWhiteRoom;
    }


    public void ShowMouse(bool _bool)
    {
        if (!_bool)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Confined;
            Cursor.visible = true;
        }
    }

    public void DesactiveInteractions(bool _bool)
    {
        // GameManager es DDOL y sobrevive los cambios de escena: el PlayerManager de la escena nueva
        // puede no haber corrido su Awake todavía (o directamente no existir, p.ej. en el menú).
        if (PlayerManager.instance == null) return;

        InteractableRay interactableRay = PlayerManager.instance.GetInteractableRay();
        if (interactableRay != null)
            interactableRay.enabled = !_bool;
    }

    public void Pause(bool _bool)
    {
        // El estado de pausa se actualiza SIEMPRE; tocar el ray es un efecto secundario que depende
        // de que haya Player en escena. Antes el orden era al revés y un PlayerManager ausente tiraba
        // NRE antes de llegar a registrar la pausa.
        paused = _bool;
        SyncGameStates();

        if (PlayerManager.instance == null) return;

        InteractableRay interactableRay = PlayerManager.instance.GetInteractableRay();
        if (interactableRay != null)
            interactableRay.enabled = !paused;
    }
    
    public void SetInteracting(bool _bool)
    {
        if (_bool)
        {
            interacting = true;
            OnInteracting?.Invoke();
        }
        else
        {
            interacting = false;
            OnStopInteracting?.Invoke();
        }

        SyncGameStates();
    }

    public void SetInventoryOpen(bool value)
    {
        inventoryOpen = value;
        SyncGameStates();
    }

    public void SetInCinematic(bool value)
    {
        inCinematic = value;
        SyncGameStates();
    }

    /// <summary>
    /// Marca que el jugador está en una sala blanca. Bloquea el inventario: su UI es un blur con
    /// iconos blancos y sobre fondo blanco no se lee nada.
    /// Llamar con true al entrar y con false al salir.
    /// </summary>
    public void SetInWhiteRoom(bool value)
    {
        inWhiteRoom = value;
        SyncGameStates();
    }

    public bool CanToggleState(List<GameState> listGameState)
    {
        for (int i = 0; i < listGameState.Count; i++)
        {
            if (gameStates[listGameState[i]])
                return false;
        }
        return true;
    }

    public void SetInventoryCanvas(GameObject gameObject)
    {
        inventoryCanvas = gameObject;
    }

    public void RestartInventoryCanvas()
    {
        inventoryCanvas.SetActive(false);
        inventoryCanvas.SetActive(true);
    }

    public void RestartPauseCanvas()
    {
        pauseCanvas.SetActive(false);
        pauseCanvas.SetActive(true);
    }

}
