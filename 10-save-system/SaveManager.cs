using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SaveManager : MonoBehaviour
{
    public static SaveManager Instance { get; private set; }

    [SerializeField] SaveSettings settings;

    JsonSaveSerializer serializer;
    bool isLoading;
    float sessionStartTime;
    float loadedPlayTime;

    // Caché en memoria del save activo: evita leer+deserializar el archivo en cada
    // checkpoint. Las entidades inactivas conservan su estado previo desde este caché.
    SaveData hotCache;
    int hotCacheSlot = -1;

    // Reusado entre guardados (los saves son esporádicos, pero no hay razón para alocar por pasada).
    // Detecta dos entidades ACTIVAS con el mismo uniqueIdentifier. Pasa de verdad: un GUID copiado al
    // duplicar un objeto entre escenas, u horneado dentro de un prefab con varias instancias. El
    // OnValidate de SaveableEntity no lo pesca porque sólo compara contra la escena ABIERTA, y los
    // recuerdos se cargan ADITIVOS sobre la casa: la colisión recién existe en runtime.
    readonly HashSet<string> idsVistosEnEstaCaptura = new HashSet<string>();

    // Copia defensiva del registry para restaurar. NO se puede iterar SaveableEntity.GetAllActive()
    // directamente: un RestoreState puede activar o instanciar objetos que llevan SaveableEntity
    // (Printer enciende 'foto3', LamparaSalaArte instancia el prefab del bulbo), y su OnEnable hace
    // registry.Add() — mutar el HashSet a mitad del foreach lanza InvalidOperationException, que
    // aborta TODA la restauración y deja la partida a medio cargar.
    readonly List<SaveableEntity> bufferRestauracion = new List<SaveableEntity>();
    readonly HashSet<SaveableEntity> entidadesYaRestauradas = new HashSet<SaveableEntity>();

    // Las entidades que aparecen DURANTE la restauración también necesitan su estado, así que se
    // restaura por pasadas hasta que no aparezca ninguna nueva. El tope corta una cadena patológica
    // (A enciende B, B enciende C, …) en vez de colgar el juego.
    const int MaxPasadasRestauracion = 4;

    public bool HasLoadedGame { get; private set; }
    public bool IsFullLoad { get; private set; }

    // Supresion de guardado para set-pieces scripteados (p.ej. el loop del climax del hospital):
    // mientras esta en true, ni el autosave de ciclo de vida ni los guardados ad-hoc escriben.
    // Decouplado y null-safe por construccion: el director del set-piece la prende/apaga; si nadie
    // la toca, guardar siempre esta permitido. (El HospitalClimaxDirector llama SetSavingSuppressed
    // en true al arrancar el loop y en false en el checkpoint de la cuna.)
    public bool IsSavingSuppressed { get; private set; }
    public void SetSavingSuppressed(bool suppressed) => IsSavingSuppressed = suppressed;

    // True SOLO durante un Continue que reanuda DENTRO de un recuerdo, desde que se detecta hasta que
    // el recuerdo está montado. Lo leen PlayerManager.RestoreState (para STASHEAR la pose en vez de
    // teletransportar al vacío antes de montar el recuerdo) y ZoneManager.OnAfterLoad (para NO
    // reactivar las zonas de la casa que el resume va a suprimir). Se resetea SIEMPRE en el finally
    // de LoadCoroutine (un flag colgado rompería el próximo load normal).
    public bool IsResumingIntoMemory { get; private set; }

    public event Action<SaveData> OnBeforeSave;
    public event Action OnAfterLoad;

    /// <summary>
    /// Se dispara al empezar una partida NUEVA, con la escena inicial ya cargada. Borrar el archivo no
    /// alcanza: los managers DDOL (Inventory, respawn, tutoriales, directores) sobreviven al cambio de
    /// escena con el estado del run anterior en memoria, así que un New Game arrancaba con el inventario
    /// lleno, los tutoriales ya vistos y el Director ya desbloqueado. Cada manager con estado se suscribe
    /// y se resetea solo. Suscribirse en Start() (NO en Awake/OnEnable): corre la race con el Awake de
    /// este singleton DDOL.
    /// </summary>
    public event Action OnNewGame;

    void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (settings == null)
        {
            Debug.LogError("[SaveManager] SaveSettings asset is not assigned! Assign it in the Inspector.");
            return;
        }

        serializer = new JsonSaveSerializer(settings);
        SaveUtils.Initialize(serializer.GetSerializer());

        EnsureSaveDirectory();

        if (settings.DeleteLegacyOnStartup)
            DeleteLegacyFiles();

#if UNITY_EDITOR
        if (settings.DeleteAllSavesOnPlay)
            DeleteAllSaves();
#endif

        sessionStartTime = Time.realtimeSinceStartup;
    }

    // --- Application lifecycle autosave ---

    void OnApplicationPause(bool pauseStatus)
    {
        // En móvil OnApplicationQuit no es fiable; OnApplicationPause(true) es el hook seguro.
        if (pauseStatus && CanLifecycleSave())
            Save(settings.AutoSaveSlotIndex);
    }

    void OnApplicationQuit()
    {
        if (CanLifecycleSave())
            Save(settings.AutoSaveSlotIndex);
    }

    bool CanLifecycleSave()
    {
        if (!CanSaveNow()) return false;
        // Nunca autoguardar desde el menú principal. El SaveManager es DDOL y sigue vivo cuando el
        // jugador vuelve al menú: sin este guard, cerrar el juego ahí graba el slot con el buildIndex
        // del MENÚ y el próximo Continue carga el menú, para siempre. En una instalación limpia,
        // además, abrir y salir sin jugar ya creaba un save fantasma que habilitaba el botón Continue.
        if (SceneManager.GetActiveScene().name == settings.MenuSceneName)
            return false;
        // No autoguardar en medio de una transición/evento scripted: el estado puede
        // estar a mitad de cambio y, en un solo slot, dejaría una partida inconsistente.
        if (SceneControllerManager.instance != null && SceneControllerManager.instance.IsTransitioning)
            return false;
        return true;
    }

    /// <summary>
    /// True si un guardado puede ocurrir AHORA: no hay una carga en curso, ningún set-piece
    /// suprimió el guardado, y el sistema está inicializado. NO incluye IsTransitioning: eso es
    /// exclusivo del autosave de ciclo de vida (los checkpoints de transición guardan a propósito
    /// durante la transición). Punto único donde vive el gate del clímax.
    /// </summary>
    public bool CanSaveNow()
    {
        if (isLoading) return false;
        if (IsSavingSuppressed) return false;
        if (settings == null || serializer == null) return false;
        return true;
    }

    /// <summary>
    /// Guarda sólo si CanSaveNow(). Punto de entrada para los guardados ad-hoc de gameplay
    /// (agarrar un item-llave, abrir una puerta con llave) que deben respetar el gate del set-piece.
    /// </summary>
    public bool TrySave()
    {
        if (!CanSaveNow()) return false;
        Save();
        return true;
    }

    // --- Public API ---

    public void Save() => Save(settings.AutoSaveSlotIndex);

    public void Save(int slot)
    {
        if (isLoading)
        {
            Debug.LogWarning("[SaveManager] Save blocked — load in progress.");
            return;
        }

        var saveData = GetOrLoadHotCache(slot);
        CaptureState(saveData.entityStates);

        saveData.metadata.timestamp = DateTime.Now.ToString("o");
        saveData.metadata.playTimeSeconds = loadedPlayTime + (Time.realtimeSinceStartup - sessionStartTime);
        saveData.metadata.sceneName = SceneManager.GetActiveScene().name;
        saveData.sceneBuildIndex = SceneManager.GetActiveScene().buildIndex;

        OnBeforeSave?.Invoke(saveData);

        WriteFile(settings.GetSavePath(slot), slot, saveData);
    }

    public void Load() => Load(settings.AutoSaveSlotIndex);

    public void Load(int slot)
    {
        if (isLoading) return;
        StartCoroutine(LoadCoroutine(slot));
    }

    public void LoadSceneState() => LoadSceneState(settings.AutoSaveSlotIndex);

    public void LoadSceneState(int slot)
    {
        if (isLoading) return;

        var data = ReadFile(slot);
        if (data == null) return;

        RunMigrations(data);

        hotCache = data;
        hotCacheSlot = slot;

        RestoreLoadedState(data, fullLoad: false);
    }

    /// <summary>
    /// Restaura las entidades activas de la escena recién montada (el recuerdo) durante un resume,
    /// reusando el SaveData YA cargado en hotCache. Espeja lo que hace la entrada normal por foto
    /// (RunPostLoadTail → LoadSceneState) pero SIN el guard de isLoading: el resume corre con isLoading
    /// todavía en true (bloquea saves/loads concurrentes durante el montaje), y el LoadSceneState
    /// público no-opearía por ese guard. Sin esta variante interna, NINGUNA entidad del recuerdo se
    /// restauraría en un resume.
    /// NOTA: RestoreState itera el registry GLOBAL de SaveableEntity (no filtra por escena). Las
    /// entidades de zonas de la casa ya salieron del registry (SetZonesInactiveExcept → OnDisable
    /// antes de esta llamada), pero los ISaveable DDOL (Player, Inventory, …) se re-restauran una 2ª
    /// vez. Hoy inocuo porque sus RestoreState son idempotentes (Player además gateado por IsFullLoad);
    /// un ISaveable nuevo con RestoreState NO idempotente (dispara sonido/spawn/contador) debe blindarse.
    /// </summary>
    public void RestoreMemoryEntities()
    {
        // Escape hatch SOLO para el montaje del resume, que corre con isLoading=true. Si se llama
        // fuera de una carga en curso es un misuse (sin el guard de isLoading que sí tiene el
        // LoadSceneState público) → warn y no-op, para que falle ruidoso en vez de pisar estado.
        if (!isLoading)
        {
            Debug.LogWarning("[SaveManager] RestoreMemoryEntities() llamado fuera de una carga en curso; ignorado.");
            return;
        }
        if (hotCache == null) return;
        RestoreLoadedState(hotCache, fullLoad: false);
    }

    // Núcleo compartido de restauración de entidades. Opera sobre el SaveData ya en memoria (no re-lee
    // disco) y NO chequea isLoading (los callers públicos que lo necesitan lo chequean antes de llamar).
    void RestoreLoadedState(SaveData data, bool fullLoad)
    {
        IsFullLoad = fullLoad;

        SaveableEntity.ClearDeferredQueue();
        RestoreState(data.entityStates);
        SaveableEntity.FlushDeferredQueue();

        OnAfterLoad?.Invoke();
    }

    public void NewGame()
    {
        if (isLoading) return;
        StartCoroutine(NewGameCoroutine());
    }

    public void Delete(int slot)
    {
        string path = settings.GetSavePath(slot);
        if (File.Exists(path)) File.Delete(path);

        string backup = settings.GetBackupPath(slot);
        if (File.Exists(backup)) File.Delete(backup);
    }

    /// <summary>
    /// ¿Hay partida en este slot? Cuenta también el BACKUP: ReadFile auto-recupera del .bak cuando
    /// falta el primario (AutoRecoverFromBackup), así que mirando sólo el primario el botón Continue
    /// quedaba deshabilitado sobre una partida perfectamente recuperable.
    /// </summary>
    public bool SlotExists(int slot)
    {
        if (File.Exists(settings.GetSavePath(slot))) return true;

        return settings.AutoRecoverFromBackup && File.Exists(settings.GetBackupPath(slot));
    }

    /// <summary>
    /// Metadata del slot (fecha, escena) para la UI de partidas. Sigue la MISMA política de
    /// recuperación que <see cref="ReadFile"/>: <see cref="SlotExists"/> ya da por válido un slot que
    /// sólo tiene .bak — es lo que habilita el botón Continue —, así que mirar únicamente el primario
    /// devolvía null y la UI mostraba fecha y escena EN BLANCO para una partida perfectamente
    /// cargable. Silencioso a propósito: esto se llama por cada slot al abrir el menú y no es un
    /// camino de error, es una sonda.
    /// </summary>
    public SaveMetadata GetSlotMetadata(int slot)
    {
        string path = settings.GetSavePath(slot);
        string backup = settings.GetBackupPath(slot);
        bool puedeUsarBackup = settings.AutoRecoverFromBackup && File.Exists(backup);

        if (!File.Exists(path))
        {
            if (!puedeUsarBackup) return null;
            path = backup;
        }

        try
        {
            return serializer.DeserializeMetadataOnly(File.ReadAllText(path));
        }
        catch
        {
            // El primario existe pero no parsea: el .bak todavía puede estar sano.
            if (!puedeUsarBackup || path == backup) return null;
            try
            {
                return serializer.DeserializeMetadataOnly(File.ReadAllText(backup));
            }
            catch
            {
                return null;
            }
        }
    }

    public int GetSlotCount() => settings.SlotCount;

    // --- Global (manager/singleton) state channel ---
    // Para estado que no pertenece a una entidad de escena con GUID.
    // Los managers escriben en su handler de OnBeforeSave y leen en OnAfterLoad.

    public void SetGlobalState(string key, object value)
    {
        var data = hotCache;
        if (data == null)
        {
            data = hotCache = new SaveData { version = settings.CurrentSaveVersion };
            hotCacheSlot = settings.AutoSaveSlotIndex;
        }
        data.globalState[key] = value;
    }

    public T GetGlobalState<T>(string key, T fallback = default)
    {
        if (hotCache == null || !hotCache.globalState.TryGetValue(key, out object raw))
            return fallback;
        try { return SaveUtils.Deserialize<T>(raw); }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveManager] GetGlobalState<{typeof(T).Name}>('{key}') falló: {e.Message}");
            return fallback;
        }
    }

    public bool HasGlobalState(string key)
        => hotCache != null && hotCache.globalState.ContainsKey(key);

    // --- Coroutines ---

    IEnumerator LoadCoroutine(int slot)
    {
        isLoading = true;
        // Estado que NUNCA debe sobrevivir a un load: un flag colgado dejaría el guardado muerto
        // (IsSavingSuppressed) o rompería el próximo load (IsResumingIntoMemory).
        IsSavingSuppressed = false;
        IsResumingIntoMemory = false;

        // try/finally garantiza que isLoading/IsResumingIntoMemory se limpien SIEMPRE, incluso si el
        // montaje del recuerdo (ResumeMemory) tira una excepción — si no, isLoading colgado en true
        // dejaría Save/Load/NewGame muertos toda la sesión. (C# permite yield dentro de try+finally.)
        try
        {
            var data = ReadFile(slot);
            if (data == null)
                yield break;

            RunMigrations(data);

            if (data.sceneBuildIndex < 0 || data.sceneBuildIndex >= SceneManager.sceneCountInBuildSettings)
            {
                Debug.LogError($"[SaveManager] sceneBuildIndex inválido ({data.sceneBuildIndex}); abortando carga.");
                yield break;
            }

            // Marcar HasLoadedGame ANTES de cargar la escena: los Start() de los objetos de escena
            // (IntroPlayer, AnimatorManager) lo leen para NO re-reproducir la intro / la pose de piso
            // en un Continue. Si se seteara después del load, esos Start() ya habrían corrido con el
            // flag en false y la intro se repetiría en cada carga.
            HasLoadedGame = true;

            yield return SceneManager.LoadSceneAsync(data.sceneBuildIndex);
            yield return null; // Wait one frame for DDOL singleton Destroy() to execute

            // Estado transitorio del pipeline (SceneControllerManager es DDOL vía PersistentObjects →
            // puede quedar colgado de una carga previa dentro de la misma sesión).
            if (SceneControllerManager.instance != null)
                SceneControllerManager.instance.ResetTransientState();

            // ¿Este save fue hecho DENTRO de un recuerdo? Resolver el PhotoData AHORA (DatabasesManager
            // vive en MainHouse: recién existe tras cargarla). Si no resuelve (GUID renombrado / DB sin
            // la foto), se cae a una carga normal de la casa (fallback seguro).
            PhotoData resumePhoto = null;
            if (!string.IsNullOrEmpty(data.recuerdoActivoId) && DatabasesManager.instance != null)
            {
                PhotoDatabase db = DatabasesManager.instance.GetPhotoDatabase();
                resumePhoto = db != null ? db.GetById(data.recuerdoActivoId) : null;
                if (resumePhoto == null)
                    Debug.LogWarning($"[SaveManager] recuerdoActivoId '{data.recuerdoActivoId}' no resolvió a un PhotoData; se carga la casa.");
            }
            // Requiere SceneControllerManager (DDOL, el que monta el recuerdo) Y PlayerManager (el que se
            // congela/reposiciona): si falta cualquiera, NO congelamos ni ponemos cortina — se cae a una
            // carga normal de la casa (degradación segura vs. dejar al player congelado en negro).
            IsResumingIntoMemory = resumePhoto != null
                && SceneControllerManager.instance != null
                && PlayerManager.instance != null;

            // Resume: cortina negra + congelar al player ANTES de encender la casa y restaurar, para
            // que no se vea la casa iluminada ni el player teletransportado a coords del recuerdo con
            // el piso del recuerdo aún sin montar (free-fall). Se descongela en ApplyPendingResumePose.
            if (IsResumingIntoMemory)
            {
                if (FadeManager.instance != null)
                    FadeManager.instance.SetBlackInstantly();
                if (PlayerManager.instance != null)
                {
                    PlayerManager.instance.CanMove(false);
                    Rigidbody prb = PlayerManager.instance.GetPlayerRigidbody();
                    if (prb != null) prb.isKinematic = true;
                }
            }

            // Two-pass: activar TODAS las zonas para que el registry esté completo antes de restaurar.
            // Sin esto, entidades en zonas desactivadas no se restaurarían.
            if (ZoneManager.instance != null)
                ZoneManager.instance.SetAllZonesActive(true);
            yield return null; // Esperar Awake/OnEnable de las zonas recién activadas

            loadedPlayTime = data.metadata.playTimeSeconds;
            sessionStartTime = Time.realtimeSinceStartup;

            hotCache = data;
            hotCacheSlot = slot;

            if (IsResumingIntoMemory)
            {
                // En un resume el player YA está congelado (kinematic) + pantalla negra; ResumeMemory es
                // la ÚNICA ruta que lo descongela y revela. Si RestoreLoadedState (RestoreState de la casa,
                // o un suscriptor de OnAfterLoad) tira, NO debe abortar la corrutina antes de ResumeMemory
                // (dejaría al player congelado en negro para siempre). Se loguea y se sigue al montaje, que
                // garantiza descongelar + revelar pase lo que pase. IsFullLoad=true se setea dentro; en un
                // resume PlayerManager.RestoreState stashea la pose y ZoneManager.OnAfterLoad no reactiva zonas.
                try { RestoreLoadedState(data, fullLoad: true); }
                catch (Exception e)
                {
                    Debug.LogError($"[SaveManager] Excepción restaurando la casa en un resume; se continúa al montaje del recuerdo.\n{e}");
                }
                // resumePhoto y SceneControllerManager.instance son no-null (IsResumingIntoMemory lo exige).
                yield return SceneControllerManager.instance.ResumeMemory(resumePhoto);
            }
            else
            {
                RestoreLoadedState(data, fullLoad: true);
            }
        }
        finally
        {
            IsResumingIntoMemory = false;
            isLoading = false;
        }
    }

    IEnumerator NewGameCoroutine()
    {
        isLoading = true;
        IsSavingSuppressed = false;

        // Mismo blindaje que LoadCoroutine, y por el mismo motivo: acá abajo hay dos cosas que pueden
        // tirar excepción — Delete() toca el disco (archivo bloqueado, antivirus) y OnNewGame?.Invoke()
        // corre N suscriptores externos que no controlamos. Sin el finally, cualquiera de las dos
        // abortaba la corrutina con isLoading colgado en true y dejaba Save/Load/NewGame muertos el
        // resto de la sesión, porque los tres gatean por ese flag.
        try
        {
            HasLoadedGame = false;
            IsFullLoad = false;
            loadedPlayTime = 0f;
            sessionStartTime = Time.realtimeSinceStartup;

            hotCache = null;
            hotCacheSlot = -1;

            // Delete() y NO un File.Delete del primario a mano: el .bak TAMBIÉN tiene que irse. ReadFile()
            // auto-recupera del backup cuando falta el primario (AutoRecoverFromBackup), así que borrar sólo
            // el slot_N.json dejaba la partida anterior viva en el .bak y el primer ReadFile de la partida
            // NUEVA la resucitaba entera y en silencio: entityStates + globalState del run viejo. Síntoma
            // visible: entrar al Recuerdo del Cumpleaños en una partida nueva y encontrar la puerta blanca
            // YA ABIERTA (LoadSceneState restauraba la rotación abierta que el run anterior había guardado).
            Delete(settings.AutoSaveSlotIndex);

            yield return SceneManager.LoadSceneAsync(settings.InitialSceneName);
            yield return null; // DDOL duplicate prevention

            // Limpiar el estado transitorio del SceneControllerManager (DDOL vía PersistentObjects): si venías
            // de un recuerdo y fuiste al menú vía PauseMenu (que NO pasa por el retorno normal que limpia esos
            // campos), IsInMemoryScene/actualPhotoData quedan colgados → el PRIMER save del New Game escribiría
            // recuerdoActivoId del recuerdo VIEJO → el próximo Continue resumiría a un recuerdo que esta partida
            // nueva nunca alcanzó (New Game corrupto). Mismo reset que hace LoadCoroutine.
            if (SceneControllerManager.instance != null)
                SceneControllerManager.instance.ResetTransientState();

            // Los managers DDOL siguen vivos con el estado del run anterior: que cada uno se limpie.
            OnNewGame?.Invoke();
        }
        finally
        {
            isLoading = false;
        }
    }

    // --- Internal ---

    void CaptureState(Dictionary<string, Dictionary<string, object>> entityStates)
    {
        idsVistosEnEstaCaptura.Clear();

        foreach (var entity in SaveableEntity.GetAllActive())
        {
            string id = entity.GetUniqueIdentifier();
            if (string.IsNullOrEmpty(id)) continue;

            if (!idsVistosEnEstaCaptura.Add(id))
            {
                Debug.LogError($"[SaveManager] uniqueIdentifier DUPLICADO entre entidades activas: '{id}' " +
                               $"(última: '{entity.name}'). Son la misma entidad de guardado: se pisan el " +
                               $"estado entre ellas. Dale un GUID propio a una.", entity);
            }

            entityStates[id] = entity.CaptureState() as Dictionary<string, object>;
        }
    }

    void RestoreState(Dictionary<string, Dictionary<string, object>> entityStates)
    {
        entidadesYaRestauradas.Clear();

        for (int pasada = 0; pasada < MaxPasadasRestauracion; pasada++)
        {
            // Snapshot: a partir de acá el registry puede crecer sin romper la iteración.
            bufferRestauracion.Clear();
            foreach (var entity in SaveableEntity.GetAllActive())
            {
                if (entity == null || entidadesYaRestauradas.Contains(entity)) continue;
                bufferRestauracion.Add(entity);
            }

            if (bufferRestauracion.Count == 0) return;

            for (int i = 0; i < bufferRestauracion.Count; i++)
            {
                var entity = bufferRestauracion[i];
                // Pudo destruirse durante esta misma pasada (otro RestoreState lo dio de baja).
                if (entity == null) continue;

                entidadesYaRestauradas.Add(entity);

                string id = entity.GetUniqueIdentifier();
                if (string.IsNullOrEmpty(id)) continue;
                if (!entityStates.ContainsKey(id)) continue;

                entity.RestoreState(entityStates[id]);
            }
        }

        Debug.LogWarning($"[SaveManager] La restauración cortó en {MaxPasadasRestauracion} pasadas y " +
                         "todavía aparecían entidades nuevas. Alguna cadena de RestoreState está " +
                         "activando objetos en cascada: revisá que no haya un ciclo.");
    }

    SaveData GetOrLoadHotCache(int slot)
    {
        if (hotCache != null && hotCacheSlot == slot)
            return hotCache;

        hotCache = ReadFile(slot) ?? new SaveData { version = settings.CurrentSaveVersion };
        hotCacheSlot = slot;
        return hotCache;
    }

    SaveData ReadFile(int slot)
    {
        string path = settings.GetSavePath(slot);

        if (!File.Exists(path))
        {
            if (settings.AutoRecoverFromBackup)
            {
                string backup = settings.GetBackupPath(slot);
                if (File.Exists(backup))
                {
                    Debug.Log($"[SaveManager] Primary file missing, recovering from backup: {backup}");
                    path = backup;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        try
        {
            string raw = File.ReadAllText(path);
            return serializer.Deserialize(raw);
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Failed to read save file: {e.Message}");

            if (settings.AutoRecoverFromBackup)
            {
                string backup = settings.GetBackupPath(slot);
                if (File.Exists(backup) && path != backup)
                {
                    Debug.Log("[SaveManager] Attempting backup recovery...");
                    try
                    {
                        string raw = File.ReadAllText(backup);
                        return serializer.Deserialize(raw);
                    }
                    catch (Exception e2)
                    {
                        Debug.LogError($"[SaveManager] Backup recovery also failed: {e2.Message}");
                    }
                }
            }

            return null;
        }
    }

    void WriteFile(string path, int slot, SaveData data)
    {
        try
        {
            string json = serializer.Serialize(data);
            string tmp = path + ".tmp";

            // Escritura atómica: escribir a temporal y reemplazar. File.Replace es
            // atómico en NTFS y genera el backup en el mismo paso (sin ventana de corrupción).
            File.WriteAllText(tmp, json);

            if (File.Exists(path))
            {
                string backup = settings.BackupEnabled ? settings.GetBackupPath(slot) : null;
                File.Replace(tmp, path, backup);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Failed to write save file: {e.Message}");
            // Fallback best-effort: escritura directa para no perder el save por completo.
            try { File.WriteAllText(path, serializer.Serialize(data)); }
            catch (Exception e2) { Debug.LogError($"[SaveManager] Fallback write also failed: {e2.Message}"); }
        }
    }

    void RunMigrations(SaveData data)
    {
        var migrationList = settings.GetMigrations();

        // Encadena migraciones en cualquier orden hasta que ninguna aplique.
        // Avanza data.version paso a paso (soporta upgrades multi-versión).
        bool advanced = true;
        int safety = 0;
        while (advanced && safety++ < 64)
        {
            advanced = false;
            for (int i = 0; i < migrationList.Count; i++)
            {
                var migration = migrationList[i];
                if (data.version == migration.FromVersion)
                {
                    Debug.Log($"[SaveManager] Running migration v{migration.FromVersion} → v{migration.ToVersion}");
                    migration.Migrate(data);
                    data.version = migration.ToVersion;
                    advanced = true;
                    break;
                }
            }
        }

        if (data.version != settings.CurrentSaveVersion)
            Debug.LogWarning($"[SaveManager] Save quedó en v{data.version} pero la versión actual es v{settings.CurrentSaveVersion}. ¿Falta una migración?");
    }

    void EnsureSaveDirectory()
    {
        string dir = settings.GetSaveDirectory();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    void DeleteLegacyFiles()
    {
        string dir = settings.GetSaveDirectory();
        if (!Directory.Exists(dir)) return;

        foreach (string file in Directory.GetFiles(dir, settings.GetLegacyPattern()))
        {
            try { File.Delete(file); }
            catch (Exception e) { Debug.LogWarning($"[SaveManager] Failed to delete legacy file: {e.Message}"); }
        }
    }

    void DeleteAllSaves()
    {
        for (int i = 0; i < settings.SlotCount; i++)
            Delete(i);
    }
}
