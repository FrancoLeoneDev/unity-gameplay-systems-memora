using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor de <see cref="DoorWithKey"/>: hace VISIBLE el seteo de la cerradura, que a mano es a ciegas.
///
/// El problema que resuelve: <c>keyFinalPosition</c> es un offset en coordenadas de MUNDO que se le
/// suma a <c>keySpawnPoint</c> (<c>finalPosition = keySpawnPoint.position + keyFinalPosition</c>).
/// Es un punto invisible: en el Inspector son tres números y la única forma de saber si la llave
/// entra bien en la cerradura era entrar a Play, mirar, salir, corregir, repetir.
///
/// Acá ese punto pasa a ser un handle que se arrastra en la Scene view, con la llave dibujada en el
/// origen y en el destino, la trayectoria y el arco de giro. Más un botón que reproduce la animación
/// completa SIN entrar a Play.
/// </summary>
[CustomEditor(typeof(DoorWithKey))]
public class DoorWithKeyEditor : Editor
{
    private const float RadioPunto  = 0.030f;
    private const float RadioArco   = 0.220f;
    private const float LargoEje    = 0.150f;
    private const float AltoLabel   = 0.120f;

    private SerializedProperty keySpawnPoint, bocaCerradura, camPos, keyNeeded;
    private SerializedProperty keyFinalPosition, rotateKeyValue, axisToRotate;
    private SerializedProperty insertKeyDuration, rotateKeyDuration;

    // ── estado de la previsualización ────────────────────────────────────────
    private static GameObject llaveFantasma;
    private static DoorWithKey puertaEnPreview;
    private static double tiempoInicio;
    private static Quaternion rotacionBocaOriginal;
    private static Quaternion rotacionLlaveAlSpawnear;

    // ── etiquetas legibles ───────────────────────────────────────────────────
    private static Texture2D fondoEtiqueta;
    private static GUIStyle estiloEtiqueta;

    /// <summary>Las etiquetas por defecto de Handles son chicas y sin fondo: sobre una pared clara no
    /// se leen. Ésta va en negrita, más grande y con fondo negro.</summary>
    private static GUIStyle Etiqueta(Color color)
    {
        if (fondoEtiqueta == null)
        {
            fondoEtiqueta = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            fondoEtiqueta.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
            fondoEtiqueta.Apply();
        }
        if (estiloEtiqueta == null)
        {
            estiloEtiqueta = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(6, 6, 3, 3),
                wordWrap  = false,
            };
            estiloEtiqueta.normal.background = fondoEtiqueta;
        }
        estiloEtiqueta.normal.textColor = color;
        return estiloEtiqueta;
    }

    private void OnEnable()
    {
        keySpawnPoint     = serializedObject.FindProperty("keySpawnPoint");
        bocaCerradura     = serializedObject.FindProperty("bocaCerradura");
        camPos            = serializedObject.FindProperty("camPos");
        keyNeeded         = serializedObject.FindProperty("keyNeeded");
        keyFinalPosition  = serializedObject.FindProperty("keyFinalPosition");
        rotateKeyValue    = serializedObject.FindProperty("rotateKeyValue");
        axisToRotate      = serializedObject.FindProperty("axisToRotate");
        insertKeyDuration = serializedObject.FindProperty("insertKeyDuration");
        rotateKeyDuration = serializedObject.FindProperty("rotateKeyDuration");
    }

    private void OnDisable() => CancelarPreview();

    // ── Inspector ────────────────────────────────────────────────────────────
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        serializedObject.Update();

        var puerta = (DoorWithKey)target;
        Transform spawn = keySpawnPoint.objectReferenceValue as Transform;
        Transform boca  = bocaCerradura.objectReferenceValue as Transform;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Cerradura", EditorStyles.boldLabel);

        if (spawn == null)
        {
            EditorGUILayout.HelpBox("Sin keySpawnPoint no hay nada que previsualizar: la llave sale de ahí.", MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox(
            "El destino de la llave es el punto AMARILLO de la escena; arrastralo para ajustarlo " +
            "(escribe keyFinalPosition, que es un offset en coordenadas de mundo).", MessageType.Info);

        if (boca == null)
        {
            EditorGUILayout.HelpBox(
                "bocaCerradura SIN ASIGNAR. Sin ella, RotateKey gira LA LLAVE sobre su propio eje local " +
                "en vez de girarla dentro de la cerradura, y ese eje depende de cómo esté modelado el " +
                "prefab, no de la puerta: por eso el giro sale para cualquier lado. Asigná un empty en " +
                "la cerradura, con su eje Z entrando en la hoja.", MessageType.Warning);
        }
        else
        {
            if (GUILayout.Button("Llevar el destino a la boca de la cerradura"))
            {
                Undo.RecordObject(puerta, "Destino de la llave");
                keyFinalPosition.vector3Value = boca.position - spawn.position;
                serializedObject.ApplyModifiedProperties();
            }

            // ¿el eje elegido apunta de verdad hacia adentro de la puerta?
            var rend = puerta.GetComponent<Renderer>();
            if (rend != null)
            {
                Vector3 s = rend.bounds.size;
                Vector3 normal = s.x <= s.y && s.x <= s.z ? Vector3.right : (s.y <= s.z ? Vector3.up : Vector3.forward);
                int i = axisToRotate.enumValueIndex;
                Vector3 ejeElegido = i == 0 ? boca.right : i == 1 ? boca.up : boca.forward;
                float alineacion = Mathf.Abs(Vector3.Dot(ejeElegido.normalized, normal));

                float aR = Mathf.Abs(Vector3.Dot(boca.right, normal));
                float aU = Mathf.Abs(Vector3.Dot(boca.up, normal));
                float aF = Mathf.Abs(Vector3.Dot(boca.forward, normal));
                int mejor = aR >= aU && aR >= aF ? 0 : (aU >= aF ? 1 : 2);

                if (alineacion < 0.85f)
                {
                    EditorGUILayout.HelpBox(
                        "El eje " + "XYZ"[i] + " de bocaCerradura no entra en la puerta (alineación " +
                        alineacion.ToString("F2") + "): la llave va a girar de costado. El que mejor " +
                        "apunta adentro es el " + "XYZ"[mejor] + ".", MessageType.Warning);
                    if (GUILayout.Button("Usar el eje " + "XYZ"[mejor]))
                    {
                        Undo.RecordObject(puerta, "Eje de giro de la llave");
                        axisToRotate.enumValueIndex = mejor;
                        serializedObject.ApplyModifiedProperties();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Eje de giro", "" + "XYZ"[i] + "  ✔ entra en la puerta (" + alineacion.ToString("F2") + ")", EditorStyles.miniLabel);
                }
            }
        }

        EditorGUILayout.Space();
        bool enPreview = llaveFantasma != null && puertaEnPreview == puerta;
        if (!enPreview)
        {
            if (GUILayout.Button("▶  Previsualizar (sin entrar a Play)", GUILayout.Height(26)))
                IniciarPreview(puerta);
        }
        else if (GUILayout.Button("■  Detener previsualización", GUILayout.Height(26)))
        {
            CancelarPreview();
        }

        if (keyNeeded.objectReferenceValue == null)
        {
            EditorGUILayout.HelpBox("keyNeeded sin asignar: la previsualización usa una llave genérica.", MessageType.None);
        }
        else
        {
            AvisarSobreElPrefab((KeyData)keyNeeded.objectReferenceValue);
        }

        serializedObject.ApplyModifiedProperties();
    }

    // ── Scene view ───────────────────────────────────────────────────────────
    private void OnSceneGUI()
    {
        serializedObject.Update();

        Transform spawn = keySpawnPoint.objectReferenceValue as Transform;
        Transform boca  = bocaCerradura.objectReferenceValue as Transform;
        Transform cam   = camPos.objectReferenceValue as Transform;
        if (spawn == null) return;

        Vector3 origen  = spawn.position;
        Vector3 destino = origen + keyFinalPosition.vector3Value;

        // origen
        Handles.color = new Color(0.35f, 1f, 0.45f);
        Handles.SphereHandleCap(0, origen, Quaternion.identity, RadioPunto, EventType.Repaint);
        Handles.Label(origen + Vector3.up * AltoLabel, "sale acá", Etiqueta(new Color(0.35f, 1f, 0.45f)));

        // trayectoria
        Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
        Handles.DrawDottedLine(origen, destino, 3f);

        // destino: handle arrastrable — esto es lo que reemplaza al vector invisible
        EditorGUI.BeginChangeCheck();
        Vector3 nuevoDestino = Handles.PositionHandle(destino, spawn.rotation);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(target, "Destino de la llave");
            keyFinalPosition.vector3Value = nuevoDestino - origen;
            serializedObject.ApplyModifiedProperties();
        }
        Handles.color = new Color(1f, 0.85f, 0.2f);
        Handles.SphereHandleCap(0, destino, Quaternion.identity, RadioPunto, EventType.Repaint);
        Handles.Label(destino + Vector3.up * AltoLabel,
            "entra acá  (" + Vector3.Distance(origen, destino).ToString("F3") + " m)",
            Etiqueta(new Color(1f, 0.85f, 0.2f)));

        // la llave, dibujada en los dos extremos TAL CUAL va a salir en el juego
        DibujarLlave(origen,  spawn.rotation, new Color(0.35f, 1f, 0.45f, 0.9f));
        DibujarLlave(destino, spawn.rotation, new Color(1f, 0.85f, 0.2f, 0.9f));

        // arco de giro sobre la boca de la cerradura
        if (boca != null)
        {
            Vector3 eje = EjeMundo(boca);
            float angulo = rotateKeyValue.floatValue;
            Handles.color = new Color(0.4f, 0.7f, 1f, 0.25f);
            Handles.DrawSolidArc(boca.position, eje, VectorDeReferencia(eje), angulo, RadioArco);
            Handles.color = new Color(0.4f, 0.7f, 1f);
            Handles.DrawLine(boca.position - eje * LargoEje, boca.position + eje * LargoEje);
            Handles.Label(boca.position - Vector3.up * AltoLabel,
                "gira " + angulo.ToString("F0") + "°", Etiqueta(new Color(0.4f, 0.7f, 1f)));
        }

        if (cam != null)
        {
            Handles.color = new Color(1f, 1f, 1f, 0.5f);
            Handles.DrawDottedLine(cam.position, destino, 2f);
            Handles.Label(cam.position, "cámara", Etiqueta(Color.white));
        }
    }

    /// <summary>
    /// Chequeos sobre el prefab de la llave: son los valores que "trolean" al setear una cerradura,
    /// porque no se ven en el Inspector de la puerta sino en otro asset.
    /// </summary>
    private void AvisarSobreElPrefab(KeyData data)
    {
        GameObject prefab = data.GetPrefab();
        if (prefab == null)
        {
            EditorGUILayout.HelpBox("El KeyData '" + data.name + "' no tiene prefab: la puerta se abre sin animación.", MessageType.Warning);
            return;
        }

        string ruta = AssetDatabase.GetAssetPath(prefab);
        if (ruta.EndsWith(".fbx") || ruta.EndsWith(".obj"))
        {
            EditorGUILayout.HelpBox(
                "El KeyData apunta al MODELO CRUDO (" + System.IO.Path.GetFileName(ruta) + "), no a un prefab. " +
                "Va a salir con la escala y el material del import, que casi nunca son los del juego.",
                MessageType.Warning);
        }

        var sobran = new System.Collections.Generic.List<string>();
        foreach (var c in prefab.GetComponentsInChildren<Component>(true))
        {
            if (c is Transform || c is MeshFilter || c is MeshRenderer) continue;
            if (!sobran.Contains(c.GetType().Name)) sobran.Add(c.GetType().Name);
        }
        if (sobran.Count > 0)
        {
            EditorGUILayout.HelpBox(
                "El prefab trae componentes de gameplay (" + string.Join(", ", sobran) + "). " +
                "DoorWithKey los deshabilita al instanciar la llave, así que no rompen nada — pero este " +
                "prefab es sólo el visual de la animación.", MessageType.Info);
        }

        EditorGUILayout.LabelField("Escala del prefab", prefab.transform.localScale.ToString("F5"), EditorStyles.miniLabel);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private Vector3 EjeMundo(Transform boca)
    {
        switch (axisToRotate.enumValueIndex)
        {
            case 0:  return boca.right;
            case 1:  return boca.up;
            default: return boca.forward;
        }
    }

    private static Vector3 VectorDeReferencia(Vector3 eje)
    {
        Vector3 v = Vector3.Cross(eje, Vector3.up);
        if (v.sqrMagnitude < 0.001f) v = Vector3.Cross(eje, Vector3.right);
        return v.normalized;
    }

    /// <summary>
    /// Dibuja la llave como REALMENTE va a salir: respetando la escala del prefab y la transformación
    /// de cada malla respecto de su raíz.
    ///
    /// Importa porque <c>DoorWithKey</c> hace <c>Instantiate(prefab, spawn.position, spawn.rotation)</c>:
    /// la posición y la rotación las manda el spawn, pero el TAMAÑO y la orientación interna de la malla
    /// salen del prefab. Dibujar la malla suelta (sin la escala de la raíz) mentía — un prefab a escala
    /// 0.01 y otro a 0.0004 se veían igual en el gizmo y distinto en el juego.
    ///
    /// Se dibuja SÓLO con Handles a propósito: la primera versión usaba Graphics.DrawMeshNow, que sin un
    /// material con SetPass activo pinta con el último material del pipeline y tapaba la Scene view.
    /// </summary>
    private void DibujarLlave(Vector3 pos, Quaternion rot, Color color)
    {
        if (Event.current.type != EventType.Repaint) return;

        var data = keyNeeded.objectReferenceValue as KeyData;
        GameObject prefab = data != null ? data.GetPrefab() : null;
        if (prefab == null) return;

        // dónde va a quedar la raíz de la llave instanciada (la escala es la del prefab)
        Matrix4x4 raiz = Matrix4x4.TRS(pos, rot, prefab.transform.localScale);
        Matrix4x4 anterior = Handles.matrix;
        Handles.color = color;

        foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;

            // transformación de esta malla RELATIVA a la raíz del prefab
            Matrix4x4 relativa = prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            Handles.matrix = raiz * relativa;

            Bounds b = mf.sharedMesh.bounds;
            Handles.DrawWireCube(b.center, b.size);

            // el lado largo de la llave = por dónde entra
            Vector3 ejeLargo = b.size.x >= b.size.y && b.size.x >= b.size.z ? Vector3.right
                             : b.size.y >= b.size.z ? Vector3.up : Vector3.forward;
            float largo = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 0.5f;
            Handles.DrawLine(b.center - ejeLargo * largo, b.center + ejeLargo * largo);
        }

        Handles.matrix = anterior;
    }

    // ── previsualización sin Play ────────────────────────────────────────────
    private void IniciarPreview(DoorWithKey puerta)
    {
        CancelarPreview();

        Transform spawn = keySpawnPoint.objectReferenceValue as Transform;
        if (spawn == null) return;

        var data = keyNeeded.objectReferenceValue as KeyData;
        GameObject prefab = data != null ? data.GetPrefab() : null;

        llaveFantasma = prefab != null
            ? Instantiate(prefab, spawn.position, spawn.rotation)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);
        if (prefab == null)
        {
            llaveFantasma.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
            llaveFantasma.transform.localScale = Vector3.one * 0.04f;
        }
        llaveFantasma.name = "[preview] llave";
        // HideAndDontSave: no se guarda con la escena ni ensucia el archivo
        llaveFantasma.hideFlags = HideFlags.HideAndDontSave;

        Transform boca = bocaCerradura.objectReferenceValue as Transform;
        rotacionBocaOriginal = boca != null ? boca.rotation : Quaternion.identity;
        // La llave conserva SIEMPRE la rotación con la que salió del spawn; el giro de la cerradura se
        // le suma como delta (en el juego eso pasa solo, porque se hace hija de bocaCerradura).
        rotacionLlaveAlSpawnear = spawn.rotation;

        puertaEnPreview = puerta;
        tiempoInicio = EditorApplication.timeSinceStartup;
        EditorApplication.update += AvanzarPreview;
    }

    private static void CancelarPreview()
    {
        EditorApplication.update -= AvanzarPreview;

        if (llaveFantasma != null) DestroyImmediate(llaveFantasma);
        llaveFantasma = null;

        // Restaurar la boca: la preview la rota de verdad, y dejarla girada modificaría la escena.
        if (puertaEnPreview != null)
        {
            var so = new SerializedObject(puertaEnPreview);
            var boca = so.FindProperty("bocaCerradura").objectReferenceValue as Transform;
            if (boca != null) boca.rotation = rotacionBocaOriginal;
        }
        puertaEnPreview = null;
    }

    private static void AvanzarPreview()
    {
        if (llaveFantasma == null || puertaEnPreview == null) { CancelarPreview(); return; }

        var so = new SerializedObject(puertaEnPreview);
        Transform spawn = so.FindProperty("keySpawnPoint").objectReferenceValue as Transform;
        Transform boca  = so.FindProperty("bocaCerradura").objectReferenceValue as Transform;
        if (spawn == null) { CancelarPreview(); return; }

        float dInsert = Mathf.Max(0.01f, so.FindProperty("insertKeyDuration").floatValue);
        float dRotate = Mathf.Max(0.01f, so.FindProperty("rotateKeyDuration").floatValue);
        Vector3 origen  = spawn.position;
        Vector3 destino = origen + so.FindProperty("keyFinalPosition").vector3Value;

        float t = (float)(EditorApplication.timeSinceStartup - tiempoInicio);

        if (t <= dInsert)
        {
            // viaje: sólo se traslada. La rotación es la del spawn y no cambia (el juego hace DOMove).
            llaveFantasma.transform.position = Vector3.Lerp(origen, destino, t / dInsert);
            llaveFantasma.transform.rotation = rotacionLlaveAlSpawnear;
            if (boca != null) boca.rotation = rotacionBocaOriginal;
        }
        else if (t <= dInsert + dRotate)
        {
            float k = (t - dInsert) / dRotate;
            k = 1f - Mathf.Pow(1f - k, 3f);                     // Ease.OutCubic, igual que el juego
            float ang = so.FindProperty("rotateKeyValue").floatValue * k;
            Vector3 ejeLocal;
            switch (so.FindProperty("axisToRotate").enumValueIndex)
            {
                case 0:  ejeLocal = Vector3.right; break;
                case 1:  ejeLocal = Vector3.up; break;
                default: ejeLocal = Vector3.forward; break;
            }

            if (boca != null)
            {
                boca.rotation = rotacionBocaOriginal * Quaternion.AngleAxis(ang, ejeLocal);

                // La llave es HIJA de la cerradura: hereda el DELTA de rotación, no su rotación absoluta.
                // Asignarle boca.rotation la hacía saltar a una orientación cualquiera apenas empezaba el giro.
                Quaternion delta = boca.rotation * Quaternion.Inverse(rotacionBocaOriginal);
                llaveFantasma.transform.rotation = delta * rotacionLlaveAlSpawnear;
                // y orbita alrededor del pivote de la cerradura, igual que un hijo real
                llaveFantasma.transform.position = boca.position + delta * (destino - boca.position);
            }
            else
            {
                // Sin bocaCerradura el juego rota LA LLAVE MISMA sobre su propio eje local
                // (RotateKey: rotator = bocaCerradura ?? key.transform). Gira en el lugar, sin orbitar,
                // y el eje depende de cómo esté modelada la llave — no de la puerta.
                llaveFantasma.transform.position = destino;
                llaveFantasma.transform.rotation = rotacionLlaveAlSpawnear * Quaternion.AngleAxis(ang, ejeLocal);
            }
        }
        else
        {
            CancelarPreview();
        }

        SceneView.RepaintAll();
    }
}
