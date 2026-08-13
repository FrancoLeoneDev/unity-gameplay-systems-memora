using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class AudioLibrary : MonoBehaviour
{
    public static AudioLibrary instance { get; private set; }

    [Header("Light Switch")]
    [SerializeField] private AudioClip lightSwitchClip;
    public AudioClip LightSwitchClip => lightSwitchClip;

    [Header("Flickering Light")]
    [SerializeField] private AudioClip flickeringLightClip;
    public AudioClip FlickeringLightClip => flickeringLightClip;


    [Header("Drawer")]
    [SerializeField] private AudioClip openDrawerClip;
    [SerializeField] private AudioClip closeDrawerClip;
    [SerializeField] private AudioClip stuckedDrawerClip;
    public enum DrawerSounds { Open, Close, Stucked }


    [Header("DoorSounds")]
    [Header("Sound Sets by Material")]
    [SerializeField] private DoorSoundSet woodSounds;
    [SerializeField] private DoorSoundSet metalSounds;
    public enum DoorSounds { Open, Close, Locked, Locking, Unlock, Creak, Slam, Blocked }

    [Header("HorrorEventsSounds")]
    [SerializeField] private AudioClip[] horrorEventsAudioArray;
    [SerializeField] private AudioClip heartBeatSound;
    public AudioClip HeartBeatSound => heartBeatSound;

    [SerializeField] private AudioClip stunedSound;
    public AudioClip StunedSound => stunedSound;

    [Header("Footsteps")]
    [SerializeField] private AudioClip carpetFloorSound;
    [SerializeField] private AudioClip tileFloorSound;
    [SerializeField] private AudioClip woodenFloorSound;
    [SerializeField] private AudioClip stairsFloorSound;
    public enum FootstepsSounds { Carpet, Tile, Wooden, Stairs}

    [Header("Pickup")]
    [SerializeField] private AudioClip notePickupClip;
    [SerializeField] private AudioClip importantItemPickupClip;
    public AudioClip NotePickupClip => notePickupClip;
    public AudioClip ImportantItemPickupClip => importantItemPickupClip;

    [Header("ArtRoom")]
    [SerializeField] private AudioClip enroscarBulboSound;
    public AudioClip EnroscarBulboSound => enroscarBulboSound;

    /// <summary>
    /// Tipo de gesto ambiental. Los eventos de la casa no guardan clips: guardan QUÉ TIPO son y piden acá.
    /// Valores nuevos SIEMPRE al final: el enum queda serializado en la escena.
    /// </summary>
    public enum DirectorSounds
    {
        None = 0,
        PictureTilt,      // cuadro que se tuerce: roce de marco contra pared
        ObjectScrape,     // algo que se arrastra: la silla del comedor
        ObjectFall,       // cae y golpea el piso sin romperse: el libro, un cuadro
        ObjectBreak,      // se rompe contra el piso: la taza
        DoorCreak,        // puerta lenta: crujido largo de bisagra
        DoorSlam,         // portazo o apertura de golpe
        DoorRattle,       // forcejeo de puerta trabada
        LightFlicker,     // chasquido o zumbido del contacto
        ThunderClose,     // trueno cercano, el del fogonazo en la ventana
        ThunderDistant,   // retumbo lejano de fondo
        WoodCreak,        // madera crujiendo cerca del jugador (global)
        Footstep,         // un paso (global, suele ir detrás del jugador)
        DistantSlam,      // portazo lejano sin puerta identificable (global)
        RadioStatic,      // estática de radio
        DoorLatchSoft,    // picaporte que se acomoda a lo lejos: la textura CALMA del portazo (global)
        AirDraft,         // corriente de aire corta, sin ventana ni puerta que la explique (global)
    }

    [Header("Director de Eventos Ambientales")]
    [SerializeField, Tooltip("Un set de clips por TIPO de gesto. Los eventos de la casa no guardan clips: " +
        "declaran su tipo y piden acá. Cargar los clips una vez por tipo en vez de objeto por objeto, y con " +
        "varios por tipo para que el mismo gesto no suene siempre igual.")]
    private DirectorSoundSet[] directorSounds = new DirectorSoundSet[0];

    /// <summary>Índice por tipo, para armar el set en O(1) sin recorrer el array en cada disparo.</summary>
    private Dictionary<DirectorSounds, DirectorSoundSet> _directorSoundsByType;

    /// <summary>Último clip devuelto por tipo: evita repetir dos veces seguidas el mismo.</summary>
    private readonly Dictionary<DirectorSounds, int> _lastDirectorIndex = new Dictionary<DirectorSounds, int>();

    [System.Serializable]
    public class DirectorSoundSet
    {
        [SerializeField] private DirectorSounds type;
        [SerializeField] private AudioClip[] clips;
        [SerializeField, Range(0f, 1f), Tooltip("Volumen relativo de este tipo de sonido.")]
        private float volume = 1f;

        public DirectorSounds Type => type;
        public AudioClip[] Clips => clips;
        public float Volume => volume;
    }

    /// <summary>
    /// Devuelve un clip al azar del set de ese tipo, evitando repetir el último. Null si el tipo no está
    /// cargado — el evento simplemente queda mudo, no falla.
    /// </summary>
    public AudioClip GetDirectorSound(DirectorSounds type)
    {
        DirectorSoundSet set = GetDirectorSoundSet(type);
        if (set == null || set.Clips == null || set.Clips.Length == 0) return null;

        int last = _lastDirectorIndex.TryGetValue(type, out int l) ? l : -1;
        int index;
        int intentos = 0;
        do
        {
            index = Random.Range(0, set.Clips.Length);
            intentos++;
        }
        while (index == last && intentos < set.Clips.Length);

        // Borrar un .mp3 de la carpeta deja un hueco NULL en el array del prefab, sin ningún error: el evento
        // salía mudo de a ratos y no había forma de darse cuenta. Si el sorteo cae en un hueco, se busca el
        // primer clip vivo a partir de ahí en vez de devolver null.
        AudioClip elegido = set.Clips[index];
        if (elegido == null) elegido = PrimerClipVivo(set.Clips, index);
        if (elegido == null) return null;

        _lastDirectorIndex[type] = index;
        return elegido;
    }

    /// <summary>Recorre el array circularmente desde <paramref name="desde"/> buscando el primer clip no nulo.</summary>
    private static AudioClip PrimerClipVivo(AudioClip[] clips, int desde)
    {
        for (int i = 1; i < clips.Length; i++)
        {
            AudioClip c = clips[(desde + i) % clips.Length];
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Volumen configurado para ese tipo (1 si no está cargado).</summary>
    public float GetDirectorVolume(DirectorSounds type)
    {
        DirectorSoundSet set = GetDirectorSoundSet(type);
        return set != null ? set.Volume : 1f;
    }


    /// <summary>
    /// Reproduce el sonido de un evento ambiental. Una sola regla para los siete actuadores:
    /// si el objeto trae clips propios ganan esos (la nota de ESE piano, la cancion de ESA radio);
    /// si no, se toma uno al azar del set de ese tipo. Estatico y null-safe: si la biblioteca todavia
    /// no existe el evento queda mudo, no falla.
    /// </summary>
    /// <returns>
    /// El clip que efectivamente sonó, o null si no sonó nada. Sirve para que un actuador pueda
    /// ajustar la DURACIÓN de su gesto a la del sonido (una silla tiene que dejar de arrastrarse
    /// cuando el arrastre deja de oírse). Ignorarlo es válido: los gestos instantáneos no lo usan.
    /// </returns>
    public static AudioClip PlayDirectorSound(AudioSource source, AudioClip[] overrideClips, DirectorSounds type, float volume)
    {
        if (source == null) return null;

        AudioClip clip = null;
        if (overrideClips != null && overrideClips.Length > 0)
            clip = overrideClips[Random.Range(0, overrideClips.Length)];

        float volumenDeTipo = 1f;
        if (clip == null)
        {
            if (instance == null || type == DirectorSounds.None) return null;
            clip = instance.GetDirectorSound(type);
            volumenDeTipo = instance.GetDirectorVolume(type);
        }

        if (clip == null) return null;
        source.PlayOneShot(clip, volume * volumenDeTipo);
        return clip;
    }

    private DirectorSoundSet GetDirectorSoundSet(DirectorSounds type)
    {
        if (type == DirectorSounds.None) return null;

        if (_directorSoundsByType == null)
        {
            _directorSoundsByType = new Dictionary<DirectorSounds, DirectorSoundSet>();
            for (int i = 0; i < directorSounds.Length; i++)
            {
                if (directorSounds[i] == null) continue;
                _directorSoundsByType[directorSounds[i].Type] = directorSounds[i];
            }
        }

        return _directorSoundsByType.TryGetValue(type, out DirectorSoundSet set) ? set : null;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;

        DontDestroyOnLoad(gameObject);
    }
    public AudioClip GetDrawerSound(DrawerSounds soundType)
    {
        switch (soundType)
        {
            case DrawerSounds.Open:
                
                return openDrawerClip;
            case DrawerSounds.Close:
                return closeDrawerClip;
            case DrawerSounds.Stucked:
                return stuckedDrawerClip;
            default:
                return null;
        }
    }
    public AudioClip GetDoorSound(DoorType doorType, DoorSounds soundType)
    {
        DoorSoundSet set = doorType == DoorType.Wooden ? woodSounds : metalSounds;

        switch (soundType)
        {
            case DoorSounds.Open:
                return set.openDoorClips.GetRandomIndex();
            case DoorSounds.Close:
                return set.closeDoorClips.GetRandomIndex();
            case DoorSounds.Locked:
                return set.lockedDoorClip;
            case DoorSounds.Locking:
                return set.lockingDoorClip;
            case DoorSounds.Unlock:
                return set.unlockDoorClip;
            case DoorSounds.Creak:
                return set.creakDoorClips.GetRandomIndex();
            case DoorSounds.Slam:
                return set.slamDoorClips.GetRandomIndex();
            case DoorSounds.Blocked:
                return set.blockedDoorClip;
            default:
                return null;
        }
    }

    // FIX C4 (auditoria §C4): guard against empty array to avoid IndexOutOfRangeException.
    public AudioClip GetHorrorEventSound()
    {
        if (horrorEventsAudioArray == null || horrorEventsAudioArray.Length == 0) return null;
        int randomIndex = Random.Range(0, horrorEventsAudioArray.Length);
        return horrorEventsAudioArray[randomIndex];
    }

    public AudioClip GetFootstepsSound(FootstepsSounds footstepsSounds)
    {
        switch (footstepsSounds)
        {
            case FootstepsSounds.Wooden:
                return woodenFloorSound;
            case FootstepsSounds.Tile:
                return tileFloorSound;
            case FootstepsSounds.Carpet:
                return carpetFloorSound;
            case FootstepsSounds.Stairs:
                return stairsFloorSound;
            default:
                return null;
        }
    }

    // FIX B2 (auditoria §B2): fields changed from public to [SerializeField] private
    // per project convention. Properties expose read-only access for external consumers.
    //
    // ⚠️ Ese rename (openDoorClips → _openDoorClips) se hizo SIN FormerlySerializedAs, y Unity
    // ante un rename no migra: descarta el valor. Resultado: TODOS los clips de puerta quedaron
    // huérfanos (el prefab Managers/AudioLibrary seguía guardándolos bajo las claves viejas, pero
    // la clase leía null) y las puertas quedaron mudas. Los atributos de abajo recuperan ese dato.
    // NO BORRARLOS hasta que el prefab se re-guarde con las claves nuevas; y si se vuelve a
    // renombrar un campo serializado, agregar FormerlySerializedAs en el mismo cambio.
    [System.Serializable]
    public class DoorSoundSet
    {
        [FormerlySerializedAs("openDoorClips")]
        [SerializeField] private AudioClip[] _openDoorClips;
        [FormerlySerializedAs("closeDoorClips")]
        [SerializeField] private AudioClip[] _closeDoorClips;
        [FormerlySerializedAs("lockedDoorClip")]
        [SerializeField] private AudioClip _lockedDoorClip;
        [FormerlySerializedAs("lockingDoorClip")]
        [SerializeField] private AudioClip _lockingDoorClip;
        [FormerlySerializedAs("unlockDoorClip")]
        [SerializeField] private AudioClip _unlockDoorClip;
        [FormerlySerializedAs("creakDoorClips")]
        [SerializeField] private AudioClip[] _creakDoorClips;
        [SerializeField] private AudioClip[] _slamDoorClips;   // nuevo: nunca tuvo dato viejo
        [SerializeField, Tooltip("Suena al intentar cerrar la puerta con alguien en el vano. " +
            "DISTINTO del de puerta trabada a proposito: son dos significados diferentes.")]
        private AudioClip _blockedDoorClip;

        public AudioClip[] openDoorClips   => _openDoorClips;
        public AudioClip[] closeDoorClips  => _closeDoorClips;
        public AudioClip   lockedDoorClip  => _lockedDoorClip;
        public AudioClip   lockingDoorClip => _lockingDoorClip;
        public AudioClip   unlockDoorClip  => _unlockDoorClip;
        public AudioClip[] creakDoorClips  => _creakDoorClips;
        public AudioClip[] slamDoorClips   => _slamDoorClips;
        public AudioClip   blockedDoorClip => _blockedDoorClip;
    }






}
