// MemoryTransitionPass.cs
// Memora — Vision Invertida photo transition controller
//
// Mirrors EyelidBlinkController lifecycle pattern:
//   Awake creates runtime CustomPassVolume (isGlobal, AfterPostProcess) +
//   FullScreenCustomPass (fetchColorBuffer=true) with a runtime material.
//   Shader.PropertyToID cached as static readonly int.
//
// Architecture: Opcion A (diseno-transicion-recuerdo.md Seccion 1.2).
// ONE injection during the transition. No Volume runtime. No GPU hang.
//
// v3.1 changes (2026-06-07):
//   - Removed: SetCrackWidth, SetPhotoAABB, SetCrackNoiseSeed, SetCrackNoiseAmp,
//              SetCrackIntensity, SetPhotoHaloFlood, ConfigurePhotoHalo.
//   - Replaced: SetPhotoHalo(Vector2, float) -> SetPhotoScreenPos(Vector2).
//     The halo intensity was always 0; only the UV position still matters (feeds UV warp).
//   - Removed Prop IDs: PropCrackIntensity, PropCrackWidth, PropPhotoAABB,
//     PropCrackNoiseSeed, PropCrackNoiseAmp, PropPhotoHaloFloodT, PropPhotoHaloColor,
//     PropPhotoHaloRadius, PropPhotoHaloFalloff, PropPhotoHaloIntensity.
//   - Removed dead material writes in ForceOff() and ApplyAllPropertiesToMaterial().
//   - PropPhotoHaloPos KEPT (still feeds _PhotoHaloPos → UV warp in shader).
//
// Dependencies:
//   - Shader "FullScreen/MemoryTransition" at Assets/Shaders/MainShaders/MemoryTransition.shader
//   - InspectedStencilPass enabled during the transition (writes the photo into _InspectedMaskTex)
//   - The photo on the "InspectedObject" layer (PhotoMemoryPortal handles this)
//
// Usage:
//   MemoryTransitionPass.instance.SetProgress(p);            // called every frame from portal
//   MemoryTransitionPass.instance.SetActive(true);           // enable/disable the pass
//   MemoryTransitionPass.instance.SetPhotoScreenPos(uv);     // photo screen UV for warp
//   MemoryTransitionPass.instance.DriveProgress(duration);   // already starts+tracks the coroutine
//   MemoryTransitionPass.instance.ForceOff();                // recovery / cleanup

using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

public class MemoryTransitionPass : MonoBehaviour
{
    public static MemoryTransitionPass instance { get; private set; }

    // ── Inspector knobs — all SerializeField, no public fields ───────────────

    [Header("Shader Reference")]
    [SerializeField] private Shader transitionShader;

    [Header("Target Camera — grade ONLY the world camera (PlayerCam)")]
    // Non-global volume targeting PlayerCam. Defaults to Camera.main (PlayerCam is MainCamera).
    // The photo is excluded from the grade via _InspectedMaskTex (the shader keeps masked
    // pixels full color), so no second camera is involved.
    [SerializeField] private Camera targetCamera;

    // NOTE: these SerializeField defaults apply to FRESH instances only. The live
    // scene component keeps its previously-serialized values — PhotoMemoryPortal
    // overrides them at hold-start via SetBreakpoints / SetGradeIntensity / SetMemoryProfile
    // so the "imponente" tuning takes effect regardless of what is serialized in the scene.
    [Header("Vignette (INVERTED — vignette DECREASES in Beat1)")]
    [SerializeField] private float vignetteBase = 0.35f;
    [SerializeField] private float vignetteOpen = 0.0f;   // periphery opens FULLY (was 0.08)
    [SerializeField] private float openCurveExp  = 0.6f;

    [Header("Saturation Factors")]
    [SerializeField] private float satHouse       = 0.85f;
    [SerializeField] private float satStripFloor  = 0.12f; // deeper grey before void (was 0.20)
    // arrivalOversat REMOVED: _ArrivalOversat removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).
    // flood-back path is dead; shader holds satFactor=0 at p>=_PVoidEnd.
    [SerializeField] private float floodCurveExp  = 0.7f;

    [Header("PostExposure Multipliers")]
    [SerializeField] private float postExpStrip = 0.60f;   // void darker → photo pops (was 0.70)
    // postExpFlood REMOVED: _PostExpFlood removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).

    [Header("Color Filters")]
    [SerializeField] private Color filterCold   = new Color(0.941f, 0.957f, 0.973f, 1f); // #F0F4F8
    [SerializeField] private Color filterSilver = new Color(0.925f, 0.941f, 0.957f, 1f); // #ECF0F4
    // filterWarm REMOVED: _FilterWarm removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).
    // Warm arrival is handled (neutralized) by MemoryArrivalGrade.cs arrivalColorFilter = Color.white.

    [Header("Film Grain")]
    [SerializeField] private float grainPeak = 0.022f;     // visible emulsion grain (was 0.009)

    [Header("Channel Offset / Emulsion Chemistry")]
    [SerializeField] private float channelOffsetMaxPx = 7f;
    [SerializeField] private float offsetYRatio        = 0.6f;
    [SerializeField] private float offsetPeakFrac      = 0.5f;
    [SerializeField] private float offsetSettle        = 3f;
    [SerializeField] private Color offsetTintWarm      = new Color(0.831f, 0.784f, 0.659f, 1f); // #D4C8A8
    [SerializeField] private Color offsetTintCool      = new Color(0.722f, 0.831f, 0.784f, 1f); // #B8D4C8

    // Photo exclusion is via the _InspectedMaskTex mask: InspectedStencilPass writes the photo
    // and MemoryTransition.shader reads mask.r > 0.5. No C# uniform / rendering-layer buffer needed.

    [Header("Phase Breakpoints (p values, set by PhotoMemoryPortal per profile)")]
    // Retimed (game-designer): flood stretched to 0.90 to kill the dead air after the
    // old 0.74 flood-end. Void pushed to 0.55 for a longer dread build before the drop.
    [SerializeField] private float pOpenEnd  = 0.18f;
    [SerializeField] private float pStripEnd = 0.42f;
    [SerializeField] private float pVoidEnd  = 0.55f;
    [SerializeField] private float pFloodEnd = 0.90f;

    [Header("UV Warp (world pulled toward the photo — per-memory: M1=0.012, M2=0.015, M3=0.022)")]
    // Driven per-hold by PhotoMemoryPortal from PhotoData.UvWarpStrength. The shader had the
    // _UVWarpStrength uniform + warp path but nothing ever pushed it → the per-memory escalation
    // axis was dead (always the shader default 0.015). SetUVWarpStrength now drives it.
    [SerializeField] private float uvWarpStrength = 0.015f;

    [Header("Volumetric White Fog (v3.3 — NDE death-tunnel climax, look spec 2026-06-09)")]
    // Static tuning pushed once at init; _WhiteFloodT itself is driven per-frame by
    // PhotoMemoryPortal.SetWhiteFlood(t). See the shader's fog block for the full doc.
    [SerializeField] private float whiteFogNoiseScale    = 1.8f;  // billow size (m) — room-scale clouds
    [SerializeField] private float whiteFogNoiseSpeed    = 0.04f; // slow ominous drift
    [SerializeField] private float whiteFogBaseDensity   = 1.5f;  // overall thickness
    [SerializeField] private float whiteFogRadialBias    = 0.5f;  // extra density pouring from the photo
    [SerializeField] private float whiteFogPeakLuminance = 2.0f;  // HDR — >1 makes Bloom glow the fog
    [SerializeField] private float whiteFogFarStart      = 3.5f;  // distance (m) where the seep mist begins

    // ── Cached property IDs — static readonly, cached once at class load ─────
    private static readonly int PropProgress       = Shader.PropertyToID("_Progress");
    private static readonly int PropPOpenEnd       = Shader.PropertyToID("_POpenEnd");
    private static readonly int PropPStripEnd      = Shader.PropertyToID("_PStripEnd");
    private static readonly int PropPVoidEnd       = Shader.PropertyToID("_PVoidEnd");
    private static readonly int PropPFloodEnd      = Shader.PropertyToID("_PFloodEnd");
    private static readonly int PropVignetteBase   = Shader.PropertyToID("_VignetteBase");
    private static readonly int PropVignetteOpen   = Shader.PropertyToID("_VignetteOpen");
    private static readonly int PropOpenCurveExp   = Shader.PropertyToID("_OpenCurveExp");
    private static readonly int PropSatHouse       = Shader.PropertyToID("_SatHouse");
    private static readonly int PropSatStripFloor  = Shader.PropertyToID("_SatStripFloor");
    // PropArrivalOversat removed: _ArrivalOversat removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).
    private static readonly int PropFloodCurveExp  = Shader.PropertyToID("_FloodCurveExp");
    private static readonly int PropPostExpStrip   = Shader.PropertyToID("_PostExpStrip");
    // PropPostExpFlood removed: _PostExpFlood removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).
    private static readonly int PropFilterCold     = Shader.PropertyToID("_FilterCold");
    private static readonly int PropFilterSilver   = Shader.PropertyToID("_FilterSilver");
    // PropFilterWarm removed: _FilterWarm removed from shader CBUFFER (v3.0 ARDE, 2026-06-05).
    private static readonly int PropGrainPeak      = Shader.PropertyToID("_GrainPeak");
    private static readonly int PropOffsetMaxPx    = Shader.PropertyToID("_ChannelOffsetMaxPx");
    private static readonly int PropOffsetYRatio   = Shader.PropertyToID("_OffsetYRatio");
    private static readonly int PropOffsetPeakFrac = Shader.PropertyToID("_OffsetPeakFrac");
    private static readonly int PropOffsetSettle   = Shader.PropertyToID("_OffsetSettle");
    private static readonly int PropOffsetTintWarm = Shader.PropertyToID("_OffsetTintWarm");
    private static readonly int PropOffsetTintCool = Shader.PropertyToID("_OffsetTintCool");
    // PropPhotoHaloPos KEPT: still feeds _PhotoHaloPos → UV Warp + Flood center in shader (v3.2).
    // All other halo/crack Prop IDs removed (v3.1 dead-code cleanup 2026-06-07):
    //   PropPhotoHaloColor, PropPhotoHaloRadius, PropPhotoHaloFalloff, PropPhotoHaloIntensity,
    //   PropPhotoHaloFloodT, PropCrackIntensity, PropCrackWidth, PropPhotoAABB,
    //   PropCrackNoiseSeed, PropCrackNoiseAmp.
    private static readonly int PropPhotoHaloPos     = Shader.PropertyToID("_PhotoHaloPos");
    private static readonly int PropUVWarpStrength   = Shader.PropertyToID("_UVWarpStrength");
    // ── Volumetric White Fog (v3.3 — replaces the v3.2 radial disc) ───────────
    private static readonly int PropWhiteFloodT          = Shader.PropertyToID("_WhiteFloodT");
    private static readonly int PropWhiteFloodColor      = Shader.PropertyToID("_WhiteFloodColor");
    private static readonly int PropWhiteFogNoiseScale   = Shader.PropertyToID("_WhiteFogNoiseScale");
    private static readonly int PropWhiteFogNoiseSpeed   = Shader.PropertyToID("_WhiteFogNoiseSpeed");
    private static readonly int PropWhiteFogBaseDensity  = Shader.PropertyToID("_WhiteFogBaseDensity");
    private static readonly int PropWhiteFogRadialBias   = Shader.PropertyToID("_WhiteFogRadialBias");
    private static readonly int PropWhiteFogPeakLuminance = Shader.PropertyToID("_WhiteFogPeakLuminance");
    private static readonly int PropWhiteFogFarStart     = Shader.PropertyToID("_WhiteFogFarStart");

    // ── Runtime state ─────────────────────────────────────────────────────────
    private Material           runtimeMaterial;
    private CustomPassVolume   customPassVolume;
    private FullScreenCustomPass fullScreenPass;
    private Coroutine          activeCoroutine;
    private float              currentProgress;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Singleton — one instance per scene, not DontDestroyOnLoad (stays with house scene).
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        InitializePass();
    }

    private void InitializePass()
    {
        if (transitionShader == null)
        {
            transitionShader = Shader.Find("FullScreen/MemoryTransition");
            if (transitionShader == null)
            {
                Debug.LogError("[MemoryTransitionPass] Cannot find shader 'FullScreen/MemoryTransition'. " +
                               "Ensure MemoryTransition.shader is in Assets/Shaders/MainShaders/.");
                return;
            }
        }

        // Create runtime material (same pattern as EyelidBlinkController).
        runtimeMaterial = new Material(transitionShader);
        runtimeMaterial.name = "MemoryTransition_Runtime";

        // Push all tuning knobs from SerializeField → material.
        ApplyAllPropertiesToMaterial();

        // Create CustomPassVolume on this GameObject.
        // NON-global + Target Camera = PlayerCam (mirrors the project's UIBlur volume).
        // This is what keeps the grade off BlurCam (the photo) — the photo stays full color.
        if (targetCamera == null) targetCamera = Camera.main;
        customPassVolume = gameObject.AddComponent<CustomPassVolume>();
        customPassVolume.isGlobal        = false;
        // The targetCamera property setter sets the internal useTargetCamera=true automatically.
        customPassVolume.targetCamera    = targetCamera;
        // BeforePostProcess: samples _ColorPyramidTexture (separate from the render target).
        // AfterPostProcess had a read-write hazard: _AfterPostProcessColorBuffer and the render target
        // are the same RTHandle (postProcessDest), so the simultaneous sample+write returns black.
        // At BeforePostProcess the pyramid is a distinct buffer — no hazard. HDRP's tone mapping
        // and LUT run on top of the graded result, which is correct behavior.
        customPassVolume.injectionPoint  = CustomPassInjectionPoint.BeforePostProcess;
        // Priority -1 so the project's UIBlur volume (priority 0) executes FIRST (higher = first,
        // per CustomPassVolume source) — MemoryTransition then grades the already-blurred result,
        // without modifying the existing UIBlur volume.
        customPassVolume.priority        = -1f;
        if (targetCamera == null)
            Debug.LogWarning("[MemoryTransitionPass] No targetCamera and no Camera.main found. " +
                             "Assign the PlayerCam in the Inspector.");

        // Add the fullscreen pass.
        fullScreenPass = customPassVolume.AddPassOfType<FullScreenCustomPass>();
        fullScreenPass.name                 = "MemoryTransition";
        fullScreenPass.fullscreenPassMaterial = runtimeMaterial;
        // fetchColorBuffer = TRUE: required for reading camera color to grade on top.
        // This resolves MSAA and binds _AfterPostProcessColorBuffer correctly.
        // (Contrast with EyelidBlink which uses false because it only draws opaque shapes.)
        fullScreenPass.fetchColorBuffer     = true;

        // Start disabled — PhotoMemoryPortal enables via SetActive(true).
        customPassVolume.enabled = false;
    }

    private void OnDestroy()
    {
        if (activeCoroutine != null)
            StopCoroutine(activeCoroutine);

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (instance == this)
            instance = null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Set the grade progress. Called every frame by PhotoMemoryPortal during the hold.
    /// p=0: no effect (but pass must already be enabled). p=1: flood at max.
    /// </summary>
    public void SetProgress(float p)
    {
        currentProgress = Mathf.Clamp01(p);
        if (runtimeMaterial != null)
            runtimeMaterial.SetFloat(PropProgress, currentProgress);
    }

    /// <summary>
    /// Enable or disable the CustomPassVolume. Call SetActive(true) before the first
    /// SetProgress(). Call ForceOff() for cleanup/recovery instead of SetActive(false).
    /// </summary>
    public void SetActive(bool active)
    {
        if (customPassVolume != null)
            customPassVolume.enabled = active;
    }

    /// <summary>
    /// Update the per-memory phase breakpoints. Call this from PhotoMemoryPortal
    /// when a new MemoryTransitionProfile is loaded (before BeginTransition).
    /// Values are the normalized p values from the profile's timing durations.
    /// </summary>
    public void SetBreakpoints(float openEnd, float stripEnd, float voidEnd, float floodEnd)
    {
        pOpenEnd  = openEnd;
        pStripEnd = stripEnd;
        pVoidEnd  = voidEnd;
        pFloodEnd = floodEnd;

        if (runtimeMaterial == null) return;
        runtimeMaterial.SetFloat(PropPOpenEnd,  pOpenEnd);
        runtimeMaterial.SetFloat(PropPStripEnd, pStripEnd);
        runtimeMaterial.SetFloat(PropPVoidEnd,  pVoidEnd);
        runtimeMaterial.SetFloat(PropPFloodEnd, pFloodEnd);
    }

    /// <summary>
    /// Override the grade intensity knobs at runtime (the "imponente" tuning).
    /// Lets PhotoMemoryPortal drive these regardless of what the live scene component
    /// has serialized — the authoritative profile lives on the portal.
    /// </summary>
    public void SetGradeIntensity(float vigOpen, float postExpStripFloor, float satFloor, float grainPeakValue)
    {
        vignetteOpen  = vigOpen;
        postExpStrip  = postExpStripFloor;
        satStripFloor = satFloor;
        grainPeak     = grainPeakValue;

        if (runtimeMaterial == null) return;
        runtimeMaterial.SetFloat(PropVignetteOpen,  vignetteOpen);
        runtimeMaterial.SetFloat(PropPostExpStrip,  postExpStrip);
        runtimeMaterial.SetFloat(PropSatStripFloor, satStripFloor);
        runtimeMaterial.SetFloat(PropGrainPeak,     grainPeak);
    }

    /// <summary>
    /// Push the photo's screen-space UV center so the UV warp is aimed correctly.
    /// Replaces the old SetPhotoHalo(uv, intensity) — intensity was always 0 and the
    /// halation block is now removed. Only the UV position is still needed by the warp.
    /// Call each frame from PhotoMemoryPortal while the hold is active.
    /// </summary>
    public void SetPhotoScreenPos(Vector2 screenPosUV)
    {
        if (runtimeMaterial != null)
            runtimeMaterial.SetVector(PropPhotoHaloPos, new Vector4(screenPosUV.x, screenPosUV.y, 0f, 0f));
    }

    /// <summary>
    /// Update per-memory visual parameters (channel offset max, flood curve, emulsion tints).
    /// Call before starting a transition with the profile's values.
    /// NOTE: arrivalOversaturation is accepted for call-site compatibility but intentionally
    /// ignored — _ArrivalOversat was removed from the shader CBUFFER (flood-back dead code,
    /// v3.0 ARDE 2026-06-05). The shader holds satFactor=0 at p>=_PVoidEnd.
    /// </summary>
    public void SetMemoryProfile(float arrivalOversaturation, float channelOffsetMax,
                                 float floodExp, Color offTintWarm, Color offTintCool)
    {
        // arrivalOversaturation intentionally unused — flood-back is dead code (v3.0 ARDE).
        channelOffsetMaxPx = channelOffsetMax;
        floodCurveExp     = floodExp;
        offsetTintWarm    = offTintWarm;
        offsetTintCool    = offTintCool;

        if (runtimeMaterial == null) return;
        runtimeMaterial.SetFloat(PropOffsetMaxPx,    channelOffsetMaxPx);
        runtimeMaterial.SetFloat(PropFloodCurveExp,  floodCurveExp);
        runtimeMaterial.SetColor(PropOffsetTintWarm, offsetTintWarm);
        runtimeMaterial.SetColor(PropOffsetTintCool, offsetTintCool);
    }

    /// <summary>
    /// Set the world UV-warp strength (per-memory pull toward the photo). Called at hold-start
    /// from PhotoMemoryPortal with PhotoData.UvWarpStrength (scaled to 0 under reduced-motion).
    /// </summary>
    public void SetUVWarpStrength(float strength)
    {
        uvWarpStrength = strength;
        if (runtimeMaterial != null)
            runtimeMaterial.SetFloat(PropUVWarpStrength, uvWarpStrength);
    }

    /// <summary>
    /// Drive the radial white flood (v3.2 climax). t=0: no white. t=1: entire screen white.
    /// Ramp t from 0 to 1 over the climax window in PhotoMemoryPortal. The flood grows
    /// radially from _PhotoHaloPos (the crack/photo screen UV center) outward.
    /// At t=1 the screen is guaranteed fully white including all corners at any aspect ratio.
    /// After t reaches 1, hand off to the UI overlay and call ForceOff() to clean up.
    /// </summary>
    public void SetWhiteFlood(float t)
    {
        if (runtimeMaterial != null)
            runtimeMaterial.SetFloat(PropWhiteFloodT, Mathf.Clamp01(t));
    }

    /// <summary>
    /// Emergency stop. Disables pass and resets progress to 0.
    /// Call on recovery (load fail, component disable, cancel after commit).
    /// Matches EyelidBlinkController.ForceOpen() pattern.
    /// </summary>
    public void ForceOff()
    {
        if (activeCoroutine != null)
        {
            StopCoroutine(activeCoroutine);
            activeCoroutine = null;
        }

        currentProgress = 0f;
        if (runtimeMaterial != null)
        {
            runtimeMaterial.SetFloat(PropProgress, 0f);
            // Note: no halo intensity to clear (PropPhotoHaloIntensity removed in v3.1).
            // Reset radial white flood (v3.2): ensures no residual white on a recovered/disabled pass.
            runtimeMaterial.SetFloat(PropWhiteFloodT, 0f);
        }

        if (customPassVolume != null)
            customPassVolume.enabled = false;
    }

    /// <summary>
    /// Drives progress 0→1 over the given duration. Starts and tracks a SINGLE coroutine
    /// internally (no double-wrapping) so ForceOff()/StopCoroutine kills it reliably.
    /// Returns the Coroutine handle (already started — do NOT StartCoroutine on it).
    /// The caller manages SetActive() separately. Useful for the MCP test recipe / playback.
    /// </summary>
    public Coroutine DriveProgress(float duration)
    {
        if (activeCoroutine != null)
            StopCoroutine(activeCoroutine);

        activeCoroutine = StartCoroutine(DriveProgressRoutine(duration));
        return activeCoroutine;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private IEnumerator DriveProgressRoutine(float duration)
    {
        SetProgress(0f);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            SetProgress(Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        SetProgress(1f);
        activeCoroutine = null;
    }

    private void ApplyAllPropertiesToMaterial()
    {
        if (runtimeMaterial == null) return;

        runtimeMaterial.SetFloat(PropProgress,       0f);
        runtimeMaterial.SetFloat(PropPOpenEnd,        pOpenEnd);
        runtimeMaterial.SetFloat(PropPStripEnd,       pStripEnd);
        runtimeMaterial.SetFloat(PropPVoidEnd,        pVoidEnd);
        runtimeMaterial.SetFloat(PropPFloodEnd,       pFloodEnd);
        runtimeMaterial.SetFloat(PropVignetteBase,    vignetteBase);
        runtimeMaterial.SetFloat(PropVignetteOpen,    vignetteOpen);
        runtimeMaterial.SetFloat(PropOpenCurveExp,    openCurveExp);
        runtimeMaterial.SetFloat(PropSatHouse,        satHouse);
        runtimeMaterial.SetFloat(PropSatStripFloor,   satStripFloor);
        // _ArrivalOversat removed from shader: no material.SetFloat here (v3.0 ARDE, 2026-06-05).
        runtimeMaterial.SetFloat(PropFloodCurveExp,   floodCurveExp);
        runtimeMaterial.SetFloat(PropPostExpStrip,    postExpStrip);
        // _PostExpFlood removed from shader: no material.SetFloat here (v3.0 ARDE, 2026-06-05).
        runtimeMaterial.SetColor(PropFilterCold,      filterCold);
        runtimeMaterial.SetColor(PropFilterSilver,    filterSilver);
        // _FilterWarm removed from shader: no material.SetColor here (v3.0 ARDE, 2026-06-05).
        runtimeMaterial.SetFloat(PropGrainPeak,       grainPeak);
        runtimeMaterial.SetFloat(PropOffsetMaxPx,     channelOffsetMaxPx);
        runtimeMaterial.SetFloat(PropOffsetYRatio,    offsetYRatio);
        runtimeMaterial.SetFloat(PropOffsetPeakFrac,  offsetPeakFrac);
        runtimeMaterial.SetFloat(PropOffsetSettle,    offsetSettle);
        runtimeMaterial.SetColor(PropOffsetTintWarm,  offsetTintWarm);
        runtimeMaterial.SetColor(PropOffsetTintCool,  offsetTintCool);

        // Photo screen pos: starts at center; PhotoMemoryPortal sets it each frame via SetPhotoScreenPos.
        // All halo/crack material writes removed (v3.1 — those uniforms no longer exist in the shader).
        runtimeMaterial.SetVector(PropPhotoHaloPos, new Vector4(0.5f, 0.5f, 0f, 0f));
        runtimeMaterial.SetFloat(PropUVWarpStrength, uvWarpStrength);

        // Volumetric white fog (v3.3): initialize to off + push static look tuning.
        // PhotoMemoryPortal drives _WhiteFloodT each frame during the climax via SetWhiteFlood(t).
        runtimeMaterial.SetFloat(PropWhiteFloodT,           0f);
        runtimeMaterial.SetColor(PropWhiteFloodColor,       Color.white);
        runtimeMaterial.SetFloat(PropWhiteFogNoiseScale,    whiteFogNoiseScale);
        runtimeMaterial.SetFloat(PropWhiteFogNoiseSpeed,    whiteFogNoiseSpeed);
        runtimeMaterial.SetFloat(PropWhiteFogBaseDensity,   whiteFogBaseDensity);
        runtimeMaterial.SetFloat(PropWhiteFogRadialBias,    whiteFogRadialBias);
        runtimeMaterial.SetFloat(PropWhiteFogPeakLuminance, whiteFogPeakLuminance);
        runtimeMaterial.SetFloat(PropWhiteFogFarStart,      whiteFogFarStart);
    }

#if UNITY_EDITOR
    // Validate that the shader can be found in editor (compile check helper).
    private void OnValidate()
    {
        if (transitionShader == null)
            transitionShader = Shader.Find("FullScreen/MemoryTransition");
    }
#endif
}
