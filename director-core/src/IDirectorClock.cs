namespace Memora.DirectorCore
{
    /// <summary>
    /// Reloj inyectable del Director (plan §11.G). Permite testear la lógica de tiempo en
    /// EditMode puro (donde Time.time queda congelado en 0) usando FakeDirectorClock.
    /// Producción: UnityTimeClock. Tests / dry-run del simulador: FakeDirectorClock.
    /// </summary>
    public interface IDirectorClock
    {
        float Now { get; }
    }
}
