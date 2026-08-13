#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Herramienta de AUTORÍA (Editor-only): dibuja en la Scene view hacia dónde va a terminar un evento
/// del Director / de Vida Lejana ANTES de entrar a Play. Resuelve la pregunta "¿para qué lado gira/abre/cae?"
/// sin tener que jugar y esperar a que el director elija ese evento.
///
/// Lo consumen los actuadores vía <see cref="HorrorEvent.DrawAuthoringGizmos"/> y
/// <see cref="VidaLejanaTarget"/>. Solo se dibuja con el objeto SELECCIONADO (OnDrawGizmosSelected),
/// así que no ensucia la escena.
///
/// Complementa a <see cref="DirectorAuthoringValidation"/>: el validador avisa de lo que está MAL
/// (zona, refs, dueño doble); esto muestra lo que va a PASAR.
/// </summary>
public static class DirectorAuthoringGizmos
{
    // ── Paleta (un color = un significado, igual en todos los actuadores) ─────
    /// <summary>Pose destino del objeto (dónde termina).</summary>
    public static readonly Color GhostColor = new Color(0.30f, 0.85f, 1f, 1f);
    /// <summary>Dirección de un empuje/desplazamiento.</summary>
    public static readonly Color ArrowColor = new Color(1f, 0.55f, 0.10f, 1f);
    /// <summary>Vínculo con un objeto referenciado (luz, ventilador, reloj, anchor).</summary>
    public static readonly Color LinkColor = new Color(0.45f, 1f, 0.45f, 1f);
    /// <summary>Referencia faltante o configuración que no va a hacer nada.</summary>
    public static readonly Color WarnColor = new Color(1f, 0.35f, 0.35f, 1f);

    // Techo de mallas por fantasma: props con jerarquías enormes no cuelgan el repaint del Editor.
    private const int MaxGhostMeshes = 32;
    private const float FallbackGhostSize = 0.25f;
    private const float ArrowHeadRatio = 0.18f;
    private const float ArrowHeadAngle = 22f;
    private const float MinArcRadius = 0.35f;
    private const float LabelOffsetY = 0.12f;
    private const float DefaultMarkerRadius = 0.15f;

    // ─────────────────────────────────────────────────────────────────────────
    // Cálculo de la pose destino (una por convención de offset — ver el manual §Chuleta de DIRECCIÓN)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Offset de posición en MUNDO + offset de rotación sobre los ejes LOCALES (MovingObjectHorrorEvent).</summary>
    public static Matrix4x4 DestinationFromOffsets(Transform t, Vector3 worldPositionOffset, Quaternion localRotationOffset)
    {
        return Matrix4x4.TRS(t.position + worldPositionOffset, t.rotation * localRotationOffset, t.lossyScale);
    }

    /// <summary>Rotación local ABSOLUTA (puertas: el ángulo del Inspector no es un delta, es el destino).</summary>
    public static Matrix4x4 DestinationFromLocalRotation(Transform t, Quaternion newLocalRotation)
    {
        Quaternion parentRotation = t.parent != null ? t.parent.rotation : Quaternion.identity;
        return Matrix4x4.TRS(t.position, parentRotation * newLocalRotation, t.lossyScale);
    }

    /// <summary>Offset de posición en espacio LOCAL (Vida Lejana Move, cajones).</summary>
    public static Matrix4x4 DestinationFromLocalPositionOffset(Transform t, Vector3 localOffset)
    {
        Vector3 worldOffset = t.parent != null ? t.parent.TransformVector(localOffset) : localOffset;
        return Matrix4x4.TRS(t.position + worldOffset, t.rotation, t.lossyScale);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dibujo
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dibuja el objeto (y sus hijos) en wireframe en la pose destino. Si no tiene mallas,
    /// cae a un cubo chico para que igual se vea el punto de llegada.
    /// </summary>
    public static void DrawGhost(Transform target, Matrix4x4 destination, Color color)
    {
        if (target == null) return;

        Color previousColor = Gizmos.color;
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.color = color;

        // El wireframe se dibuja en la pose destino manteniendo la jerarquía relativa del prop.
        Matrix4x4 worldToTarget = target.worldToLocalMatrix;
        MeshFilter[] filters = target.GetComponentsInChildren<MeshFilter>(true);

        int drawn = 0;
        for (int i = 0; i < filters.Length && drawn < MaxGhostMeshes; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (mesh == null) continue;

            Gizmos.matrix = destination * (worldToTarget * filters[i].transform.localToWorldMatrix);
            Gizmos.DrawWireMesh(mesh);
            drawn++;
        }

        if (drawn == 0)
        {
            Gizmos.matrix = destination;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one * FallbackGhostSize);
        }

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }

    /// <summary>Flecha desde un origen hacia una dirección de MUNDO (empuje, desplazamiento).</summary>
    public static void DrawArrow(Vector3 origin, Vector3 direction, float length, Color color)
    {
        if (direction.sqrMagnitude < Mathf.Epsilon || length <= 0f) return;

        Color previousColor = Gizmos.color;
        Gizmos.color = color;

        Vector3 dir = direction.normalized;
        Vector3 tip = origin + dir * length;
        Gizmos.DrawLine(origin, tip);

        // Punta: cuatro aristas hacia atrás, rotadas alrededor de un eje perpendicular cualquiera.
        Vector3 perpendicular = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.right : Vector3.up).normalized;
        float headLength = length * ArrowHeadRatio;
        for (int i = 0; i < 4; i++)
        {
            Quaternion spin = Quaternion.AngleAxis(i * 90f, dir);
            Vector3 back = Quaternion.AngleAxis(180f - ArrowHeadAngle, spin * perpendicular) * dir;
            Gizmos.DrawLine(tip, tip + back * headLength);
        }

        Gizmos.color = previousColor;
    }

    /// <summary>
    /// Arco de barrido de una puerta alrededor de su eje local Y, desde el reposo hasta el ángulo destino.
    /// El signo del ángulo es justamente lo que decide hacia qué lado abre.
    /// </summary>
    public static void DrawSwingArc(Transform pivot, float targetLocalAngleY, Color color)
    {
        if (pivot == null || Mathf.Approximately(targetLocalAngleY, 0f)) return;

        float radius = Mathf.Max(EstimateRadius(pivot), MinArcRadius);
        Quaternion parentRotation = pivot.parent != null ? pivot.parent.rotation : Quaternion.identity;
        Vector3 axis = parentRotation * Vector3.up;
        Vector3 from = parentRotation * Vector3.forward;

        Color previousColor = Handles.color;
        Handles.color = color;
        Handles.DrawWireArc(pivot.position, axis, from, targetLocalAngleY, radius);
        Handles.DrawLine(pivot.position, pivot.position + from * radius);
        Handles.DrawLine(pivot.position, pivot.position + (Quaternion.AngleAxis(targetLocalAngleY, axis) * from) * radius);
        Handles.color = previousColor;
    }

    /// <summary>Línea + esfera hacia el objeto referenciado (luz, ventilador, reloj, anchor de audio).</summary>
    public static void DrawReferenceLink(Vector3 from, Vector3 to, float markerRadius, Color color)
    {
        Color previousColor = Gizmos.color;
        Gizmos.color = color;
        Gizmos.DrawLine(from, to);
        Gizmos.DrawWireSphere(to, markerRadius);
        Gizmos.color = previousColor;
    }

    /// <summary>
    /// Caso común de los actuadores que delegan en otro componente (luz, vela, ventilador, reloj):
    /// no hay dirección que dibujar, lo único que importa es QUÉ objeto van a tocar — y si falta la ref.
    /// </summary>
    public static void DrawComponentLink(Transform origin, Component reference, string label, string missingLabel)
    {
        if (origin == null) return;

        if (reference == null)
        {
            DrawLabel(origin.position, missingLabel, WarnColor);
            return;
        }

        Vector3 target = reference.transform.position;
        DrawReferenceLink(origin.position, target, DefaultMarkerRadius, LinkColor);
        DrawLabel(target, label, LinkColor);
    }

    /// <summary>Esfera de radio (alcances de audio, radios de sampleo).</summary>
    public static void DrawRadius(Vector3 center, float radius, Color color)
    {
        if (radius <= 0f) return;

        Color previousColor = Gizmos.color;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(center, radius);
        Gizmos.color = previousColor;
    }

    /// <summary>Etiqueta de texto en la Scene view (qué es lo que estoy viendo).</summary>
    public static void DrawLabel(Vector3 position, string text, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;

        var style = new GUIStyle(EditorStyles.boldLabel);
        style.normal.textColor = color;
        Handles.Label(position + Vector3.up * LabelOffsetY, text, style);
    }

    /// <summary>Radio aproximado del prop, para dimensionar arcos y flechas sin números mágicos por objeto.</summary>
    public static float EstimateRadius(Transform t)
    {
        if (t == null) return MinArcRadius;

        Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return MinArcRadius;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

        // Distancia del pivot al punto más lejano del volumen (la puerta rota desde su borde, no desde el centro).
        return Vector3.Distance(t.position, bounds.center) + bounds.extents.magnitude;
    }
}
#endif
