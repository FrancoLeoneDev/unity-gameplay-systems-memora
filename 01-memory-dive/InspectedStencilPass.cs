// InspectedStencilPass.cs
// ============================================================================
// Memora -- Single-camera blur: script-owned mask texture for inspected objects.
//
// ROOT CAUSE of the previous blur failure (2026-06-16):
//   The old implementation wrote HDRP UserBit0 into the stencil buffer via a
//   DrawRenderersCustomPass (AfterOpaqueDepthAndNormal), then read it back from
//   _StencilTexture in BackgroundBlur.shader (AfterPostProcess). HDRP does NOT
//   rebind _StencilTexture for CustomPostProcessVolumeComponent passes at
//   AfterPostProcess (HDRenderPipeline.PostProcess.cs lines 1890-1893 only rebind
//   _CameraDepthTexture / _NormalBufferTexture / _CameraMotionVectorsTexture /
//   _CustomPostProcessInput). So _StencilTexture returned 0 everywhere and the
//   inspected object blurred with the background.
//
// THE FIX -- _InspectedMaskTex global (script-owned RenderTexture):
//   This MonoBehaviour owns a persistent R8 RenderTexture (m_MaskRT) and a global
//   CustomPassVolume (injection: AfterOpaqueAndSky) with ONE nested CustomPass
//   (InspectedMaskPass). Each frame, InspectedMaskPass:
//     1. Clears m_MaskRT to 0.
//     2. Draws the "InspectedObject" layer with InspectedStencilWrite.shader
//        (ColorMask R, ZTest Always) -- R = 1.0 where the inspected object covers.
//     3. Calls ctx.cmd.SetGlobalTexture("_InspectedMaskTex", m_MaskRT). Because
//        "_InspectedMaskTex" is never touched by HDRP internals AND the texture is a
//        plain RenderTexture (NOT RenderGraph-managed, never recycled), the binding
//        SURVIVES all the way to AfterPostProcess.
//
//   BackgroundBlur.shader (AfterPostProcess) and MemoryTransition.shader
//   (BeforePostProcess) both sample _InspectedMaskTex.r > 0.5. No stencil involved.
//
// WHY A PLAIN RenderTexture (not RTHandles.Alloc):
//   RTHandles.Alloc uses HDRP's SHARED RTHandleSystem. A persistent RTHandle held in
//   Awake trips "RTHandleSystem.Initialize ... unreleased RTHandle _InspectedMaskTex"
//   when HDRP re-initializes that system on Play enter. A plain RenderTexture owned by
//   this script has no lifecycle entanglement with HDRP and is released in OnDestroy.
//
// ALWAYS-ON / SELF-REGULATING:
//   The pass is always enabled. Each frame it clears the mask to 0, then draws whatever
//   is currently on the InspectedObject layer. Nothing on that layer (rest / pause /
//   inventory) => mask stays 0 => all pixels blur normally. SetEnabled() is a no-op kept
//   for call-site compatibility (and it fixes the latent bug where PauseMenu never
//   called SetEnabled(true)).
//
// PLACEMENT: add this component to the Player prefab (or any DDOL manager). Singleton.
//
// Design doc: plans/blur-singlecamera-marker-design.md
// ============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;   // GraphicsFormat

// NOT [ExecuteAlways]: the pass has no edit-mode purpose, and running Awake in the editor
// risks a deferred-Destroy race that adds a second CustomPassVolume onto the prefab.
public class InspectedStencilPass : MonoBehaviour
{
    public static InspectedStencilPass instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────────────────

    [Header("Mask Write Shader")]
    [Tooltip("Assign Hidden/Memora/InspectedStencilWrite (Assets/Shaders/MainShaders/). " +
             "Falls back to Shader.Find at runtime if left null.")]
    [SerializeField] private Shader stencilWriteShader;

    [Header("Layer to draw into mask")]
    [Tooltip("Must be the 'InspectedObject' Unity layer. Only objects on this layer " +
             "are drawn into the mask each frame.")]
    [SerializeField] private LayerMask inspectedLayerMask;

    // ── Runtime state ──────────────────────────────────────────────────────────────

    // R8 persistent RenderTexture. Written each frame by InspectedMaskPass, bound globally
    // as _InspectedMaskTex so shaders at any injection point can sample it. Plain
    // RenderTexture (NOT an RTHandle) to avoid HDRP RTHandleSystem lifecycle conflicts.
    private RenderTexture m_MaskRT;

    // CoreUtils.CreateEngineMaterial wrapper around stencilWriteShader.
    private Material m_MaskMat;

    // The volume is always enabled -- self-regulating via the per-frame clear.
    private CustomPassVolume m_Volume;

    // Cached shader property ID -- one allocation at Awake, reused every Execute().
    private static readonly int s_InspectedMaskTexId = Shader.PropertyToID("_InspectedMaskTex");

    // ── Lifecycle ──────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            // Destroy this component only -- never the GameObject (lives on the Player prefab
            // alongside many other components).
            Destroy(this);
            return;
        }

        instance = this;
        InitPass();
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;

        if (m_MaskMat != null)
            CoreUtils.Destroy(m_MaskMat);

        ReleaseMaskRT();
    }

    private void Update()
    {
        ResolveTargetCamera();   // retry until the player camera is resolved (see InitPass)

        // Bind the mask GLOBALLY from the MAIN THREAD (Shader.SetGlobalTexture), NOT via the custom
        // pass's cmd.SetGlobalTexture. VERIFIED at runtime: a cmd-set global inside a RenderGraph
        // custom pass does NOT persist to the AfterPostProcess custom-post-process where BackgroundBlur
        // reads it (Shader.GetGlobalTexture("_InspectedMaskTex") returned NULL there). A CPU-side
        // Shader.SetGlobalTexture is a true persistent global visible to every later pass this frame.
        EnsureMaskRT(Screen.width, Screen.height);
        if (m_MaskRT != null)
            Shader.SetGlobalTexture(s_InspectedMaskTexId, m_MaskRT);
    }

    // The mask RT, created + globally bound on the main thread (Update). The render pass draws into it.
    internal RenderTexture MaskRT => m_MaskRT;

    // ── Public API ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// No-op for call-site compatibility (InspectObject and PhotoMemoryPortal call this).
    /// The pass is always-on and self-regulating: each frame it clears the mask to 0, then
    /// draws whatever is on the InspectedObject layer. When nothing is on that layer
    /// (rest / pause), the mask stays 0 -- all pixels blur -- so "enabled" vs "disabled"
    /// make no functional difference. Also fixes the latent bug where PauseMenu never
    /// called SetEnabled(true) after SetEnabled(false) at inspection exit.
    /// </summary>
    public void SetEnabled(bool value)
    {
        // Intentional no-op. See XML doc above.
    }

    // ── Mask RenderTexture management ───────────────────────────────────────────────

    // Ensure m_MaskRT exists and matches the camera render size. Recreated on resize.
    // Called from the pass each frame (the camera size is only known during rendering).
    internal RenderTexture EnsureMaskRT(int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        if (m_MaskRT != null && m_MaskRT.width == width && m_MaskRT.height == height)
            return m_MaskRT;

        ReleaseMaskRT();

        // CRITICAL fix (the "gray screen" bug): the reader shaders declare _InspectedMaskTex as
        // TEXTURE2D_X. On D3D11 (TextureXR.useTexArray == true) that expands to Texture2DArray, and
        // SAMPLE_TEXTURE2D_X samples a slice. A plain Tex2D RenderTexture bound to that array slot =>
        // the driver substitutes its default-gray 1x1 texture => uniform ~0.5 everywhere. So the mask
        // MUST be a Tex2DArray matching TextureXR.dimension/slices -- exactly like HDRP's own camera
        // RTHandles (BackgroundBlur.m_TempRT). On non-array platforms slices==1/dimension==Tex2D too.
        var desc = new RenderTextureDescriptor(width, height, GraphicsFormat.R8_UNorm, 0)
        {
            dimension        = TextureXR.dimension,
            volumeDepth      = TextureXR.slices,
            msaaSamples      = 1,
            useMipMap        = false,
            autoGenerateMips = false,
        };
        m_MaskRT = new RenderTexture(desc)
        {
            name       = "_InspectedMaskTex",
            filterMode = FilterMode.Point,   // hard 0/1 mask, no bilinear needed
            wrapMode   = TextureWrapMode.Clamp,
        };
        m_MaskRT.Create();
        return m_MaskRT;
    }

    private void ReleaseMaskRT()
    {
        if (m_MaskRT == null) return;
        m_MaskRT.Release();
        CoreUtils.Destroy(m_MaskRT);
        m_MaskRT = null;
    }

    // ── Private ────────────────────────────────────────────────────────────────────

    private void InitPass()
    {
        // Resolve the mask-write shader.
        if (stencilWriteShader == null)
            stencilWriteShader = Shader.Find("Hidden/Memora/InspectedStencilWrite");

        if (stencilWriteShader == null)
        {
            Debug.LogError("[InspectedStencilPass] Cannot find 'Hidden/Memora/InspectedStencilWrite'. " +
                           "Ensure Assets/Shaders/MainShaders/InspectedStencilWrite.shader is compiled.");
            return;
        }

        m_MaskMat = CoreUtils.CreateEngineMaterial(stencilWriteShader);

        // Build the global CustomPassVolume on this GameObject.
        // AfterOpaqueAndSky: well before ALL post-process (BeforePostProcess AND
        // AfterPostProcess), so the global binding set here is visible to both reader shaders.
        m_Volume                = gameObject.AddComponent<CustomPassVolume>();
        // NOT isGlobal: HDRP only executes a GLOBAL CustomPassVolume when its GameObject's LAYER is in
        // the camera's volumeLayerMask (CustomPassVolume.IsVisible, line ~352 of the HDRP source). This
        // component lives on the Player (layer "Player"), which is NOT in PlayerCam's volumeLayerMask
        // -> the pass would NEVER execute (verified at runtime: execPerHalfSec=0, mask empty). Targeting
        // the camera directly takes the useTargetCamera path that BYPASSES that layer check -- the
        // working MemoryTransitionPass uses exactly this targetCamera pattern.
        m_Volume.isGlobal       = false;
        m_Volume.injectionPoint = CustomPassInjectionPoint.AfterOpaqueAndSky;
        m_Volume.priority       = 99f;   // run last among AfterOpaqueAndSky passes

        var pass = m_Volume.AddPassOfType<InspectedMaskPass>();
        pass.name        = "InspectedMaskPass";
        pass.Owner       = this;
        pass.MaskMat     = m_MaskMat;
        pass.LayerMask   = inspectedLayerMask;
        pass.MaskTexId   = s_InspectedMaskTexId;

        m_Volume.enabled = true;
        ResolveTargetCamera();
    }

    // Point the CustomPassVolume at the player camera (NOT isGlobal) so it bypasses the volumeLayerMask
    // check in CustomPassVolume.IsVisible. Retried from Update because Camera.main / the child camera may
    // not be resolvable yet at Awake.
    private void ResolveTargetCamera()
    {
        if (m_Volume == null || m_Volume.targetCamera != null)
            return;

        Camera cam = null;
        foreach (var c in GetComponentsInChildren<Camera>(true))
            if (c.gameObject.name == "PlayerCam") { cam = c; break; }
        if (cam == null)
            cam = Camera.main;
        if (cam != null)
            m_Volume.targetCamera = cam;   // setter also sets useTargetCamera = true
    }

    // ── Nested CustomPass ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inner pass that draws the InspectedObject layer into the owner's mask RenderTexture
    /// each frame, then binds it globally as _InspectedMaskTex.
    /// </summary>
    private class InspectedMaskPass : CustomPass
    {
        internal InspectedStencilPass Owner;
        internal Material  MaskMat;
        internal LayerMask LayerMask;
        internal int       MaskTexId;

        // CRITICAL: CustomPassUtils.DrawRenderers does NOT re-cull -- it reuses the volume's
        // shared cull results. CustomPassVolume.Cull seeds the cull mask to 0 and only includes
        // what each pass aggregates here (the default AggregateCullingParameters is a no-op). So
        // without this override, the "InspectedObject" layer reaches the renderer list ONLY if it
        // happens to be in the camera's culling mask. OR our layers into the cull so the inspected
        // object is ALWAYS drawn into the mask -- canonical HDRP pattern, mirrors
        // DrawRenderersCustomPass.AggregateCullingParameters.
        protected override void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCamera hdCamera)
        {
            cullingParameters.cullingMask |= (uint)(int)LayerMask;
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (Owner == null || MaskMat == null)
                return;

            // The mask RT is created + globally bound on the main thread (Update).
            var maskRT = Owner.MaskRT;
            if (maskRT == null || !maskRT.IsCreated())
                return;

            // Step 1: bind the mask as the render target and clear it to 0. No depth target:
            // the mask shader uses ZTest Always (the inspected object is held in front of the
            // camera, so occlusion testing is unnecessary -- and skipping depth avoids any
            // RTHandle-vs-RenderTexture size mismatch with the camera depth buffer).
            ctx.cmd.SetRenderTarget(maskRT);
            ctx.cmd.ClearRenderTarget(false, true, Color.clear);   // clearDepth:false, clearColor:true

            // Step 2: draw the InspectedObject layer with the mask-write material.
            // CustomPassUtils.DrawRenderers draws to the currently-bound target (maskRT).
            // litForwardTags (Forward + ForwardOnly + SRPDefaultUnlit) covers Lit AND Unlit.
            CustomPassUtils.DrawRenderers(
                ctx,
                LayerMask,
                renderQueueFilter:     RenderQueueType.AllOpaque,
                overrideMaterial:      MaskMat,
                overrideMaterialIndex: 0);

            // Step 3: bind the filled mask globally. Survives to AfterPostProcess because the
            // name is custom (HDRP never overwrites it) and the RT is not RenderGraph-managed.
            ctx.cmd.SetGlobalTexture(MaskTexId, maskRT);
        }
    }
}
