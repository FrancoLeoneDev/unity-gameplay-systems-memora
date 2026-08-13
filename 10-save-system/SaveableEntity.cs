using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SaveableEntity : MonoBehaviour
{
    [SerializeField] string uniqueIdentifier = "";

    static readonly HashSet<SaveableEntity> registry = new HashSet<SaveableEntity>();
    static readonly Queue<Action> deferredActions = new Queue<Action>();

    // Cacheados en Awake: evita GetComponents() + GetType().ToString() por entidad en cada save.
    ISaveable[] saveables;
    string[] saveableKeys;

    public string GetUniqueIdentifier() => uniqueIdentifier;

    public static IReadOnlyCollection<SaveableEntity> GetAllActive() => registry;

    void Awake() => CacheSaveables();

    void OnEnable() => registry.Add(this);
    void OnDisable() => registry.Remove(this);

    void CacheSaveables()
    {
        saveables = GetComponents<ISaveable>();
        saveableKeys = new string[saveables.Length];
        for (int i = 0; i < saveables.Length; i++)
            saveableKeys[i] = saveables[i].GetType().ToString();
    }

    // --- Capture / Restore ---

    public Dictionary<string, object> CaptureState()
    {
        if (saveables == null) CacheSaveables();

        var state = new Dictionary<string, object>();
        for (int i = 0; i < saveables.Length; i++)
        {
            // A saveable component can be Destroy()'d at runtime while its GameObject (and this
            // SaveableEntity) survive, leaving a dangling entry in this cached array. The interface
            // reference is a "fake null": an ISaveable-typed `== null` bypasses Unity's operator
            // overload, so we cast to UnityEngine.Object to detect the destroyed component and skip
            // it — otherwise CaptureState() throws MissingReferenceException and aborts the save.
            if (saveables[i] is UnityEngine.Object obj && obj == null) continue;

            object captured = saveables[i].CaptureState();
            if (captured == null) continue;
            state[saveableKeys[i]] = captured;
        }
        return state;
    }

    public void RestoreState(object state)
    {
        if (state is not Dictionary<string, object> stateDict) return;
        if (saveables == null) CacheSaveables();

        for (int i = 0; i < saveables.Length; i++)
        {
            // Skip a saveable component Destroy()'d at runtime (see CaptureState). This is the
            // actual throw site for the door-knocker soft-lock: a dead TriggerEvents whose
            // RestoreState() touches its native .gameObject raises MissingReferenceException,
            // which unwinds the (unguarded) memory-entry transition and freezes the screen.
            if (saveables[i] is UnityEngine.Object obj && obj == null) continue;

            if (stateDict.TryGetValue(saveableKeys[i], out object value))
                saveables[i].RestoreState(value);
        }
    }

    // --- Runtime removal of a single saveable component ---

    /// <summary>
    /// Destroy a saveable component AT GAMEPLAY TIME without stranding a dead reference in its
    /// SaveableEntity's cached array. Drops the component from the cache first, then Destroys it.
    /// Use this instead of Destroy(component) for any ISaveable that shares a GameObject with a
    /// SaveableEntity but whose GameObject must survive (door logic stripped while the mesh stays,
    /// etc.) — a bare Destroy(component) leaves a dangling entry that the next save/restore trips
    /// over (MissingReferenceException). NOTE: for destruction that happens DURING a save/restore
    /// pass (e.g. inside RestoreState), use MarkForDeferredAction instead so the array is not
    /// mutated mid-iteration.
    /// </summary>
    public static void DestroySaveable(UnityEngine.Component saveableComponent)
    {
        if (saveableComponent == null) return;

        if (saveableComponent is ISaveable saveable)
        {
            SaveableEntity entity = saveableComponent.GetComponent<SaveableEntity>();
            if (entity != null) entity.Forget(saveable);
        }

        Destroy(saveableComponent);
    }

    /// <summary>Removes a saveable from this entity's cached arrays so it is no longer captured/restored.</summary>
    void Forget(ISaveable saveable)
    {
        if (saveables == null || saveable == null) return;

        int idx = System.Array.IndexOf(saveables, saveable);
        if (idx < 0) return;

        var trimmedSaveables = new ISaveable[saveables.Length - 1];
        var trimmedKeys = new string[saveableKeys.Length - 1];
        for (int i = 0, j = 0; i < saveables.Length; i++)
        {
            if (i == idx) continue;
            trimmedSaveables[j] = saveables[i];
            trimmedKeys[j] = saveableKeys[i];
            j++;
        }
        saveables = trimmedSaveables;
        saveableKeys = trimmedKeys;
    }

    // --- Deferred Action Queue ---

    public static void MarkForDestroy(GameObject go)
    {
        deferredActions.Enqueue(() =>
        {
            if (go != null) Destroy(go);
        });
    }

    public static void MarkForDeferredAction(Action action)
    {
        deferredActions.Enqueue(action);
    }

    public static void ClearDeferredQueue()
    {
        deferredActions.Clear();
    }

    public static void FlushDeferredQueue()
    {
        while (deferredActions.Count > 0)
        {
            var action = deferredActions.Dequeue();
            try { action?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[SaveableEntity] Deferred action failed: {e}"); }
        }
    }

    // --- GUID Generation (Editor Only) ---

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.IsPlaying(gameObject)) return;

        // NUNCA generar un GUID sobre el PREFAB. Un uniqueIdentifier horneado en el asset lo heredan
        // TODAS sus instancias, en cualquier escena, y como SaveManager indexa entityStates por ese
        // GUID sin mirar de qué escena viene, pasan a ser LA MISMA entidad de guardado.
        // El chequeo de abajo (scene.path vacío) pretendía cubrir esto pero no alcanza: al abrir el
        // prefab para editarlo, su preview scene tiene como path la ruta del .prefab, así que
        // OnValidate corría igual y le horneaba un GUID adentro. Así se llenaron de GUIDs los prefabs
        // del proyecto. IsPreviewSceneObject cubre las dos vías (Prefab Stage y LoadPrefabContents).
        if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(gameObject)) return;
        if (PrefabUtility.IsPartOfPrefabAsset(this)) return;

        if (string.IsNullOrEmpty(gameObject.scene.path)) return;

        var so = new SerializedObject(this);
        var prop = so.FindProperty("uniqueIdentifier");

        if (string.IsNullOrEmpty(prop.stringValue) || !IsGuidUnique(prop.stringValue))
        {
            prop.stringValue = Guid.NewGuid().ToString();
            so.ApplyModifiedProperties();
        }
    }

    bool IsGuidUnique(string candidate)
    {
        var allEntities = FindObjectsByType<SaveableEntity>(FindObjectsSortMode.None);
        foreach (var entity in allEntities)
        {
            if (entity == this) continue;
            if (entity.uniqueIdentifier == candidate)
                return false;
        }
        return true;
    }
#endif
}
