using DG.Tweening;
using System;
using System.Collections;
using UnityEngine;

[RequireComponent(typeof(SaveableEntity))]
public class Printer : InteractableObject, IMenuInteractable, ISaveable
{
    [SerializeField] Transform camPos;
    [SerializeField] UniqueKeyItemData keyItemNeeded;
    [SerializeField] Transform paperSpawnPoint;
    private bool hasPaper;
    private bool printing;
    private bool hasPrinted;

    AudioSource audioSource;

    [SerializeField] Transform photo3Transform;
    public bool InteractionSucceeded { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        audioSource = GetComponent<AudioSource>();
    }
    public override void Interact()
    {
        MenuInteractionCamera.MoveTo(this, camPos);
        OpenMenu();
    }

    public void OpenMenu()
    {
        InteractionsMenuManager.instance.OpenMenu(this);
    }


    public void TryPrint(Action noPaper, Action exitPC)
    {
        if (hasPaper)
        {
            Print();
            exitPC();
        }
        else
        {
            noPaper();
        }
    }
    

    /// <summary>Guard de re-entrada: "ya se metió el papel", distinto del veredicto del intento.</summary>
    private bool paperAlreadyUsed;

    public bool UseItem(ScriptableObject selectedItem)
    {
        if (paperAlreadyUsed)
        {
            Debug.LogWarning("Ya se uso el papel o hay una transicion en curso.", this);
            return false;
        }

        // keyItemNeeded sin asignar + un ítem que no sea UniqueKeyItemData daba null == null → la
        // impresora aceptaba CUALQUIER cosa y se la comía del inventario.
        if (keyItemNeeded == null)
        {
            Debug.LogError("[Printer] keyItemNeeded sin asignar en el Inspector.", this);
            return false;
        }
        if (!(selectedItem is UniqueKeyItemData paperData) || paperData != keyItemNeeded) return false;

        // Cableado incompleto: se avisa y NO se consume el papel (a diferencia de la puerta, acá no
        // hay soft-lock posible — el papel sigue en el inventario y se puede reintentar).
        if (paperSpawnPoint == null || paperData.GetPrefab() == null)
        {
            Debug.LogError($"[Printer] '{name}' sin cablear: paperSpawnPoint o prefab del papel en NULL.", this);
            return false;
        }

        InteractionSucceeded = true;
        paperAlreadyUsed = true;
        Inventory.instance.DeleteKeyItem(paperData);
        StartCoroutine(InsertPaper(paperData));
        return true;
    }

    private IEnumerator InsertPaper(UniqueKeyItemData paper)
    {
        GameObject paperInstance = Instantiate(paper.GetPrefab(), paperSpawnPoint.position, paperSpawnPoint.rotation);
        Transform paperTransform = paperInstance.transform;
        paperTransform.SetParent(paperSpawnPoint);
        //Vector3 endValue = new(111f, 99f, 780f);
        yield return new WaitForSeconds(0.5f);
        paperTransform.DOLocalMoveZ(780f, 1f).OnComplete(() =>
        {
            gameObject.layer = LayerMask.NameToLayer("Default");
            hasPaper = true;
            InteractionsMenuManager.instance.CloseMenu();
        });
    }

    private void Print()
    {
        if (printing || hasPrinted)
            return;

        printing = true;
        photo3Transform.gameObject.SetActive(true);
        photo3Transform.gameObject.layer = LayerMask.NameToLayer("Default");

        audioSource.Play();

        float duration = audioSource.clip.length;
        int steps = 15; // cantidad de "tirones"

        // El tiempo de cada movimiento y cada pausa para que el total = duraci�n del audio
        float stepTime = duration / (steps * 2);

        Vector3 paperStartPos = paperSpawnPoint.localPosition;
        Vector3 paperEndPos = new(-40f, -18f, 32f);

        Vector3 photo3StartPos = photo3Transform.position;
        Vector3 photo3EndPos = new(-7.032f, 5.359f, 28.784f);

        Sequence seq = DOTween.Sequence();

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;

            // Avanza un pedacito la hoja blanca
            seq.Append(paperSpawnPoint.DOLocalMove(
                Vector3.Lerp(paperStartPos, paperEndPos, t), stepTime)
                .SetEase(Ease.Linear));

            // Avanza un pedacito la hoja impresa en simult�neo
            seq.Join(photo3Transform.DOMove(
                Vector3.Lerp(photo3StartPos, photo3EndPos, t), stepTime)
                .SetEase(Ease.Linear));

            // Pausa corta, proporcional al audio
            seq.AppendInterval(stepTime);
        }

        seq.OnComplete(() =>
        {
            Destroy(paperSpawnPoint.gameObject);
            photo3Transform.gameObject.layer = LayerMask.NameToLayer("Inspectable");
            Debug.Log("Impresion terminada!");
            // Componente NO destruido: debe sobrevivir para CaptureState (anti soft-lock).
            hasPrinted = true;
        });
    }

    // --- ISaveable (Fase 2 anti soft-lock) ---

    [Serializable]
    private struct PrinterSaveData
    {
        public bool hasPaper;
        public bool hasPrinted;
    }

    public object CaptureState()
    {
        return new PrinterSaveData { hasPaper = hasPaper, hasPrinted = hasPrinted };
    }

    public void RestoreState(object state)
    {
        var data = SaveUtils.Deserialize<PrinterSaveData>(state);

        if (data.hasPrinted)
        {
            // Impresión ya completada: saltar al estado final sin re-reproducir el tween/audio.
            hasPaper = true;
            hasPrinted = true;
            printing = true; // bloquea cualquier re-disparo de Print()

            if (photo3Transform != null)
            {
                photo3Transform.gameObject.SetActive(true);
                photo3Transform.gameObject.layer = LayerMask.NameToLayer("Inspectable");
            }

            if (paperSpawnPoint != null)
                Destroy(paperSpawnPoint.gameObject);
        }
        else if (data.hasPaper)
        {
            // Papel insertado pero impresión no disparada aún.
            hasPaper = true;
            InteractionSucceeded = true;
            paperAlreadyUsed = true;   // el papel ya está puesto: no aceptar otro al restaurar
            gameObject.layer = LayerMask.NameToLayer("Default");
        }
    }
}
