namespace Memora.DirectorCore
{
    /// <summary>
    /// Sistema 2 (Vida Lejana, §F): cuántos cambios puede aplicar el sistema a una zona según hace cuánto el
    /// jugador la abandonó. PURO (sin Unity) ⇒ testeable en EditMode.
    /// Fórmula: B(tAusente) = clamp(floor((tAusente − tFloor) / tStep), 0, bMax).
    /// Defaults de diseño (§F): tFloor=90s, tStep=90s, bMax=2 (primer cambio a los ~180s de ausencia;
    /// son knobs — si en playtest se siente lento, bajar tFloor/tStep).
    /// </summary>
    public static class AbsenceBudget
    {
        public static int Compute(float secondsAbsent, float tFloor, float tStep, int bMax)
        {
            if (tStep <= 0f || bMax <= 0) return 0;          // guard
            if (secondsAbsent < tFloor) return 0;            // todavía no alcanzó el piso de ausencia
            int b = (int)System.Math.Floor((secondsAbsent - tFloor) / tStep);
            if (b < 0) b = 0;
            if (b > bMax) b = bMax;
            return b;
        }
    }
}
