namespace Memora.DirectorCore
{
    /// <summary>
    /// Reloj controlable para tests EditMode y para el dry-run del simulador (loop síncrono
    /// con tiempo falso). Comparten DirectorScheduleEngine => cero drift entre dry-run y juego real.
    /// </summary>
    public sealed class FakeDirectorClock : IDirectorClock
    {
        public float Now { get; private set; }

        public FakeDirectorClock(float start = 0f)
        {
            Now = start;
        }

        /// <summary>Avanza el reloj. Lanza si el delta es negativo (el tiempo no retrocede).</summary>
        public void Advance(float seconds)
        {
            if (seconds < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(seconds), "El reloj no retrocede.");
            Now += seconds;
        }
    }
}
