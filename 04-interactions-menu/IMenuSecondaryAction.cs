/// <summary>
/// Capacidad OPCIONAL para un <see cref="IMenuInteractable"/> que expone una acción SECUNDARIA
/// mientras el menú de interacción está abierto — p. ej. la lámpara de la sala de arte sacando con F
/// un bulbo ya colocado. Con el menú abierto, este es dueño TANTO del contexto de input COMO de la
/// barra de hints (una tecla del contexto Player como FlipOrFlashlight queda muerta ahí, y el menú
/// dibuja su propia lista de hints), así que el menú es el único lugar que puede ofrecer esta acción:
/// le pregunta al objeto al abrir para agregar el hint, y pollea una entrada del contexto Menu
/// (FlipNote = F) para dispararla.
///
/// Los implementadores que no necesitan acción secundaria simplemente no implementan la interfaz — el
/// pattern-match del menú los saltea, así que puertas, printer, reloj, etc. no se ven afectados (Open/Closed).
/// </summary>
public interface IMenuSecondaryAction
{
    /// <summary>True mientras la acción secundaria está disponible (hint visible + input vivo).</summary>
    bool CanInvokeSecondary { get; }

    /// <summary>Hint que el menú agrega a su barra mientras <see cref="CanInvokeSecondary"/> es true.</summary>
    HintDisplayData SecondaryHint { get; }

    /// <summary>Ejecuta la acción secundaria. El menú bloquea más input hasta que se cierra.</summary>
    void InvokeSecondary();
}
