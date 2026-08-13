using System.Collections.Generic;
using System.IO;
using UnityEngine;

[CreateAssetMenu(fileName = "SaveSettings", menuName = "Saving/Save Settings")]
public class SaveSettings : ScriptableObject
{
    [Header("Slots")]
    [SerializeField] int slotCount = 3;
    [SerializeField] int autoSaveSlotIndex = 0;

    [Header("Scenes")]
    [Tooltip("Escena inicial al empezar partida nueva. Por NOMBRE, no por build index: " +
             "los índices se renumeran al deshabilitar/reordenar escenas y difieren entre Editor y build, " +
             "lo que rompe silenciosamente la carga. El nombre es inequívoco en ambos entornos.")]
    [SerializeField] string initialSceneName = "MainHouseTesis";

    [Tooltip("Escena del menú principal. Estando en ella NUNCA se autoguarda: Save() sella el " +
             "sceneBuildIndex con la escena activa, así que un autoguardado desde el menú dejaría el " +
             "slot apuntando al menú y el Continue volvería a cargar el menú para siempre.")]
    [SerializeField] string menuSceneName = "MenuPrincipal";

    [Header("File System")]
    [SerializeField] string saveDirectoryName = "saves";
    [SerializeField] string fileExtension = "json";
    [SerializeField] string slotPrefix = "slot_";
    [SerializeField] string backupSuffix = ".bak";

    [Header("Backup")]
    [SerializeField] bool backupEnabled = true;
    [SerializeField] bool autoRecoverFromBackup = true;

    [Header("Serialization")]
    [Tooltip("Activar solo para depurar (saves legibles). En producción aumenta tamaño y tiempo de serialización.")]
    [SerializeField] bool indentedFormatting = false;

    [Header("Versioning")]
    [SerializeField] int currentSaveVersion = 1;
    [SerializeField] List<ScriptableObject> migrations = new List<ScriptableObject>();

    [Header("Legacy Cleanup")]
    [SerializeField] bool deleteLegacyOnStartup = true;
    [SerializeField] string legacyFileExtension = "sav";

    [Header("Editor Only")]
    [SerializeField] bool deleteAllSavesOnPlay;

    public int SlotCount => slotCount;
    public int AutoSaveSlotIndex => autoSaveSlotIndex;
    public string InitialSceneName => initialSceneName;
    public string MenuSceneName => menuSceneName;
    public bool BackupEnabled => backupEnabled;
    public bool AutoRecoverFromBackup => autoRecoverFromBackup;
    public bool IndentedFormatting => indentedFormatting;
    public int CurrentSaveVersion => currentSaveVersion;
    public bool DeleteLegacyOnStartup => deleteLegacyOnStartup;
    public bool DeleteAllSavesOnPlay => deleteAllSavesOnPlay;

    public string GetSaveDirectory()
        => Path.Combine(Application.persistentDataPath, saveDirectoryName);

    public string GetSavePath(int slot)
        => Path.Combine(GetSaveDirectory(), $"{slotPrefix}{slot}.{fileExtension}");

    public string GetBackupPath(int slot)
        => Path.Combine(GetSaveDirectory(), $"{slotPrefix}{slot}.{fileExtension}{backupSuffix}");

    public string GetLegacyPattern()
        => $"*.{legacyFileExtension}";

    public IReadOnlyList<ISaveMigration> GetMigrations()
    {
        var result = new List<ISaveMigration>();
        foreach (var so in migrations)
        {
            if (so is ISaveMigration migration)
                result.Add(migration);
        }
        return result;
    }

    void OnValidate()
    {
        slotCount = Mathf.Max(1, slotCount);
        autoSaveSlotIndex = Mathf.Clamp(autoSaveSlotIndex, 0, slotCount - 1);

        if (autoRecoverFromBackup && !backupEnabled)
            Debug.LogWarning("[SaveSettings] AutoRecoverFromBackup está activo pero BackupEnabled está desactivado — la recuperación nunca se disparará.");
    }
}
