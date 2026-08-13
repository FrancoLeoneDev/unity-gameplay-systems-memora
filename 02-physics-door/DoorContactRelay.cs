using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Relay de colisión jugador↔hoja. Va en el GameObject de la HOJA (el que tiene el Rigidbody del
/// <see cref="HingeJoint"/>), que puede ser un pivot hijo distinto del GameObject donde vive
/// <see cref="PhysicDoor"/> — Unity despacha OnCollision* al GameObject del Rigidbody, así que es
/// el único lugar donde llegan estos eventos.
///
/// Reenvía a <see cref="PhysicDoor"/> las tres cosas que dependen del contacto: forcejeo de puerta
/// trabada, manija bajada mientras el jugador empuja, y portazo si venía corriendo.
/// </summary>
public sealed class DoorContactRelay : MonoBehaviour
{
    [SerializeField, Tooltip("La PhysicDoor a la que reenviar el contacto. Asignar en el prefab.")]
    private PhysicDoor door;

    [SerializeField, Tooltip("Tag del jugador.")]
    private string playerTag = "Player";

    /// <summary>
    /// Colliders del jugador que están tocando la hoja AHORA. El jugador empuja con más de uno
    /// (el cuerpo y el 'colisionador puertas' adelantado), así que Unity dispara un par
    /// Enter/Exit por cada uno. Sin contarlos, el Exit del pusher levantaba la manija mientras
    /// el cuerpo seguía apoyado en la puerta.
    /// </summary>
    private readonly HashSet<Collider> touchingPlayerColliders = new HashSet<Collider>();

    /// <summary>PlayerMovement del jugador, cacheado en el primer contacto (vive en el mismo
    /// GameObject que su Rigidbody). Sirve para preguntar si está corriendo en vez de deducirlo.</summary>
    private PlayerMovement playerMovement;

    private void OnCollisionEnter(Collision collision)
    {
        if (door == null || !IsPlayer(collision)) return;

        touchingPlayerColliders.Add(collision.collider);

        if (playerMovement == null && collision.rigidbody != null)
            playerMovement = collision.rigidbody.GetComponent<PlayerMovement>();

        // Se reenvía SIEMPRE, no solo en el primer contacto: el portazo depende de ESTE impacto
        // (su propio cooldown evita que un segundo collider lo duplique).
        door.OnPlayerContactEnter(collision.GetContact(0).point, GetPushVelocity(collision),
            playerMovement != null && playerMovement.IsSprinting);
    }

    private void OnCollisionExit(Collision collision)
    {
        if (door == null || !IsPlayer(collision)) return;

        touchingPlayerColliders.Remove(collision.collider);
        touchingPlayerColliders.RemoveWhere(c => c == null);   // colliders destruidos/desactivados

        if (touchingPlayerColliders.Count == 0)
            door.OnPlayerContactExit();
    }

    private void OnDisable()
    {
        // Si la hoja se desactiva en pleno contacto no llega ningún Exit: limpiar para no arrancar
        // la próxima vez creyendo que el jugador sigue apoyado.
        touchingPlayerColliders.Clear();
    }

    /// <summary>
    /// Olvida los contactos en curso. Lo llama <see cref="PhysicDoor.Freeze"/>: congelar cambia la
    /// capa de la hoja y puede cortar un contacto SIN que llegue OnCollisionExit, y una entrada
    /// vieja acá dejaría el contador en >0 para siempre → la manija no volvería a soltarse nunca.
    /// </summary>
    public void ResetContacts() => touchingPlayerColliders.Clear();

    /// <summary>
    /// El jugador empuja con varios colliders (el cuerpo y el 'colisionador puertas' adelantado),
    /// y no todos tienen por qué llevar el tag. Aceptamos también el Rigidbody raíz, así el relay
    /// no depende de cómo esté taggeado cada collider hijo del player.
    /// </summary>
    private bool IsPlayer(Collision collision)
    {
        if (collision.collider.CompareTag(playerTag)) return true;
        Rigidbody otherRb = collision.rigidbody;
        return otherRb != null && otherRb.CompareTag(playerTag);
    }

    /// <summary>
    /// Velocidad HORIZONTAL del jugador en mundo. Se toma del Rigidbody del jugador y no de
    /// <c>collision.relativeVelocity</c> para que el signo no dependa de cuál de los dos cuerpos
    /// reporta la colisión. PhysicDoor la usa SOLO como DIRECCIÓN (hacia qué lado abre el portazo);
    /// si el portazo corresponde o no lo decide <c>IsSprinting</c>, no la magnitud.
    /// La Y se descarta para que la gravedad/escalones no metan componente vertical en el empuje.
    /// </summary>
    private Vector3 GetPushVelocity(Collision collision)
    {
        Rigidbody otherRb = collision.rigidbody;
        Vector3 v = otherRb != null ? otherRb.linearVelocity : -collision.relativeVelocity;
        v.y = 0f;
        return v;
    }
}
