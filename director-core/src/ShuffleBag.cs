using System;
using System.Collections.Generic;

namespace Memora.DirectorCore
{
    /// <summary>
    /// Bolsa de barajado con pesos. Implementa plan §2 / §11.G del Director de Eventos Ambientales.
    /// Garantías: peso = multiplicidad; ningún item se repite hasta que el ciclo cierra;
    /// el primer draw de un ciclo nuevo != último del anterior (con >=2 distintos);
    /// seedeable (misma seed => mismo orden); bolsa de 1 nunca se cuelga (bug do-while RandomSoundsManager:35).
    /// C# puro (sin UnityEngine) para ser testeable en EditMode con FakeDirectorClock.
    /// Lógica verificada por TDD red-green fuera de Unity antes de integrarse.
    /// </summary>
    public sealed class ShuffleBag<T>
    {
        private readonly List<T> _bag = new List<T>();
        private readonly HashSet<T> _distinct = new HashSet<T>();
        private readonly Random _rng;
        private static readonly IEqualityComparer<T> Eq = EqualityComparer<T>.Default;

        private int _remaining;   // items aún no sacados en el ciclo actual
        private T _lastReturned;
        private bool _hasLast;

        public ShuffleBag(int seed)
        {
            _rng = new Random(seed);
        }

        /// <summary>Total de copias en la bolsa (suma de pesos).</summary>
        public int Count => _bag.Count;

        /// <summary>Cantidad de items distintos.</summary>
        public int DistinctCount => _distinct.Count;

        /// <summary>Agrega un item con su peso (multiplicidad, >= 1). Reinicia el ciclo.</summary>
        public void Add(T item, int weight = 1)
        {
            if (weight < 1) throw new ArgumentOutOfRangeException(nameof(weight), "El peso debe ser >= 1.");
            for (int i = 0; i < weight; i++) _bag.Add(item);
            _distinct.Add(item);
            _remaining = 0; // cualquier alta reinicia el ciclo
        }

        /// <summary>Saca el próximo item de la bolsa, rebarajando al cerrar el ciclo.</summary>
        public T Next()
        {
            if (_bag.Count == 0) throw new InvalidOperationException("ShuffleBag vacía.");

            bool freshCycle = _remaining <= 0;
            if (freshCycle) _remaining = _bag.Count;

            int frontier = _remaining - 1;
            int grab = _rng.Next(0, _remaining);
            T item = _bag[grab];

            // Guarda anti-repetición en el borde de reshuffle: solo en el primer draw de un
            // ciclo nuevo, con >=2 distintos, evitá repetir el último del ciclo anterior.
            if (freshCycle && _hasLast && DistinctCount > 1 && Eq.Equals(item, _lastReturned))
            {
                grab = PickDifferentFromLast();
                item = _bag[grab];
            }

            // swap a la frontera y achicar el ciclo
            _bag[grab] = _bag[frontier];
            _bag[frontier] = item;
            _remaining--;

            _lastReturned = item;
            _hasLast = true;
            return item;
        }

        // Devuelve un índice en [0, _remaining) cuyo valor != _lastReturned.
        // Existe garantizado porque DistinctCount > 1. Intenta al azar y cae a un scan determinista.
        private int PickDifferentFromLast()
        {
            for (int tries = 0; tries < _remaining; tries++)
            {
                int idx = _rng.Next(0, _remaining);
                if (!Eq.Equals(_bag[idx], _lastReturned)) return idx;
            }
            for (int idx = 0; idx < _remaining; idx++)
                if (!Eq.Equals(_bag[idx], _lastReturned)) return idx;
            return 0; // inalcanzable con DistinctCount > 1
        }
    }
}
