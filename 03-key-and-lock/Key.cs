using UnityEngine;
using UnityEngine.Events;

public class Key : InspectableInteractableObject, IKeyItem
{
    [SerializeField] private KeyData keyData;

    [Tooltip("Se dispara cuando el jugador CIERRA el examen de la llave, no al agarrarla. Es el momento " +
             "correcto para encadenar un evento: la llave ya está en el inventario y la UI de inspección " +
             "ya se fue, así que lo que pase después no se come el examen ni queda tapado por el panel.")]
    [SerializeField] private UnityEvent alSalirDelExamen;

    // Clave de estado persistente "esta llave YA fue recogida" (canal globalState del SaveManager).
    // Necesaria porque el objeto-mundo de la llave se DESTRUYE al recogerla (InspectObject) y
    // Inventory.HasUniqueKeyItem se vuelve false si la llave se CONSUME en la puerta → ninguno de los
    // dos sirve para "ya la agarré alguna vez". El flag es permanente: sobrevive al consumo.
    private string ObtainedKey => keyData != null ? $"key.obtained.{keyData.ID}" : null;

    /// <summary>
    /// "El jugador YA consiguió esta llave alguna vez en esta partida". Permanente: sobrevive a que la
    /// llave se consuma en su puerta. Lo consulta quien tenga que reconstruir el mundo alrededor de una
    /// llave ya obtenida (la caja de música, para no volver a ofrecer su animación ni la llave adentro).
    /// </summary>
    public bool YaObtenida => keyData != null && SaveManager.Instance != null
                              && SaveManager.Instance.GetGlobalState<bool>(ObtainedKey);

    protected override void Start()
    {
        base.Start();

        // Si esta llave ya fue recogida en esta partida, el objeto-mundo NO debe reaparecer al
        // reconstruir el recuerdo (resume dentro del recuerdo / re-entrada por la foto). Se auto-destruye
        // para no ofrecer una llave duplicada. GetGlobalState lee el hotCache ya cargado (poblado antes
        // de montar la escena del recuerdo), así que en Start el flag ya está disponible.
        if (YaObtenida)
        {
            // Success() ANTES de destruirse. Para las llaves cuya recogida DISPARA algo en el mundo
            // (la de la caja de música abre la puerta blanca, única salida del recuerdo), saltearlo
            // dejaba al jugador encerrado: sin llave en el piso y con la puerta cerrada. Las
            // consecuencias son idempotentes justamente para poder re-dispararlas acá.
            // restaurando:true → esto NO es el beat, es reconstruir un estado que el jugador ya
            // alcanzó: la consecuencia debe aparecer YA HECHA. Animarla acá hacía que la puerta se
            // abriera con su chirrido en la cara del jugador apenas levanta la cortina del recuerdo.
            Success(restaurando: true);
            Destroy(gameObject);
        }
    }

    public override void Interact()
    {
        Inventory.instance.AddUniqueKeyItem(keyData, added =>
        {
            // Marcar la llave como recogida (persistente) ANTES del save ad-hoc que dispara
            // InspectObject al salir del examine → el flag queda en disco y el resume sabe no
            // re-instanciar la llave.
            if (added && keyData != null)
                SaveManager.Instance?.SetGlobalState(ObtainedKey, true);

            // Success() corre también cuando added == false. added responde "¿la agregué recién?",
            // no "¿el jugador la tiene?": si la llave ya estaba en el inventario (re-agarrar, resume,
            // re-entrada al recuerdo) el callback venía en false y el evento encadenado no se
            // disparaba NUNCA. Con la llave de la caja de música eso era un soft-lock.
            Success(restaurando: false);
        });
    }

    /// <summary>Consecuencia en el mundo de haber conseguido esta llave.</summary>
    /// <param name="restaurando">
    /// false: el jugador ACABA de agarrarla — la consecuencia es el beat que va a ver, con
    /// animación y sonido. true: se está reconstruyendo el estado de una llave ya obtenida
    /// (Start) — la consecuencia se aplica instantánea y en silencio, sin re-actuar el beat.
    /// </param>
    protected virtual void Success(bool restaurando)
    {

    }

    public override void SetOnExitExamine()
    {
        alSalirDelExamen?.Invoke();
    }
}
