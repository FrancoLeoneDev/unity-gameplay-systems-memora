using UnityEngine;

/// <summary>
/// Micro-movimiento SUB-UMBRAL del Doctor (respiración/balanceo en X+Y vía Perlin) para que el recorte
/// estático lea como PRESENCIA viva, no como prop/asset. Estrictamente sub-umbral: en una sola mirada NO
/// se debe notar; con 3-5s de hold SÍ se "siente". NUNCA un paso ni un giro (eso = revelación = gasta el
/// símbolo). Lo uncanny es la AUSENCIA del micro-movimiento esperado: mantener al Doctor MÁS quieto que
/// cualquier prop ambiental.
///
/// Arranca enabled=false (reposo); el evento lo prende en el reveal (F4) y lo apaga en F5/Restore.
/// Frame-rate-independiente (Perlin sampleado por tiempo, wrapeado &lt;50k). Restaura localPosition en OnDisable.
/// </summary>
public sealed class DoctorSubtleSway : MonoBehaviour
{
    [SerializeField, Tooltip("Amplitud vertical (m) — la 'respiración'. ~0.012 = sub-umbral a 4-5m.")]
    private float amplitudeY = 0.012f;
    [SerializeField, Tooltip("Frecuencia vertical (Hz). Lenta = respiración.")]
    private float freqY = 0.22f;
    [SerializeField, Tooltip("Amplitud horizontal (m) — balanceo mínimo. Menor que la vertical.")]
    private float amplitudeX = 0.007f;
    [SerializeField, Tooltip("Frecuencia horizontal (Hz). Distinta de la vertical → Lissajous orgánico (no loop).")]
    private float freqX = 0.16f;

    private Vector3 _baseLocalPos;
    private float _seedX;
    private float _seedY;
    private float _t;

    private void Awake()
    {
        _baseLocalPos = transform.localPosition;
        _seedX = Random.Range(0f, 1000f);
        _seedY = Random.Range(0f, 1000f);
    }

    private void OnEnable()
    {
        _baseLocalPos = transform.localPosition; // por si lo reposicionaron entre activaciones
        _t = 0f;
    }

    private void OnDisable()
    {
        transform.localPosition = _baseLocalPos; // restaurar exacto
    }

    private void LateUpdate()
    {
        _t += Time.deltaTime;
        if (_t > 50000f) _t = 0f; // PerlinNoise glitchea > 50k

        float ox = (Mathf.PerlinNoise(_seedX, _t * freqX) - 0.5f) * 2f * amplitudeX;
        float oy = (Mathf.PerlinNoise(_seedY, _t * freqY) - 0.5f) * 2f * amplitudeY;
        transform.localPosition = _baseLocalPos + new Vector3(ox, oy, 0f);
    }
}
