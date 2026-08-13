using System.Collections.Generic;

namespace Memora.DirectorCore
{
    /// <summary>
    /// Evento seleccionable por el director. El HorrorEvent (MonoBehaviour, en Assembly-CSharp) lo
    /// implementa; IsEligible reúne CanTrigger + cooldown + look gate por tier.
    /// </summary>
    public interface ISelectableEvent
    {
        int Weight { get; }              // peso = multiplicidad en la bolsa
        bool IsEligible(float now);      // elegibilidad completa en el instante now
        EventTier Tier { get; }          // para el racionamiento / cooldown por tier (IEventGate)
        DiegeticSource Source { get; }   // para el cooldown global por fuente (IEventGate)
    }

    /// <summary>
    /// Gate de elegibilidad dependiente del ESTADO del director (no del evento en sí): cooldown global por
    /// (fuente, tier) + racionamiento de jumpscares. Lo implementa el ZoneEventDirector; el EventSelector lo
    /// consulta en el pre-check para saltear eventos bloqueados SIN dead-air. null = sin gate extra.
    /// </summary>
    public interface IEventGate
    {
        bool IsBlocked(ISelectableEvent ev, float now);
    }

    /// <summary>
    /// Selector de evento de UNA zona (plan §2/§11.G): ShuffleBag con pesos + filtro de elegibilidad.
    /// "Skip sin consumir + salida O(distinct)": si NINGÚN evento es elegible devuelve null SIN tocar la
    /// bolsa (silencio forzado, sin loop infinito — el bug do-while de RandomSoundsManager:35); si hay al
    /// menos uno, saca de la bolsa hasta dar con uno elegible (termina seguro). El director crea uno por
    /// zona. C# puro, verificado por TDD red-green fuera de Unity antes de integrarse.
    /// </summary>
    public sealed class EventSelector
    {
        private readonly ISelectableEvent[] _events;
        private readonly ShuffleBag<int> _bag;
        private readonly IEventGate _gate; // opcional: cooldown/racionamiento del director. null = sin gate.

        public EventSelector(IReadOnlyList<ISelectableEvent> pool, int seed, IEventGate gate = null)
        {
            _events = new ISelectableEvent[pool.Count];
            for (int i = 0; i < pool.Count; i++) _events[i] = pool[i];

            _bag = new ShuffleBag<int>(seed);
            for (int i = 0; i < _events.Length; i++) _bag.Add(i, _events[i].Weight);
            _gate = gate;
        }

        // Elegibilidad completa = la del evento + el gate de estado del director (si hay).
        private bool Eligible(ISelectableEvent e, float now)
            => e.IsEligible(now) && (_gate == null || !_gate.IsBlocked(e, now));

        public ISelectableEvent SelectNext(float now)
        {
            if (_events.Length == 0) return null;

            // Pre-chequeo O(distinct): ¿hay alguno elegible? Si no, silencio forzado sin consumir la bolsa.
            bool anyEligible = false;
            for (int i = 0; i < _events.Length; i++)
            {
                if (Eligible(_events[i], now)) { anyEligible = true; break; }
            }
            if (!anyEligible) return null;

            // Hay al menos uno elegible => sacar de la bolsa hasta darlo (termina seguro).
            while (true)
            {
                int idx = _bag.Next();
                if (Eligible(_events[idx], now)) return _events[idx];
            }
        }
    }
}
