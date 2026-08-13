// Implements: refactor-input-system-memora.md — Cluster E1
// Input: EnterMemoryHeld (replaces Input.GetKey(KeyCode.Space)).
// No Hold interaction on the action — InputHandler.EnterMemoryHeld uses IsPressed() so this
// class keeps full control of holdTimer, commitThreshold, and all per-frame FX.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using DG.Tweening;

public class PhotoMemoryPortal : MonoBehaviour
{
    public static event Action OnMemoryEntryFromUI;

    [Header("Hold Settings")]
    [SerializeField] private float holdDuration = 3.0f;   // tightened (was 3.5) — kills dead air at the end
    [SerializeField] private float frontFaceThreshold = -0.3f;
    // Commit point (point of no return): once progress passes this, the black cut fires.
    // 0.96 lands just AFTER the flood peak (pFloodEnd 0.90) so the flood fully plays out.
    [SerializeField] private float commitThreshold = 0.96f;
    // Hold-start: the photo squares up to face the camera (DOTween) before concentration begins.
    [SerializeField] private float alignDuration = 0.3f;
    // White flood (climax): the volumetric white fog fills the space over
    // [whiteFloodStart, commitThreshold] of the hold; the curtain then hands off the white.
    // 0.5 on a ~3.5s hold = ~1.6s of fill (the old 0.78 gave only ~0.6s = too abrupt).
    // The fog's internal staging (seep → build → whiteout) lives in the shader; this drive is
    // LINEAR. The curtain HOLD + fade-out length live on SceneControllerManager.
    [SerializeField] private float whiteFloodStart = 0.5f;

    [Header("Vision Invertida — Grade Profile (pushed to MemoryTransitionPass at hold start)")]
    // The authoritative timing/intensity profile lives HERE on the portal so it overrides
    // whatever the live MemoryTransitionPass component has serialized in the scene.
    [SerializeField] private float pOpenEnd  = 0.18f;
    [SerializeField] private float pStripEnd = 0.42f;
    [SerializeField] private float pVoidEnd  = 0.55f;
    [SerializeField] private float pFloodEnd = 0.90f;
    [SerializeField] private float gradeVignetteOpen      = 0.0f;   // periphery opens fully
    [SerializeField] private float gradePostExpStrip      = 0.60f;  // void darker → photo pops
    [SerializeField] private float gradeSatStripFloor     = 0.12f;  // deeper grey
    [SerializeField] private float gradeGrainPeak         = 0.022f; // visible emulsion grain
    [SerializeField] private float gradeArrivalOversat    = 1.25f;  // punchier flood
    [SerializeField] private float gradeChannelOffsetMaxPx = 7f;
    [SerializeField] private float gradeFloodCurveExp     = 0.7f;
    [SerializeField] private Color gradeOffsetTintWarm = new Color(0.831f, 0.784f, 0.659f, 1f); // #D4C8A8
    [SerializeField] private Color gradeOffsetTintCool = new Color(0.722f, 0.831f, 0.784f, 1f); // #B8D4C8

    [SerializeField] private bool  photoGlowEnabled = true;    // hold at peak through cut

    [Header("Photo RESPLANDOR — Crack-Glow (jagged crack of light on the photo)")]
    // The overlay draws a thin jagged crack at the photo center that glows with
    // a contained warm-dirty-white light (per CD ruling 2026-06-06). Three glow
    // bands (core/mid/skin) sum under additive One/One. No post-processing bloom —
    // density is authored in-shader (core clips >1 in additive = white at seam).
    //
    // Design: the print FAILS, splits, and warm light bleeds from the wound.
    // NOT a sunburst, NOT a portal. See plans/diseno-transicion-recuerdo.md.
    //
    // Calibration order after centering is confirmed:
    //   1. crackCoreW      — thin vs smear (#1 priority)
    //   2. crackSkinW      — "un poco" master dial (keep <= 0.08)
    //   3. crackCoreInt    — density (let it clip above 1.0 — that's the point)
    //   4. crackJagAmp     — reads as a fracture vs a straight slit
    //   5. crackLenMax     — how far crack propagates (M1=floor, M3=ceiling)

    [SerializeField] private bool  photoHaloEnabled = true;
    [SerializeField] private bool  photoHaloFlipY = false;

    // ── Crack onset ───────────────────────────────────────────────────────────
    [SerializeField] private float crackGrowStart = 0.10f;   // sp fraction where crack first appears

    // ── Color (single tint — warm-dirty white, NOT pure/gold) ────────────────
    // CD ruling: ~(1.0, 0.97, 0.92) — biased to photo amber, reads warm/structural.
    // Pure white is reserved for the exit corridor and the final flood.
    [SerializeField] private Color crackColor = new Color(1f, 0.97f, 0.92f, 1f);

    // ── Crack centering: OBJECT-SPACE (no serialized center — computed at runtime) ─
    // pCube1 has non-linear/curled UVs, so UV(0.5,0.5) is NOT the photo's visual
    // center. We center the crack on the mesh's geometric center (bounds.center)
    // in OBJECT space, computed per-photo in SetupPhotoGlow → guaranteed centered.
    // crackSwap flips the length/width axes if a photo reads rotated
    // (default 0 = crack length runs along the longer/X axis = portrait vertical).
    [SerializeField] private float crackSwap = 0f;   // 0 = length along X, 1 = swap to Z

    // ── Crack shape (units are NORMALIZED object-space: 1.0 == photo half-extent) ──
    [SerializeField] private float crackJagAmp     = 0.06f;  // lateral fracture wander (#1 "reads as crack" dial)
    [SerializeField] private float crackJagFreq    = 3.0f;   // kink frequency along the crack

    // Propagation: crack grows from lenMin to lenMax over the hold (1.0 = photo edge).
    [SerializeField] private float crackLenMin     = 0.04f;  // hairline seed at grow=0
    [SerializeField] private float crackLenMax     = 0.65f;  // length at peak (stays inside the photo)
    [SerializeField] private float crackLenFeather = 0.10f;  // tip taper

    // ── Glow band 1: CORE (fracture spine — clips to HDR white = dense) ──────────
    [SerializeField] private float crackCoreW   = 0.015f;   // thin bright spine half-width
    [SerializeField] private float crackCoreExp = 5.0f;     // knife-edge sharpness
    [SerializeField] private float crackCoreInt = 4.0f;     // HDR brightness (clips = dense white)

    // ── Glow band 2: MID (body — gives the white volume) ─────────────────────────
    [SerializeField] private float crackMidW   = 0.06f;
    [SerializeField] private float crackMidExp = 3.0f;
    [SerializeField] private float crackMidInt = 1.3f;

    // ── Glow band 3: SKIN (the WHITE FOG that bleeds OUT of the crack) ───────────
    // This is the visible "fog blanco que sale de la grieta" — wider + brighter
    // than the earlier timid halo. The user explicitly wants visible white fog.
    [SerializeField] private float crackSkinW   = 0.20f;    // fog width (fraction of half-extent)
    [SerializeField] private float crackSkinExp = 2.0f;
    [SerializeField] private float crackSkinInt = 0.7f;     // fog brightness

    // ── Sparkles (glints along the seam as it opens — see PhotoPortalSplit shader) ──
    // Tech-art ruling: keep LOW. Many tiny constant glints = fairy dust (wrong).
    // Few/rare flashes = debris spitting from a wound (right). Density 4-6 sites.
    [Header("Crack Sparkles")]
    [SerializeField] private float sparkleAmount  = 0.3f;
    [SerializeField] private float sparkleDensity = 5f;
    [SerializeField] private float sparkleSpeed   = 1.2f;   // slow = embers spitting from a wound (not fairy-dust twinkle)
    [SerializeField] private float sparkleSeamW   = 0.016f;   // radial OFFSET band for glints (push out of the bright core)
    [SerializeField] private float sparkleCorona  = 0.008f;   // individual glint radius (tight = pinpoint)

    [Header("Crack Núcleo + Rays (central white starburst — Tom Riddle diary)")]
    // Defaults calibrated by the art-director review (2026-06-07): 12 rays read as lens diffraction
    // (not a digital 8-ray asterisk), a tight corona that does not swamp the photo, imperceptible
    // rotation over a 3s hold, a sharp HDR core that Bloom turns into the halo, pulse that BUILDS.
    [SerializeField] private float coreInt        = 12f;     // HDR core brightness — drives HDRP Bloom (calibrate vs scene exposure)
    [SerializeField] private float coreRadius     = 0.05f;
    [SerializeField] private float coreFalloff    = 4.5f;    // sharp spike; Bloom makes the shoulder
    [SerializeField] private float corePulseAmp   = 0.35f;   // PEAK pulse amplitude at full grow (ramps up from ~0.10)
    [SerializeField] private float corePulseSpeed = 9.4f;    // ~1.5 Hz base heartbeat
    [SerializeField] private float rayIntensity   = 0.40f;   // corona, not protagonist
    [SerializeField] private float rayCount       = 12f;     // lens diffraction, not a digital octagon
    [SerializeField] private float raySharpness   = 7f;
    [SerializeField] private float raySpeed       = 0.06f;   // imperceptible drift over a 3s hold
    [SerializeField] private float rayInnerFade   = 0.04f;
    [SerializeField] private float rayEnvRadius   = 0.24f;   // tight corona around the núcleo only
    // Núcleo + ray tint. PURE WHITE by default (user ruling). Optional escalation: dial a
    // barely-perceptible warm (e.g. 1, 1, 0.97) for the BUILD so the flood (always pure white)
    // lands as a colour-temperature beat. The flood is unaffected by this tint.
    [SerializeField] private Color crackCoreTint  = Color.white;

    [Header("Photo Room Light (REAL volumetric point light — the light pours from the photo)")]
    // A runtime-spawned shadowless HDRP point light between the photo and the camera, ramped
    // over the climax window. The scene's volumetric fog (already enabled) gives it a REAL
    // glowing scattering ball + physically lights the B&W room white. Default light layer
    // (bit 0) lights the ROOM but not the photo (photo renderers are on rendering layer bit 7).
    // Look spec 2026-06-09: heartbeat pulse during the build, flatline at the curtain (the
    // "heart stops" beat), warm→pure-white colour ramp (personal → transcendent).
    [SerializeField] private bool  photoLightEnabled         = true;
    [SerializeField] private float photoLightPeakLumens      = 10000f; // #1 dial: 8000-20000 = how blinding
    [SerializeField] private float photoLightRange           = 6f;     // meters — covers the inspection alcove
    [SerializeField] private float photoLightVolumetricDimmer = 1.5f;  // >1 boosts the visible scattering ball
    [SerializeField] private float photoLightCameraOffset    = 0.2f;   // meters toward camera (mesh won't occlude the ball)
    [SerializeField] private Color photoLightWarmColor       = new Color(1f, 0.77f, 0.54f); // ~3800K — the memory's warmth
    [SerializeField] private float photoLightPulseAmp        = 0.12f;  // heartbeat ±12% during the build
    [SerializeField] private float photoLightPulseHz         = 1.5f;   // matches the crack núcleo heartbeat

    // ── GLOW HALO (the "resplandor" rendered IN FRONT of the photo) ──────────────
    // The point light's volumetric resplandor lives on PlayerCam → behind the BlurCam photo.
    // This is a SECOND additive quad (reusing the PhotoPortalSplit shader as a pure WIDE soft
    // núcleo — a wide radius reads soft WITHOUT bloom, which BlurCam lacks), bigger than the photo,
    // sitting just in front of the crack quad → BlurCam composites it OVER the photo. The behind
    // point-light fog ball is killed (volumetricDimmer=0) so the resplandor only shows in front.
    [Header("Glow Halo (resplandor IN FRONT of the photo)")]
    [SerializeField] private bool  photoGlowHaloEnabled = true;
    [SerializeField] private float photoHaloScale       = 1.6f;   // quad size vs photo face (the halo spills past the edges)
    [SerializeField] private float photoHaloCoreInt     = 2.5f;   // peak glow intensity (subtle — the resplandor is soft)
    [SerializeField] private float photoHaloCoreRadius  = 0.55f;  // WIDE radius → soft glow without bloom
    [SerializeField] private Color photoHaloTint        = new Color(1f, 0.88f, 0.72f, 1f); // warm resplandor
    [SerializeField] private float photoHaloSpCap       = 0.84f;  // drive sp up to here (below flood 0.85) → glow, never whiteout

    // ── FRONT FILL FLOOD (the climax whiteout, rendered IN FRONT of the photo) ───
    // A THIRD additive quad (reuses PhotoPortalSplit via a new _FillProgress radial block),
    // sized to the viewport, at renderQueue 3002 (in front of crack 3001 + halo 3000). At the
    // climax a billowy white wave pours from the crack and fills the screen — replacing the
    // old behind-camera fog (which was the "luz detrás"). The crack/núcleo/rays stay the star
    // during the build; this only ignites from fillFloodStart onward.
    [Header("Front Fill Flood (climax white-light fill — covers photo + fills screen, IN FRONT)")]
    [SerializeField] private bool  photoFillFloodEnabled = true;
    [SerializeField] private float fillFloodStart        = 0.72f;  // linearProgress where the fill begins expanding
    [SerializeField] private float fillFloodPeakInt      = 18f;    // peak additive brightness (HDR; clamps to white)
    [SerializeField] private Color fillFloodTint         = new Color(1f, 1f, 1f, 1f); // PURE WHITE — same as the rest of the effect's light
    [SerializeField] private float fillFloodScreenMargin = 1.3f;   // quad oversize vs the viewport (corner coverage)

    // ── Realistic bloom (Option B — relayer) ────────────────────────────────────
    // The crack's HDR emissive only blooms when rendered by PlayerCam (which owns the
    // HDRP Bloom). At hold-start the photo + crack are relayered to crackBloomLayer and
    // the background blur is faded so the now-PlayerCam-rendered photo is not blurred.
    [Header("Crack Realistic Bloom (relayer to PlayerCam)")]
    [SerializeField] private bool   crackBloomEnabled = true;
    // Single-camera: the photo stays on "InspectedObject" through the transition — PlayerCam renders
    // it (so the crack's HDR emissive blooms) AND InspectedStencilPass stamps it (so MemoryTransition
    // excludes it from the B&W drain). One layer, both jobs. (Was "Default" under the old 2-camera path.)
    [SerializeField] private string crackBloomLayer = "InspectedObject"; // PlayerCam renders + blooms + stencil-stamped
    [SerializeField] private float  crackEmissiveExposureWeight = 0f; // 0 = supernatural (ignore scene exposure)

    [Header("Despierta / Membrana — the memory pressing through the photo")]
    // Replaces the burn (which read as mush on busy photos). The membrane between the print
    // and the memory THINS: a domain-warp shimmer + a single saturation swell on the photo,
    // synced to the emerging sound. Global/uniform → reads identically on every photo.
    // The curve is ONE swell + plateau: sub-perceptual early (the SOUND does the work),
    // "too much" held at the end → resolved by the hard cut (NOT a breathing loop — that's
    // the cliché). Designed by the 4-agent deep pass.
    [SerializeField] private AnimationCurve photoAwakenCurve = new AnimationCurve(
        new Keyframe(0f,    0f),
        new Keyframe(0.12f, 0.06f),   // sub-perceptual — the sound carries this phase
        new Keyframe(0.40f, 0.55f),   // building, becoming detectable
        new Keyframe(0.70f, 0.92f),   // clearly alive
        new Keyframe(0.90f, 1.0f),    // plateau — "too much", held past comfort
        new Keyframe(1f,    1.0f));   // → resolved by the hard cut
    [SerializeField] private int   holdFrameRateCap   = 60;    // cap fps during the hold (project runs uncapped)

    [Header("Memory Emerge Audio (sound emerging FROM the photo)")]
    // The photo WHISPERS its memory the WHOLE time it is inspected (spawned at photo-context-set,
    // before any hold): barely audible, heavily muffled — the subliminal lure that invites the
    // player to "listen closer" (= hold). The hold swells whisper → full along photoAwakenCurve.
    // Releasing before commit ramps BACK to the whisper (the memory never dies mid-inspection);
    // exiting inspection kills it. Completed photos never activate the portal → spent memories
    // stay silent.
    [SerializeField] private float photoSoundTargetVolume  = 0.8f;
    [SerializeField] private float emergeWhisperVolume     = 0.10f; // "muy bajita" while just inspecting
    [SerializeField] private float emergeWhisperReturnTime = 0.6f;  // release → back-to-whisper ramp
    // After the end-of-transition fade reaches 0, the muted source lives this long so the
    // reverb's wet tail (5s decay) rings out instead of being truncated (the audible "cut").
    [SerializeField] private float emergeReverbTailGrace   = 2.5f;
    [SerializeField] private UnityEngine.Audio.AudioMixerGroup photoSoundMixerGroup; // optional routing (null = default)

    [Header("Memory Emerge Audio — Ghost FX")]
    // Whether the runtime ghost chain (HP/LP/chorus/reverb) is added is PER-PHOTO data:
    // PhotoData.EmergenceClipNeedsGhostFx (the raw-vs-baked nature belongs to the clip, not
    // to this system). The dials below only apply when that flag is ON for the held photo.
    // Chorus→Reverb chain on the emerge source. Chain order on the GameObject:
    // AudioHighPassFilter → AudioLowPassFilter → AudioChorusFilter → AudioReverbFilter.
    // Unity processes DSP filters in component add-order, so the chain is:
    //   source → highpass → lowpass → chorus → reverb  (thins body, then muffles, then tails).
    [SerializeField] private float emergeMaxCutoff          = 3200f;   // emerge-only LP ceiling — voices never fully clarify
    [SerializeField] private float emergeMinCutoff          = 500f;    // emerge LP floor (decoupled from camera minLowPassCutoff)
    [SerializeField] private float emergeHighPassCutoff     = 180f;    // thins the body — "disembodied" memory
    [SerializeField] private float emergeHighPassResonanceQ = 0.8f;
    [SerializeField] private float emergePitchLFOAmplitude  = 0.008f;  // wow/flutter depth (ratio)
    [SerializeField] private float emergePitchLFORate       = 0.11f;   // wow/flutter rate (Hz)
    [SerializeField] private float emergeReverbDecayTime    = 5.0f;
    [SerializeField] private float emergeReverbDecayHFRatio = 0.35f;
    [SerializeField] private float emergeReverbRoom         = -500f;
    [SerializeField] private float emergeReverbRoomHF       = -2000f;
    [SerializeField] private float emergeReverbReflections  = -3500f;
    [SerializeField] private float emergeReverbWetLevel     = -600f;
    [SerializeField] private float emergeReverbDryLevel     = -2000f;
    [SerializeField] private float emergeChorusRate         = 0.08f;
    [SerializeField] private float emergeChorusDepth        = 0.08f;
    [SerializeField] private float emergeChorusWetMix       = 0.18f;
    [SerializeField] private float emergePitch              = 0.965f;

    [Header("Blur")]
    [SerializeField] private float maxBlurIntensity = 6f;

    [Header("Camera Concentration")]
    [SerializeField] private float fovDrop = 5f;
    [SerializeField] private float zBreathingAmplitude = 0.015f;
    [SerializeField] private float zBreathingSpeed = 2.5f;
    [SerializeField] private float microTiltMaxDegrees = 1f;
    [SerializeField] private float microTiltSpeed = 1.8f;

    [Header("Photo Pulsation")]
    [SerializeField] private float pulsationAmplitude = 0.005f;
    [SerializeField] private float pulsationSpeed = 1.5f;

    [Header("Photo Split (Doors of Light) — climax overlay")]
    // Guard: if false (or if anything is null), the split overlay is never spawned —
    // the existing resplandor/commit flow continues unaffected.
    [SerializeField] private bool photoSplitEnabled = true;
    // Assign the Memora/PhotoPortalSplit shader here in the Inspector (build-safe).
    // Falls back to Shader.Find("Memora/PhotoPortalSplit") when null (editor only).
    [SerializeField] private Shader photoSplitShader;

    [Header("Audio")]
    [SerializeField] private AudioClip holdSound;
    [SerializeField] private AudioSource audioSource;

    [Header("Audio Low Pass")]
    [SerializeField] private float minLowPassCutoff = 800f;
    [SerializeField] private float maxLowPassCutoff = 22000f;

    private InspectObject inspectObject;

    private bool isActive;
    private PhotoData currentPhotoData;
    private bool currentIsUIMode;
    private float holdTimer;
    private bool isHolding;
    private bool isEnteringMemory;
    private bool blinkTriggered;
    private HintInstance enterMemoryHint;

    // Front-face detection
    private Transform cachedFaceTransform;
    private Vector3 cachedLocalFaceNormal;

    // Camera concentration state
    private Camera playerCamera;
    private Transform cameraTransform;
    private float originalFOV;
    private Vector3 originalCameraLocalPos;
    private Quaternion originalCameraLocalRot;

    // Dead-freeze state: at the void the camera/photo oscillators FREEZE (stillness amplifies
    // the silence). We capture the pose at void-entry and hold it hard through the cut.
    private bool cameraFrozen;
    private Vector3 frozenCamLocalPos;
    private Quaternion frozenCamLocalRot;
    private float frozenFOV;
    private Vector3 frozenPhotoScale;
    private Vector3 frozenPhotoLocalPos;  // captures grown Z-push pose at pVoidEnd

    // Photo pulsation + engulf state
    private Transform inspectedPhotoTransform;
    private Vector3 originalPhotoScale;
    private Vector3 originalPhotoLocalPos;  // cached at hold-start for Z-push restore

    // Audio low pass
    private AudioLowPassFilter lowPassFilter;

    // Last screen-space UV of the photo center (viewport coords, updated each frame in ApplyPhotoHalo).
    // Cached for use by the commit-flood step (future commit — not consumed here yet).
    private Vector2 lastPhotoScreenUV;

    // Photo glow state (MPB used only by the emissive fallback path in ApplyPhotoHalo; never for the decay)
    private Renderer[] photoGlowRenderers;

    // Doors of Light split overlay state (created at hold-start, destroyed at teardown).
    // Null when photoSplitEnabled=false or shader is missing — safe to skip in Apply/Reset.
    private GameObject splitOverlayGO;
    private Material   splitOverlayMaterial;
    private GameObject glowHaloGO;       // the "resplandor" quad — additive, IN FRONT of the photo
    private Material   glowHaloMaterial;
    private GameObject fillFloodGO;       // the climax whiteout quad — additive, IN FRONT, fills the screen
    private Material   fillFloodMaterial;

    // ── Crack-bloom relayer state (Option B — validated 2026-06-06) ──────────────
    // The crack's HDR emissive only blooms when rendered by PlayerCam (which owns the
    // HDRP Bloom). During the hold the photo + crack overlay are moved from the inspect
    // layer ("RenderAbove", composited by the post-less BlurCam) to a PlayerCam-rendered
    // layer so the crack blooms. Restored on teardown. -1 == not currently relayered.
    private int crackBloomOriginalLayer = -1;

    // Per-hold cached state for escalation + accessibility + perf.
    private float activeHoldDuration;
    private float motionScale = 1f;          // 0 under reduced-motion (kills sustained vestibular FX)
    private bool  frameRateCapped;
    private int   previousTargetFrameRate;
    // Photo texture property IDs (used by the split overlay to bind the photo texture)
    private static readonly int PropBaseColorMap    = Shader.PropertyToID("_BaseColorMap");
    private static readonly int PropMainTex         = Shader.PropertyToID("_MainTex");
    private static readonly int PropFillProgress    = Shader.PropertyToID("_FillProgress");
    // Split overlay property IDs — crack-glow dials, all pushed once at hold-start.
    private static readonly int PropSplitProgress        = Shader.PropertyToID("_SplitProgress");
    private static readonly int PropSplitCrackNoiseSeed  = Shader.PropertyToID("_CrackNoiseSeed");
    private static readonly int PropCrackCenterOS        = Shader.PropertyToID("_CrackCenterOS");
    private static readonly int PropCrackHalfX           = Shader.PropertyToID("_CrackHalfX");
    private static readonly int PropCrackHalfZ           = Shader.PropertyToID("_CrackHalfZ");
    private static readonly int PropCrackSwap            = Shader.PropertyToID("_CrackSwap");
    private static readonly int PropCrackColor           = Shader.PropertyToID("_CrackColor");
    private static readonly int PropCrackGrowStart       = Shader.PropertyToID("_CrackGrowStart");
    private static readonly int PropCrackJagAmp          = Shader.PropertyToID("_CrackJagAmp");
    private static readonly int PropCrackJagFreq         = Shader.PropertyToID("_CrackJagFreq");
    private static readonly int PropCrackLenMin          = Shader.PropertyToID("_CrackLenMin");
    private static readonly int PropCrackLenMax          = Shader.PropertyToID("_CrackLenMax");
    private static readonly int PropCrackLenFeather      = Shader.PropertyToID("_CrackLenFeather");
    private static readonly int PropCrackCoreW           = Shader.PropertyToID("_CrackCoreW");
    private static readonly int PropCrackCoreExp         = Shader.PropertyToID("_CrackCoreExp");
    private static readonly int PropCrackCoreInt         = Shader.PropertyToID("_CrackCoreInt");
    private static readonly int PropCrackMidW            = Shader.PropertyToID("_CrackMidW");
    private static readonly int PropCrackMidExp          = Shader.PropertyToID("_CrackMidExp");
    private static readonly int PropCrackMidInt          = Shader.PropertyToID("_CrackMidInt");
    private static readonly int PropCrackSkinW           = Shader.PropertyToID("_CrackSkinW");
    private static readonly int PropCrackSkinExp         = Shader.PropertyToID("_CrackSkinExp");
    private static readonly int PropCrackSkinInt         = Shader.PropertyToID("_CrackSkinInt");
    private static readonly int PropSparkleAmount        = Shader.PropertyToID("_SparkleAmount");
    private static readonly int PropSparkleDensity       = Shader.PropertyToID("_SparkleDensity");
    private static readonly int PropSparkleSpeed         = Shader.PropertyToID("_SparkleSpeed");
    private static readonly int PropSparkleSeamW         = Shader.PropertyToID("_SparkleSeamW");
    private static readonly int PropSparkleCorona          = Shader.PropertyToID("_SparkleCorona");
    // Núcleo + rays property IDs — central white starburst, pushed once at hold-start.
    private static readonly int PropCoreInt             = Shader.PropertyToID("_CoreInt");
    private static readonly int PropCoreRadius          = Shader.PropertyToID("_CoreRadius");
    private static readonly int PropCoreFalloff         = Shader.PropertyToID("_CoreFalloff");
    private static readonly int PropCorePulseAmp        = Shader.PropertyToID("_CorePulseAmp");
    private static readonly int PropCorePulseSpeed      = Shader.PropertyToID("_CorePulseSpeed");
    private static readonly int PropRayIntensity        = Shader.PropertyToID("_RayIntensity");
    private static readonly int PropRayCount            = Shader.PropertyToID("_RayCount");
    private static readonly int PropRaySharpness        = Shader.PropertyToID("_RaySharpness");
    private static readonly int PropRaySpeed            = Shader.PropertyToID("_RaySpeed");
    private static readonly int PropRayInnerFade        = Shader.PropertyToID("_RayInnerFade");
    private static readonly int PropRayEnvRadius        = Shader.PropertyToID("_RayEnvRadius");
    private static readonly int PropCoreTint            = Shader.PropertyToID("_CoreTint");
    private static readonly int PropEmissiveExposureWeight = Shader.PropertyToID("_EmissiveExposureWeight");

    // Memory emerge audio state (ephemeral — created at hold-start, destroyed at teardown)
    private AudioSource          emergeSource;
    private AudioLowPassFilter   emergeLowPass;
    private AudioHighPassFilter  emergeHighPass;
    private AudioReverbFilter    emergeReverb;
    private AudioChorusFilter    emergeChorus;
    private float                emergeLogMinCutoff;
    private float                emergeLogMaxCutoff;
    // Release→whisper ramp (tracked so a re-hold can cancel it cleanly).
    private Coroutine            whisperReturnCo;

    // Hold-start alignment state: true while the photo tweens to face the camera (Phase 1).
    private bool                 isAligning;

    // Photo room light state (ephemeral — spawned at hold-start, destroyed at teardown).
    private GameObject            photoLightGO;
    private Light                 photoLightComp;
    private HDAdditionalLightData photoLightHD;

    private void Awake()
    {
        inspectObject = GetComponent<InspectObject>();
    }

    private void OnEnable()
    {
        if (inspectObject != null)
        {
            inspectObject.OnPhotoContextSet += HandlePhotoContextSet;
            inspectObject.OnExamineEnded += HandleExamineEnded;
        }
    }

    private void OnDisable()
    {
        if (inspectObject != null)
        {
            inspectObject.OnPhotoContextSet -= HandlePhotoContextSet;
            inspectObject.OnExamineEnded -= HandleExamineEnded;
        }
        Cleanup();

        // Guaranteed framerate-cap restore. Cleanup() early-returns while isEnteringMemory,
        // so the 60fps hold cap could otherwise leak for the rest of the session if this
        // component is disabled during the commit window. Restore unconditionally here.
        if (frameRateCapped)
        {
            Application.targetFrameRate = previousTargetFrameRate;
            frameRateCapped = false;
        }

        // Fallback: if disabled mid-transition the handed-off emerge audio + its subscription would
        // leak (Cleanup early-returns while isEnteringMemory). Tear them down unconditionally.
        if (SceneControllerManager.instance != null)
            SceneControllerManager.instance.OnPhotoRevealStarted -= HandleRevealStarted;
        ResetMemoryEmergeAudio();
    }

    private void HandlePhotoContextSet(PhotoData photoData, bool isUI)
    {
        if (isEnteringMemory) return;

        if (photoData == null || !photoData.HasMemoryScene)
            return;

        if (Inventory.instance != null && Inventory.instance.IsPhotoCompleted(photoData.ID))
            return;

        if (SceneControllerManager.instance != null && SceneControllerManager.instance.IsInMemoryScene)
            return;

        isActive = true;
        currentPhotoData = photoData;
        currentIsUIMode = isUI;

        CacheFrontFaceNormal();

        // The memory starts WHISPERING the moment the photo is in the player's hands —
        // before any hold. The hold (ApplyMemoryEmergeAudio) swells it from here.
        SetupMemoryEmergeAudio();

        if (TutorialManager.instance != null && !TutorialManager.instance.FirstPhotoExamined)
        {
            inspectObject.LockExit(true);
            // UIButtonsManager es scene-local (su DontDestroyOnLoad está comentado a propósito), así que
            // durante una transición de escena .instance puede ser null: mismo guard que el resto del archivo.
            if (UIButtonsManager.instance != null)
                UIButtonsManager.instance.ShowHints(inspectObject.GetHintDisplayDataList());
        }
    }

    private void HandleExamineEnded()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        if (isEnteringMemory) return;

        if (isAligning)
            AbortAlign();

        if (isHolding)
        {
            if (inspectObject != null)
                inspectObject.PauseRotation(false);

            StopHoldAudio();
        }

        ResetConcentrationEffects();
        ResetMemoryEmergeAudio();   // cancel/exit: stop the emerge sound (commit keeps it playing)
        ResetCameraEffects();
        ResetBlurIntensity();
        RemoveHint();

        // Defensive: ensure the stencil stamp is off when inspection fully ends. (The commit path
        // early-returns above on isEnteringMemory and turns it off in EnterMemoryWhileBlack instead;
        // a cancelled hold does NOT reach Cleanup, so the stamp correctly stays on for the return.)
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(false);

        isActive = false;
        holdTimer = 0f;
        isHolding = false;
        isAligning = false;
        blinkTriggered = false;
        currentPhotoData = null;
        currentIsUIMode = false;
        cachedFaceTransform = null;
    }

    private void Update()
    {
        if (!isActive || isEnteringMemory) return;
        if (!inspectObject.ExamineProp) return;

        Transform inspectedTransform = inspectObject.InspectedObjectTransform;
        if (inspectedTransform == null) return;

        // Front-face detection (shared logic via InspectObject)
        Vector3 worldFrontFace;
        if (cachedFaceTransform != null)
            worldFrontFace = cachedFaceTransform.TransformDirection(cachedLocalFaceNormal);
        else
            worldFrontFace = inspectedTransform.TransformDirection(currentPhotoData.FrontFaceDirection);

        float faceDot = inspectObject.GetFaceDot(worldFrontFace);
        bool isFrontFacing = faceDot > frontFaceThreshold;


        // Hint management
        if (isFrontFacing && enterMemoryHint == null)
        {
            HintDisplayData hintData = HintDisplayData.Make(HintType.EnterMemory, HintIconType.SpaceIcon);
            if (UIButtonsManager.instance != null)
                enterMemoryHint = UIButtonsManager.instance.AddAdditionalHint(hintData);
        }
        else if (!isFrontFacing && enterMemoryHint != null)
        {
            RemoveHint();
        }

        // Hold mechanic — EnterMemoryHeld is IsPressed() (no Hold interaction); this class owns the timer.
        bool spaceHeld = (InputHandler.instance != null && InputHandler.instance.EnterMemoryHeld) && isFrontFacing;

        if (spaceHeld && !isHolding && !isAligning)
        {
            // Phase 1 — ALIGN: the photo squares up to face the camera (DOTween) BEFORE the
            // concentration begins, so the crack + radial flood are dead-centre. BeginHold() runs
            // in the align callback. Already-square photos skip instantly (AlignFaceToCamera
            // early-outs, callback same frame). Reduced-motion = instant snap (dur 0).
            isAligning = true;
            inspectObject.PauseRotation(true);
            StartSquareAlign(alignDuration);
        }
        else if (spaceHeld && isHolding)
        {
            // Continue holding — apply all effects
            holdTimer += Time.deltaTime;
            float linearProgress = Mathf.Clamp01(holdTimer / activeHoldDuration);

            ApplyConcentrationEffects(linearProgress);

            // Commit at threshold — once triggered, committed to entering memory (point of no return)
            if (linearProgress >= commitThreshold && !blinkTriggered)
            {
                blinkTriggered = true;
                isEnteringMemory = true;

                // NOTE: do NOT turn the grade off here — let it hold at peak through the
                // black cut. ForceOff happens under black in EnterMemoryWhileBlack (no flash).
                ResetBlurIntensity();

                StopHoldAudio();
                StartCoroutine(EnterMemoryWhileBlack());
            }
        }
        else if (!spaceHeld && isHolding && !blinkTriggered)
        {
            // Cancel — player released before commitment point
            isHolding = false;
            holdTimer = 0f;
            inspectObject.PauseRotation(false);

            // Discard any in-flight preload — the player bailed before the commit.
            if (SceneControllerManager.instance != null)
                SceneControllerManager.instance.CancelPhotoScenePreload();

            ResetConcentrationEffects();
            ResetCameraEffects();
            ResetBlurIntensity();
            StopHoldAudio();
            StartWhisperReturn();   // cancel: the memory ramps BACK to its whisper (not killed)
        }
        else if (!spaceHeld && isAligning)
        {
            // Released during the align phase — abort before the hold ever starts.
            AbortAlign();
        }

        // Whisper idle: while merely inspecting (no hold), keep the tape wow/flutter alive on
        // the whispering memory so the pitch never snaps when a hold starts/ends. Volume is
        // owned by setup (whisper) / WhisperReturnRoutine (release ramp) — not written here.
        if (!isHolding && emergeSource != null)
            ApplyEmergePitchLFO();
    }

    private IEnumerator EnterMemoryWhileBlack()
    {
        PhotoData photoToLoad = currentPhotoData;
        bool wasUIMode = currentIsUIMode;

        isActive = false;
        isHolding = false;
        holdTimer = 0f;
        currentPhotoData = null;
        currentIsUIMode = false;

        if (TutorialManager.instance != null && !TutorialManager.instance.FirstPhotoExamined)
            TutorialManager.instance.MarkFirstPhotoExamined();

        RemoveHint();

        // The emerge sound KEEPS PLAYING through the white (NOT stopped here, and the reverb is NOT
        // killed — its tail drains naturally as the volume fades = lush). It fades out IN SYNC with
        // the white fade-out: SceneControllerManager fires OnPhotoRevealStarted(duration) when the
        // curtain starts fading, and HandleRevealStarted fades the source over that same duration,
        // then destroys it (equal-power crossfade into the memory's own audio).
        if (SceneControllerManager.instance != null)
            SceneControllerManager.instance.OnPhotoRevealStarted += HandleRevealStarted;

        // WHITE flood hand-off: the crack's radial light has already flooded the screen white, so
        // popping the white curtain here is invisible — it HOLDS the white through the scene load +
        // player reposition, then FadeOut reveals the memory (and resets to black for the next,
        // black, transition).
        if (FadeManager.instance != null)
            FadeManager.instance.SetWhiteInstantly();

        yield return null;

        // Invisible cleanup (eyelid is closed or screen is black)
        ResetConcentrationEffects();
        ResetCameraEffects();
        ResetBlurIntensity();
        inspectObject.ForceExitForMemoryEntry();

        // The memory scene takes over — drop the stencil stamp (the inspected photo is gone).
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(false);

        if (wasUIMode)
            OnMemoryEntryFromUI?.Invoke();

        // Activate the preloaded scene (started during hold-start).
        // ActivatePreloadedPhotoScene falls back to a direct load if no preload was started,
        // so nothing breaks when the preload path is unavailable.
        if (SceneControllerManager.instance != null)
            SceneControllerManager.instance.ActivatePreloadedPhotoScene(photoToLoad);

        blinkTriggered = false;
        isEnteringMemory = false;
    }

    // Phase 1 — square the photo DEAD-ON to the camera (upright), then OnAlignComplete begins the
    // hold. Robust + config-free: instead of trusting PhotoData.FrontFaceDirection (which can be
    // misconfigured → the face aligns edge-on), it AUTO-DETECTS which of the photo's local axes is
    // currently facing the camera and which is "up", then builds a clean LookRotation that maps
    // face→(-cameraForward) and up→cameraUp. Reduced-motion snaps instantly.
    private void StartSquareAlign(float duration)
    {
        Transform pivot = inspectObject.InspectedObjectTransform;
        Transform camT = (PlayerManager.instance != null)
            ? PlayerManager.instance.GetPlayerCam().transform
            : (Camera.main != null ? Camera.main.transform : null);
        if (pivot == null || camT == null) { OnAlignComplete(); return; }

        // The photo's FACE is GEOMETRICALLY its thinnest axis (the flat-slab normal). That never
        // depends on the viewing angle, so the face is identified reliably EVERY time (the old
        // "axis most toward the camera" picked an in-plane axis when the photo was tilted → edge-on).
        // Read it from the photo MESH (a child of the inspected pivot).
        MeshFilter mf = pivot.GetComponentInChildren<MeshFilter>();
        Transform meshT = (mf != null) ? mf.transform : pivot;

        Vector3 faceLocal, inPlaneA, inPlaneB;
        if (mf != null && mf.sharedMesh != null)
        {
            // World-scaled extents along each local axis → the smallest is the slab normal (face).
            Vector3 ext = Vector3.Scale(mf.sharedMesh.bounds.size, AbsVec(meshT.lossyScale));
            int thin = (ext.x <= ext.y && ext.x <= ext.z) ? 0 : (ext.y <= ext.z ? 1 : 2);
            faceLocal = AxisVec(thin);
            inPlaneA  = AxisVec((thin + 1) % 3);   // the two in-plane axes (candidates for "up")
            inPlaneB  = AxisVec((thin + 2) % 3);
        }
        else
        {
            faceLocal = Vector3.forward; inPlaneA = Vector3.up; inPlaneB = Vector3.right; // fallback
        }

        Vector3 faceTarget = -camT.forward;   // face points back at the camera
        Vector3 upTarget   = camT.up;

        // Current world direction of the face axis, signed toward the camera (front side).
        Vector3 worldFace = meshT.TransformDirection(faceLocal);
        if (Vector3.Dot(worldFace, faceTarget) < 0f) worldFace = -worldFace;

        // "Up" = whichever in-plane axis is currently MORE vertical (robust to portrait/landscape),
        // signed toward camera-up. Only the ROLL is view-derived; the FACE itself is pure geometry.
        Vector3 wA = meshT.TransformDirection(inPlaneA);
        Vector3 wB = meshT.TransformDirection(inPlaneB);
        Vector3 worldUp = (Mathf.Abs(Vector3.Dot(wA, upTarget)) >= Mathf.Abs(Vector3.Dot(wB, upTarget))) ? wA : wB;
        if (Vector3.Dot(worldUp, upTarget) < 0f) worldUp = -worldUp;

        // delta maps the photo's CURRENT (face,up) world frame onto the TARGET frame; apply to the pivot
        // (the mesh is a rigid child, so a world-space delta on the pivot lands the mesh dead-square).
        Quaternion delta  = Quaternion.LookRotation(faceTarget, upTarget) *
                            Quaternion.Inverse(Quaternion.LookRotation(worldFace, worldUp));
        Quaternion target = delta * pivot.rotation;

        float dur = MotionAccessibility.ReduceMotion ? 0f : duration;
        if (dur <= 0f) { pivot.rotation = target; OnAlignComplete(); return; }
        pivot.DORotateQuaternion(target, dur).SetEase(Ease.OutQuad).OnComplete(OnAlignComplete);
    }

    private static Vector3 AxisVec(int i) => i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward);
    private static Vector3 AbsVec(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));

    // Begins the actual hold once the photo has squared up to the camera (align callback).
    private void OnAlignComplete()
    {
        if (!isAligning) return;   // aborted (released / exited inspection) during the tween
        isAligning = false;
        BeginHold();
    }

    // Aborts the Phase-1 align before the hold starts (player released or exited inspection).
    private void AbortAlign()
    {
        isAligning = false;
        if (inspectObject != null)
        {
            if (inspectObject.InspectedObjectTransform != null)
                DOTween.Kill(inspectObject.InspectedObjectTransform);
            inspectObject.PauseRotation(false);
        }
    }

    // Phase 2 — the concentration hold proper. Rotation is already paused from the align phase.
    private void BeginHold()
    {
        isHolding = true;

        // A pending release→whisper ramp must not fight the hold swell (ApplyMemoryEmergeAudio
        // owns the volume from here).
        if (whisperReturnCo != null)
        {
            StopCoroutine(whisperReturnCo);
            whisperReturnCo = null;
        }

        StartHoldAudio();
        CacheConcentrationState();
        SpawnPhotoLight();

        // Begin preloading the memory scene during the hold so the cut at commit is
        // instantaneous (no hitch). SceneControllerManager owns the async op.
        if (SceneControllerManager.instance != null)
            SceneControllerManager.instance.BeginPhotoScenePreload(currentPhotoData);

        // Enable the Vision Invertida grade pass (it renders only when progress > 0).
        // Push the authoritative profile first so the live pass uses the portal's tuning.
        if (MemoryTransitionPass.instance != null)
        {
            PushTransitionProfile();
            MemoryTransitionPass.instance.SetActive(true);
        }

        // Keep the mask write ON through the transition: MemoryTransition.shader reads the
        // _InspectedMaskTex mask to exclude the photo from the B&W drain, exactly as BackgroundBlur
        // does for inspected objects. SetupPhotoGlow keeps the photo on the masked layer.
        if (InspectedStencilPass.instance != null)
            InspectedStencilPass.instance.SetEnabled(true);
    }

    #region Concentration Effects (FOV, Z Breathing, Tilt, Pulsation, Low Pass, Camera Effects, Blur)

    private void CacheConcentrationState()
    {
        // Per-memory hold duration (transport-axis escalation; fallback to the serialized default).
        activeHoldDuration = (currentPhotoData != null && currentPhotoData.HoldDuration > 0.01f)
            ? currentPhotoData.HoldDuration : holdDuration;

        // Reduced-motion accessibility: zero the sustained vestibular FX (breathing/tilt/pulse/FOV/
        // warp) for sensitive players. Non-motion feedback (desaturation, halation, audio) stays.
        motionScale = MotionAccessibility.ReduceMotion ? 0f : 1f;
        cameraFrozen = false; // re-armed each hold; the void freeze sets it true

        // Cap framerate during the hold: the full-screen pass + per-frame drives cost 3-4x more
        // uncapped on thesis hardware (the project runs targetFrameRate=-1).
        if (!frameRateCapped)
        {
            previousTargetFrameRate = Application.targetFrameRate;
            Application.targetFrameRate = holdFrameRateCap;
            frameRateCapped = true;
        }

        // Cache camera via project pattern
        if (PlayerManager.instance != null)
        {
            PlayerCam playerCam = PlayerManager.instance.GetPlayerCam();
            playerCamera = playerCam.GetCamera();
            cameraTransform = playerCam.transform;
        }
        else
        {
            playerCamera = Camera.main;
            cameraTransform = playerCamera != null ? playerCamera.transform : null;
        }

        if (playerCamera != null)
        {
            originalFOV = playerCamera.fieldOfView;
            originalCameraLocalPos = cameraTransform.localPosition;
            originalCameraLocalRot = cameraTransform.localRotation;
        }

        // Cache photo transform for pulsation + engulf
        inspectedPhotoTransform = inspectObject.InspectedObjectTransform;
        if (inspectedPhotoTransform != null)
        {
            originalPhotoScale    = inspectedPhotoTransform.localScale;
            originalPhotoLocalPos = inspectedPhotoTransform.localPosition;
        }

        // Cache photo renderers + original emissive for the glow (clean restore on exit).
        SetupPhotoGlow();

        // NOTE: the memory-emerge AudioSource is NOT spawned here anymore — it spawns at
        // photo-context-set (HandlePhotoContextSet) so the photo whispers BEFORE any hold.

        // Set up low pass filter on camera GO
        if (playerCamera != null)
        {
            lowPassFilter = playerCamera.GetComponent<AudioLowPassFilter>();
            if (lowPassFilter == null)
                lowPassFilter = playerCamera.gameObject.AddComponent<AudioLowPassFilter>();
            lowPassFilter.cutoffFrequency = maxLowPassCutoff;
            lowPassFilter.enabled = true;
        }
    }

    private void ApplyConcentrationEffects(float linearProgress)
    {
        // Vision Invertida grade pass (replaces the old MemoryCameraEffects Volume).
        // Drives _Progress on the AfterPostProcess pass targeting PlayerCam; the photo
        // (composited by BlurCam on top) stays full color. See MemoryTransitionPass.
        if (MemoryTransitionPass.instance != null)
        {
            MemoryTransitionPass.instance.SetProgress(linearProgress);

            // White flood (climax): the volumetric fog fills the space over
            // [whiteFloodStart, commitThreshold], handing off to the curtain at commit.
            // LINEAR drive — the shader owns the staging curves (seep → build → whiteout).
            float floodT = Mathf.InverseLerp(whiteFloodStart, commitThreshold, linearProgress);
            // Behind-camera fog KILLED (0): it rendered on PlayerCam, BEHIND the BlurCam photo
            // composite → the "luz detrás". The whiteout now pours from the FRONT fill quad
            // (fillFloodGO). floodT is still computed below for the optional beam (ApplyPhotoLight).
            MemoryTransitionPass.instance.SetWhiteFlood(0f);

            // REAL volumetric point light pouring from the photo (rides the same window).
            ApplyPhotoLight(floodT);
        }

        // Photo halation — the photo's warmth bleeds into the surrounding grey world.
        ApplyPhotoHalo(linearProgress);

        // Memory sound emerging from the photo (volume + lowpass open with the same curve).
        ApplyMemoryEmergeAudio(linearProgress);

        // Background blur ramp — SKIPPED when relayered for bloom (blurMain is hard-disabled
        // in SetupPhotoGlow so the photo+crack render sharp through PlayerCam). Legacy path
        // (no relayer) still ramps the blur up for the concentration focus effect.
        bool relayeredForBloom = crackBloomEnabled && crackBloomOriginalLayer >= 0;
        if (BlurManager.instance != null && !relayeredForBloom)
        {
            float baseBlur = BlurManager.instance.DefaultBlurIntensity;
            float blurProgress = Mathf.Pow(linearProgress, 1.3f);
            BlurManager.instance.SetBlurIntensity(Mathf.Lerp(baseBlur, maxBlurIntensity, blurProgress));
        }

        float normalizedProgress = Mathf.Clamp01(linearProgress / commitThreshold);

        // ── DEAD FREEZE at the void ─────────────────────────────────────────────
        // The 0.4s of true silence is the horror climax — it demands STILLNESS. At
        // pVoidEnd we capture the current camera/photo pose and HOLD it hard (no taper)
        // through the rest of the hold and the cut. The oscillators were still "breathing
        // through their own climax"; freezing amplifies the silence AND lowers the
        // vestibular load on the forced tutorial (net motion change: negative).
        if (linearProgress >= pVoidEnd)
        {
            if (!cameraFrozen)
            {
                cameraFrozen = true;
                if (playerCamera != null)            frozenFOV          = playerCamera.fieldOfView;
                if (cameraTransform != null)        { frozenCamLocalPos  = cameraTransform.localPosition; frozenCamLocalRot = cameraTransform.localRotation; }
                if (inspectedPhotoTransform != null)
                {
                    frozenPhotoScale    = inspectedPhotoTransform.localScale;
                    frozenPhotoLocalPos = inspectedPhotoTransform.localPosition;
                }
            }
            if (playerCamera != null)     playerCamera.fieldOfView = frozenFOV;
            if (cameraTransform != null) { cameraTransform.localPosition = frozenCamLocalPos; cameraTransform.localRotation = frozenCamLocalRot; }
            if (inspectedPhotoTransform != null)
            {
                inspectedPhotoTransform.localScale    = frozenPhotoScale;
                inspectedPhotoTransform.localPosition = frozenPhotoLocalPos;
            }
        }
        else
        {
            // FOV drop (subtle zoom-in)
            if (playerCamera != null)
                playerCamera.fieldOfView = Mathf.Lerp(originalFOV, originalFOV - fovDrop * motionScale, normalizedProgress);

            // Micro Z breathing (oscillation increases with progress)
            if (cameraTransform != null)
            {
                float zOffset = Mathf.Sin(holdTimer * zBreathingSpeed) * zBreathingAmplitude * motionScale * normalizedProgress;
                cameraTransform.localPosition = originalCameraLocalPos + new Vector3(0f, 0f, zOffset);
            }

            // Micro tilt (Phase 2+ only, starts at 0.4)
            if (cameraTransform != null)
            {
                float tiltProgress = Mathf.Clamp01((linearProgress - 0.4f) / 0.45f);
                float tiltAngle = Mathf.Sin(holdTimer * microTiltSpeed) * microTiltMaxDegrees * motionScale * tiltProgress;
                cameraTransform.localRotation = originalCameraLocalRot * Quaternion.Euler(0f, 0f, tiltAngle);
            }

                // RESPLANDOR pivot: the photo stays PUT (no grow, no Z-push — the user rejected
                // the "photo approaches" feel). Instead it EMITS a white light burst, driven by
                // the photo-halo + rays in the grade pass (see ApplyPhotoHalo + MemoryTransition).
                // Only a subtle pulse of life remains on the photo itself.
                if (inspectedPhotoTransform != null && linearProgress > 0.5f)
                {
                    float pulseProgress = Mathf.Clamp01((linearProgress - 0.5f) / 0.35f);
                    float pulse = 1f + Mathf.Sin(holdTimer * pulsationSpeed) * pulsationAmplitude * motionScale * pulseProgress;
                    inspectedPhotoTransform.localScale = originalPhotoScale * pulse;
                }
        }

        // Audio low pass filter (progressive muffling)
        if (lowPassFilter != null)
            lowPassFilter.cutoffFrequency = Mathf.Lerp(maxLowPassCutoff, minLowPassCutoff, normalizedProgress);
    }

    private void ResetConcentrationEffects()
    {
        // Restore FOV
        if (playerCamera != null)
            playerCamera.fieldOfView = originalFOV;

        // Restore camera position/rotation (Z breathing + tilt)
        if (cameraTransform != null)
        {
            cameraTransform.localPosition = originalCameraLocalPos;
            cameraTransform.localRotation = originalCameraLocalRot;
        }

        // Restore photo scale + position (pulsation + engulf)
        if (inspectedPhotoTransform != null)
        {
            inspectedPhotoTransform.localScale    = originalPhotoScale;
            inspectedPhotoTransform.localPosition = originalPhotoLocalPos;
        }

        // Restore photo emissive (glow) to its original value.
        ResetPhotoGlow();

        // NOTE: the memory-emerge AudioSource is NOT torn down here. On COMMIT it keeps playing
        // and is faded out later (HandleRevealStarted, in sync with the white). On CANCEL the
        // callers (Cleanup / the Update cancel branch) call ResetMemoryEmergeAudio() explicitly.

        // Disable low pass filter
        if (lowPassFilter != null)
        {
            lowPassFilter.cutoffFrequency = maxLowPassCutoff;
            lowPassFilter.enabled = false;
        }

        // Restore the framerate cap set at hold-start.
        if (frameRateCapped)
        {
            Application.targetFrameRate = previousTargetFrameRate;
            frameRateCapped = false;
        }

        playerCamera = null;
        cameraTransform = null;
        inspectedPhotoTransform = null;
    }

    #endregion

    #region Vision Invertida Profile & Photo Glow

    // Push the portal's authoritative grade profile to the live pass (overrides serialized values).
    private void PushTransitionProfile()
    {
        MemoryTransitionPass pass = MemoryTransitionPass.instance;
        if (pass == null) return;

        pass.SetBreakpoints(pOpenEnd, pStripEnd, pVoidEnd, pFloodEnd);
        pass.SetGradeIntensity(gradeVignetteOpen, gradePostExpStrip, gradeSatStripFloor, gradeGrainPeak);
        pass.SetMemoryProfile(gradeArrivalOversat, gradeChannelOffsetMaxPx, gradeFloodCurveExp,
                              gradeOffsetTintWarm, gradeOffsetTintCool);

        // Per-memory world UV-warp pull (scaled to 0 under reduced-motion). This was never pushed
        // before → the per-memory escalation axis was dead (always the shader default).
        float warp = (currentPhotoData != null ? currentPhotoData.UvWarpStrength : 0.015f) * motionScale;
        pass.SetUVWarpStrength(warp);
    }

    // Create the Silver Drowning transparent overlay on top of the photo.
    // The photo's own HDRP/Lit material is NEVER touched — the warm room light
    // keeps lighting it, so there is zero Lit→Unlit color/brightness pop by construction.
    // The overlay is a co-planar quad starting at alpha=0 (fully invisible).
    private void SetupPhotoGlow()
    {
        photoGlowRenderers   = null;
        splitOverlayGO       = null;  // defensive: cleared here so early-returns below leave them null
        splitOverlayMaterial = null;
        if (!photoGlowEnabled || inspectedPhotoTransform == null) return;

        // Cache renderers (used only by the emissive MPB path in ApplyPhotoHalo).
        Renderer[] renderers = inspectedPhotoTransform.GetComponentsInChildren<Renderer>();
        if (renderers != null && renderers.Length > 0)
        {
            photoGlowRenderers = renderers;
        }

        // Locate the child MeshFilter that holds the photo geometry.
        MeshFilter photoMF = inspectedPhotoTransform.GetComponentInChildren<MeshFilter>();
        if (photoMF == null) return;

        // Find the photo texture from the photo's own material (for luma masking in the shader).
        Texture photoTex = null;
        if (photoGlowRenderers != null && photoGlowRenderers.Length > 0)
        {
            Material src = photoGlowRenderers[0].sharedMaterial;
            if (src != null)
            {
                if (src.HasProperty(PropBaseColorMap)) photoTex = src.GetTexture(PropBaseColorMap);
                if (photoTex == null && src.HasProperty(PropMainTex)) photoTex = src.GetTexture(PropMainTex);
            }
        }

        Transform photoMeshTransform = photoMF.transform;

        // ── DOORS OF LIGHT: split overlay ───────────────────────────────────────
        // Guard: skip entirely if disabled or shader cannot be resolved.
        splitOverlayGO       = null;
        splitOverlayMaterial = null;
        if (!photoSplitEnabled) return;

        Shader resolvedSplitShader = photoSplitShader != null
            ? photoSplitShader
            : Shader.Find("Memora/PhotoPortalSplit");
        if (resolvedSplitShader == null) return;

        splitOverlayMaterial = new Material(resolvedSplitShader) { name = "PhotoPortalSplit_Runtime" };

        // Transparency setup — ADDITIVE blend (_DstBlend=One) so the crack fog reads as
        // radiant light accumulating on top of the photo, not as an opaque layer painted
        // over it. The photo is visible everywhere the fog contribution is low; only the
        // flood phase drives contribution to 1 everywhere → white fill.
        // SrcBlend=One (add full contribution), DstBlend=One (preserve destination fully).
        splitOverlayMaterial.SetFloat("_SurfaceType", 1f);
        splitOverlayMaterial.SetFloat("_SrcBlend",    1f);   // One   (additive src)
        splitOverlayMaterial.SetFloat("_DstBlend",    1f);   // One   (additive dst — preserve photo beneath)
        splitOverlayMaterial.SetFloat("_ZWrite",      0f);
        splitOverlayMaterial.SetFloat("_CullMode",    0f);   // Off — the clean Quad must show regardless of front/back orientation
        splitOverlayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        splitOverlayMaterial.renderQueue = 3001; // +1 vs decay overlay — renders on top

        // Provide the same photo texture (bound for CBUFFER completeness; the crack
        // effect itself does not re-sample it, but the HLSL declares the sampler).
        if (photoTex != null)
            splitOverlayMaterial.SetTexture(PropBaseColorMap, photoTex);

        // Start fully invisible — invariant II (no pop). With additive blend,
        // contribution=0 means zero light added → photo untouched.
        splitOverlayMaterial.SetFloat(PropSplitProgress, 0f);

        // Z-FIGHT FIX: Always draw on top, never fight with the co-planar photo mesh.
        // Value 8 = CompareFunction.Always — the ForwardOnly pass uses ZTest [_ZTestDepthEqualForOpaque].
        // SetInt is required: _ZTestDepthEqualForOpaque is declared as Int in the Properties block
        // and is used as a ZTest state directive (not a float uniform). SetFloat pushes to the wrong
        // slot type and may land the wrong compare function on some drivers → overlay culled or z-fights.
        splitOverlayMaterial.SetInt("_ZTestDepthEqualForOpaque", 8);

        // Per-photo noise seed — offsets CrackNoise phase → unique fracture fingerprint.
        // Same hash formula as MemoryTransitionPass so the PhotoPortalSplit crack and
        // the MemoryTransition crack stay aligned (they share CrackNoise + the same seed).
        float splitCrackSeed = currentPhotoData != null
            ? (Mathf.Abs(currentPhotoData.ID.GetHashCode()) % 997)
            : 137f;
        splitOverlayMaterial.SetFloat(PropSplitCrackNoiseSeed, splitCrackSeed);

        // ── Crack color (single warm-dirty-white tint — NOT gold) ─────────────
        splitOverlayMaterial.SetColor(PropCrackColor, crackColor);

        // ── Crack onset ───────────────────────────────────────────────────────
        splitOverlayMaterial.SetFloat(PropCrackGrowStart, crackGrowStart);

        // ── Crack centering: CLEAN-QUAD UV SPACE ──────────────────────────────
        // The overlay is a built-in Quad with linear 0..1 UVs (UV 0.5,0.5 == the
        // visual center), NOT the photo's curled-atlas cube mesh (whose UVs are
        // non-linear, so the old object-space approach drifted off-center). The
        // shader computes the crack in UV space; we only feed it the photo aspect
        // (so the seam reads the same width on non-square photos) + the swap axis.
        Mesh photoMesh = photoMF.sharedMesh;
        Bounds mb = (photoMesh != null) ? photoMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
        float photoAspect = Mathf.Max(mb.size.x, 1e-3f) / Mathf.Max(mb.size.z, 1e-3f);
        splitOverlayMaterial.SetFloat(PropCrackHalfX, photoAspect);   // reused as quad aspect
        splitOverlayMaterial.SetFloat(PropCrackSwap,  crackSwap);

        // ── Crack shape ───────────────────────────────────────────────────────
        splitOverlayMaterial.SetFloat(PropCrackJagAmp,     crackJagAmp);
        splitOverlayMaterial.SetFloat(PropCrackJagFreq,    crackJagFreq);
        splitOverlayMaterial.SetFloat(PropCrackLenMin,     crackLenMin);
        splitOverlayMaterial.SetFloat(PropCrackLenMax,     crackLenMax);
        splitOverlayMaterial.SetFloat(PropCrackLenFeather, crackLenFeather);

        // ── Glow bands: core (dense white spine) / mid (body) / skin (tight halo) ──
        // With REAL bloom (Option B) the soft halo comes from HDRP Bloom on PlayerCam;
        // keep the core thin + HDR-bright, the mid/skin restrained (skinW <= ~0.06).
        splitOverlayMaterial.SetFloat(PropCrackCoreW,   crackCoreW);
        splitOverlayMaterial.SetFloat(PropCrackCoreExp, crackCoreExp);
        splitOverlayMaterial.SetFloat(PropCrackCoreInt, crackCoreInt);
        splitOverlayMaterial.SetFloat(PropCrackMidW,    crackMidW);
        splitOverlayMaterial.SetFloat(PropCrackMidExp,  crackMidExp);
        splitOverlayMaterial.SetFloat(PropCrackMidInt,  crackMidInt);
        splitOverlayMaterial.SetFloat(PropCrackSkinW,   crackSkinW);
        splitOverlayMaterial.SetFloat(PropCrackSkinExp, crackSkinExp);
        splitOverlayMaterial.SetFloat(PropCrackSkinInt, crackSkinInt);

        // ── Sparkles (glints along the seam) ──────────────────────────────────
        splitOverlayMaterial.SetFloat(PropSparkleAmount,  sparkleAmount);
        splitOverlayMaterial.SetFloat(PropSparkleDensity, sparkleDensity);
        splitOverlayMaterial.SetFloat(PropSparkleSpeed,   sparkleSpeed);
        splitOverlayMaterial.SetFloat(PropSparkleSeamW,   sparkleSeamW);
        splitOverlayMaterial.SetFloat(PropSparkleCorona,  sparkleCorona);

        // ── Núcleo + Rays (central white starburst — pushed once at hold-start) ──
        splitOverlayMaterial.SetFloat(PropCoreInt,        coreInt);
        splitOverlayMaterial.SetFloat(PropCoreRadius,     coreRadius);
        splitOverlayMaterial.SetFloat(PropCoreFalloff,    coreFalloff);
        splitOverlayMaterial.SetFloat(PropCorePulseAmp,   corePulseAmp);
        splitOverlayMaterial.SetFloat(PropCorePulseSpeed, corePulseSpeed);
        splitOverlayMaterial.SetFloat(PropRayIntensity,   rayIntensity);
        splitOverlayMaterial.SetFloat(PropRayCount,       rayCount);
        splitOverlayMaterial.SetFloat(PropRaySharpness,   raySharpness);
        splitOverlayMaterial.SetFloat(PropRaySpeed,       raySpeed);
        splitOverlayMaterial.SetFloat(PropRayInnerFade,   rayInnerFade);
        splitOverlayMaterial.SetFloat(PropRayEnvRadius,   rayEnvRadius);
        splitOverlayMaterial.SetColor(PropCoreTint,       crackCoreTint);

        // ── Realistic bloom: exposure-independent so the crack does not respond to
        // the dark scene's auto-exposure (it is a supernatural light, not a lamp). ──
        splitOverlayMaterial.SetFloat(PropEmissiveExposureWeight, crackEmissiveExposureWeight);

        // ── Build the split overlay as a CLEAN QUAD (linear UV) ────────────────
        // Built-in Quad (default normal +Z) rotated so its normal aligns with the
        // photo front face, sized to the photo face. Linear 0..1 UVs land the
        // shader's UV-space crack dead-center.
        splitOverlayGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        splitOverlayGO.name = "PhotoPortalSplitOverlay";
        Collider quadCol = splitOverlayGO.GetComponent<Collider>();
        if (quadCol != null) Destroy(quadCol);

        MeshRenderer splitMR = splitOverlayGO.GetComponent<MeshRenderer>();
        splitMR.sharedMaterial    = splitOverlayMaterial;
        splitMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        splitMR.receiveShadows    = false;

        // EXACTLY match the validated test harness (CrackTestDriver) — the setup the user
        // approved. The clean Quad is rotated -90° about X (normal → +Y, surface in the
        // X-Z plane = the photo face), sized to the photo face, lifted just in front along
        // +Y. Do NOT use FromToRotation/faceDir here — that rotated the quad off the photo
        // (the "línea sobresale / recta" bug). Paired with crackSwap=1 (harness value).
        splitOverlayGO.transform.SetParent(photoMeshTransform, worldPositionStays: false);
        splitOverlayGO.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        splitOverlayGO.transform.localScale    = new Vector3(Mathf.Max(mb.size.x, 1e-3f), Mathf.Max(mb.size.z, 1e-3f), 1f);
        splitOverlayGO.transform.localPosition = new Vector3(0f, mb.extents.y + 0.01f, 0f);

        // ── Relayer for REAL bloom + stencil stamp ─────────────────────────────
        // Move the photo + crack onto crackBloomLayer ("InspectedObject"): PlayerCam renders it so
        // the crack's HDR emissive is caught by HDRP Bloom, AND InspectedStencilPass stamps it so
        // MemoryTransition excludes it from the B&W drain. BackgroundBlur is hard-disabled below
        // (MemoryTransition owns the blur during the transition). Restored in ResetPhotoGlow.
        if (crackBloomEnabled)
        {
            int bloomLayer = LayerMask.NameToLayer(crackBloomLayer);
            if (bloomLayer >= 0)
            {
                crackBloomOriginalLayer = inspectedPhotoTransform.gameObject.layer;
                SetLayerRecursive(inspectedPhotoTransform, bloomLayer); // also catches the crack quad (now a child)

                // SUPRIME el blur de pantalla completa: la foto ahora renderiza por PlayerCam, y el
                // blur taparía (y escondería) toda la salida de PlayerCam — foto Y grieta. Bajar
                // _UIBlur a 0 no daba un passthrough limpio para la grieta transparente, así que se
                // suprime de una. Suppress y no Release: la inspección sigue siendo la DUEÑA del
                // blur, sólo queremos ocultarlo un rato. Se devuelve en ResetPhotoGlow.
                if (BlurManager.instance != null)
                    BlurManager.instance.Suppress(true);
            }
            else
            {
                splitOverlayGO.layer = photoMF.gameObject.layer; // layer not found → fall back, no bloom
            }
        }
        else
        {
            splitOverlayGO.layer = photoMF.gameObject.layer; // legacy: crack on the inspect layer (no bloom)
        }

        // ── GLOW HALO: the resplandor, IN FRONT of the photo (additive wide-núcleo quad) ──
        SetupGlowHalo(photoMeshTransform, mb);
        SetupFillFlood(photoMeshTransform, mb);
    }

    // Build the glow-halo quad: the "resplandor" rendered IN FRONT of the photo. Reuses the
    // PhotoPortalSplit shader configured as a PURE WIDE SOFT NÚCLEO (no crack line, no rays) — a
    // wide radius reads soft WITHOUT bloom (which BlurCam lacks). Sits just in front of the crack
    // quad, bigger than the photo, same layer → BlurCam composites it OVER the photo. Additive so
    // it BRIGHTENS (never covers — the crack stays the star). The behind point-light ball is killed.
    private void SetupGlowHalo(Transform photoMeshTransform, Bounds mb)
    {
        glowHaloGO = null;
        glowHaloMaterial = null;
        if (!photoGlowHaloEnabled || splitOverlayGO == null || photoMeshTransform == null) return;

        Shader resolvedSplitShader = photoSplitShader != null ? photoSplitShader : Shader.Find("Memora/PhotoPortalSplit");
        if (resolvedSplitShader == null) return;

        glowHaloMaterial = new Material(resolvedSplitShader) { name = "PhotoGlowHalo_Runtime" };
        // Additive transparent — same blend setup as the crack overlay.
        glowHaloMaterial.SetFloat("_SurfaceType", 1f);
        glowHaloMaterial.SetFloat("_SrcBlend",    1f);   // One
        glowHaloMaterial.SetFloat("_DstBlend",    1f);   // One → additive (brightens, never covers)
        glowHaloMaterial.SetFloat("_ZWrite",      0f);
        glowHaloMaterial.SetFloat("_CullMode",    0f);   // Off
        glowHaloMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        glowHaloMaterial.SetInt("_ZTestDepthEqualForOpaque", 8); // Always
        glowHaloMaterial.renderQueue = 3000; // BELOW the crack quad (3001) so the crack stays on top

        // PURE WIDE SOFT NÚCLEO — zero the crack line / rays / sparkles; keep only the radial glow.
        glowHaloMaterial.SetFloat(PropCrackCoreInt,   0f);
        glowHaloMaterial.SetFloat(PropCrackMidInt,    0f);
        glowHaloMaterial.SetFloat(PropCrackSkinInt,   0f);
        glowHaloMaterial.SetFloat(PropRayIntensity,   0f);
        glowHaloMaterial.SetFloat(PropSparkleAmount,  0f);
        glowHaloMaterial.SetFloat(PropCrackGrowStart, crackGrowStart);
        glowHaloMaterial.SetFloat(PropCoreRadius,     photoHaloCoreRadius); // WIDE = soft without bloom
        glowHaloMaterial.SetFloat(PropCoreFalloff,    3f);
        glowHaloMaterial.SetFloat(PropCorePulseAmp,   0f);
        glowHaloMaterial.SetColor(PropCoreTint,       photoHaloTint);
        glowHaloMaterial.SetFloat(PropCoreInt,        photoHaloCoreInt);
        float haloAspect = Mathf.Max(mb.size.x, 1e-3f) / Mathf.Max(mb.size.z, 1e-3f);
        glowHaloMaterial.SetFloat(PropCrackHalfX,     haloAspect);
        glowHaloMaterial.SetFloat(PropCrackSwap,      crackSwap);
        glowHaloMaterial.SetFloat(PropSplitProgress,  0f); // ramped each frame in ApplyPhotoHalo

        glowHaloGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        glowHaloGO.name = "PhotoGlowHalo";
        Collider haloCol = glowHaloGO.GetComponent<Collider>();
        if (haloCol != null) Destroy(haloCol);
        MeshRenderer haloMR = glowHaloGO.GetComponent<MeshRenderer>();
        haloMR.sharedMaterial    = glowHaloMaterial;
        haloMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        haloMR.receiveShadows    = false;

        // Mirror the crack quad: rotated -90° about X, sized to the photo face × photoHaloScale,
        // lifted just IN FRONT of the crack quad (0.02 vs the crack's 0.01), same layer.
        glowHaloGO.transform.SetParent(photoMeshTransform, worldPositionStays: false);
        glowHaloGO.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        glowHaloGO.transform.localScale    = new Vector3(
            Mathf.Max(mb.size.x, 1e-3f) * photoHaloScale,
            Mathf.Max(mb.size.z, 1e-3f) * photoHaloScale, 1f);
        glowHaloGO.transform.localPosition = new Vector3(0f, mb.extents.y + 0.02f, 0f);
        glowHaloGO.layer = splitOverlayGO.layer;
    }

    // Build the FILL FLOOD quad: the climax whiteout rendered IN FRONT of the photo. Reuses the
    // PhotoPortalSplit shader with a new _FillProgress radial block (all crack/núcleo/ray elements
    // zeroed, sp pinned at 0 → grow=0 → no núcleo). Sized to the viewport so the radial wave
    // expands across the whole screen at the climax, additive so it BRIGHTENS (covers without a
    // flat opaque sheet). RenderQueue 3002 → drawn in front of the crack (3001) and halo (3000).
    private void SetupFillFlood(Transform photoMeshTransform, Bounds mb)
    {
        fillFloodGO = null;
        fillFloodMaterial = null;
        if (!photoFillFloodEnabled || splitOverlayGO == null || photoMeshTransform == null) return;

        Shader resolvedShader = photoSplitShader != null ? photoSplitShader : Shader.Find("Memora/PhotoPortalSplit");
        if (resolvedShader == null) return;

        fillFloodMaterial = new Material(resolvedShader) { name = "PhotoFillFlood_Runtime" };
        // Additive transparent — same blend as the crack/halo.
        fillFloodMaterial.SetFloat("_SurfaceType", 1f);
        fillFloodMaterial.SetFloat("_SrcBlend",    1f);   // One
        fillFloodMaterial.SetFloat("_DstBlend",    1f);   // One → additive
        fillFloodMaterial.SetFloat("_ZWrite",      0f);
        fillFloodMaterial.SetFloat("_CullMode",    0f);   // Off
        fillFloodMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        fillFloodMaterial.SetInt("_ZTestDepthEqualForOpaque", 8); // Always
        fillFloodMaterial.renderQueue = 3002; // in front of crack (3001) and halo (3000)

        // PURE FILL — zero every crack/núcleo/ray element. sp pinned at 0 → grow=0 → no núcleo.
        // The radial fill block reads _FillProgress + _CoreInt + _CoreTint directly (driven each frame).
        fillFloodMaterial.SetFloat(PropCrackCoreInt,   0f);
        fillFloodMaterial.SetFloat(PropCrackMidInt,    0f);
        fillFloodMaterial.SetFloat(PropCrackSkinInt,   0f);
        fillFloodMaterial.SetFloat(PropRayIntensity,   0f);
        fillFloodMaterial.SetFloat(PropSparkleAmount,  0f);
        fillFloodMaterial.SetFloat(PropCorePulseAmp,   0f);
        fillFloodMaterial.SetFloat(PropCrackGrowStart, crackGrowStart); // sp(0) < this → grow=0 → no núcleo
        fillFloodMaterial.SetFloat(PropCoreInt,        0f);             // driven to fillFloodPeakInt in ApplyPhotoHalo
        fillFloodMaterial.SetColor(PropCoreTint,       fillFloodTint);
        fillFloodMaterial.SetFloat(PropSplitProgress,  0f);
        fillFloodMaterial.SetFloat(PropFillProgress,   0f);
        // Screen aspect → the radial wave is circular ON SCREEN (the quad is sized to the viewport).
        float screenAspect = (playerCamera != null) ? playerCamera.aspect : (16f / 9f);
        fillFloodMaterial.SetFloat(PropCrackHalfX, screenAspect);
        fillFloodMaterial.SetFloat(PropCrackSwap,  crackSwap);

        fillFloodGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fillFloodGO.name = "PhotoFillFlood";
        Collider fc = fillFloodGO.GetComponent<Collider>();
        if (fc != null) Destroy(fc);
        MeshRenderer fmr = fillFloodGO.GetComponent<MeshRenderer>();
        fmr.sharedMaterial    = fillFloodMaterial;
        fmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        fmr.receiveShadows    = false;

        // ── SCREEN-SPACE ANCHORING (immune to photo aspect/scale/orientation) ──────
        // Parent to the CAMERA, not the photo: the quad sits at the photo's depth along the camera
        // forward axis, sized to exactly cover the frustum at that depth × margin. NO photo AABB is
        // read — pure frustum math. (The AABB approach broke for portrait photos: the photo mesh is a
        // 3D slab whose world AABB ≠ the visible face → the scale came out photo-shaped, not screen.)
        Transform camTransform = (playerCamera != null) ? playerCamera.transform : null;
        if (camTransform == null)
        {
            // Fallback (no camera): photo-parented, generous fixed scale — no hard crash.
            fillFloodGO.transform.SetParent(photoMeshTransform, worldPositionStays: false);
            fillFloodGO.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            fillFloodGO.transform.localScale    = new Vector3(10f, 10f, 1f);
            fillFloodGO.transform.localPosition = new Vector3(0f, mb.extents.y + 0.03f, 0f);
        }
        else
        {
            // Photo depth along the camera forward axis (always positive = in front).
            float dist = (inspectedPhotoTransform != null)
                ? Mathf.Max(Vector3.Dot(inspectedPhotoTransform.position - camTransform.position, camTransform.forward), 0.1f)
                : 0.6f;
            float fov      = (playerCamera != null) ? playerCamera.fieldOfView : 60f;
            float frustumH = 2f * Mathf.Tan(0.5f * fov * Mathf.Deg2Rad) * dist;
            float frustumW = frustumH * screenAspect;
            // Built-in Quad is 1×1 in local space → scale directly to the frustum × margin. Parent to
            // the camera; localPos (0,0,dist) = straight ahead; identity rot faces the cam (the fill
            // material is Cull Off, so the back-facing Quad still renders). UV center = screen center
            // → the radial wave is centered (≈ the crack, since the photo is squared+centered on hold).
            fillFloodGO.transform.SetParent(camTransform, worldPositionStays: false);
            fillFloodGO.transform.localPosition = new Vector3(0f, 0f, dist);
            fillFloodGO.transform.localRotation = Quaternion.identity;
            fillFloodGO.transform.localScale    = new Vector3(frustumW * fillFloodScreenMargin, frustumH * fillFloodScreenMargin, 1f);
        }

        fillFloodGO.layer = splitOverlayGO.layer;
    }

    // Recursively set the layer on a transform hierarchy (relayer helper for the bloom path).
    private static void SetLayerRecursive(Transform root, int layer)
    {
        if (root == null) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursive(root.GetChild(i), layer);
    }

    // Per-frame crack driver: feeds the grade pass the photo's screen position (for the world
    // UV-warp toward the photo) and advances the on-photo crack overlay's _SplitProgress from the
    // hold progress. Called each frame in ApplyConcentrationEffects BEFORE the dead-freeze block,
    // so the crack keeps building through the void silence (light erupts while the world is still).
    private void ApplyPhotoHalo(float linearProgress)
    {
        if (!photoHaloEnabled) return;

        MemoryTransitionPass pass = MemoryTransitionPass.instance;
        if (pass == null || playerCamera == null || inspectedPhotoTransform == null) return;

        // CENTERING FIX: use the photo renderer's bounds center instead of the transform
        // pivot, which can be offset from the visible mesh center on non-uniform prefabs.
        // photoGlowRenderers is cached in SetupPhotoGlow (guarded by photoGlowEnabled).
        Renderer photoR = (photoGlowRenderers != null && photoGlowRenderers.Length > 0)
            ? photoGlowRenderers[0] : null;
        Vector3 worldCenter = photoR != null ? photoR.bounds.center : inspectedPhotoTransform.position;
        Vector3 vp = playerCamera.WorldToViewportPoint(worldCenter);
        float screenY = photoHaloFlipY ? 1f - vp.y : vp.y;
        Vector2 photoScreenUV = new Vector2(vp.x, screenY);
        lastPhotoScreenUV = photoScreenUV;

        // ── Feed the UV-warp its photo anchor ─────────────────────────────────
        // The screen-space crack/halo were removed; the grade pass now only needs the
        // photo's screen position (the world is warped toward it). The crack itself
        // lives entirely on the photo (PhotoPortalSplit overlay, in the photo's UV space).
        pass.SetPhotoScreenPos(photoScreenUV);

        // ── DRIVE THE CRACK OVERLAY (photo-UV crack of light) ─────────────────
        // sp computed DIRECTLY from linearProgress (no curve): 0→1 over
        // [crackGrowStart, commitThreshold] — hairline → grows → núcleo pulses →
        // floods white only at the very end. The overlay shader maps sp to length/flood.
        if (splitOverlayMaterial != null)
        {
            float denom = Mathf.Max(commitThreshold - crackGrowStart, 0.001f);
            float sp = Mathf.Clamp01((linearProgress - crackGrowStart) / denom);
            splitOverlayMaterial.SetFloat(PropSplitProgress, sp);
            // NOTE: the crack quad is NEVER scaled up anymore — the user rejected the
            // "photo approaches/engulfs" feel. The crack OPENS in place (shader gap) and
            // the shader's in-quad white flood whites the photo at the climax → hard cut.
        }

        // ── DRIVE THE GLOW HALO (the resplandor, IN FRONT) ────────────────────
        // Same sp as the crack, but CAPPED below the flood (0.85) so it stays a soft glow and
        // never whites out — the crack + curtain own the whiteout, the halo is just the resplandor.
        if (glowHaloMaterial != null)
        {
            float haloDenom = Mathf.Max(commitThreshold - crackGrowStart, 0.001f);
            float haloSp = Mathf.Clamp01((linearProgress - crackGrowStart) / haloDenom);
            glowHaloMaterial.SetFloat(PropSplitProgress, Mathf.Min(haloSp, photoHaloSpCap));
        }

        // ── DRIVE THE FILL FLOOD (climax white-light fill, IN FRONT, covers + fills) ──
        // Invisible during the build (fillT=0 until fillFloodStart). From fillFloodStart→commit a
        // billowy white wave expands from the crack across the screen and saturates to pure white
        // right as the FadeManager curtain fires (seamless handoff). Intensity ramps quadratically
        // (the wave SPREADS first, then BLOOMS bright); color crosses warm→white over the back half.
        if (fillFloodMaterial != null)
        {
            float fillT = Mathf.Clamp01((linearProgress - fillFloodStart) / Mathf.Max(commitThreshold - fillFloodStart, 0.001f));
            fillFloodMaterial.SetFloat(PropFillProgress, fillT);
            fillFloodMaterial.SetFloat(PropCoreInt, Mathf.Lerp(0f, fillFloodPeakInt, fillT * fillT));
            fillFloodMaterial.SetColor(PropCoreTint, fillFloodTint); // PURE WHITE — like the rest of the effect's light
        }

        // NOTE: the photo mesh is NEVER driven emissive anymore — that drive was the
        // "whole photo turns white" bug. The photo keeps its Lit material and stays
        // fully visible; the ONLY white during the hold is the crack overlay above.
        // The crack noise seed + amp are pushed once at hold start (SetupPhotoGlow).
    }

    // Destroy the Silver Drowning overlay GameObject and its runtime material.
    // The photo's own HDRP/Lit material was never modified — nothing to restore.
    private void ResetPhotoGlow()
    {
        // ── Restore the bloom relayer FIRST (before the photo transform is cleared) ──
        // Move the photo back to its inspect layer + re-enable the blur overlay so a
        // cancelled hold returns to normal inspection (photo on RenderAbove → BlurCam,
        // blurMain on). On commit, the scene transition manages blur after this.
        if (crackBloomOriginalLayer >= 0)
        {
            if (inspectedPhotoTransform != null)
                SetLayerRecursive(inspectedPhotoTransform, crackBloomOriginalLayer);
            if (BlurManager.instance != null)
            {
                BlurManager.instance.Suppress(false);
                BlurManager.instance.ResetBlurIntensity();
            }
        }
        crackBloomOriginalLayer = -1;

        // Tear down the ephemeral room light (cancel: light vanishes with the hold;
        // commit: runs under the white curtain → never seen popping off).
        DestroyPhotoLight();

        // ── DOORS OF LIGHT: tear down split overlay (mirrors decay teardown exactly) ─
        if (splitOverlayGO != null)
        {
            Destroy(splitOverlayGO);
            splitOverlayGO = null;
        }
        if (splitOverlayMaterial != null)
        {
            Destroy(splitOverlayMaterial);
            splitOverlayMaterial = null;
        }

        // ── GLOW HALO teardown (the resplandor quad) ──────────────────────────
        if (glowHaloGO != null)
        {
            Destroy(glowHaloGO);
            glowHaloGO = null;
        }
        if (glowHaloMaterial != null)
        {
            Destroy(glowHaloMaterial);
            glowHaloMaterial = null;
        }

        // ── FILL FLOOD teardown (the climax whiteout quad) ────────────────────
        if (fillFloodGO != null)
        {
            Destroy(fillFloodGO);
            fillFloodGO = null;
        }
        if (fillFloodMaterial != null)
        {
            Destroy(fillFloodMaterial);
            fillFloodMaterial = null;
        }

        // Clear any MPB overrides that may have been set by the halo path.
        if (photoGlowRenderers != null)
        {
            for (int i = 0; i < photoGlowRenderers.Length; i++)
            {
                Renderer r = photoGlowRenderers[i];
                if (r != null) r.SetPropertyBlock(null);
            }
        }
        photoGlowRenderers = null;
    }

    // ── Photo room light (REAL volumetric point light) ──────────────────────────
    // Spawned at hold-start at zero intensity; ramped over the climax window by
    // ApplyPhotoLight. The scene's volumetric fog (verified enabled) gives the light a
    // real scattering ball — white light physically pouring from the photo into the room.
    private void SpawnPhotoLight()
    {
        photoLightGO = null; photoLightComp = null; photoLightHD = null;
        if (!photoLightEnabled || inspectedPhotoTransform == null || playerCamera == null) return;

        photoLightGO = new GameObject("PhotoRoomLight_Ephemeral");

        photoLightComp         = photoLightGO.AddComponent<Light>();
        photoLightComp.type    = LightType.Point;
        photoLightComp.color   = photoLightWarmColor;       // warm start; ramps to pure white
        photoLightComp.shadows = LightShadows.None;         // shadowless: perf + GPU-fragility history

        photoLightHD = photoLightGO.AddComponent<HDAdditionalLightData>();
        photoLightHD.SetIntensity(0f, UnityEngine.Rendering.LightUnit.Lumen);     // photometric; invisible at spawn
        photoLightHD.range             = photoLightRange;
        photoLightHD.volumetricDimmer  = 0f;                // ramped with intensity (soft ball fade-in)
        photoLightHD.affectsVolumetric = true;
        // Light layer left at DEFAULT (bit 0): lights the room, NOT the photo (renderers on bit 7).

        photoLightGO.transform.position = ComputePhotoLightPosition();
    }

    // Light sits BETWEEN the photo and the camera so the photo mesh never occludes the
    // volumetric scattering ball — the glow surrounds the photo's silhouette.
    private Vector3 ComputePhotoLightPosition()
    {
        Renderer photoR = (photoGlowRenderers != null && photoGlowRenderers.Length > 0)
            ? photoGlowRenderers[0] : null;
        Vector3 center = photoR != null ? photoR.bounds.center : inspectedPhotoTransform.position;
        Vector3 toCam  = (playerCamera.transform.position - center).normalized;
        return center + toCam * photoLightCameraOffset;
    }

    // Drives the light over the climax window (floodT = the same 0→1 as the white fog).
    // Cubic ease-in (slow wake, bursts at the end) × a heartbeat pulse that FLATLINES at
    // floodT 0.8 — the heart stops exactly as the whiteout breaks (look spec death-beat).
    // Colour ramps warm→pure white over floodT 0.65→0.95 (personal → transcendent).
    private void ApplyPhotoLight(float floodT)
    {
        if (photoLightHD == null) return;

        float rampT = floodT * floodT * floodT; // cubic ease-in

        // Heartbeat: sharp attack + exponential decay per cycle (biological asymmetry).
        // Active only during the build; locked to 1 (flatline) once the whiteout begins.
        float pulse = 1f;
        if (floodT > 0.001f && floodT < 0.8f)
        {
            float phase = (Time.time * photoLightPulseHz) % 1f; // wrapped — no unbounded timer
            float beat  = Mathf.Exp(-phase * 4f);               // 1 → ~0.02 over the cycle
            pulse = 1f + photoLightPulseAmp * (beat - 0.3f);
        }

        photoLightHD.SetIntensity(rampT * photoLightPeakLumens * pulse, UnityEngine.Rendering.LightUnit.Lumen);
        // Volumetric KILLED (0): the point light's fog ball WAS the resplandor BEHIND the photo.
        // The resplandor now renders IN FRONT via the glow-halo quad (SetupGlowHalo). The light
        // still lights room surfaces subtly (intensity above) — just no visible ball behind the photo.
        photoLightHD.volumetricDimmer = 0f;

        if (photoLightComp != null)
        {
            float colorT = Mathf.InverseLerp(0.65f, 0.95f, floodT);
            photoLightComp.color = Color.Lerp(photoLightWarmColor, Color.white, colorT);
        }

        // Follow the photo (it can micro-pulse; safe during the dead-freeze too).
        photoLightGO.transform.position = ComputePhotoLightPosition();
    }

    private void DestroyPhotoLight()
    {
        if (photoLightGO != null)
        {
            Destroy(photoLightGO);
            photoLightGO = null;
            photoLightComp = null;
            photoLightHD = null;
        }
    }

    // Spawn the ephemeral memory-emerge AudioSource. Parented to THIS portal (not the photo —
    // the photo is destroyed at commit), 2D (the "from the photo" read is the AV-lock, not 3D
    // panning), looped, starting at the WHISPER volume (the photo murmurs while inspected).
    private void SetupMemoryEmergeAudio()
    {
        // Defensive: kill any previous instance first (re-entrant context-set must not leak GOs).
        ResetMemoryEmergeAudio();

        if (currentPhotoData == null || currentPhotoData.MemoryEmergenceClip == null) return;

        GameObject go = new GameObject("MemoryEmergeAudio_Ephemeral");
        go.transform.SetParent(transform, false);

        emergeSource = go.AddComponent<AudioSource>();
        emergeSource.clip          = currentPhotoData.MemoryEmergenceClip;
        emergeSource.loop          = true;
        emergeSource.playOnAwake   = false;
        emergeSource.spatialBlend  = 0f;
        emergeSource.volume        = emergeWhisperVolume;  // whispering from frame 0 of inspection
        if (photoSoundMixerGroup != null)
            emergeSource.outputAudioMixerGroup = photoSoundMixerGroup;

        if (currentPhotoData.EmergenceClipNeedsGhostFx)
        {
            // RUNTIME GHOST CHAIN — for RAW clips only (per-photo flag on PhotoData).
            // Aggressive by design: HP180→LP500 at whisper = a narrow band; reverb dry at
            // -20 dB. A clip PRE-PROCESSED in a DAW (already ghostly, energy in the highs)
            // gets near-SILENCED by this — baked clips keep the flag OFF (clean playback).
            // Highpass FIRST in the DSP chain — thins the low body so the memory feels
            // disembodied ("remembered, not heard"). Unity processes filters in add-order.
            emergeHighPass = go.AddComponent<AudioHighPassFilter>();
            emergeHighPass.cutoffFrequency    = emergeHighPassCutoff;
            emergeHighPass.highpassResonanceQ = emergeHighPassResonanceQ;

            emergeLowPass = go.AddComponent<AudioLowPassFilter>();
            emergeLowPass.cutoffFrequency = emergeMinCutoff; // muffled/distant at start

            // Cache logs for the perceptually-correct exponential cutoff ramp (no per-frame Log()).
            // Uses the emerge-only floor/ceiling so voices never fully clarify (ghostly read preserved).
            emergeLogMinCutoff = Mathf.Log(Mathf.Max(emergeMinCutoff, 1f));
            emergeLogMaxCutoff = Mathf.Log(Mathf.Max(emergeMaxCutoff, 1f));

            // Ghost FX chain: source → lowpass (already added) → chorus → reverb.
            emergeChorus = go.AddComponent<AudioChorusFilter>();
            emergeChorus.dryMix  = 1f - emergeChorusWetMix;
            emergeChorus.wetMix1 = emergeChorusWetMix;
            emergeChorus.wetMix2 = 0f;
            emergeChorus.wetMix3 = 0f;
            emergeChorus.delay   = 30f;
            emergeChorus.rate    = emergeChorusRate;
            emergeChorus.depth   = emergeChorusDepth;

            emergeReverb = go.AddComponent<AudioReverbFilter>();
            emergeReverb.reverbPreset    = AudioReverbPreset.User; // REQUIRED first — else parameter writes are ignored
            emergeReverb.decayTime       = emergeReverbDecayTime;
            emergeReverb.decayHFRatio    = emergeReverbDecayHFRatio;
            emergeReverb.room            = emergeReverbRoom;
            emergeReverb.roomHF          = emergeReverbRoomHF;
            emergeReverb.reflectionsLevel = emergeReverbReflections;
            emergeReverb.reverbLevel     = emergeReverbWetLevel;
            emergeReverb.dryLevel        = emergeReverbDryLevel;
        }
        // else: pre-processed clip → NO filters. The whisper→full volume swell (and the tape
        // pitch LFO) still run; ApplyMemoryEmergeAudio/WhisperReturnRoutine null-check the LP.

        emergeSource.pitch = emergePitch; // fixed detune set before Play() so frame 0 isn't pitch 1.0

        emergeSource.Play(); // already whispering — the photo murmurs its memory in your hands
    }

    // Drive the emerge sound from the SAME curve as the visual membrane — volume SWELLS from the
    // inspection whisper up to full + the lowpass opens (distant→present) → causally locked to
    // the visuals = "the sound from the photo". Plays the full hold; cut only with the white.
    private void ApplyMemoryEmergeAudio(float linearProgress)
    {
        if (emergeSource == null) return;

        float awaken = photoAwakenCurve.Evaluate(linearProgress);
        // awaken=0 → exactly the whisper (no jump when the hold starts).
        emergeSource.volume = Mathf.Lerp(emergeWhisperVolume, photoSoundTargetVolume, awaken);
        if (emergeLowPass != null)
            emergeLowPass.cutoffFrequency = Mathf.Exp(Mathf.Lerp(emergeLogMinCutoff, emergeLogMaxCutoff, awaken));

        ApplyEmergePitchLFO();
    }

    // Tape wow/flutter — a slow pitch drift around the base detune. Absolute-time LFO (phase
    // independent of the hold) shared by the hold swell AND the idle whisper, so the tape
    // instability never snaps when transitioning between states.
    private void ApplyEmergePitchLFO()
    {
        if (emergeSource == null) return;
        float lfo = Mathf.Sin(Time.time * emergePitchLFORate * Mathf.PI * 2f);
        emergeSource.pitch = emergePitch + lfo * emergePitchLFOAmplitude;
    }

    // Release-before-commit: ramp the sound back DOWN to the inspection whisper (volume + lowpass).
    // The memory does not die — it recedes into the photo again.
    private void StartWhisperReturn()
    {
        if (emergeSource == null) return;
        if (whisperReturnCo != null) StopCoroutine(whisperReturnCo);
        whisperReturnCo = StartCoroutine(WhisperReturnRoutine());
    }

    private IEnumerator WhisperReturnRoutine()
    {
        float dur      = Mathf.Max(emergeWhisperReturnTime, 0.01f);
        float startVol = emergeSource.volume;
        float startCut = emergeLowPass != null ? emergeLowPass.cutoffFrequency : emergeMinCutoff;
        float elapsed  = 0f;

        while (elapsed < dur && emergeSource != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / dur);
            emergeSource.volume = Mathf.Lerp(startVol, emergeWhisperVolume, t);
            if (emergeLowPass != null)
                emergeLowPass.cutoffFrequency = Mathf.Lerp(startCut, emergeMinCutoff, t);
            yield return null;
        }
        whisperReturnCo = null;
    }

    // Stop + destroy the ephemeral emerge AudioSource. Runs on exit-inspection (Cleanup), at the
    // end of the white fade (FadeEmergeWithWhite), and defensively on re-setup/disable.
    private void ResetMemoryEmergeAudio()
    {
        if (whisperReturnCo != null)
        {
            StopCoroutine(whisperReturnCo);
            whisperReturnCo = null;
        }
        if (emergeSource != null)
        {
            emergeSource.Stop();
            Destroy(emergeSource.gameObject);
            emergeSource = null;
            emergeLowPass = null;
            emergeHighPass = null;
            emergeReverb = null;
            emergeChorus = null;
        }
    }

    // Fired by SceneControllerManager when the white curtain begins fading out (after the load +
    // reposition + hold). Fades the emerge sound to TRUE silence in sync with the white:
    //   • SmoothStep volume curve — zero slope at BOTH ends, so the landing at 0 is gentle
    //     (the old √ equal-power curve plunged at the very end and read as a cut).
    //   • After reaching 0, the source GO stays alive (muted) for a tail-grace window so the
    //     reverb's 5s decay rings out naturally — destroying it at volume 0 truncated the
    //     still-audible wet tail, which was the audible "click" at the end of the transition.
    private void HandleRevealStarted(float fadeDuration)
    {
        if (SceneControllerManager.instance != null)
            SceneControllerManager.instance.OnPhotoRevealStarted -= HandleRevealStarted;
        if (emergeSource != null)
            StartCoroutine(FadeEmergeWithWhite(fadeDuration));
        else
            ResetMemoryEmergeAudio(); // nothing to fade — just clean up state
    }

    private IEnumerator FadeEmergeWithWhite(float duration)
    {
        if (emergeSource == null) yield break;
        duration = Mathf.Max(duration, 0.01f);
        float startVol = emergeSource.volume;
        float elapsed  = 0f;
        while (elapsed < duration && emergeSource != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            emergeSource.volume = Mathf.SmoothStep(startVol, 0f, t); // gentle take-off AND landing
            yield return null;
        }

        // Silence reached — let the reverb tail ring out before destroying the chain.
        if (emergeSource != null)
        {
            emergeSource.volume = 0f;
            yield return new WaitForSeconds(emergeReverbTailGrace);
        }
        ResetMemoryEmergeAudio();
    }

    #endregion

    #region Camera & Blur

    private void ResetCameraEffects()
    {
        // Vision Invertida: turn the grade pass off (replaces the old MemoryCameraEffects reset).
        if (MemoryTransitionPass.instance != null)
            MemoryTransitionPass.instance.ForceOff();
    }

    private void ResetBlurIntensity()
    {
        if (BlurManager.instance != null)
            BlurManager.instance.ResetBlurIntensity();
    }

    #endregion

    #region Front Face Detection

    private void CacheFrontFaceNormal()
    {
        cachedFaceTransform = null;
        cachedLocalFaceNormal = Vector3.forward;

        Transform objTransform = inspectObject.InspectedObjectTransform;
        if (objTransform == null) return;

        // Prefer configurable LocalFaceNormal from InspectableInteractableObject
        if (objTransform.TryGetComponent<InspectableInteractableObject>(out var inspectable))
        {
            cachedFaceTransform = objTransform;
            cachedLocalFaceNormal = inspectable.LocalFaceNormal;
            return;
        }

        // Fallback: cachedFaceTransform stays null → Update uses PhotoData.FrontFaceDirection
    }

    #endregion

    #region Hints & Audio

    private void RemoveHint()
    {
        if (enterMemoryHint != null)
        {
            if (UIButtonsManager.instance != null)
                UIButtonsManager.instance.RemoveAdditionalHint(enterMemoryHint);
            enterMemoryHint = null;
        }
    }

    private void StartHoldAudio()
    {
        if (holdSound != null && audioSource != null)
        {
            audioSource.clip = holdSound;
            audioSource.loop = true;
            audioSource.Play();
        }
    }

    private void StopHoldAudio()
    {
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
    }

    #endregion
}
