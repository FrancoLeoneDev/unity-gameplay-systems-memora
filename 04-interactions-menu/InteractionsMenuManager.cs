using System;
using UnityEngine;

public class InteractionsMenuManager : MonoBehaviour
{
    public static InteractionsMenuManager instance { get; private set; }

    /// <summary>
    /// Fired when the interactions menu closes.
    /// Parameters: (IMenuInteractable interactable, bool wasSuccessful)
    /// </summary>
    public event Action<IMenuInteractable, bool> OnMenuClosed;

    private InteractionsMenu interactionsMenu;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else
        {
            // return obligatorio: Destroy() no corta el método, sigue en el frame. Sin él, el duplicado
            // CONDENADO seguía ejecutando DontDestroyOnLoad y tocando estado compartido antes de morir.
            Destroy(gameObject);
            return;
        }

        DontDestroyOnLoad(gameObject);
        interactionsMenu = GetComponent<InteractionsMenu>();
        interactionsMenu.enabled = false;
    }

    private void OnDestroy()
    {
        // Dejar `instance` apuntando a un objeto destruido hace que los null-checks de otros sistemas
        // den "no es null" sobre una referencia muerta.
        if (instance == this) instance = null;
    }

    public void OpenMenu(IMenuInteractable menuInteractable)
    {
        interactionsMenu.enabled = true;
        interactionsMenu.OpenMenu(menuInteractable);
    }

    public bool OpenedMenu()
    {
        return interactionsMenu.OpenedMenu;
    }

    public void CloseMenu()
    {
        interactionsMenu.enabled = false;
        StartCoroutine(interactionsMenu.CloseMenu(true));
    }

    public void NotifyMenuClosed(IMenuInteractable interactable, bool success)
    {
        OnMenuClosed?.Invoke(interactable, success);
    }
}
