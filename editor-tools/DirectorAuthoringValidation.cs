#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using Memora.DirectorCore;

/// <summary>
/// Validador de AUTORÍA (editor-only, cero costo en runtime): Franco cablea los eventos A MANO recorriendo
/// la escena; estos chequeos corren desde OnValidate y avisan al instante los errores clásicos:
///   - Zone configurada que no coincide con la zona física del objeto,
///   - referencias nulas según el tipo de cambio (luz/puerta),
///   - ángulo fuera de los límites del HingeJoint,
///   - la misma PhysicDoor usada por los DOS sistemas (un dueño por puerta).
/// Solo valida el objeto SELECCIONADO (evita spam/costo al cargar la escena).
/// </summary>
public static class DirectorAuthoringValidation
{
    /// <summary>Valida solo mientras Franco edita ESE objeto (Inspector abierto sobre él).</summary>
    private static bool IsBeingEdited(Component c) =>
        c != null && Selection.activeGameObject == c.gameObject;

    /// <summary>
    /// Margen de tolerancia al buscar la zona de un objeto. Las cajas de zona terminan en la cara interna de
    /// la pared, así que TODO lo que cuelga de una pared (cuadros, apliques, interruptores) cae por 2-5 cm del
    /// lado de afuera. Sin este margen el validador marcaría error en cada uno de ellos y el aviso se volvería
    /// ruido que se aprende a ignorar — que es peor que no tenerlo.
    /// </summary>
    private const float ZoneMarginMeters = 0.5f;

    /// <summary>
    /// Zona física del objeto. DOS pasadas a propósito: primero busca la caja que lo contiene DE VERDAD y
    /// solo si ninguna lo hace reintenta con <see cref="ZoneMarginMeters"/> de tolerancia. Con una sola pasada
    /// tolerante, un objeto pegado a la pared que separa dos zonas cae dentro de las dos y gana la primera que
    /// se itere — le adjudicaba el pasillo a un libro que está en el cuarto de los padres.
    /// insideAny=false si ninguna caja lo contiene ni siquiera con el margen.
    /// </summary>
    private static DirectorZone PhysicalZoneOf(Vector3 pos, out bool insideAny)
    {
        var triggers = Object.FindObjectsByType<ZoneBoundaryTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (TryFindZone(triggers, pos, 0f, out DirectorZone exact)) { insideAny = true; return exact; }
        if (TryFindZone(triggers, pos, ZoneMarginMeters, out DirectorZone near)) { insideAny = true; return near; }

        insideAny = false;
        return DirectorZone.None;
    }

    private static bool TryFindZone(ZoneBoundaryTrigger[] triggers, Vector3 pos, float margin, out DirectorZone zone)
    {
        for (int i = 0; i < triggers.Length; i++)
        {
            var col = triggers[i].GetComponent<BoxCollider>();
            if (col == null) continue;

            Bounds b = col.bounds;
            if (b.size == Vector3.zero) continue;   // trigger apagado: sus bounds son cero, no es una zona real
            if (margin > 0f) b.Expand(margin * 2f); // Expand toma el total por eje, no el radio

            if (b.Contains(pos)) { zone = triggers[i].Zone; return true; }
        }

        zone = DirectorZone.None;
        return false;
    }

    /// <summary>
    /// S1: las zonas son las del JUGADOR — el objeto puede vivir en otra (un cuarto cerrado que se oye desde el
    /// pasillo). Por eso el mismatch es un aviso suave; lo que sí es error duro es quedarse SIN zonas.
    /// </summary>
    public static void ValidateHorrorEventZones(Component c, System.Collections.Generic.IReadOnlyList<DirectorZone> cfgZones)
    {
        if (!IsBeingEdited(c)) return;

        if (cfgZones == null || cfgZones.Count == 0)
        {
            Debug.LogWarning($"[Autoría] '{c.name}': sin zonas — nunca va a disparar. Agregá las zonas del " +
                "JUGADOR desde las que se percibe (o un elemento 'None' si querés que sea global).", c);
            return;
        }

        // Duplicados: no rompen (el catálogo deduplica) pero delatan un copy-paste en el Inspector.
        for (int i = 0; i < cfgZones.Count; i++)
            for (int j = i + 1; j < cfgZones.Count; j++)
                if (cfgZones[i] == cfgZones[j])
                {
                    Debug.LogWarning($"[Autoría] '{c.name}': la zona {cfgZones[i]} está repetida en la lista.", c);
                    break;
                }

        // 'None' mezclado con zonas concretas: el evento ya es global, las otras entradas no agregan nada.
        bool hasGlobal = false;
        for (int i = 0; i < cfgZones.Count; i++) if (cfgZones[i] == DirectorZone.None) { hasGlobal = true; break; }
        if (hasGlobal && cfgZones.Count > 1)
            Debug.LogWarning($"[Autoría] '{c.name}': la lista tiene 'None' (global) junto a zonas concretas. " +
                "Siendo global ya entra en todas las bolsas; las otras entradas sobran.", c);
        if (hasGlobal) return;

        DirectorZone phys = PhysicalZoneOf(c.transform.position, out bool inside);
        if (!inside) return;

        for (int i = 0; i < cfgZones.Count; i++) if (cfgZones[i] == phys) return;

        Debug.LogWarning($"[Autoría] '{c.name}': el objeto está físicamente en {phys}, que NO está entre sus " +
            "zonas. OK si es intencional (se ve/oye desde las zonas listadas, p.ej. un cuarto cerrado); " +
            "si no, agregá esa zona.", c);
    }

    /// <summary>S2: `zone` = zona del OBJETO (al revés que S1) — acá el mismatch es error, no elección.</summary>
    public static void ValidateVidaLejanaZone(Component c, DirectorZone cfgZone)
    {
        if (!IsBeingEdited(c)) return;
        DirectorZone phys = PhysicalZoneOf(c.transform.position, out bool inside);
        if (cfgZone == DirectorZone.None)
            Debug.LogWarning($"[Autoría] '{c.name}': VidaLejanaTarget sin Zone — nunca va a disparar.", c);
        else if (inside && phys != cfgZone)
            Debug.LogWarning($"[Autoría] '{c.name}': Zone={cfgZone} pero el objeto está físicamente en {phys}. " +
                "En Vida Lejana el Zone ES la zona del objeto — corregilo.", c);
    }

    /// <summary>Ángulo objetivo vs límites del HingeJoint de la puerta (el clásico "abre hacia la pared").</summary>
    public static void ValidateDoorAngle(Component c, PhysicDoor door, float angleY)
    {
        if (!IsBeingEdited(c) || door == null || door.Pivot == null) return;
        var hinge = door.Pivot.GetComponent<HingeJoint>();
        if (hinge == null) return;
        if (!hinge.useLimits)
            Debug.LogWarning($"[Autoría] '{c.name}': el HingeJoint de '{door.name}' tiene useLimits=false — " +
                "sin red: un signo equivocado la gira hacia la pared sin aviso. Verificá el signo en Play.", c);
        else if (angleY < hinge.limits.min || angleY > hinge.limits.max)
            Debug.LogWarning($"[Autoría] '{c.name}': ángulo {angleY:F0}° FUERA de los límites del hinge " +
                $"[{hinge.limits.min:F0}, {hinge.limits.max:F0}] de '{door.name}'.", c);
    }

    /// <summary>
    /// Un dueño por puerta: la misma PhysicDoor no puede tenerla el Director (S1) Y Vida Lejana (S2).
    /// Del lado del Director cuentan LOS DOS actuadores de puerta — el que la abre y el que la forcejea —
    /// porque los dos toman BeginDirectorControl y se pisarían el estado entre ellos y con S2.
    /// </summary>
    public static void ValidateDoorSingleOwner(Component owner, PhysicDoor door)
    {
        if (!IsBeingEdited(owner) || door == null) return;

        var abre = door.GetComponent<DoorHorrorEvent>();
        var forcejea = door.GetComponent<DoorRattleEvent>();

        if (abre != null && forcejea != null)
        {
            Debug.LogWarning($"[Autoría] la puerta '{door.name}' tiene DoorHorrorEvent (la abre) Y DoorRattleEvent " +
                "(la forcejea). Elegí uno: una puerta que se abre sola no es la misma que resiste.", owner);
        }

        // Director (la abre) + Vida Lejana (la deja distinta) sobre la MISMA puerta es la configuración
        // buscada desde 2026-07-28: escriben el mismo estado (ángulo + isOpen) y los dos la dejan viva, así
        // que no se corrompen. Lo que sí es contradictorio es forcejear una puerta que además se abre sola:
        // el forcejeo dice "esto no cede" y lo otro dice lo contrario.
        bool s2 = false;
        var targets = Object.FindObjectsByType<VidaLejanaTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] != (owner as VidaLejanaTarget) && targets[i].UsesDoor(door)) { s2 = true; break; }
        if (owner is VidaLejanaTarget ownTarget && ownTarget.UsesDoor(door)) s2 = true;

        if (forcejea != null && s2)
            Debug.LogWarning($"[Autoría] la puerta '{door.name}' se FORCEJEA (S1) y además Vida Lejana la ABRE. " +
                "Se contradicen: si el jugador la encuentra abierta, el forcejeo deja de significar algo.", owner);
    }
}
#endif
