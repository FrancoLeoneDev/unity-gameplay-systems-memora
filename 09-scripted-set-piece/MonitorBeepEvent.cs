using System.Collections;
using UnityEngine;

/// <summary>
/// Beep de monitor cardíaco (EKG) en loop a BPM, sample-accurate vía <c>AudioSource.PlayScheduled</c>
/// (evita el jitter de frame de un timer en Update). Un solo source basta: el beep (~0.3 s) es mucho
/// más corto que el intervalo (~1 s a 60 BPM). Reusable (hospital).
///
/// NOTA (D13): durante el preview de disociación NO debe correr — el monitor lo trae RecallDaze
/// (bus InnerVoice). <see cref="MonitorBeepEvent"/> queda SOLO para la ruta fail-open (RecallDaze ausente).
/// </summary>
public class MonitorBeepEvent : MonoBehaviour
{
    [Header("Fuente y clip")]
    [SerializeField, Tooltip("AudioSource 3D (spatialBlend=1, rolloff Logarithmic) ruteado al grupo Effects.")]
    private AudioSource beepSource;

    [SerializeField, Tooltip("El beep de monitor (~0.3 s). Si es null no suena (no crashea).")]
    private AudioClip beepClip;

    [Header("Cadencia")]
    [SerializeField, Range(40f, 100f), Tooltip("Latidos por minuto. 60 = normal = uncanny (indiferencia).")]
    private float bpm = 60f;

    private bool _running;
    private Coroutine _routine;

    /// <summary>Arranca el beep en loop. No hace nada si ya corre, o si falta source/clip.</summary>
    public void Play()
    {
        if (_running || beepSource == null || beepClip == null) return;
        _running = true;
        _routine = StartCoroutine(BeepRoutine());
    }

    /// <summary>Detiene el beep.</summary>
    public void Stop()
    {
        _running = false;
        if (_routine != null) { StopCoroutine(_routine); _routine = null; }
        if (beepSource != null) beepSource.Stop();
    }

    private IEnumerator BeepRoutine()
    {
        beepSource.clip = beepClip;
        float interval = 60f / Mathf.Max(1f, bpm);
        var wait = new WaitForSeconds(interval);
        double next = AudioSettings.dspTime + 0.1; // pequeña anticipación; el ritmo se ancla al dsp clock
        while (_running)
        {
            beepSource.PlayScheduled(next);
            next += interval;
            yield return wait;
        }
    }

    private void OnDisable() => Stop();
}
