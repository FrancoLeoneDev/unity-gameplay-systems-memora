using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;

public interface IMenuInteractable
{
    void OpenMenu();

    /// <summary>
    /// Intenta usar <paramref name="selectedItem"/> sobre este objeto. DEVUELVE el veredicto de
    /// ESTE intento: true = era el ítem correcto y la acción arrancó.
    ///
    /// El veredicto se devuelve y NO se publica en una propiedad a propósito. Antes el menú leía
    /// <see cref="InteractionSucceeded"/> (entonces llamado IsItemCorrect) DESPUÉS de llamar acá,
    /// así que cualquier `return` temprano que se olvidara de resetearlo dejaba vigente el veredicto
    /// de un uso anterior: el menú se comía del inventario el ítem equivocado y encima no
    /// reproducía el feedback de error. Con el valor de retorno eso es imposible de escribir mal —
    /// el compilador obliga a que todo camino de salida diga qué pasó.
    ///
    /// CONTRATO: no consumir el ítem del inventario hasta estar seguro de que la acción se puede
    /// completar. Consumir y después fallar deja la partida sin el ítem y sin el efecto (soft-lock).
    /// </summary>
    bool UseItem(ScriptableObject selectedItem);

    /// <summary>
    /// PEGAJOSA a propósito y distinta del valor de retorno de <see cref="UseItem"/>: responde
    /// "esta interacción terminó en éxito", no "el ítem que acabás de elegir era el correcto".
    /// La lee el menú al CERRAR (para no devolverle el control al jugador en medio de la animación
    /// de la cerradura, y para avisarle a los suscriptores de OnMenuClosed cómo terminó).
    /// Hay implementadores que la ponen en true fuera de UseItem (restaurar un save, una acción
    /// secundaria), y por eso no puede ser el mismo canal que el veredicto de un intento puntual.
    /// </summary>
    bool InteractionSucceeded { get; }
}

public interface IDisplayItem
{
    LocalizedString GetName();
    LocalizedString GetDescription();
    Sprite GetIconSprite();
    GameObject GetPrefab();

    Vector3 GetCustomDistance();
}
