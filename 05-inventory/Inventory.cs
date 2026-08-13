using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[RequireComponent(typeof(SaveableEntity))]
public class Inventory : MonoBehaviour, ISaveable
{
    public static Inventory instance { get; private set; }

    [SerializeField] private List<PhotoData> photosList = new List<PhotoData>();
    [SerializeField] private List<NoteData> notesList = new List<NoteData>();
    [SerializeField] private List<UniqueKeyItemData> uniqueItemsList = new List<UniqueKeyItemData>();

    private Dictionary<UniqueKeyItemData, int> uniqueItemsUsesDictionary = new Dictionary<UniqueKeyItemData, int>();
    private HashSet<string> completedPhotoIDs = new HashSet<string>();
    private HashSet<string> discoveredItemIDs = new HashSet<string>();
    private HashSet<string> seenItemIDs = new HashSet<string>();

    public event Action<string> OnPhotoCompleted;

    /// <summary>Se dispara cuando un item pasa a "visto" (apaga su indicador de nuevo). Lo escucha InventoryUI.</summary>
    public event Action<string> OnItemSeen;

    private void Awake()
    {
        instance = this;
    }

    private void Start()
    {
        if (SceneControllerManager.instance != null)
        {
            SceneControllerManager.instance.OnMemoryCompleted += HandleMemoryCompleted;
        }

        if (SaveManager.Instance != null)
        {
            SaveManager.Instance.OnNewGame += HandleNewGame;
        }
    }

    private void OnDestroy()
    {
        if (SceneControllerManager.instance != null)
        {
            SceneControllerManager.instance.OnMemoryCompleted -= HandleMemoryCompleted;
        }

        if (SaveManager.Instance != null)
        {
            SaveManager.Instance.OnNewGame -= HandleNewGame;
        }
    }

    /// <summary>
    /// Partida nueva: este inventario es DDOL y sobrevive al cambio de escena, así que sin esto el
    /// New Game arrancaba con las fotos, notas y llaves de la partida anterior (y con los indicadores
    /// de "nuevo" ya apagados). Deja el mismo estado que tiene un inventario recién creado.
    /// </summary>
    private void HandleNewGame()
    {
        photosList.Clear();
        notesList.Clear();
        uniqueItemsList.Clear();
        uniqueItemsUsesDictionary.Clear();
        completedPhotoIDs.Clear();
        discoveredItemIDs.Clear();
        seenItemIDs.Clear();
    }

    private void HandleMemoryCompleted(PhotoData photoData)
    {
        if (photoData != null) MarkPhotoCompleted(photoData.ID);
    }

    public void MarkPhotoCompleted(string photoID)
    {
        if (completedPhotoIDs.Add(photoID))
        {
            OnPhotoCompleted?.Invoke(photoID);
        }
    }

    public bool IsPhotoCompleted(string photoID)
    {
        return completedPhotoIDs.Contains(photoID);
    }

    /// <summary>Marca un item como "visto" (apaga su indicador de nuevo). Idempotente; solo notifica la 1ra vez.</summary>
    public void MarkItemSeen(string itemID)
    {
        if (string.IsNullOrEmpty(itemID)) return;
        if (seenItemIDs.Add(itemID))
            OnItemSeen?.Invoke(itemID);
    }

    /// <summary>True si el item ya fue visto. Un item es "nuevo" mientras NO esté visto.</summary>
    public bool IsItemSeen(string itemID)
    {
        return !string.IsNullOrEmpty(itemID) && seenItemIDs.Contains(itemID);
    }

    public void AddUniqueKeyItem(UniqueKeyItemData item, Action<bool> onComplete)
    {
        // Un item NULO se agregaba a la lista como cualquier otro y el callback devolvía true: el objeto
        // del mundo se consumía (el examine lo destruye) y en el inventario quedaba una entrada que no
        // dibuja nada. Para el jugador es "agarré la llave y desapareció", sin un solo error en consola.
        // Pasó con la llave del armario, que estuvo un tiempo con su KeyData sin asignar.
        if (item == null)
        {
            Debug.LogError("[Inventory] AddUniqueKeyItem con item NULO: hay un Key en la escena sin su " +
                           "KeyData asignado. El objeto NO se agrega y el callback avisa que falló.");
            onComplete?.Invoke(false);
            return;
        }

        if (!uniqueItemsList.Contains(item))
        {
            uniqueItemsList.Add(item);
            if (!uniqueItemsUsesDictionary.ContainsKey(item))
            {
                uniqueItemsUsesDictionary[item] = item.GetRemainingUses();
            }
            TryPlayDiscoverySound(item.ID, AudioLibrary.instance.ImportantItemPickupClip);
            onComplete?.Invoke(true);
        }
        else
        {
            onComplete?.Invoke(false);
        }
    }

    public bool HasUniqueKeyItem(UniqueKeyItemData item)
    {
        return uniqueItemsList.Contains(item);
    }

    public void UseKeyItem(UniqueKeyItemData item)
    {
        if (!item.IsLimitedUses())
            return;

        if (uniqueItemsUsesDictionary.ContainsKey(item) && uniqueItemsUsesDictionary[item] > 0)
        {
            uniqueItemsUsesDictionary[item]--;
            if (uniqueItemsUsesDictionary[item] <= 0)
            {
                uniqueItemsUsesDictionary.Remove(item);
                DeleteKeyItem(item);
            }
        }
    }

    public void DeleteKeyItem(UniqueKeyItemData item)
    {
        if (uniqueItemsList.Contains(item))
        {
            uniqueItemsList.Remove(item);
        }
    }

    public List<ScriptableObject> GetKeyItemsList()
    {
        List<ScriptableObject> combinedList = new List<ScriptableObject>();
        combinedList.AddRange(uniqueItemsList);
        return combinedList;
    }

    public void AddNote(NoteData noteData)
    {
        if (!notesList.Contains(noteData))
        {
            notesList.Add(noteData);
            TryPlayDiscoverySound(noteData.ID, AudioLibrary.instance.NotePickupClip);
        }
        else
        {
            Debug.LogWarning("La nota ya est\u00e1 en la lista.");
        }
    }

    public List<NoteData> GetNotesList()
    {
        return notesList;
    }

    public void AddPhoto(PhotoData photoData, Action<bool> onComplete)
    {
        if (!photosList.Contains(photoData))
        {
            photosList.Add(photoData);
            TryPlayDiscoverySound(photoData.ID, AudioLibrary.instance.ImportantItemPickupClip);
            onComplete?.Invoke(true);
        }
        else
        {
            Debug.LogWarning("La foto ya est\u00e1 en la lista.");
            onComplete?.Invoke(false);
        }
    }

    public void DeletePhoto(PhotoData photoData)
    {
        if (photosList.Contains(photoData))
        {
            photosList.Remove(photoData);
        }
    }

    public List<PhotoData> GetPhotosList()
    {
        return photosList;
    }

    public bool HasItem(ScriptableObject item)
    {
        return uniqueItemsList.Contains(item);
    }

    private void PlayPickupSound(AudioClip clip)
    {
        if (AudioManager2D.instance != null && clip != null)
            AudioManager2D.instance.PlayOneShot(clip);
    }

    /// Plays clip only the first time this itemID is discovered. No-op on repeat calls.
    private void TryPlayDiscoverySound(string itemID, AudioClip clip)
    {
        if (string.IsNullOrEmpty(itemID)) return;
        if (discoveredItemIDs.Add(itemID))
            PlayPickupSound(clip);
    }

    /// Plays the discovery sound for an important item at the moment it is grabbed,
    /// before it formally enters the inventory on inspection exit. No-op if already discovered.
    public void NotifyImportantItemDiscovered(IIdentificable item)
    {
        if (item == null) return;
        if (AudioLibrary.instance == null) return;
        TryPlayDiscoverySound(item.ID, AudioLibrary.instance.ImportantItemPickupClip);
    }

    [System.Serializable]
    struct PlayerInventoryData
    {
        public List<string> photoIDs;
        public List<string> noteIDs;
        public List<string> keyItemIDs;
        public Dictionary<string, int> keyItemUses;
        public List<string> completedPhotoIDs;
        public List<string> discoveredItemIDs;
        public List<string> seenItemIDs;
    }

    public object CaptureState()
    {
        PlayerInventoryData data = new PlayerInventoryData();

        data.photoIDs = photosList.Where(p => p != null).Select(p => p.ID).ToList();
        data.noteIDs = notesList.Where(n => n != null).Select(n => n.ID).ToList();
        data.keyItemIDs = uniqueItemsList.Where(k => k != null).Select(k => k.ID).ToList();
        data.completedPhotoIDs = completedPhotoIDs.ToList();
        data.discoveredItemIDs = discoveredItemIDs.ToList();
        data.seenItemIDs = seenItemIDs.ToList();

        data.keyItemUses = new Dictionary<string, int>();
        foreach (var kvp in uniqueItemsUsesDictionary)
        {
            if (kvp.Key != null)
                data.keyItemUses[kvp.Key.ID] = kvp.Value;
        }

        return data;
    }

    public void RestoreState(object state)
    {
        var data = SaveUtils.Deserialize<PlayerInventoryData>(state);

        photosList = data.photoIDs
            .Select(id => DatabasesManager.instance.GetPhotoDatabase().GetById(id))
            .Where(p => p != null).ToList();
        notesList = data.noteIDs
            .Select(id => DatabasesManager.instance.GetNoteDatabase().GetById(id))
            .Where(n => n != null).ToList();
        uniqueItemsList = data.keyItemIDs
            .Select(id => DatabasesManager.instance.GetKeyItemDatabase().GetById(id))
            .Where(k => k != null).ToList();

        completedPhotoIDs = data.completedPhotoIDs != null
            ? new HashSet<string>(data.completedPhotoIDs)
            : new HashSet<string>();
        discoveredItemIDs = data.discoveredItemIDs != null
            ? new HashSet<string>(data.discoveredItemIDs)
            : new HashSet<string>();
        seenItemIDs = data.seenItemIDs != null
            ? new HashSet<string>(data.seenItemIDs)
            : new HashSet<string>();

        uniqueItemsUsesDictionary.Clear();
        if (data.keyItemUses != null)
        {
            foreach (var kvp in data.keyItemUses)
            {
                var item = DatabasesManager.instance.GetKeyItemDatabase().GetById(kvp.Key);
                if (item != null)
                    uniqueItemsUsesDictionary[item] = kvp.Value;
            }
        }
    }
}
