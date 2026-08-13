
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;

public enum Memory { Birthday, ArtRoom, Hospital }

[CreateAssetMenu(fileName = "Foto", menuName = "ScriptableObjects/Foto", order = 1)]
public class PhotoData : ScriptableObject, IIdentificable
{
    [Header("Photo Data")]
    [SerializeField] private LocalizedString photoTitle;
    [SerializeField] private string sceneToLoad;
    [SerializeField] private Sprite photoIcon;
    [SerializeField] private bool revealed = true;
    [SerializeField] private GameObject prefab;
    [SerializeField] private Memory memoryToGo;
    [SerializeField] private List<Zone> zonesToAvoid = new List<Zone>();

    public LocalizedString PhotoTitle => photoTitle;
    public string SceneToLoad => sceneToLoad;
    public Sprite PhotoIcon => photoIcon;
    public bool IsRevealed => revealed;
    public GameObject Prefab => prefab;
    public Memory MemoryToGo => memoryToGo;
    public List<Zone> ZonesToAvoid => zonesToAvoid;

    [Header("Memory Entry")]
    [SerializeField] private Vector3 frontFaceDirection = Vector3.forward;
    public Vector3 FrontFaceDirection => frontFaceDirection;

    [Header("Memory Emergence Audio")]
    // The memory's sound that EMERGES from the photo during the hold (laughter, brushstrokes,
    // monitor tone). Looped, starts at volume 0, ramped open (volume + lowpass) by the SAME
    // hold curve that drives the visual membrane → it reads as the sound coming FROM the photo.
    // Leave null = silent. See PhotoMemoryPortal.SetupMemoryEmergeAudio.
    [SerializeField] private AudioClip memoryEmergenceClip;
    public AudioClip MemoryEmergenceClip => memoryEmergenceClip;

    // OFF (default) = the clip is PRE-PROCESSED in a DAW (baked ghostly) → played clean
    //   (only the whisper→full volume swell + tape pitch LFO).
    // ON = raw clip → the portal adds its runtime ghost chain (HP/LP/chorus/reverb).
    //   Careful: the runtime chain band-passes ~180–500 Hz at whisper — it can silence an
    //   already-processed bright clip entirely, so enable it ONLY for unprocessed audio.
    [SerializeField] private bool emergenceClipNeedsGhostFx = false;
    public bool EmergenceClipNeedsGhostFx => emergenceClipNeedsGhostFx;

    // ── Per-memory transition escalation ──────────────────────────────────────
    // The entry effect escalates ONLY on the TRANSPORT axis (how it drags you in),
    // never on photo CONTENT. M1 = voluntary/learning, M3 = forced/violent.
    // Data-driven so M2/M3 can feel qualitatively worse without touching code/shader.
    [Header("Memory Transition — per-memory escalation (defaults = M1)")]
    [Tooltip("Seconds to hold to commit. M1=3.0 (learning), M2~2.8, M3~2.4 (forced).")]
    [SerializeField] private float holdDuration = 3.0f;
    [Tooltip("World UV pull toward the photo. M1=0.012, M2=0.015, M3=0.022.")]
    [SerializeField] private float uvWarpStrength = 0.012f;

    // Los diales de "Photo Burn (ARDE)" y "Silver Drowning Decay" vivían acá con tooltips de tuning
    // por memoria, pero NINGÚN script los leía: quedaron huérfanos cuando la transición pasó al
    // crack-glow actual. Eran perillas visibles en el Inspector que no hacían nada al moverlas.
    public float HoldDuration => holdDuration;
    public float UvWarpStrength => uvWarpStrength;

    [Header("Solved State (leave empty for placeholder)")]
    [SerializeField] private GameObject solvedPrefab;
    [SerializeField] private Sprite solvedIcon;

    public bool HasMemoryScene => !string.IsNullOrEmpty(sceneToLoad);

    public GameObject GetActivePrefab(bool isSolved)
        => isSolved && solvedPrefab != null ? solvedPrefab : prefab;

    public Sprite GetActiveIcon(bool isSolved)
        => isSolved && solvedIcon != null ? solvedIcon : photoIcon;

    [SerializeField, HideInInspector] private string id;
    public string ID => id;

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(id))
        {
            id = System.Guid.NewGuid().ToString();
            UnityEditor.EditorUtility.SetDirty(this);
        }
    }
#endif
}
