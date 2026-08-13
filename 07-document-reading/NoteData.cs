using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Serialization;

[CreateAssetMenu(fileName = "New Note", menuName = "Notes/NoteData")]
public class NoteData : ScriptableObject, IIdentificable
{
    public LocalizedString noteName;

    [Tooltip("Prefab usado SOLO para mostrar la nota en el inventario. En el mundo se lee el objeto in situ.")]
    public Transform notePrefab;

    [Tooltip("Si la nota se guarda en el inventario y desaparece del mundo al salir de la lectura.")]
    public bool isPickable;

    [Header("Contenido")]
    [Tooltip("Texto localizado que muestra el panel de documentos. Admite rich text de TMP (ej: <color=#E8D44D>palabra clave</color>).")]
    [FormerlySerializedAs("frontContent")]
    public LocalizedString content;

    [SerializeField, HideInInspector] private string id;
    public string ID => id;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(id))
        {
            id = System.Guid.NewGuid().ToString();
            UnityEditor.EditorUtility.SetDirty(this); // Marca como modificado para guardar
        }
    }
#endif
}
