// BackgroundBlur.cs
// ============================================================================
// Memora — AfterPostProcess CustomPostProcessVolumeComponent that blurs the
// fully post-processed (tonemapped + graded) frame.
//
// INJECTION POINT: CustomPostProcessInjectionPoint.AfterPostProcess
//   - HDRP calls Render() with DISTINCT source and destination RTHandles, so
//     there is NO read-write hazard (unlike a FullScreenCustomPass at AfterPostProcess).
//   - The source texture is the complete graded frame — bloom, tonemapping, LUT
//     are all baked in. The blur therefore matches the game's exact grade.
//
// SINGLE-CAMERA INSPECTION (2026-06-16):
//   No second camera. The inspected object is rendered by PlayerCam on the
//   "InspectedObject" layer. InspectedStencilPass (a DrawRenderers CustomPass at
//   AfterOpaqueAndSky on that layer) writes those pixels into a script-owned mask
//   RenderTexture, bound globally as _InspectedMaskTex via Shader.SetGlobalTexture.
//   Pass 1 (FragBlurV) samples _InspectedMaskTex and passes pixels where mask.r > 0.5
//   through SHARP from _OriginalTexture (dilated by the kernel extent to kill edge
//   halos). Everything else blurs.
//
//   The source RTHandle is bound as BOTH _InputTexture (Pass 0 horizontal input) AND
//   _OriginalTexture (Pass 1 sharp-passthrough + intensity-blend baseline).
//   _InputTexture for Pass 1 is m_TempRT (the horizontally-blurred intermediate).
//   Render() also re-binds _InspectedMaskTex right before its DrawFullScreen for reliability.
//
// ALGORITHM: Separable 2-pass Gaussian, 9-tap per pass.
//   Pass 0 (BlurH): source -> m_TempRT (horizontal)
//   Pass 1 (BlurV): m_TempRT -> destination (vertical + intensity blend + mask passthrough)
//
// PAUSE CASE (InspectedStencilPass disabled / mask cleared):
//   Nothing on the "InspectedObject" layer → _InspectedMaskTex.r = 0 for all pixels →
//   isInspected = false → all pixels blur → pause and inventory work unchanged.
//
// WIRING: BlurManager drives intensity and isActive state.
//   All existing call sites (PauseMenu, InspectObject, InventoryUI, etc.) call
//   BlurManager.SetActiveBlur / SetBlurIntensity / ResetBlurIntensity — no changes needed.
//
// REGISTRATION (manual — NOT done by this script):
//   Project Settings > HDRP Default Settings (Graphics) >
//   Custom Post Process Orders > After Post Process > add BackgroundBlur.
//
// Historia: 2026-06-16 — fix de cámara única.
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

[Serializable, VolumeComponentMenu("Post-processing/Custom/Background Blur")]
public sealed class BackgroundBlur : CustomPostProcessVolumeComponent, IPostProcessComponent
{
    // ── Volume parameters (driven by BlurManager at runtime) ──────────────

    [Tooltip("Master blur intensity. 0 = off (IsActive returns false, no GPU cost). " +
             "1 = fully blurred. BlurManager writes this every frame during inspection.")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);

    [Tooltip("Blur width. The half-kernel reaches 4x this value in pixels. " +
             "MAX 8: the kernel spends a fixed 16 sample-cells per side, so the cell width is " +
             "radius/4 texels — above 8 the cells exceed the 2-texel reach of a bilinear tap and " +
             "the gaps come back as a moire/dot grid. Tuned in the Volume override.")]
    public ClampedFloatParameter blurRadius = new ClampedFloatParameter(6f, 0f, 8f);

    [Tooltip("UNIFORM dim of the blurred background (the sharp inspected object is never dimmed). " +
             "1 = off, 0 = black. Lever for UI contrast over bright backgrounds — e.g. the cream " +
             "inventory UI over the blown-out 'sala blanca'. REPLACED the old luminance-adaptive " +
             "darkening, which turned every bright zone into a flat grey patch ringed by its own " +
             "threshold ramp: the blur must be uniform, never a function of what is behind it.")]
    public ClampedFloatParameter bgDim = new ClampedFloatParameter(1f, 0f, 1f);

    // NOTE: inspectBit (old renderingLayerMask approach) removed 2026-06-16.
    // The blur exclusion marker is now the _InspectedMaskTex mask texture written by
    // InspectedStencilPass. No inspector knob needed — both BackgroundBlur.shader and
    // MemoryTransition.shader sample _InspectedMaskTex.r > 0.5.

    // ── Internal state ────────────────────────────────────────────────────

    // The material is created from the hidden shader at Setup() and destroyed at Cleanup().
    // NOT serialized — runtime-only.
    private Material m_Material;

    // Ping-pong temp buffer for the horizontal pass output.
    // Allocated once in Setup() (auto-scales with the camera via useDynamicScale).
    private RTHandle m_TempRT;

    // ── Shader property ID cache (static — one set for all instances) ─────

    private static readonly int PropInputTexture    = Shader.PropertyToID("_InputTexture");
    private static readonly int PropOriginalTexture = Shader.PropertyToID("_OriginalTexture");
    private static readonly int PropInspectedMaskTex = Shader.PropertyToID("_InspectedMaskTex");
    private static readonly int PropBlurRadius      = Shader.PropertyToID("_BlurRadius");
    private static readonly int PropIntensity       = Shader.PropertyToID("_Intensity");
    private static readonly int PropBgDim = Shader.PropertyToID("_BgDim");

    // Shader pass indices (must match SubShader Pass order in BackgroundBlur.shader).
    private const int k_PassBlurH = 0;
    private const int k_PassBlurV = 1;

    // Shader path used by Shader.Find (must match the shader's "Shader Name" string).
    private const string k_ShaderName = "Hidden/Shader/BackgroundBlur";

    // ── IPostProcessComponent ─────────────────────────────────────────────

    /// <summary>
    /// Returns false when intensity is 0 or the material failed to create.
    /// HDRP skips Render() entirely when this returns false — zero GPU cost.
    /// </summary>
    public bool IsActive() => m_Material != null && intensity.value > 0f;

    // ── CustomPostProcessVolumeComponent ─────────────────────────────────

    public override CustomPostProcessInjectionPoint injectionPoint =>
        CustomPostProcessInjectionPoint.AfterPostProcess;

    /// <summary>
    /// Called once when the post-process stack initialises. Creates the runtime material.
    /// </summary>
    public override void Setup()
    {
        Shader shader = Shader.Find(k_ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[BackgroundBlur] Shader '{k_ShaderName}' not found. " +
                           "Ensure Assets/Shaders/MainShaders/BackgroundBlur.shader is compiled " +
                           "and added to 'Always Included Shaders' (or referenced by a scene Volume).");
            return;
        }

        m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        // Temp buffer for the horizontal pass. Vector2.one + useDynamicScale makes the
        // RTHandle track the camera's actual (possibly DRS-scaled) render size automatically,
        // so no per-frame reallocation — and no need to query a camera scale. Tex2DArray
        // dimension (TextureXR) keeps the TEXTURE2D_X sampling in the shader valid on XR paths.
        //
        // FORMAT: R16G16B16A16_SFloat (NOT B10G11R11 — that has no alpha). The masked-normalized
        // blur stores float4(premultipliedColor.rgb, accumulatedBackgroundWeight) here; the ALPHA
        // channel carries the normalization weight that Pass 1 divides by. A 3-channel format would
        // silently drop the weight -> Pass 1 divides by 0 -> NaN/black. 64bpp vs 32bpp is acceptable
        // for a single intermediate RT that only runs during active inspection.
        m_TempRT = RTHandles.Alloc(
            Vector2.one,
            TextureXR.slices,
            filterMode:      FilterMode.Bilinear,
            dimension:       TextureXR.dimension,
            colorFormat:     UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
            useDynamicScale: true,
            name:            "BackgroundBlur_TempRT"
        );
    }

    /// <summary>
    /// Called every frame when IsActive() returns true.
    /// source and destination are distinct RTHandles — no read-write hazard.
    /// </summary>
    public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
    {
        if (m_Material == null || m_TempRT == null)
            return;

        // _BlurRadius and _Intensity are identical for both passes — safe to set once.
        m_Material.SetFloat(PropBlurRadius, blurRadius.value);
        m_Material.SetFloat(PropIntensity,  intensity.value);

        // Uniform background dim (Pass 1 only reads it, but setting once is harmless).
        m_Material.SetFloat(PropBgDim, bgDim.value);

        // The inspected-object exclusion is read from the _InspectedMaskTex mask texture
        // (mask.r > 0.5 in BackgroundBlur.shader). No per-pixel param needed here — the
        // InspectedStencilPass CustomPass writes the mask; this pass binds + reads it below.

        // CRITICAL: bind textures through the CommandBuffer (cmd.SetGlobalTexture), NOT
        // m_Material.SetTexture. SetTexture is an immediate CPU write, but the two DrawFullScreen
        // calls are RECORDED and execute later — so both would read the LAST texture set. SetGlobalTexture
        // is recorded into the buffer and executes in order. (This is the canonical HDRP pattern.)

        // Resolve + bind the inspected-object mask BEFORE Pass 0 so BOTH passes see it:
        //   Pass 0 (FragBlurH) zero-weights foreground taps -> the background blur never samples the
        //     object (kills the edge halo at its source — masked-normalized convolution), and
        //   Pass 1 (FragBlurV) reads it again to composite the sharp object over the blurred background.
        // Fallback to the XR-DIMENSIONED black (Tex2DArray on D3D11) so the TEXTURE2D_X array slot is
        // always satisfied -- a plain Tex2D black would re-trigger the gray-slot mismatch. An all-black
        // mask means every tap is background -> the convolution reduces to a plain Gaussian
        // (pause/inventory blur the whole screen, unchanged).
        Texture maskTex = (InspectedStencilPass.instance != null && InspectedStencilPass.instance.MaskRT != null)
            ? (Texture)InspectedStencilPass.instance.MaskRT
            : TextureXR.GetBlackTexture().rt;
        cmd.SetGlobalTexture(PropInspectedMaskTex, maskTex);

        // ── Pass 0: Horizontal masked-normalized convolution — source -> m_TempRT ──
        // Outputs float4(premultipliedColor.rgb, accumulatedBackgroundWeight); Pass 1 normalizes once.
        // m_TempRT is R16G16B16A16_SFloat to carry the weight in alpha.
        cmd.SetGlobalTexture(PropInputTexture, source);
        HDUtils.DrawFullScreen(cmd, m_Material, m_TempRT, null, k_PassBlurH);

        // ── Pass 1: Vertical convolution + normalize + sharp-foreground composite — m_TempRT -> destination ──
        // _InputTexture    = m_TempRT (premultiplied colour + weight from Pass 0)
        // _OriginalTexture = source   (true sharp graded frame: sharp passthrough for the inspected
        //                              object + unblurred baseline for lerp(sharp, blurredBg, intensity))
        cmd.SetGlobalTexture(PropInputTexture,    m_TempRT);
        cmd.SetGlobalTexture(PropOriginalTexture, source);
        // _InspectedMaskTex already bound above (before Pass 0) — still bound for Pass 1.
        HDUtils.DrawFullScreen(cmd, m_Material, destination, null, k_PassBlurV);
    }

    /// <summary>
    /// Called when the effect is torn down (scene unload / asset reload).
    /// Must release all GPU resources acquired in Setup() and Render().
    /// </summary>
    public override void Cleanup()
    {
        CoreUtils.Destroy(m_Material);
        m_Material = null;

        ReleaseTempRT();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void ReleaseTempRT()
    {
        if (m_TempRT == null) return;
        m_TempRT.Release();
        m_TempRT = null;
    }
}

// ============================================================================
// MANUAL EDITOR STEPS (not handled by code — do these in the Unity Editor):
// Single-camera mask blur. InspectedStencilPass renders the inspected object into the
// _InspectedMaskTex mask texture; this shader reads mask.r > 0.5 to keep those pixels
// sharp. (The class is named "Stencil" for historical reasons — it uses a mask RT now.)
//
// 0. IMPORT InspectedStencilPass.cs: focus Unity / RefreshAssets so its .meta + GUID are
//    generated (a freshly-created script is unwireable until imported).
//
// 1. CREATE the "InspectedObject" layer: Project Settings > Tags and Layers. All 32 slots
//    may be full — free/repurpose a runtime-unused layer first, then name it "InspectedObject".
//
// 2. ADD + WIRE InspectedStencilPass on Player.prefab:
//    - AddComponent InspectedStencilPass.
//    - Stencil Write Shader = Assets/Shaders/MainShaders/InspectedStencilWrite.shader.
//    - Inspected Layer Mask = "InspectedObject" (+ "InspectionRaycast" so chain-examine
//      sub-elements stay sharp too). Singleton — only one instance.
//
// 3. ADD "InspectedObject" (and confirm "InspectionRaycast") to PlayerCam's Culling Mask
//    so the inspected object is rendered (and thus stamped) by PlayerCam.
//
// 4. SET PhotoMemoryPortal.crackBloomLayer = "InspectedObject" in MainHouseTesis.unity
//    (the serialized scene value overrides the code default).
//
// 5. DISABLE the Rendering Layer Mask Buffer (perf win — the old photo-exclusion path used it;
//    the _InspectedMaskTex mask replaces it; no project shader reads _RenderingLayersTexture):
//    HDRP Asset > Rendering > Rendering Layer Mask Buffer = OFF, plus any PlayerCam Custom
//    Frame Settings override of it.
//
// 6. REGISTER BackgroundBlur (likely already present): Project Settings > Graphics > HDRP
//    Global Settings > Custom Post Process Orders > After Post Process > [+] BackgroundBlur.
//
// 7. WIRE the BackgroundBlur Volume: BlurManager's m_BlurVolume → a global Volume whose profile
//    holds a BackgroundBlur override (intensity 0 = off by default).
//
// 8. TUNE blurRadius in Play (Volume override; ~6 default). InspectionRenderLayers (LightLayer7
//    renderingLayerMask) is a SEPARATE lighting-isolation concern — leave it as-is.
// ============================================================================
