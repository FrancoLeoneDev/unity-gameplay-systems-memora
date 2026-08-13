using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// "Ir a" — arranca el juego DIRECTO en un punto de la demo, para no jugar 33 minutos por iteración.
///
/// Menú: un submenú por recuerdo, y adentro los momentos de ese recuerdo.
///     Memora ▸ Ir a ▸ Memoria 1 — Cumpleaños  ▸ Principio / Recién completado
///                   ▸ Memoria 2 — Sala de Arte ▸ Principio / Recién completado
///                   ▸ Memoria 3 — Hospital     ▸ Principio / El loop (clímax)
///
/// La SECUENCIA es genérica; lo único que cambia de un destino a otro son cinco datos, y por eso viven
/// juntos en un <see cref="Destino"/>. Agregar un momento = declarar un Destino + un
/// <see cref="MenuItem"/> de dos líneas. Lo que NO hay (y no debería haber hasta que haga falta) es un
/// registro de saltos en ScriptableObject ni marcadores de escena.
///
/// El ORDEN es lo único delicado:
///   1. abrir MainHouseTesis (el jugador es DDOL y vive en la casa: un recuerdo no corre solo),
///   2. prender el <c>skipInEditor</c> que YA tiene IntroPlayer (no inventamos un segundo skip),
///   3. entrar a Play y esperar a que existan jugador + managers + AudioLibrary,
///   4. dar el loadout,
///   5. cargar el recuerdo por el MISMO camino que usa el juego
///      (<see cref="SceneControllerManager.LoadPhotoScene"/>) — una carga aditiva a mano dejaría
///      <c>actualPhotoData</c>/<c>IsInMemoryScene</c> en cualquier cosa y después fallan el guardado
///      dentro del recuerdo y la vuelta a la casa,
///   6. recién ahí, lo propio del momento (abrir la puerta blanca, ubicarte en el loop…).
///
/// Vive en Assets/Editor: NO existe en el build.
/// </summary>
[InitializeOnLoad]
internal static class MemoraIrA
{
    private const string Prefijo = "[Memora/Ir a] ";
    private const string EscenaCasa = "Assets/Scenes/MainHouseTesis.unity";
    private const float TimeoutSegundos = 60f;

    // Sobreviven al domain reload que dispara entrar a Play (SessionState se limpia al cerrar el Editor).
    private const string ClaveDestinoPendiente = "Memora.IrA.DestinoPendiente";
    private const string ClaveSkipIntroPrevio = "Memora.IrA.SkipIntroPrevio";
    private const string CampoSkipIntro = "skipInEditor";

    /// <summary>Un asset del loadout: GUID (estable ante renombres/movidas) + nombre para el log.</summary>
    private readonly struct Entrada
    {
        public string Guid { get; }
        public string Nombre { get; }
        public Entrada(string guid, string nombre) { Guid = guid; Nombre = nombre; }
    }

    /// <summary>Un momento del juego: qué tenés encima, en qué recuerdo caés y qué pasa al llegar.</summary>
    private sealed class Destino
    {
        public string Nombre { get; set; }
        public Entrada[] Fotos { get; set; } = Array.Empty<Entrada>();
        /// <summary>Memorias YA vividas: cambia el ícono/prefab de esas fotos al estado resuelto.</summary>
        public Entrada[] FotosCompletadas { get; set; } = Array.Empty<Entrada>();
        public Entrada[] Objetos { get; set; } = Array.Empty<Entrada>();
        public Entrada[] Notas { get; set; } = Array.Empty<Entrada>();
        public bool ConLinterna { get; set; }
        /// <summary>GUID de la PhotoData del recuerdo a cargar. Vacío = te quedás en la casa.</summary>
        public string GuidFotoDelRecuerdo { get; set; }
        /// <summary>
        /// Qué pasa una vez montado todo (abrir la puerta blanca, ubicarte en el loop…). Recibe la escena
        /// del recuerdo y devuelve si LOGRÓ hacerlo. Null = te quedás en el SpawnPoint del recuerdo, que
        /// para un "principio" ES el arranque correcto: por eso los destinos de principio no necesitan
        /// una sola línea de ubicación.
        ///
        /// Devuelve bool y no void a propósito: antes esto era Action y el resumen final decía "listo"
        /// aunque la ubicación hubiera fallado, así que el salto parecía andar cuando en realidad te
        /// dejaba en el spawn. Un reporte que miente es peor que no tener reporte.
        /// </summary>
        public Func<Scene, bool> AlLlegar { get; set; }
    }

    /// <summary>
    /// Nombre de la escena del recuerdo, LEÍDO de la PhotoData. No se escribe a mano en ningún lado:
    /// duplicarlo es cómo se rompe esto en silencio (BirthdayPhoto carga "RecuerdoCumpleaños", con ñ).
    /// </summary>
    private static string EscenaDe(Destino destino)
    {
        if (string.IsNullOrEmpty(destino.GuidFotoDelRecuerdo)) return null;
        PhotoData foto = Cargar<PhotoData>(new Entrada(destino.GuidFotoDelRecuerdo, "foto de " + destino.Nombre));
        return foto != null ? foto.SceneToLoad : null;
    }

    private static Scene EscenaCargadaDe(Destino destino)
    {
        string nombre = EscenaDe(destino);
        return string.IsNullOrEmpty(nombre) ? default : SceneManager.GetSceneByName(nombre);
    }

    // ══ Assets del loadout ════════════════════════════════════════════════════
    // Criterio (decidido con Franco): en cada momento, SOLO lo que a esa altura todavía CONSERVÁS.
    // Las llaves y el papel tienen limitedUses=1 y el inventario los borra al usarlos
    // (Inventory.UseKeyItem), así que aparecen únicamente en los momentos en que todavía no los gastaste.

    private const string GuidFotoCumple = "a9a256066172da94d85e983fe3df72c1";
    private const string GuidFotoArtRoom = "38a2856fccc29a447beb44fc060d41a1";
    private const string GuidFotoHospital = "7bc5604927500ca4094f6374abfb2f08";

    private static readonly Entrada FotoCumple = new Entrada(GuidFotoCumple, "BirthdayPhoto");
    private static readonly Entrada FotoArtRoom = new Entrada(GuidFotoArtRoom, "ArtRoomPhoto");
    private static readonly Entrada FotoHospital = new Entrada(GuidFotoHospital, "HospitalPhoto");

    private static readonly Entrada Rollo = new Entrada("1cdeadfb48ece0b4eaa59c92f17e2c61", "PhotoRoll");
    private static readonly Entrada Linterna = new Entrada("57cd894444634fd48992f0de0d82924b", "Flashlight");
    private static readonly Entrada LlaveAlaIzquierda = new Entrada("378248742b86a3e4c80c4f749c5a2b3c", "LeftWingKey");

    private static readonly Entrada NotaAbuelo = new Entrada("fd68b1c60cd4f5a4fa92313fceef2df6", "RegaloAbuelo");
    private static readonly Entrada NotaCumplePadre = new Entrada("41b852d9aadc0144b809e9a3fd0e1c7e", "Cumple padre");
    private static readonly Entrada NotaPoema = new Entrada("ce76d7f22e980124bb2eb131bf9317f4", "PoemaArtRoom");
    private static readonly Entrada NotaContrasena = new Entrada("b8842a89ced50e745822f9b827ee7f91", "ContraseñaPC");
    private static readonly Entrada ActaNacimiento = new Entrada("f8d6d3ccf51ad5844a91c5d193957f92", "ActaDeNacimiento");
    private static readonly Entrada ActaDefuncion = new Entrada("f8cc9d71aeec69b47a8be09b54e34630", "ActaDefuncion");

    // ══ MEMORIA 1 — Cumpleaños ════════════════════════════════════════════════

    /// <summary>F1-03: recién agarraste el rollo y te dieron la foto. Es lo ÚNICO que tenés encima.</summary>
    private static readonly Destino M1Principio = new Destino
    {
        Nombre = "M1 — Cumpleaños · principio",
        Fotos = new[] { FotoCumple },
        Objetos = new[] { Rollo },
        GuidFotoDelRecuerdo = GuidFotoCumple,
    };

    /// <summary>
    /// F1-04 resuelto: la caja musical ya te dio la llave del ala izquierda (todavía sin usar) y la
    /// puerta blanca se está abriendo. La nota del abuelo estaba dentro de la caja, así que ya la tenés.
    /// </summary>
    private static readonly Destino M1Completado = new Destino
    {
        Nombre = "M1 — Cumpleaños · recién completado",
        Fotos = new[] { FotoCumple },
        Objetos = new[] { Rollo, LlaveAlaIzquierda },
        Notas = new[] { NotaAbuelo },
        GuidFotoDelRecuerdo = GuidFotoCumple,
        AlLlegar = CompletarCumpleanos,
    };

    [MenuItem("Memora/Ir a/Memoria 1 — Cumpleaños/Principio", false, 0)]
    private static void IrM1Principio() => Ir(M1Principio);

    [MenuItem("Memora/Ir a/Memoria 1 — Cumpleaños/Recién completado (puerta blanca)", false, 1)]
    private static void IrM1Completado() => Ir(M1Completado);

    /// <summary>Lo que hace <c>MusicBoxKey.Success()</c> al agarrar la llave de la caja musical.</summary>
    private static bool CompletarCumpleanos(Scene recuerdo)
    {
        if (RecuerdoCumpleañosManager.instance == null)
        {
            Debug.LogError(Prefijo + "No hay RecuerdoCumpleañosManager en la escena: no puedo abrir la puerta blanca.");
            return false;
        }

        // instantaneo: este atajo MONTA el estado "ya completado", no actúa el beat. Animarla te dejaba
        // mirando cómo se abre sola con su chirrido justo después del teletransporte.
        RecuerdoCumpleañosManager.instance.AbrirPuertaBlanca(instantaneo: true);
        return UbicarFrenteALaPuertaBlanca(recuerdo);
    }

    // ══ MEMORIA 2 — Sala de Arte ══════════════════════════════════════════════

    /// <summary>
    /// F3-03 al entrar. SIN linterna a propósito: la linterna se consigue ADENTRO de este recuerdo, y
    /// dártela acá sería romper el puzzle que venís a probar. La llave del ala izquierda tampoco está:
    /// la gastaste abriendo el ala.
    /// </summary>
    private static readonly Destino M2Principio = new Destino
    {
        Nombre = "M2 — Sala de Arte · principio",
        Fotos = new[] { FotoCumple, FotoArtRoom },
        FotosCompletadas = new[] { FotoCumple },
        Objetos = new[] { Rollo },
        Notas = new[] { NotaAbuelo, NotaCumplePadre },
        GuidFotoDelRecuerdo = GuidFotoArtRoom,
    };

    /// <summary>F3-03 resuelto: bulbos puestos, linterna en mano, "Sofia" escrito y la puerta abriéndose.</summary>
    private static readonly Destino M2Completado = new Destino
    {
        Nombre = "M2 — Sala de Arte · recién completado",
        Fotos = new[] { FotoCumple, FotoArtRoom },
        FotosCompletadas = new[] { FotoCumple },
        Objetos = new[] { Rollo, Linterna },
        Notas = new[] { NotaAbuelo, NotaCumplePadre, NotaPoema },
        ConLinterna = true,
        GuidFotoDelRecuerdo = GuidFotoArtRoom,
        AlLlegar = CompletarSalaDeArte,
    };

    [MenuItem("Memora/Ir a/Memoria 2 — Sala de Arte/Principio", false, 0)]
    private static void IrM2Principio() => Ir(M2Principio);

    [MenuItem("Memora/Ir a/Memoria 2 — Sala de Arte/Recién completado (puerta blanca)", false, 1)]
    private static void IrM2Completado() => Ir(M2Completado);

    /// <summary>
    /// Los tres avisos que el ArtRoomManager espera, EN ESTE ORDEN: <c>FlashlightPickuped()</c>
    /// early-returnea si el puzzle no está marcado, y la puerta solo abre con sofiaRevealed &amp;&amp;
    /// flashlightPickuped. Cambiar el orden hace que no pase nada, en silencio.
    /// </summary>
    private static bool CompletarSalaDeArte(Scene recuerdo)
    {
        ArtRoomManager manager = ArtRoomManager.instance;
        if (manager == null)
        {
            Debug.LogError(Prefijo + "No hay ArtRoomManager en la escena: no puedo completar el puzzle.");
            return false;
        }

        manager.PuzzleCompleted();
        manager.FlashlightPickuped();
        manager.SofiaRevealed();
        return UbicarFrenteALaPuertaBlanca(recuerdo);
    }

    // ══ MEMORIA 3 — Hospital ══════════════════════════════════════════════════

    /// <summary>F6-01: entrás al hospital. Las actas todavía no: se encuentran adentro.</summary>
    private static readonly Destino M3Principio = new Destino
    {
        Nombre = "M3 — Hospital · principio",
        Fotos = new[] { FotoCumple, FotoArtRoom, FotoHospital },
        FotosCompletadas = new[] { FotoCumple, FotoArtRoom },
        Objetos = new[] { Rollo, Linterna },
        Notas = new[] { NotaAbuelo, NotaCumplePadre, NotaPoema, NotaContrasena },
        ConLinterna = true,
        GuidFotoDelRecuerdo = GuidFotoHospital,
    };

    /// <summary>F6-03: ya pasaste las actas y el llanto; quedás en el preloop, con la puerta blanca entreabierta.</summary>
    private static readonly Destino M3Loop = new Destino
    {
        Nombre = "M3 — Hospital · el preloop",
        Fotos = new[] { FotoCumple, FotoArtRoom, FotoHospital },
        FotosCompletadas = new[] { FotoCumple, FotoArtRoom },
        Objetos = new[] { Rollo, Linterna },
        Notas = new[] { NotaAbuelo, NotaCumplePadre, NotaPoema, NotaContrasena, ActaNacimiento, ActaDefuncion },
        ConLinterna = true,
        GuidFotoDelRecuerdo = GuidFotoHospital,
        AlLlegar = UbicarEnElPreloopDelHospital,
    };

    [MenuItem("Memora/Ir a/Memoria 3 — Hospital/Principio", false, 0)]
    private static void IrM3Principio() => Ir(M3Principio);

    [MenuItem("Memora/Ir a/Memoria 3 — Hospital/El loop (clímax)", false, 1)]
    private static void IrM3Loop() => Ir(M3Loop);

    // ══ CASA ══════════════════════════════════════════════════════════════════
    // Estos NO cargan recuerdo: te dejan en MainHouseTesis. Se anclan a objetos que YA existen en la
    // escena (los triggers de cada beat), no a coordenadas: si Franco los mueve, la tool los sigue.

    private const string NombreTriggerDoctor = "TriggerArmDoctor";
    private const string NombreTriggerCorteLuz = "TriggerCorteLuz";
    /// <summary>Cuánto se retrocede desde el trigger del Doctor para quedar ANTES de armarlo.</summary>
    private const float RetrocesoAntesDelDoctor = 2f;

    /// <summary>
    /// F3-01, justo antes de entrar: ya exploraste el ala izquierda (la llave del ala está gastada) y
    /// todavía NO hiciste M2, así que no tenés linterna.
    /// </summary>
    private static readonly Destino CasaPasilloDelDoctor = new Destino
    {
        Nombre = "Casa — pasillo del Doctor (antes de entrar)",
        Fotos = new[] { FotoCumple },
        FotosCompletadas = new[] { FotoCumple },
        Objetos = new[] { Rollo },
        Notas = new[] { NotaAbuelo, NotaCumplePadre },
        AlLlegar = UbicarAntesDelDoctor,
    };

    /// <summary>F4-02: salís del cuarto de los padres y se corta la luz. La linterna (de M2) ahora es vital.</summary>
    private static readonly Destino CasaCorteDeLuz = new Destino
    {
        Nombre = "Casa — corte de luz (recién cortada)",
        Fotos = new[] { FotoCumple, FotoArtRoom },
        FotosCompletadas = new[] { FotoCumple, FotoArtRoom },
        Objetos = new[] { Rollo, Linterna },
        Notas = new[] { NotaAbuelo, NotaCumplePadre, NotaPoema, NotaContrasena },
        ConLinterna = true,
        AlLlegar = DispararCorteDeLuz,
    };

    [MenuItem("Memora/Ir a/Casa/Pasillo del Doctor (antes de entrar)", false, 6)]
    private static void IrCasaDoctor() => Ir(CasaPasilloDelDoctor);

    [MenuItem("Memora/Ir a/Casa/Corte de luz (recién cortada)", false, 7)]
    private static void IrCasaCorteDeLuz() => Ir(CasaCorteDeLuz);

    /// <summary>
    /// Te para en el trigger del Doctor pero un poco ANTES, sin armarlo, mirando pasillo adentro: la
    /// idea es que entres caminando y el beat pase como está diseñado.
    ///
    /// La dirección sale de la geometría real: el Doctor está al fondo del recto, así que
    /// (trigger − doctor) ES el sentido por el que llega el jugador. Nada que adivinar.
    /// </summary>
    private static bool UbicarAntesDelDoctor(Scene _)
    {
        Scene casa = EscenaDeLaCasa();
        Transform trigger = BuscarPorNombre(casa, NombreTriggerDoctor);
        if (trigger == null)
        {
            Debug.LogError(Prefijo + $"No encontré '{NombreTriggerDoctor}' en la casa: te dejo donde arrancás.");
            return false;
        }

        // DoctorSubtleSway vive EN el modelo del Doctor: sirve de mira sin tocar campos privados.
        var doctor = BuscarEnEscena<DoctorSubtleSway>(casa);
        if (doctor == null)
        {
            Debug.LogWarning(Prefijo + "No encontré el modelo del Doctor (DoctorSubtleSway): te dejo sobre el " +
                                       "trigger sin poder orientarte hacia el pasillo.");
            ColocarEn(trigger.position, trigger.eulerAngles.y, trigger.position.y);
            return true;
        }

        Vector3 desdeDondeVenis = trigger.position - doctor.transform.position;
        desdeDondeVenis.y = 0f;
        if (desdeDondeVenis.sqrMagnitude < 0.01f)
        {
            Debug.LogWarning(Prefijo + "El Doctor está encima del trigger: no puedo deducir el sentido del pasillo.");
            ColocarEn(trigger.position, trigger.eulerAngles.y, trigger.position.y);
            return true;
        }

        Vector3 direccion = desdeDondeVenis.normalized;
        Vector3 destino = trigger.position + direccion * RetrocesoAntesDelDoctor;
        float yaw = Quaternion.LookRotation(-direccion, Vector3.up).eulerAngles.y; // mirando al fondo del recto
        ColocarEn(destino, yaw, trigger.position.y);
        Debug.Log(Prefijo + $"En el pasillo del Doctor, {RetrocesoAntesDelDoctor:0.#} m antes del trigger, " +
                            "mirando al fondo. Caminá y el beat arranca solo.");
        return true;
    }

    /// <summary>F4-02: te para donde el corte ocurre y lo dispara, con el mismo método que usa el trigger.</summary>
    private static bool DispararCorteDeLuz(Scene _)
    {
        Scene casa = EscenaDeLaCasa();
        Transform trigger = BuscarPorNombre(casa, NombreTriggerCorteLuz);
        if (trigger != null)
            ColocarEn(trigger.position, trigger.eulerAngles.y, trigger.position.y);
        else
            Debug.LogWarning(Prefijo + $"No encontré '{NombreTriggerCorteLuz}': disparo el corte donde estés parado.");

        var secuencia = BuscarEnEscena<PowerCutSequence>(casa);
        if (secuencia == null)
        {
            Debug.LogError(Prefijo + "No hay PowerCutSequence en la casa: no puedo cortar la luz.");
            return false;
        }

        secuencia.TriggerPowerCut();
        Debug.Log(Prefijo + "Corte de luz disparado.");
        return trigger != null;
    }

    private static readonly Destino[] Todos =
    {
        M1Principio, M1Completado,
        M2Principio, M2Completado,
        M3Principio, M3Loop,
        CasaPasilloDelDoctor, CasaCorteDeLuz,
    };

    // ══ Ubicaciones ═══════════════════════════════════════════════════════════

    /// <summary>Metros antes del umbral del loop, para que camines vos y se dispare todo como jugando.</summary>
    private const float MargenAntesDelUmbral = 4f;
    /// <summary>A qué distancia de la puerta blanca te deja, para verla abrirse entera.</summary>
    private const float DistanciaALaPuertaBlanca = 3.5f;
    /// <summary>Desde dónde se tira el rayo para buscar el piso.</summary>
    private const float AlturaBusquedaPiso = 6f;
    /// <summary>Red de seguridad si no se puede medir cuánto mide el jugador: mejor un poco alto (cae) que enterrado.</summary>
    private const float AlturaJugadorPorDefecto = 1.2f;

    /// <summary>
    /// Te para frente a la puerta blanca, mirándola. De qué LADO pararse no se adivina ni se deduce de
    /// los ejes locales de la puerta (que varían por escena): se usa el SpawnPoint del recuerdo como
    /// referencia, porque ese punto está por definición del lado de adentro.
    /// </summary>
    private static bool UbicarFrenteALaPuertaBlanca(Scene recuerdo)
    {
        var puerta = BuscarEnEscena<PuertaSalaBlanca>(recuerdo);
        if (puerta == null)
        {
            Debug.LogWarning(Prefijo + "No encontré la PuertaSalaBlanca: la abrí (si pude) pero te dejo en el spawn.");
            return false;
        }

        var spawn = BuscarEnEscena<SpawnPoint>(recuerdo);
        if (spawn == null)
        {
            Debug.LogWarning(Prefijo + "No encontré el SpawnPoint: sin él no sé de qué lado de la puerta pararte. " +
                                       "Te dejo donde estás.");
            return false;
        }

        Vector3 haciaAdentro = spawn.transform.position - puerta.transform.position;
        haciaAdentro.y = 0f;
        if (haciaAdentro.sqrMagnitude < 0.01f)
        {
            Debug.LogWarning(Prefijo + "El SpawnPoint está encima de la puerta: no puedo deducir el lado. Te dejo donde estás.");
            return false;
        }

        Vector3 direccion = haciaAdentro.normalized;
        Vector3 destino = puerta.transform.position + direccion * DistanciaALaPuertaBlanca;
        destino.y = AlturaDeParado(destino, GeometriaDe(puerta.transform).min.y);

        // Mirando HACIA la puerta = al revés de "hacia adentro".
        float yaw = Quaternion.LookRotation(-direccion, Vector3.up).eulerAngles.y;
        TeletransportarJugador(destino, yaw);
        Debug.Log(Prefijo + $"Frente a la puerta blanca, a {DistanciaALaPuertaBlanca:0.#} m, mirándola.");
        return true;
    }

    /// <summary>
    /// Deja al jugador entrando a la sala 1 del loop, mirando hacia adelante. Nada de coordenadas
    /// mágicas: la dirección y el largo de sala se DERIVAN de LoopPart1 y LoopPart2, y la altura sale
    /// de buscar el PISO con un rayo. Si Franco mueve las salas, esto las sigue.
    ///
    /// El disparador del clímax, si está, se usa solo para afinar el carril lateral: es un marcador que
    /// ya está puesto donde se camina. Si NO está (clímax todavía sin cablear), igual te deja en el loop.
    /// </summary>
    private static bool UbicarEnElPreloopDelHospital(Scene recuerdo)
    {
        // El TriggerAbrirLoop marca el umbral del loop y ADEMÁS conoce las salas: nada de buscar
        // "LoopPart1" por nombre. Si Franco renombra las salas, esto lo sigue.
        var abrirLoop = UnityEngine.Object.FindFirstObjectByType<TriggerAbrirLoop>(FindObjectsInactive.Include);
        if (abrirLoop == null)
        {
            Debug.LogError(Prefijo + "No hay TriggerAbrirLoop en la escena: no sé dónde empieza el loop. " +
                                     "Quedaste en el spawn normal del recuerdo.");
            return false;
        }
        if (!TryDireccionDelLoop(out Vector3 direccion)) return false;

        // Te deja del lado de ACÁ del umbral, mirando hacia el loop: al caminar cruzás el trigger y
        // todo lo demás (apagar el hospital, encender salas) pasa solo, como jugando.
        Vector3 destino = abrirLoop.transform.position - direccion * MargenAntesDelUmbral;
        destino.y = AlturaDeParado(destino, abrirLoop.transform.position.y);

        float yaw = Quaternion.LookRotation(direccion, Vector3.up).eulerAngles.y;
        TeletransportarJugador(destino, yaw);

        // La puerta ya entreabierta: en este punto de la historia el acta está leída.
        abrirLoop.AbrirInstantaneo();

        Debug.Log(Prefijo + $"En el preloop, a {MargenAntesDelUmbral:0.#} m del umbral, mirando al loop. " +
                            "La puerta blanca quedó entreabierta.");
        return true;
    }

    /// <summary>
    /// Caja que ocupa REALMENTE un objeto en el mundo: la unión de todos sus renderers, incluidos los
    /// desactivados.
    ///
    /// Existe porque el pivote de las salas del loop NO está en su centro — está ~19 m adelante, tanto
    /// que el pivote de LoopPart1 cae dentro de la geometría de LoopPart2. Cualquier cuenta hecha sobre
    /// el pivote (como "pivote menos media sala") te deja en la sala equivocada. La geometría no miente.
    /// </summary>
    private static Bounds GeometriaDe(Transform objeto)
    {
        Bounds caja = default;
        bool primero = true;

        foreach (Renderer renderer in objeto.GetComponentsInChildren<Renderer>(true))
        {
            // Los ParticleSystemRenderer que todavía no simularon devuelven una caja VACÍA EN EL ORIGEN
            // del mundo. Encapsularla estira la caja de la sala desde su geometría real hasta (0,0,0) y
            // arruina cualquier cuenta de bordes. (Las 4 DustEffect de LoopPart1 hacían exactamente eso.)
            if (renderer.bounds.size.sqrMagnitude < 0.0001f) continue;

            if (primero) { caja = renderer.bounds; primero = false; }
            else caja.Encapsulate(renderer.bounds);
        }

        if (primero)
        {
            Debug.LogWarning(Prefijo + $"'{objeto.name}' no tiene geometría medible: caigo al pivote, " +
                                       "que puede estar corrido respecto del objeto.");
            return new Bounds(objeto.position, Vector3.zero);
        }

        return caja;
    }

    /// <summary>Media-anchura de una caja proyectada sobre una dirección (extensión de un AABB).</summary>
    private static float ExtensionEnDireccion(Bounds caja, Vector3 direccion)
    {
        return Mathf.Abs(caja.extents.x * direccion.x)
             + Mathf.Abs(caja.extents.y * direccion.y)
             + Mathf.Abs(caja.extents.z * direccion.z);
    }

    /// <summary>
    /// Y a la que hay que dejar al jugador para que quede PARADO en ese punto: piso buscado con un rayo
    /// más su altura real, medida ahí mismo comparando su posición actual contra el piso que tiene debajo
    /// (el juego acaba de pararlo bien en el spawn del recuerdo, así que ese número es el correcto).
    /// </summary>
    private static float AlturaDeParado(Vector3 destino, float alturaDeReferencia)
    {
        float alto = AlturaJugadorPorDefecto;
        PlayerManager pm = PlayerManager.instance;
        if (pm != null && Physics.Raycast(pm.transform.position, Vector3.down, out RaycastHit bajoJugador,
                                          AlturaBusquedaPiso, ~0, QueryTriggerInteraction.Ignore))
        {
            float medido = pm.transform.position.y - bajoJugador.point.y;
            // Un valor casi cero significa que el pivote está pegado al piso y la medición no sirve:
            // aplicarla lo dejaría medio enterrado y trabado. En ese caso mandamos el default.
            if (medido > 0.1f) alto = medido;
        }

        Vector3 desde = new Vector3(destino.x, alturaDeReferencia + AlturaBusquedaPiso, destino.z);
        if (Physics.Raycast(desde, Vector3.down, out RaycastHit piso, AlturaBusquedaPiso * 2f, ~0,
                            QueryTriggerInteraction.Ignore))
            return piso.point.y + alto;

        Debug.LogWarning(Prefijo + $"No encontré piso en {destino.ToString("F1")}; uso la altura de referencia.");
        return alturaDeReferencia + alto;
    }

    /// <summary>
    /// Hacia dónde avanza el loop del hospital, derivado de las dos primeras salas.
    /// Lo usa también el reinicio del clímax para saber hacia dónde mirás.
    ///
    /// Las salas se piden al <see cref="TriggerAbrirLoop"/>, que es quien las tiene serializadas por
    /// referencia. Nada de buscarlas por nombre: renombrarlas no rompe nada.
    /// </summary>
    internal static bool TryDireccionDelLoop(out Vector3 direccion)
    {
        direccion = Vector3.forward;

        Scene recuerdo = EscenaCargadaDe(M3Loop);
        if (!recuerdo.IsValid() || !recuerdo.isLoaded) return false;

        var abrirLoop = UnityEngine.Object.FindFirstObjectByType<TriggerAbrirLoop>(FindObjectsInactive.Include);
        if (abrirLoop == null || abrirLoop.SalasDelLoop.Count < 2)
        {
            Debug.LogError(Prefijo + "No hay un TriggerAbrirLoop con al menos 2 salas cargadas: no puedo " +
                                     "deducir hacia dónde avanza el loop. Cargale las salas en el Inspector.");
            return false;
        }
        GameObject sala1 = abrirLoop.SalasDelLoop[0];
        GameObject sala2 = abrirLoop.SalasDelLoop[1];
        if (sala1 == null || sala2 == null)
        {
            Debug.LogError(Prefijo + "Las dos primeras salas del TriggerAbrirLoop están vacías.");
            return false;
        }

        // Centros de GEOMETRÍA, no pivotes: los pivotes están corridos ~19 m y no todos igual.
        Vector3 avance = GeometriaDe(sala2.transform).center - GeometriaDe(sala1.transform).center;
        avance.y = 0f;
        if (avance.sqrMagnitude < 0.01f)
        {
            Debug.LogError(Prefijo + "Las salas 1 y 2 están una encima de la otra: no puedo deducir el sentido del loop.");
            return false;
        }

        if (Mathf.Abs(avance.z) < Mathf.Abs(avance.x))
            Debug.LogWarning(Prefijo + "El loop parece correr sobre X y no sobre Z. La ubicación puede quedar torcida.");

        direccion = avance.normalized;
        return true;
    }

    /// <summary>Teletransporta apoyando al jugador en el piso de ese punto. Único lugar que junta las dos cosas.</summary>
    private static void ColocarEn(Vector3 destino, float yaw, float alturaDeReferencia)
    {
        destino.y = AlturaDeParado(destino, alturaDeReferencia);
        TeletransportarJugador(destino, yaw);
    }

    private static Scene EscenaDeLaCasa() => SceneManager.GetSceneByPath(EscenaCasa);

    /// <summary>
    /// Busca por nombre en TODA la jerarquía de una escena, incluidos objetos desactivados.
    ///
    /// Recursiva a propósito: hubo una versión que solo miraba los objetos raíz y se rompió en cuanto
    /// Franco metió las salas del loop dentro de un contenedor. Reordenar la jerarquía es trabajo
    /// normal de escena; una tool de dev no puede exigir que nada se mueva de lugar.
    /// </summary>
    private static Transform BuscarPorNombre(Scene escena, string nombre)
    {
        if (!escena.IsValid() || !escena.isLoaded) return null;
        foreach (GameObject raiz in escena.GetRootGameObjects())
            foreach (Transform t in raiz.GetComponentsInChildren<Transform>(true))
                if (t.name == nombre) return t;
        return null;
    }

    /// <summary>
    /// Busca un componente DENTRO de una escena concreta. El filtro por escena importa: la casa sigue
    /// cargada debajo del recuerdo y tiene sus propios objetos que podrían confundirse.
    /// </summary>
    private static T BuscarEnEscena<T>(Scene escena) where T : Component
    {
        if (!escena.IsValid() || !escena.isLoaded) return null;
        foreach (T componente in UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                                                                        FindObjectsSortMode.None))
            if (componente.gameObject.scene == escena) return componente;
        return null;
    }

    // ══ La secuencia (genérica) ═══════════════════════════════════════════════

    private enum Paso { EsperandoManagers, EsperandoRecuerdo }

    private static Destino _destino;
    private static Paso _paso;
    private static double _arranque;
    private static bool _corriendo;

    static MemoraIrA()
    {
        EditorApplication.playModeStateChanged += AlCambiarPlayMode;
    }

    private static void Ir(Destino destino)
    {
        if (EditorApplication.isPlaying)
        {
            // Ya está corriendo: se saltea el ceremonial de arranque y se hace el salto en caliente.
            Arrancar(destino);
            return;
        }

        if (!AbrirLaCasa()) return;
        if (!PrenderSkipIntro()) return;

        SessionState.SetString(ClaveDestinoPendiente, destino.Nombre);
        EditorApplication.EnterPlaymode();
    }

    /// <summary>
    /// Deja MainHouseTesis abierta y sola.
    ///
    /// Si hay CUALQUIER escena con cambios sin guardar, ABORTA en vez de ofrecer guardar/descartar.
    /// Es deliberado: abrir la casa cierra lo que estés editando, y un "Don't Save" a las apuradas se
    /// lleva puesto trabajo que no está en ningún commit (así se perdió el objeto ClimaxHospital).
    /// Una herramienta de iteración no puede tener un camino que destruya trabajo.
    /// </summary>
    private static bool AbrirLaCasa()
    {
        if (SceneManager.GetActiveScene().path == EscenaCasa && SceneManager.sceneCount == 1)
            return true;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene abierta = SceneManager.GetSceneAt(i);
            if (!abierta.isDirty) continue;

            Debug.LogWarning(Prefijo + $"'{abierta.name}' tiene cambios sin guardar y abrir la casa la cerraría. " +
                                       "Guardá (Ctrl+S) y volvé a pedir el salto. No toco nada.");
            EditorUtility.DisplayDialog(
                "Memora ▸ Ir a",
                $"La escena '{abierta.name}' tiene cambios sin guardar.\n\n" +
                "Para arrancar en un punto del juego hay que abrir MainHouseTesis, y eso cerraría esa escena.\n\n" +
                "Guardala primero (Ctrl+S) y volvé a pedir el salto.",
                "Ok");
            return false;
        }

        EditorSceneManager.OpenScene(EscenaCasa, OpenSceneMode.Single);
        return true;
    }

    /// <summary>
    /// Prende la casilla que YA tiene IntroPlayer, en la INSTANCIA DE ESCENA y sin guardar: entrar a
    /// Play usa la escena en memoria, así que el archivo en disco (y el prefab del Player) quedan
    /// intactos. El valor previo se restaura al salir de Play.
    /// </summary>
    private static bool PrenderSkipIntro()
    {
        IntroPlayer intro = UnityEngine.Object.FindFirstObjectByType<IntroPlayer>(FindObjectsInactive.Include);
        if (intro == null)
        {
            Debug.LogError(Prefijo + "No encontré IntroPlayer en MainHouseTesis. Sin él la intro corre entera.");
            return false;
        }

        var so = new SerializedObject(intro);
        SerializedProperty prop = so.FindProperty(CampoSkipIntro);
        if (prop == null)
        {
            Debug.LogError(Prefijo + $"IntroPlayer ya no tiene el campo '{CampoSkipIntro}'. " +
                                     "¿Lo renombraron? Actualizá MemoraIrA.CampoSkipIntro.");
            return false;
        }

        // Int y no Bool: hace falta distinguir "estaba en false" de "no había nada stasheado".
        SessionState.SetInt(ClaveSkipIntroPrevio, prop.boolValue ? 1 : 0);
        prop.boolValue = true;
        so.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    private static void AlCambiarPlayMode(PlayModeStateChange estado)
    {
        if (estado == PlayModeStateChange.EnteredPlayMode)
        {
            string pendiente = SessionState.GetString(ClaveDestinoPendiente, string.Empty);
            if (string.IsNullOrEmpty(pendiente)) return;
            SessionState.EraseString(ClaveDestinoPendiente);

            foreach (Destino d in Todos)
                if (d.Nombre == pendiente) { Arrancar(d); return; }

            Debug.LogError(Prefijo + $"Quedó pendiente el destino '{pendiente}' pero ya no existe.");
            return;
        }

        if (estado == PlayModeStateChange.EnteredEditMode)
        {
            Detener();
            RestaurarSkipIntro();
        }
    }

    private static void RestaurarSkipIntro()
    {
        int previo = SessionState.GetInt(ClaveSkipIntroPrevio, -1);
        if (previo < 0) return; // no había nada stasheado
        SessionState.EraseInt(ClaveSkipIntroPrevio);

        IntroPlayer intro = UnityEngine.Object.FindFirstObjectByType<IntroPlayer>(FindObjectsInactive.Include);
        if (intro == null) return;

        var so = new SerializedObject(intro);
        SerializedProperty prop = so.FindProperty(CampoSkipIntro);
        if (prop == null) return;

        prop.boolValue = previo == 1;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Arrancar(Destino destino)
    {
        Detener();
        _destino = destino;
        _paso = Paso.EsperandoManagers;
        _arranque = EditorApplication.timeSinceStartup;
        _corriendo = true;
        EditorApplication.update += Tick;
    }

    private static void Detener()
    {
        if (!_corriendo) return;
        _corriendo = false;
        EditorApplication.update -= Tick;
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) { Detener(); return; }

        if (EditorApplication.timeSinceStartup - _arranque > TimeoutSegundos)
        {
            Debug.LogError(Prefijo + $"Timeout ({TimeoutSegundos:F0}s) esperando en el paso '{_paso}' de " +
                                     $"'{_destino.Nombre}'. Mirá la consola: probablemente falte un manager " +
                                     "o el recuerdo no cargó.");
            Detener();
            return;
        }

        switch (_paso)
        {
            case Paso.EsperandoManagers:
                if (!ManagersListos()) return;
                DarLoadout(_destino);

                if (string.IsNullOrEmpty(_destino.GuidFotoDelRecuerdo)) { Terminar(); return; }

                // Ya adentro del recuerdo (pediste el salto en caliente): volver a llamar a
                // LoadPhotoScene montaría una SEGUNDA copia aditiva de la escena. Solo reubicamos.
                if (EscenaCargadaDe(_destino).isLoaded)
                {
                    Debug.Log(Prefijo + "El recuerdo ya estaba montado: no lo recargo, solo aplico el momento.");
                    Terminar();
                    return;
                }

                if (!CargarRecuerdo(_destino)) { Detener(); return; }
                _paso = Paso.EsperandoRecuerdo;
                return;

            case Paso.EsperandoRecuerdo:
                Scene recuerdo = EscenaCargadaDe(_destino);
                if (!recuerdo.IsValid() || !recuerdo.isLoaded) return;
                if (SceneControllerManager.instance.IsTransitioning) return;
                Terminar();
                return;
        }
    }

    private static void Terminar()
    {
        bool logrado = _destino.AlLlegar == null || _destino.AlLlegar(EscenaCargadaDe(_destino));

        if (logrado)
            Debug.Log(Prefijo + $"'{_destino.Nombre}' listo.");
        else
            Debug.LogError(Prefijo + $"'{_destino.Nombre}': el inventario quedó bien, pero NO pude aplicar el " +
                                     "momento (ver el error de arriba). Estás en el arranque del recuerdo, " +
                                     "no donde pediste.");
        Detener();
    }

    /// <summary>
    /// AudioLibrary entra en la lista a propósito: Inventory.AddPhoto/AddNote/AddUniqueKeyItem
    /// leen <c>AudioLibrary.instance.XxxClip</c> sin null-check y tiran NullReference si todavía no existe.
    /// </summary>
    private static bool ManagersListos()
    {
        return PlayerManager.instance != null
            && SceneControllerManager.instance != null
            && Inventory.instance != null
            && DatabasesManager.instance != null
            && AudioLibrary.instance != null;
    }

    private static void DarLoadout(Destino destino)
    {
        Inventory inv = Inventory.instance;

        foreach (Entrada e in destino.Fotos)
        {
            PhotoData foto = Cargar<PhotoData>(e);
            if (foto != null) inv.AddPhoto(foto, null);
        }

        foreach (Entrada e in destino.FotosCompletadas)
        {
            PhotoData foto = Cargar<PhotoData>(e);
            if (foto != null) inv.MarkPhotoCompleted(foto.ID);
        }

        foreach (Entrada e in destino.Objetos)
        {
            UniqueKeyItemData item = Cargar<UniqueKeyItemData>(e);
            if (item != null) inv.AddUniqueKeyItem(item, null);
        }

        foreach (Entrada e in destino.Notas)
        {
            NoteData nota = Cargar<NoteData>(e);
            if (nota != null) inv.AddNote(nota);
        }

        // La linterna del inventario y la linterna EQUIPADA son dos cosas distintas: el ítem es lo que
        // ves en el menú, este flag es lo que hace que la tecla la prenda. Y FixFlashlight por si venías
        // de una corrida anterior donde el clímax te la rompió (BreakFlashlight).
        if (destino.ConLinterna)
        {
            PlayerManager.instance.SetHasFlashlight(true);
            FlashlightOffset linterna = PlayerManager.instance.GetFlashlightOffset();
            if (linterna != null) linterna.FixFlashlight();
        }

        Debug.Log(Prefijo + $"Loadout de '{destino.Nombre}': {destino.Fotos.Length} fotos · " +
                            $"{destino.Objetos.Length} objetos · {destino.Notas.Length} notas" +
                            (destino.ConLinterna ? " · linterna equipada y sana." : "."));
    }

    private static T Cargar<T>(Entrada e) where T : UnityEngine.Object
    {
        string path = AssetDatabase.GUIDToAssetPath(e.Guid);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogError(Prefijo + $"No existe el asset '{e.Nombre}' (GUID {e.Guid}). " +
                                     "¿Se borró? Sacalo del loadout del destino en MemoraIrA.");
            return null;
        }

        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null)
            Debug.LogError(Prefijo + $"'{e.Nombre}' está en {path} pero no es un {typeof(T).Name}.");
        return asset;
    }

    private static bool CargarRecuerdo(Destino destino)
    {
        PhotoData foto = Cargar<PhotoData>(new Entrada(destino.GuidFotoDelRecuerdo, "foto de " + destino.Nombre));
        if (foto == null) return false;

        if (!foto.HasMemoryScene)
        {
            Debug.LogError(Prefijo + $"La foto de '{destino.Nombre}' no tiene sceneToLoad: no hay recuerdo que cargar.");
            return false;
        }

        // El camino del juego de verdad: fija actualPhotoData/actualMemory/IsInMemoryScene, apaga las
        // zonas de la casa, corre el post-load y deja el guardado y la vuelta funcionando.
        SceneControllerManager.instance.LoadPhotoScene(foto);
        return true;
    }

    /// <summary>
    /// Teletransporte INSTANTÁNEO. <c>PlayerManager.TpPlayer</c> usa MovePosition, que interpola: para un
    /// salto de dev deja al jugador viajando y chocando lo que haya en el medio. Se escribe rb.position
    /// directo + SyncTransforms, igual que hace el reset de la corrida del clímax.
    /// </summary>
    internal static void TeletransportarJugador(Vector3 posicion, float yaw)
    {
        PlayerManager pm = PlayerManager.instance;
        if (pm == null) return;

        Rigidbody rb = pm.GetPlayerRigidbody();
        if (rb != null)
        {
            rb.position = posicion;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
        }

        PlayerMovement mov = pm.GetPlayerMovement();
        if (mov != null) mov.ResetMotion();

        PlayerCam cam = pm.GetPlayerCam();
        if (cam != null) cam.SetCamPosRotationInstant(new Vector3(0f, yaw, 0f));

        pm.CanMove(true);
    }
}
