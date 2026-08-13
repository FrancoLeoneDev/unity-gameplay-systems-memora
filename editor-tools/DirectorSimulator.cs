using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Memora.DirectorCore;

/// <summary>
/// SIMULADOR DE PARTIDAS (herramienta de balance de Franco — sin entrar a Play):
/// un "jugador fantasma" recorre las zonas REALES de la escena abierta con un perfil de juego,
/// y el MOTOR REAL del director (DirectorScheduleEngine + EventSelector + cooldowns + racionamiento)
/// decide qué dispara. Corre N partidas de ~33 min en segundos y reporta:
/// cuántos eventos por partida (mín/promedio/máx), cuáles, en qué zona y de qué tier.
///
/// Uso: abrir MainHouseTesis → menú Memora → Director → Simulador de Partidas → [Simular].
/// Lee los eventos CABLEADOS de la escena abierta (0 eventos = reporte en 0: cablear primero).
/// Supuestos (documentados en el reporte): gate de distancia ignorado (jugador "en rango"),
/// eventos con LiberationGate excluidos (dependen de beats), supresión por beats no simulada.
/// </summary>
public sealed class DirectorSimulator : EditorWindow
{
    private enum PlayerProfile { Explorador, Corredor }

    private int runs = 100;
    private float durationMin = 33f;
    private float phase2Min = 14f;
    private float phase3Min = 26f;
    private PlayerProfile profile = PlayerProfile.Explorador;
    private bool useFixedSeed;
    private int fixedSeed = 12345;
    [Tooltip("Cuántas de las primeras partidas se listan evento por evento, con minuto y zona.")]
    private int detailRuns = 3;

    private Vector2 _scroll;
    private string _results = "";

    [MenuItem("Memora/Director/Simulador de Partidas")]
    private static void Open() => GetWindow<DirectorSimulator>("Simulador Director");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Simula N partidas SIN Play con el motor real y los eventos cableados en la escena ABIERTA. " +
            "Abrí MainHouseTesis antes de simular.", MessageType.Info);
        runs = EditorGUILayout.IntSlider("Partidas", runs, 10, 500);
        durationMin = EditorGUILayout.Slider("Duración (min)", durationMin, 10f, 60f);
        phase2Min = EditorGUILayout.Slider("Fase 2 en el minuto", phase2Min, 1f, 60f);
        phase3Min = EditorGUILayout.Slider("Fase 3 en el minuto", phase3Min, 1f, 60f);
        profile = (PlayerProfile)EditorGUILayout.EnumPopup("Perfil del jugador", profile);
        useFixedSeed = EditorGUILayout.Toggle("Seed fija (reproducible)", useFixedSeed);
        if (useFixedSeed) fixedSeed = EditorGUILayout.IntField("Seed", fixedSeed);

        if (GUILayout.Button("Simular", GUILayout.Height(30)))
            _results = Simulate();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_results, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ── Vida Lejana (Sistema 2) ───────────────────────────────────────────────
    // Espeja VidaLejanaDirector.Evaluate(): cambios silenciosos en zonas que el jugador DEJÓ.
    // Ojo: la zona de un target es la del OBJETO, al revés que en el Sistema 1.

    private sealed class SimVLTarget
    {
        public string Name;
        public DirectorZone Zone;
        public VidaLejanaTone Tone;
        public int MinPhase;
        public bool Applied;
    }

    private sealed class SimVLSettings
    {
        public float TFloor = 90f, TStep = 90f, EvalInterval = 2f;
        public int BMaxPerZone = 2, CapPerZone = 3, GlobalCap = 8;
        public int WNeutro = 5, WDuelo = 3, WInquietante = 2;

        public int Weight(VidaLejanaTone t) => t == VidaLejanaTone.Neutro ? WNeutro
                                             : t == VidaLejanaTone.Duelo ? WDuelo : WInquietante;
    }

    private static SimVLSettings ReadVLSettings(out List<SimVLTarget> targets)
    {
        targets = new List<SimVLTarget>();
        foreach (var t in UnityEngine.Object.FindObjectsByType<VidaLejanaTarget>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            targets.Add(new SimVLTarget { Name = t.gameObject.name, Zone = t.Zone, Tone = t.Tone, MinPhase = t.MinPhase });

        var s = new SimVLSettings();
        var dir = UnityEngine.Object.FindFirstObjectByType<VidaLejanaDirector>();
        if (dir == null) return s;

        var so = new SerializedObject(dir);
        float F(string n, float d) { var p = so.FindProperty(n); return p != null ? p.floatValue : d; }
        int I(string n, int d) { var p = so.FindProperty(n); return p != null ? p.intValue : d; }
        s.TFloor = F("tFloor", s.TFloor);
        s.TStep = F("tStep", s.TStep);
        s.EvalInterval = F("evalInterval", s.EvalInterval);
        s.BMaxPerZone = I("bMaxPerZone", s.BMaxPerZone);
        s.CapPerZone = I("capPerZone", s.CapPerZone);
        s.GlobalCap = I("globalCapPerSession", s.GlobalCap);
        s.WNeutro = I("weightNeutro", s.WNeutro);
        s.WDuelo = I("weightDuelo", s.WDuelo);
        s.WInquietante = I("weightInquietante", s.WInquietante);
        return s;
    }

    // ── Definición estática de un evento cableado (leída de la escena por SerializedObject) ──
    private sealed class SimEventDef
    {
        public string Name;
        public DirectorZone[] Zones;   // zonas del JUGADOR desde las que es elegible ('None' = global)
        public int Weight;
        public EventTier Tier;
        public DiegeticSource Source;
        public float Cooldown;
        public bool OneShot;
        public int MinPhase;
        public bool Muted;   // PositionalAudioEvent sin clips => CanTrigger false
        public bool Gated;   // con LiberationGate => depende de beats, excluido de la sim
    }

    /// <summary>¿El evento es elegible desde esa zona? (multi-zona: basta con que la liste).</summary>
    private static bool PerceivedFrom(SimEventDef def, DirectorZone zone)
    {
        if (def.Zones == null) return false;
        for (int i = 0; i < def.Zones.Length; i++) if (def.Zones[i] == zone) return true;
        return false;
    }

    private static DirectorZone[] ToArray(System.Collections.Generic.IReadOnlyList<DirectorZone> zones)
    {
        if (zones == null) return System.Array.Empty<DirectorZone>();
        var copy = new DirectorZone[zones.Count];
        for (int i = 0; i < zones.Count; i++) copy[i] = zones[i];
        return copy;
    }

    // ── Instancia por-partida ──
    private sealed class SimEvent : ISelectableEvent
    {
        public SimEventDef Def;
        public bool Present;          // ausencia-por-partida 25%
        public bool Fired;            // one-shot
        public float CooldownUntil;
        public Func<int> GetPhase;

        public int Weight => Def.Weight;
        public EventTier Tier => Def.Tier;
        public DiegeticSource Source => Def.Source;
        public bool IsEligible(float now) =>
            Present && !Def.Muted && !Def.Gated && !(Def.OneShot && Fired)
            && now >= CooldownUntil && GetPhase() >= Def.MinPhase;
    }

    private sealed class SimProbe : IOpportunityProbe
    {
        public bool Still;
        public float SinceInteraction = 9999f;
        public bool IsPlayerStill => Still;
        public float TimeSinceLastInteraction => SinceInteraction;
        public bool IsPlayerBusy => false;
    }

    /// <summary>Réplica del IEventGate del ZoneEventDirector: cooldown (fuente,tier) + racionamiento PA.</summary>
    private sealed class SimGate : IEventGate
    {
        public DirectorConfig Config;
        public SourceTierCooldown Cooldown = new SourceTierCooldown();
        public float SessionStart;
        public int PAFired;
        public float LastPAFire = -9999f;
        public Func<int> GetPhase;
        public Func<float> GetNow;

        public bool IsBlocked(ISelectableEvent ev, float now)
        {
            if (Cooldown.IsOnCooldown(ev.Source, ev.Tier, now, Config.CooldownForSource(ev.Source)))
                return true;
            if (ev.Tier == EventTier.PhysicalAggressive &&
                !JumpscareRationing.IsAllowed(now, SessionStart, Config.PAUnlockAfterSec,
                    PAFired, Config.MaxPAPerSession, GetPhase(), Config.PAMinPhase,
                    0f, LastPAFire, Config.PostPACooldownSec))
                return true;
            return false;
        }
    }

    private string Simulate()
    {
        // ── Recolección de la escena abierta ──
        var director = UnityEngine.Object.FindObjectsByType<ZoneEventDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        DirectorConfig cfg = null;
        if (director.Length > 0)
            cfg = new SerializedObject(director[0]).FindProperty("config").objectReferenceValue as DirectorConfig;
        if (cfg == null)
            cfg = AssetDatabase.LoadAssetAtPath<DirectorConfig>("Assets/ScriptableObjects/DirectorConfig.asset");
        if (cfg == null) return "ERROR: no encontré DirectorConfig (ni en la escena ni en Assets/ScriptableObjects).";

        var defs = new List<SimEventDef>();
        int muted = 0, gated = 0, zoneless = 0;
        foreach (var he in UnityEngine.Object.FindObjectsByType<HorrorEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var so = new SerializedObject(he);
            var def = new SimEventDef
            {
                Name = he.gameObject.name + " (" + he.GetType().Name + ")",
                Zones = ToArray(he.Zones),   // API pública: la lista de zonas ya está expuesta por HorrorEvent
                Weight = Mathf.Max(1, so.FindProperty("weight").intValue),
                Tier = (EventTier)so.FindProperty("tier").enumValueIndex,
                Source = (DiegeticSource)so.FindProperty("source").enumValueIndex,
                Cooldown = so.FindProperty("localCooldown").floatValue,
                OneShot = so.FindProperty("oneShot").boolValue,
                MinPhase = so.FindProperty("minPhase").intValue,
                Gated = so.FindProperty("liberationGate").objectReferenceValue != null,
            };
            if (he is PositionalAudioEvent)
            {
                // Mudo = ni clips propios NI tipo de AudioLibrary. Los globales no llevan clips: declaran
                // su tipo y la biblioteca resuelve, así que mirar solo el array los daba por mudos y los
                // dejaba afuera de la simulación entera.
                bool clipsPropios = so.FindProperty("clips").arraySize > 0;
                var propTipo = so.FindProperty("soundType");
                bool tipoDeclarado = propTipo != null
                    && (AudioLibrary.DirectorSounds)propTipo.enumValueIndex != AudioLibrary.DirectorSounds.None;
                def.Muted = !clipsPropios && !tipoDeclarado;
            }
            if (def.Muted) muted++;
            if (def.Gated) gated++;
            if (def.Zones.Length == 0) zoneless++;   // sin zonas no entra a ninguna bolsa: silencio invisible
            defs.Add(def);
        }

        var zones = new List<DirectorZone>();
        var bounds = new Dictionary<DirectorZone, Bounds>();
        foreach (var zbt in UnityEngine.Object.FindObjectsByType<ZoneBoundaryTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var col = zbt.GetComponent<BoxCollider>();
            if (col == null) continue;
            if (!bounds.ContainsKey(zbt.Zone)) { zones.Add(zbt.Zone); bounds[zbt.Zone] = col.bounds; }
        }
        if (zones.Count == 0) return "ERROR: la escena abierta no tiene ZoneBoundaryTriggers (¿abriste MainHouseTesis?).";

        // Grafo de vecindad para el random-walk del fantasma (boxes que se tocan).
        var adjacency = new Dictionary<DirectorZone, List<DirectorZone>>();
        foreach (var a in zones)
        {
            var list = new List<DirectorZone>();
            Bounds ea = bounds[a]; ea.Expand(1.5f);
            foreach (var b in zones) if (a != b && ea.Intersects(bounds[b])) list.Add(b);
            adjacency[a] = list;
        }

        // ── Correr N partidas ──
        var totals = new List<int>();
        var perEvent = new Dictionary<string, int>();
        var perZone = new Dictionary<DirectorZone, int>();
        var perTier = new Dictionary<EventTier, int>();
        var baseRng = new System.Random(useFixedSeed ? fixedSeed : Environment.TickCount);

        var detalle = new StringBuilder();
        var vlTotals = new List<int>();
        for (int run = 0; run < runs; run++)
        {
            int seed = useFixedSeed ? fixedSeed + run : baseRng.Next(int.MinValue, int.MaxValue);

            // Las primeras N partidas se registran evento por evento: el agregado dice CUÁNTOS,
            // pero para ver si una corrida concreta tiene sentido hace falta el orden y los huecos.
            List<string> log = run < detailRuns ? new List<string>() : null;
            int fired = SimulateOne(seed, cfg, defs, zones, adjacency, perEvent, perZone, perTier, out int vl, log);
            totals.Add(fired);
            vlTotals.Add(vl);

            if (log != null)
            {
                log.Sort(StringComparer.Ordinal);   // las dos capas se registran por separado: ordenar por minuto
                detalle.AppendLine($"── PARTIDA {run + 1} (seed {seed}) — {fired} del Director + {vl} de Vida Lejana ──");
                foreach (var linea in log) detalle.AppendLine("   " + linea);
                detalle.AppendLine();
            }
        }

        // ── Reporte ──
        totals.Sort();
        float avg = 0f; foreach (int t in totals) avg += t; avg /= totals.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"SIMULACIÓN — {runs} partidas de {durationMin:F0} min · perfil {profile} · F2@{phase2Min:F0}' F3@{phase3Min:F0}'");
        sb.AppendLine($"Eventos cableados: {defs.Count} (mudos sin clips: {muted} · con gate/beat: {gated} — ambos excluidos de la sim)");
        if (zoneless > 0)
            sb.AppendLine($"⚠ SIN ZONAS: {zoneless} evento(s) no listan ninguna zona — no entran a ninguna bolsa y NUNCA disparan.");
        sb.AppendLine();
        sb.AppendLine($"EVENTOS POR PARTIDA →  mín {totals[0]}  ·  mediana {totals[totals.Count / 2]}  ·  promedio {avg:F1}  ·  máx {totals[totals.Count - 1]}");

        vlTotals.Sort();
        float vlAvg = 0f; foreach (int v in vlTotals) vlAvg += v; vlAvg /= Math.Max(1, vlTotals.Count);
        sb.AppendLine($"VIDA LEJANA POR PARTIDA →  mín {vlTotals[0]}  ·  mediana {vlTotals[vlTotals.Count / 2]}  ·  promedio {vlAvg:F1}  ·  máx {vlTotals[vlTotals.Count - 1]}");
        sb.AppendLine($"TOTAL que percibe el jugador →  ~{avg + vlAvg:F1} por partida " +
                      $"({avg:F1} presenciados + {vlAvg:F1} descubiertos al volver)");
        sb.AppendLine();
        sb.AppendLine("Por tier:");
        foreach (var kv in perTier) sb.AppendLine($"  {kv.Key}: {kv.Value} ({kv.Value / (float)runs:F1}/partida)");
        sb.AppendLine();
        sb.AppendLine("Por zona (total en todas las partidas):");
        foreach (var kv in perZone) sb.AppendLine($"  {kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("Por evento:");
        foreach (var kv in perEvent) sb.AppendLine($"  {kv.Key}: {kv.Value} ({kv.Value / (float)runs:F2}/partida)");
        if (detalle.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"════ DETALLE DE LAS PRIMERAS {detailRuns} PARTIDAS ════");
            sb.AppendLine();
            sb.Append(detalle);
        }
        sb.AppendLine();
        sb.AppendLine("Supuestos: distancia al jugador ignorada; supresión por beats no simulada; ausencia-por-partida 25% activa.");
        sb.AppendLine("Vida Lejana (líneas con ~): respeta zona actual, vecindad, presupuesto de ausencia y caps. " +
                      "NO simula el cono de visión (el fantasma no tiene cámara), así que en el juego real " +
                      "dispara UN POCO menos que acá.");
        Debug.Log($"[DirectorSimulator] {runs} partidas: mín {totals[0]} · prom {avg:F1} · máx {totals[totals.Count - 1]}");
        return sb.ToString();
    }

    private int SimulateOne(int seed, DirectorConfig cfg, List<SimEventDef> defs, List<DirectorZone> zones,
        Dictionary<DirectorZone, List<DirectorZone>> adjacency,
        Dictionary<string, int> perEvent, Dictionary<DirectorZone, int> perZone, Dictionary<EventTier, int> perTier,
        out int vlFired, List<string> log = null)
    {
        vlFired = 0;
        var clock = new FakeDirectorClock();
        var probe = new SimProbe();
        var settings = cfg.ToScheduleSettings();
        var walkRng = new System.Random(seed + 7);
        var jitterRng = new System.Random(seed + 1);

        int phase = 1;
        DirectorZone currentZone = zones.Contains(DirectorZone.Entrada) ? DirectorZone.Entrada : zones[0];

        var gate = new SimGate { Config = cfg, SessionStart = 0f, GetPhase = () => phase, GetNow = () => clock.Now };

        // Instancias por partida + ausencia 25% (misma regla que el ZoneEventDirector).
        var absenceRng = new System.Random(seed + 1000);
        var simEvents = new List<SimEvent>();
        foreach (var d in defs)
            simEvents.Add(new SimEvent { Def = d, Present = absenceRng.NextDouble() >= 0.25, GetPhase = () => phase });

        // Selectores por zona: pool = eventos de la zona + globales (respetando la zona segura).
        var selectors = new Dictionary<DirectorZone, EventSelector>();
        var globals = simEvents.FindAll(e => PerceivedFrom(e.Def, DirectorZone.None));
        foreach (var z in zones)
        {
            var pool = new List<ISelectableEvent>();
            foreach (var e in simEvents) if (PerceivedFrom(e.Def, z)) pool.Add(e);
            // Mismo criterio que el ZoneEventDirector: un global que además lista la zona no entra dos veces.
            if (!cfg.IsZoneExcludedFromGlobals(z))
                foreach (var g in globals) if (!pool.Contains(g)) pool.Add(g);
            if (pool.Count > 0) selectors[z] = new EventSelector(pool, seed + (int)z, gate);
        }

        int fired = 0;
        DirectorScheduleEngine engine = null;
        Func<FireReason, OpportunitySignal, bool> tryFire = (reason, signal) =>
        {
            if (!selectors.TryGetValue(currentZone, out var sel)) return false;
            var e = sel.SelectNext(clock.Now) as SimEvent;
            if (e == null) return false;
            float jit = 1f + ((float)jitterRng.NextDouble() * 2f - 1f) * 0.25f;
            e.CooldownUntil = clock.Now + e.Def.Cooldown * jit;
            if (e.Def.OneShot) e.Fired = true;
            gate.Cooldown.RegisterFire(e.Def.Source, e.Def.Tier, clock.Now);
            if (e.Def.Tier == EventTier.PhysicalAggressive) { gate.PAFired++; gate.LastPAFire = clock.Now; }
            fired++;
            perEvent[e.Def.Name] = perEvent.TryGetValue(e.Def.Name, out int c1) ? c1 + 1 : 1;
            perZone[currentZone] = perZone.TryGetValue(currentZone, out int c2) ? c2 + 1 : 1;
            perTier[e.Def.Tier] = perTier.TryGetValue(e.Def.Tier, out int c3) ? c3 + 1 : 1;

            if (log != null)
            {
                int min = (int)(clock.Now / 60f);
                int seg = (int)(clock.Now % 60f);
                log.Add($"{min:00}:{seg:00}  F{phase}  [{currentZone}]  {e.Def.Name}  ({e.Def.Tier})");
            }
            return true;
        };

        engine = new DirectorScheduleEngine(clock, probe, new SystemRandomSource(seed), settings, tryFire);
        engine.Unlock();
        engine.AdvancePhase(1);

        // ── Vida Lejana: estado de la partida ─────────────────────────────────
        var vlSet = ReadVLSettings(out List<SimVLTarget> vlTargets);
        var vlRng = new System.Random(seed + 555);
        var lastLeft = new Dictionary<DirectorZone, float>();      // cuándo el jugador dejó cada zona
        var vlPorZona = new Dictionary<DirectorZone, int>();
        int vlTotal = 0;
        float vlAccum = 0f;

        Func<DirectorZone, bool> esVecina = z =>
        {
            var adj = adjacency[currentZone];
            for (int i = 0; i < adj.Count; i++) if (adj[i] == z) return true;
            return false;
        };

        Action EvaluarVidaLejana = () =>
        {
            if (vlTotal >= vlSet.GlobalCap) return;

            foreach (var zona in zones)
            {
                if (zona == DirectorZone.None || zona == currentZone) continue;
                if (esVecina(zona)) continue;
                if (!lastLeft.TryGetValue(zona, out float dejada)) continue;   // nunca la abandonó

                int aplicados = vlPorZona.TryGetValue(zona, out int a) ? a : 0;
                if (aplicados >= vlSet.CapPerZone) continue;
                if (aplicados >= AbsenceBudget.Compute(clock.Now - dejada, vlSet.TFloor, vlSet.TStep, vlSet.BMaxPerZone)) continue;

                // Selección pesada por tono entre los elegibles de esa zona.
                int totalW = 0;
                for (int i = 0; i < vlTargets.Count; i++)
                {
                    var t = vlTargets[i];
                    if (t.Applied || t.Zone != zona || t.MinPhase > phase) continue;
                    totalW += vlSet.Weight(t.Tone);
                }
                if (totalW <= 0) continue;

                int roll = vlRng.Next(totalW);
                SimVLTarget elegido = null;
                for (int i = 0; i < vlTargets.Count; i++)
                {
                    var t = vlTargets[i];
                    if (t.Applied || t.Zone != zona || t.MinPhase > phase) continue;
                    roll -= vlSet.Weight(t.Tone);
                    if (roll < 0) { elegido = t; break; }
                }
                if (elegido == null) continue;

                elegido.Applied = true;
                vlPorZona[zona] = aplicados + 1;
                vlTotal++;

                if (log != null)
                {
                    int m = (int)(clock.Now / 60f), sg = (int)(clock.Now % 60f);
                    log.Add($"{m:00}:{sg:00}  F{phase}  [{zona}]  ~ {elegido.Name}  (VIDA LEJANA · {elegido.Tone})");
                }
                if (vlTotal >= vlSet.GlobalCap) return;
            }
        };

        // Perfil del fantasma.
        bool explorer = profile == PlayerProfile.Explorador;
        float DwellDraw() => explorer ? 25f + (float)walkRng.NextDouble() * 35f : 8f + (float)walkRng.NextDouble() * 10f;
        float dwell = DwellDraw();
        float stillLeft = 0f;
        float duration = durationMin * 60f;
        float p2 = phase2Min * 60f, p3 = phase3Min * 60f;
        const float dt = 0.25f;

        for (float t = 0f; t < duration; t += dt)
        {
            clock.Advance(dt);

            // Escalada por fase (aprox. de las vueltas de recuerdo) + gap corto de regreso.
            if (phase < 2 && clock.Now >= p2) { phase = 2; engine.AdvancePhase(2); engine.ResumeWithGap(cfg.ResumeGapSec); }
            if (phase < 3 && clock.Now >= p3) { phase = 3; engine.AdvancePhase(3); engine.ResumeWithGap(cfg.ResumeGapSec); }

            // Movimiento del fantasma entre zonas vecinas.
            dwell -= dt;
            if (dwell <= 0f)
            {
                var adj = adjacency[currentZone];
                lastLeft[currentZone] = clock.Now;   // Vida Lejana solo toca zonas ya abandonadas
                currentZone = adj.Count > 0 ? adj[walkRng.Next(adj.Count)] : zones[walkRng.Next(zones.Count)];
                engine.OnZoneChanged();
                dwell = DwellDraw();
            }

            // Vida Lejana corre en su propio intervalo, independiente del cronograma del Director.
            vlAccum += dt;
            if (vlAccum >= vlSet.EvalInterval) { vlAccum = 0f; EvaluarVidaLejana(); }

            // Episodios de quietud (el explorador frena a mirar; el corredor casi nunca).
            if (stillLeft > 0f) stillLeft -= dt;
            else if (walkRng.NextDouble() < (explorer ? 0.010 : 0.002))
                stillLeft = 3f + (float)walkRng.NextDouble() * 5f;
            probe.Still = stillLeft > 0f;

            // Interacciones ocasionales (S3): el explorador toca cosas seguido.
            probe.SinceInteraction += dt;
            if (walkRng.NextDouble() < (explorer ? 0.006 : 0.002)) probe.SinceInteraction = 0f;

            engine.Tick();
        }
        vlFired = vlTotal;
        return fired;
    }
}
