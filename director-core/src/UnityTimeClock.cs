using UnityEngine;

namespace Memora.DirectorCore
{
    /// <summary>Reloj de producción del Director: lee Time.time del engine.</summary>
    public sealed class UnityTimeClock : IDirectorClock
    {
        public float Now => Time.time;
    }
}
