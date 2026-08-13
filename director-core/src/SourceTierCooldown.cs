using System.Collections.Generic;

namespace Memora.DirectorCore
{
    /// <summary>
    /// Cooldown global por (fuente, tier) (§7-§B / R5): tras disparar un evento de cierta fuente+tier, otros
    /// de la MISMA combinación quedan en cooldown un rato (evita ráfagas del mismo personaje/intensidad —
    /// ej. Nicholas no pisa a Nicholas). PURO (sin Unity). Estado = última hora de disparo por clave.
    /// El director lo comparte entre las zonas y el EventSelector lo consulta en el pre-check (evita dead-air).
    /// La DURACIÓN se pasa por parámetro (el caller la resuelve por fuente/tier desde config) => esto solo trackea.
    /// </summary>
    public sealed class SourceTierCooldown
    {
        private readonly Dictionary<int, float> _lastFire = new Dictionary<int, float>();

        // Clave compacta (sin alloc): fuente en los bits altos, tier en los bajos.
        private static int Key(DiegeticSource source, EventTier tier) => ((int)source << 4) | (int)tier;

        public bool IsOnCooldown(DiegeticSource source, EventTier tier, float now, float cooldownSec)
        {
            if (cooldownSec <= 0f) return false;
            return _lastFire.TryGetValue(Key(source, tier), out float last) && now - last < cooldownSec;
        }

        public void RegisterFire(DiegeticSource source, EventTier tier, float now)
            => _lastFire[Key(source, tier)] = now;

        /// <summary>Limpia el estado (al reiniciar la sesión / Unlock).</summary>
        public void Reset() => _lastFire.Clear();
    }
}
