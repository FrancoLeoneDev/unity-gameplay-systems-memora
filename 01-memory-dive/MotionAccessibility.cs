// MotionAccessibility.cs
// Global reduced-motion accessibility flag for the photo-hold camera effects
// (Z-breathing, micro-tilt, photo pulsation, world UV warp, FOV drop). These are a
// sustained ~3s vestibular stimulus on a FORCED tutorial hold — a motion-sensitive
// player must be able to turn them off. Backed by PlayerPrefs, mirroring the
// SensitivityController static-accessor idiom. A pause-menu toggle calls
// SetReduceMotion(bool); PhotoMemoryPortal reads ReduceMotion at hold-start.
using UnityEngine;

public static class MotionAccessibility
{
    private const string Key = "ReduceMotion";
    private static int cached = -1; // -1 = not yet read from PlayerPrefs

    /// <summary>True when the player has opted into reduced motion (default: false = full motion).</summary>
    public static bool ReduceMotion
    {
        get
        {
            if (cached < 0)
                cached = PlayerPrefs.GetInt(Key, 0);
            return cached == 1;
        }
    }

    /// <summary>Call from a settings/pause-menu toggle. Persists immediately.</summary>
    public static void SetReduceMotion(bool on)
    {
        cached = on ? 1 : 0;
        PlayerPrefs.SetInt(Key, cached);
        PlayerPrefs.Save();
    }
}
