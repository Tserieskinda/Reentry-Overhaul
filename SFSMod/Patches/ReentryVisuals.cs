using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using ModLoader.IO;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.World.Drag;
using UITools;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Type = SFS.UI.ModGUI.Type;

namespace SFSMod.Patches
{
    // ============================================================
    // SHARED PHYSICS HELPER - one place for "what is this rocket's
    // current velocity", instead of duplicating the Aero_Rocket cast
    // in both the particle code and the new velocity-based hue code.
    // ============================================================

    public static class ReentryPhysics
    {
        public static Vector2 GetVelocity(AeroModule instance)
        {
            return (instance is Aero_Rocket aeroRocket) ? aeroRocket.rocket.rb2d.linearVelocity : Vector2.zero;
        }
    }


    // ============================================================
    // BASE SHAPE - the game's own stock values, captured once.
    // Every layer (original or extra) scales FROM these, so nothing
    // compounds across ticks regardless of how many layers exist.
    // ============================================================

    public static class ReentryBaseShape
    {
        public static bool captured;

        public static Vector2 edgeSize;
        public static float edgeFadeX, edgeFadeM;

        public static float tailScale;
        public static float outerFadeX, outerFadeM;
        public static float tailAcceleration, tailInitialSlope;

        public static void CaptureIfNeeded(AeroData aeroData)
        {
            if (captured) return;
            captured = true;

            edgeSize = aeroData.reentry_Edge.size;
            edgeFadeX = aeroData.reentry_Edge.side_FadeX;
            edgeFadeM = aeroData.reentry_Edge.side_FadeM;

            tailScale = aeroData.reentry_Outer.tail_Scale;
            outerFadeX = aeroData.reentry_Outer.side_FadeX;
            outerFadeM = aeroData.reentry_Outer.side_FadeM;
            tailAcceleration = aeroData.reentry_Outer.tail_Acceleration;
            tailInitialSlope = aeroData.reentry_Outer.tail_InitialSlope;
        }
    }


    // ============================================================
    // REENTRY LAYER SETTINGS - now an open-ended list instead of
    // 4 fixed names. edgeA/outerA are the two special "original"
    // layers tied to the game's own real mesh (can't be removed).
    // Everything else is a duplicate, added/removed at runtime.
    // ============================================================

    public static class ReentryLayers
    {
        public class LayerSettings
        {
            public string id;
            public bool isEdge;
            public bool isOriginal;

            public float hue;
            public float coldHue;
            public float hueMinVel;
            public float hueMaxVel;
            public float brightness;
            public float opacity;
            public float offset;
            public float animationSpeed;
            public float posX;
            public float posY;

            public float widthScale;
            public float lengthScale;

            public float fadeXScale;
            public float fadeMScale;

            public float straightness;

            public int sortingOrder;
        }

        public static readonly LayerSettings edgeA = new LayerSettings
        {
            id = "edgeA", isEdge = true, isOriginal = true,
            hue = 20f, coldHue = 20f, hueMinVel = 200f, hueMaxVel = 1500f,
            brightness = 3f, opacity = 1f, offset = 0f, animationSpeed = 0f,
            widthScale = 0.7f, lengthScale = 0.7f, fadeXScale = 1f, fadeMScale = 1f,
            straightness = 1f, sortingOrder = 1
        };

        public static readonly LayerSettings outerA = new LayerSettings
        {
            id = "outerA", isEdge = false, isOriginal = true,
            hue = 260f, coldHue = 20f, hueMinVel = 200f, hueMaxVel = 1500f,
            brightness = 3f, opacity = 1f, offset = 0f, animationSpeed = 0f,
            widthScale = 0.7f, lengthScale = 0.6f, fadeXScale = 1f, fadeMScale = 1f,
            straightness = 1f, sortingOrder = 1
        };

        public static readonly List<LayerSettings> extraLayers = new List<LayerSettings>();

        private static int nextExtraId = 1;

        public static IEnumerable<LayerSettings> AllLayers()
        {
            yield return edgeA;
            yield return outerA;
            foreach (LayerSettings l in extraLayers)
                yield return l;
        }

        public static Dictionary<string, LayerSettings> byId =>
            AllLayers().ToDictionary(l => l.id, l => l, StringComparer.OrdinalIgnoreCase);

        /// <summary>Used by the "+" UI buttons / "addedge"/"addouter" console commands.</summary>
        public static LayerSettings AddLayer(bool isEdge)
        {
            string id = (isEdge ? "edge" : "outer") + nextExtraId;
            nextExtraId++;
            return AddLayerWithId(id, isEdge);
        }

        /// <summary>Used by Load() to recreate saved extra layers with their original ids.</summary>
        public static LayerSettings EnsureLayer(string id, bool isEdge)
        {
            if (byId.TryGetValue(id, out LayerSettings existing))
                return existing;

            // Keep future auto-generated ids from colliding with a loaded one.
            string digits = new string(id.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int n) && n >= nextExtraId)
                nextExtraId = n + 1;

            return AddLayerWithId(id, isEdge);
        }

        private static LayerSettings AddLayerWithId(string id, bool isEdge)
        {
            LayerSettings l = new LayerSettings
            {
                id = id, isEdge = isEdge, isOriginal = false,
                hue = isEdge ? 45f : 300f, coldHue = isEdge ? 45f : 20f, hueMinVel = 200f, hueMaxVel = 1500f,
                brightness = 1.2f, opacity = 0.5f, offset = 0f, animationSpeed = 0f,
                widthScale = 1.1f, lengthScale = 1.1f, fadeXScale = 1f, fadeMScale = 1f,
                straightness = 1f, sortingOrder = 0
            };
            extraLayers.Add(l);
            return l;
        }

        public static void RemoveLayer(LayerSettings layer)
        {
            if (layer.isOriginal) return; // never remove the real meshes
            extraLayers.Remove(layer);
            AeroMesh_DuplicateLayers.DestroyClonesFor(layer);
            meshOwner.RemoveAll(layer);
        }

        // --------------------------------------------------------
        // Which LayerSettings a given AeroMesh belongs to. Originals
        // are matched by exact name once, then cached; clones are
        // registered explicitly at creation time (see
        // AeroMesh_DuplicateLayers) - no string-guessing for those.
        // --------------------------------------------------------

        private static class meshOwner
        {
            private static readonly Dictionary<AeroMesh, LayerSettings> map =
                new Dictionary<AeroMesh, LayerSettings>();

            public static LayerSettings Get(AeroMesh mesh)
            {
                if (map.TryGetValue(mesh, out LayerSettings s))
                    return s;

                if (mesh.name == "Reentry Edge") return Set(mesh, edgeA);
                if (mesh.name == "Reentry Outer") return Set(mesh, outerA);
                return null;
            }

            public static LayerSettings Set(AeroMesh mesh, LayerSettings settings)
            {
                map[mesh] = settings;
                return settings;
            }

            public static void RemoveAll(LayerSettings layer)
            {
                foreach (AeroMesh key in map.Where(kv => kv.Value == layer).Select(kv => kv.Key).ToList())
                    map.Remove(key);
            }
        }

        public static LayerSettings GetSettings(AeroMesh mesh) => meshOwner.Get(mesh);

        public static void RegisterClone(AeroMesh clone, LayerSettings settings) =>
            meshOwner.Set(clone, settings);

        // --------------------------------------------------------
        // Current speed per mesh instance (original or clone), set
        // right before that mesh's GenerateMesh/SetTemperature runs
        // each tick (see AeroMesh_ShapeTuningA and
        // AeroMesh_DuplicateLayers), read back inside
        // ApplyPropertyBlock below to drive the cold/hot hue blend.
        // Keyed per-mesh rather than a single global value so multiple
        // simultaneous rockets each get their own correct speed, even
        // though the hueMinVel/hueMaxVel/coldHue RANGE settings
        // themselves are shared per LayerSettings (consistent with
        // brightness/width/etc already being shared that way).
        // --------------------------------------------------------

        private static readonly Dictionary<AeroMesh, float> currentSpeed = new Dictionary<AeroMesh, float>();

        public static void SetCurrentSpeed(AeroMesh mesh, float speed) => currentSpeed[mesh] = speed;

        private static float GetCurrentSpeed(AeroMesh mesh) =>
            currentSpeed.TryGetValue(mesh, out float s) ? s : 0f;

        static readonly int HueProp = Shader.PropertyToID("_Hue");
        static readonly int AlphaMultProp = Shader.PropertyToID("_AlphaMultiplier");
        static readonly int OffsetProp = Shader.PropertyToID("_Offset");

        // --------------------------------------------------------
        // FIX: no more base-position caching. AeroMesh.GenerateMesh
        // (called every tick, immediately before SetTemperature, for
        // both original meshes and clones) already force-sets
        // transform.position/eulerAngles in WORLD space, billboarded
        // purely to the velocity direction - independent of the
        // craft's own rotation and of any parent transform. There is
        // nothing to cache: GenerateMesh re-establishes ground truth
        // from scratch every tick.
        //
        // Previously this code wrote mesh.transform.localPosition
        // against a basePos captured once. Because localPosition is
        // resolved through the parent transform (which DOES rotate
        // with the craft), that stale local baseline diverged further
        // from correct the more the craft's actual rotation drifted
        // from whatever it was at capture time - small when stable,
        // large while tumbling. Instead we now rotate the raw offset
        // by the craft's current body angle and add it directly onto
        // the world position GenerateMesh just wrote this tick.
        // --------------------------------------------------------

        public static void ApplyPropertyBlock(AeroMesh mesh, LayerSettings settings)
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            mesh.meshRenderer.GetPropertyBlock(block);

            // Blend between coldHue (at/below hueMinVel) and hue (at/above
            // hueMaxVel) based on this mesh's current speed - e.g. stays
            // near-native/white-ish at low velocity instead of pink/purple
            // the instant any heating starts, and smoothly saturates into
            // the full tuned hue as speed climbs. Set hueMinVel ==
            // hueMaxVel (or leave hueMaxVel <= hueMinVel) to disable this
            // and just always show "hue" as before.
            float effectiveHue = settings.hue;
            if (settings.hueMaxVel > settings.hueMinVel)
            {
                float speed = GetCurrentSpeed(mesh);
                float t = Mathf.Clamp01((speed - settings.hueMinVel) / (settings.hueMaxVel - settings.hueMinVel));
                effectiveHue = Mathf.Lerp(settings.coldHue, settings.hue, t);
            }
            block.SetFloat(HueProp, effectiveHue);

            block.SetFloat(AlphaMultProp, settings.opacity);

            // animationSpeed drives real motion: the streak pattern
            // scrolls over time instead of sitting at one fixed offset.
            float animatedOffset = settings.offset + Time.time * settings.animationSpeed;
            block.SetFloat(OffsetProp, animatedOffset);

            mesh.meshRenderer.SetPropertyBlock(block);
            mesh.meshRenderer.sortingOrder = settings.sortingOrder;

            if (settings.posX != 0f || settings.posY != 0f)
            {
                // NOTE: assumes mesh.transform.parent tracks the craft's
                // actual body rotation (true for both original meshes
                // and clones, since clones are Instantiated with
                // original.transform.parent - see GetOrCreateClone
                // below). If in testing the offset stays glued to the
                // velocity/world frame instead of following the craft,
                // this angle needs to come from the AeroModule/Rocket
                // transform instead (threaded down from
                // AeroMesh_DuplicateLayers.Postfix's __instance).
                float craftAngleRad = mesh.transform.parent.eulerAngles.z * Mathf.Deg2Rad;
                float cos = Mathf.Cos(craftAngleRad);
                float sin = Mathf.Sin(craftAngleRad);
                Vector2 rotated = new Vector2(
                    settings.posX * cos - settings.posY * sin,
                    settings.posX * sin + settings.posY * cos
                );
                mesh.transform.position += (Vector3)rotated;
            }
        }

        public static readonly string[] ParamNames =
        {
            "hue", "coldhue", "huemin", "huemax",
            "brightness", "opacity", "offset", "speed",
            "posx", "posy",
            "width", "length", "fadex", "fadem", "straightness", "order"
        };

        public static void SetParam(LayerSettings layer, string parameter, float value)
        {
            switch (parameter)
            {
                case "hue": layer.hue = value; break;
                case "coldhue": layer.coldHue = value; break;
                case "huemin": layer.hueMinVel = value; break;
                case "huemax": layer.hueMaxVel = value; break;
                case "brightness": layer.brightness = value; break;
                case "opacity": layer.opacity = value; break;
                case "offset": layer.offset = value; break;
                case "speed": layer.animationSpeed = value; break;
                case "posx": layer.posX = value; break;
                case "posy": layer.posY = value; break;
                case "width": layer.widthScale = value; break;
                case "length": layer.lengthScale = value; break;
                case "fadex": layer.fadeXScale = value; break;
                case "fadem": layer.fadeMScale = value; break;
                case "straightness": layer.straightness = value; break;
                case "order": layer.sortingOrder = Mathf.RoundToInt(value); break;
            }
        }

        public static float GetParam(LayerSettings layer, string parameter)
        {
            switch (parameter)
            {
                case "hue": return layer.hue;
                case "coldhue": return layer.coldHue;
                case "huemin": return layer.hueMinVel;
                case "huemax": return layer.hueMaxVel;
                case "brightness": return layer.brightness;
                case "opacity": return layer.opacity;
                case "offset": return layer.offset;
                case "speed": return layer.animationSpeed;
                case "posx": return layer.posX;
                case "posy": return layer.posY;
                case "width": return layer.widthScale;
                case "length": return layer.lengthScale;
                case "fadex": return layer.fadeXScale;
                case "fadem": return layer.fadeMScale;
                case "straightness": return layer.straightness;
                case "order": return layer.sortingOrder;
                default: return 0f;
            }
        }
    }


    // ============================================================
    // BURN PARTICLES - reuses the game's own "effects/Burn"
    // WorldParticle prefab (already used for things like separator
    // sparks/debris), so particles inherit the game's own gravity,
    // drag, and floating-origin handling instead of us reinventing it.
    // ============================================================

    public static class ReentryBurnParticles
    {
        public static bool enabled = false;
        public static float particlesPerSecond = 20f;
        public static float velocityRange = 3f;
        public static float spawnSpread = 1.2f;

        private static WorldParticle prefab;
        private static bool loadFailed;
        private static float accumulator;

        public static readonly string[] ParamNames = { "enabled", "rate", "velocity", "spread" };

        public static void SetParam(string parameter, float value)
        {
            switch (parameter)
            {
                case "enabled": enabled = value >= 0.5f; break;
                case "rate": particlesPerSecond = value; break;
                case "velocity": velocityRange = value; break;
                case "spread": spawnSpread = value; break;
            }
        }

        public static float GetParam(string parameter)
        {
            switch (parameter)
            {
                case "enabled": return enabled ? 1f : 0f;
                case "rate": return particlesPerSecond;
                case "velocity": return velocityRange;
                case "spread": return spawnSpread;
                default: return 0f;
            }
        }

        public static void Tick(AeroModule instance, float temperature)
        {
            if (!enabled || temperature <= 0f || loadFailed)
                return;

            if (prefab == null)
            {
                prefab = UnityEngine.Resources.Load<WorldParticle>("effects/Burn");
                if (prefab == null)
                {
                    Debug.LogWarning("[ReentryMod] Could not find effects/Burn particle prefab - disabling particles");
                    loadFailed = true;
                    return;
                }
            }

            accumulator += particlesPerSecond * Time.fixedDeltaTime;
            int count = Mathf.FloorToInt(accumulator);
            if (count <= 0) return;
            accumulator -= count;

            Vector3 basePos = instance.reentryEdge.transform.position;

            // FIX: particles need the rocket's own velocity added in, same
            // as the base game's ParticleModule.Spawn() does (rocket
            // velocity + a small random spread). Without it, particles
            // only carry a few m/s of random spread while the rocket
            // (which the camera follows) can be moving at hundreds or
            // thousands of m/s during reentry - so they get left behind
            // almost instantly, reading as "spawn off screen."
            Vector2 rocketVelocity = ReentryPhysics.GetVelocity(instance);

            (Vector3, Vector3)[] particles = new (Vector3, Vector3)[count];
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * spawnSpread;
                Vector2 vel = rocketVelocity + UnityEngine.Random.insideUnitCircle * velocityRange;
                particles[i] = (basePos + (Vector3)offset, vel);
            }
            prefab.Spawn(particles);
        }
    }

    [HarmonyPatch(typeof(AeroModule), "FixedUpdate_Reentry_And_Heating")]
    class AeroMesh_BurnParticles
    {
        static void Postfix(AeroModule __instance, float temperature)
        {
            ReentryBurnParticles.Tick(__instance, temperature);
        }
    }


    // ============================================================
    // SAVE / LOAD
    // ============================================================

    public static class ReentrySaveLoad
    {
        private static string FilePath =>
            System.IO.Path.Combine(SFSMod.MyMod.Main.ModFolder, "ReentryVisuals.txt");

        // Baked-in default settings, applied the first time the mod runs
        // on an install with no ReentryVisuals.txt yet - so a fresh
        // install starts from this tuned look instead of the bare
        // hardcoded LayerSettings defaults. Uses the same line format
        // Save() writes, so it flows through the exact same parsing path.
        private static readonly string[] DefaultConfig =
        {
            "layers=outer1,outer2,edge3",
            "edgeA.hue=-120",
            "edgeA.brightness=1.5",
            "edgeA.opacity=1.25",
            "edgeA.offset=0",
            "edgeA.speed=0",
            "edgeA.posx=0",
            "edgeA.posy=0",
            "edgeA.width=0.7",
            "edgeA.length=0.7",
            "edgeA.fadex=1",
            "edgeA.fadem=0.9",
            "edgeA.straightness=-0.15",
            "edgeA.order=1",
            "outerA.hue=-20",
            "outerA.brightness=2.3",
            "outerA.opacity=0.74",
            "outerA.offset=0",
            "outerA.speed=0",
            "outerA.posx=0",
            "outerA.posy=0",
            "outerA.width=0.3",
            "outerA.length=1",
            "outerA.fadex=1",
            "outerA.fadem=1",
            "outerA.straightness=6",
            "outerA.order=1",
            "outer1.kind=outer",
            "outer1.hue=260",
            "outer1.brightness=1.2",
            "outer1.opacity=0.5",
            "outer1.offset=0",
            "outer1.speed=0",
            "outer1.posx=0",
            "outer1.posy=0",
            "outer1.width=0.45",
            "outer1.length=5.249998",
            "outer1.fadex=1",
            "outer1.fadem=1",
            "outer1.straightness=9",
            "outer1.order=0",
            "outer2.kind=outer",
            "outer2.hue=260",
            "outer2.brightness=1.5",
            "outer2.opacity=0.8",
            "outer2.offset=0",
            "outer2.speed=0",
            "outer2.posx=0",
            "outer2.posy=-0.1",
            "outer2.width=0.1",
            "outer2.length=0.4",
            "outer2.fadex=5",
            "outer2.fadem=1",
            "outer2.straightness=1",
            "outer2.order=0",
            "edge3.kind=edge",
            "edge3.hue=0",
            "edge3.brightness=3",
            "edge3.opacity=1",
            "edge3.offset=0",
            "edge3.speed=0",
            "edge3.posx=0",
            "edge3.posy=0",
            "edge3.width=1.1",
            "edge3.length=1.1",
            "edge3.fadex=1",
            "edge3.fadem=1",
            "edge3.straightness=1",
            "edge3.order=0",
            "particles.enabled=0",
            "particles.rate=20",
            "particles.velocity=3",
            "particles.spread=1.2",
        };

        public static void Save()
        {
            try
            {
                List<string> lines = new List<string>();

                List<ReentryLayers.LayerSettings> extras = ReentryLayers.extraLayers;
                lines.Add("layers=" + string.Join(",", extras.Select(l => l.id)));

                foreach (ReentryLayers.LayerSettings layer in ReentryLayers.AllLayers())
                {
                    if (!layer.isOriginal)
                        lines.Add($"{layer.id}.kind={(layer.isEdge ? "edge" : "outer")}");

                    foreach (string param in ReentryLayers.ParamNames)
                    {
                        float value = ReentryLayers.GetParam(layer, param);
                        lines.Add($"{layer.id}.{param}=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                foreach (string param in ReentryBurnParticles.ParamNames)
                {
                    float value = ReentryBurnParticles.GetParam(param);
                    lines.Add($"particles.{param}=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                System.IO.File.WriteAllLines(FilePath, lines);
                Debug.Log($"[ReentryMod] Saved settings to {FilePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReentryMod] Failed to save settings: {e}");
            }
        }

        public static void Load()
        {
            try
            {
                bool hasSaveFile = System.IO.File.Exists(FilePath);
                string[] rawLines = hasSaveFile
                    ? System.IO.File.ReadAllLines(FilePath)
                    : DefaultConfig;

                // First pass: recreate any saved extra layers before
                // setting their params, so byId lookups below succeed.
                foreach (string rawLine in rawLines)
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("layers="))
                    {
                        string listPart = line.Substring("layers=".Length);
                        if (listPart.Length == 0) continue;
                        foreach (string id in listPart.Split(','))
                        {
                            string kindLine = rawLines.FirstOrDefault(l => l.Trim().StartsWith($"{id}.kind="));
                            if (kindLine == null) continue;
                            bool isEdge = kindLine.Trim().EndsWith("edge");
                            ReentryLayers.EnsureLayer(id, isEdge);
                        }
                    }
                }

                // Second pass: apply every param and particle value.
                foreach (string rawLine in rawLines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || !line.Contains("=") || line.StartsWith("layers="))
                        continue;

                    int eq = line.IndexOf('=');
                    string key = line.Substring(0, eq);
                    string valueText = line.Substring(eq + 1);

                    int dot = key.IndexOf('.');
                    if (dot < 0) continue;

                    string scope = key.Substring(0, dot);
                    string param = key.Substring(dot + 1);

                    if (param == "kind") continue;

                    if (scope == "particles")
                    {
                        if (float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float pv))
                            ReentryBurnParticles.SetParam(param, pv);
                        continue;
                    }

                    if (!ReentryLayers.byId.TryGetValue(scope, out ReentryLayers.LayerSettings layer))
                        continue;

                    if (!float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float value))
                        continue;

                    ReentryLayers.SetParam(layer, param, value);
                }

                Debug.Log(hasSaveFile
                    ? $"[ReentryMod] Loaded settings from {FilePath}"
                    : "[ReentryMod] No saved settings found - applied built-in defaults");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReentryMod] Failed to load settings: {e}");
            }
        }
    }


    // ============================================================
    // SHADER INSPECTION (diagnostic only)
    // ============================================================

    [HarmonyPatch(typeof(AeroMesh), "Start")]
    class AeroMesh_InspectShader
    {
        static void Postfix(AeroMesh __instance)
        {
            if (__instance.meshRenderer == null) return;
            Material mat = __instance.meshRenderer.sharedMaterial;
            if (mat == null) return;

            Debug.Log($"[ReentryMod] {__instance.name} shader = {mat.shader.name}");
            int count = mat.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                Debug.Log($"[ReentryMod]   {mat.shader.GetPropertyName(i)} : {mat.shader.GetPropertyType(i)}");
        }
    }


    // ============================================================
    // APPLY VISUAL SETTINGS - fires for ANY AeroMesh.SetTemperature
    // call, original or clone, since GetSettings now resolves both
    // via the same map lookup.
    // ============================================================

    [HarmonyPatch(typeof(AeroMesh), nameof(AeroMesh.SetTemperature))]
    class AeroMesh_ApplyLayerSettings
    {
        static void Prefix(AeroMesh __instance, ref float strength)
        {
            ReentryLayers.LayerSettings settings = ReentryLayers.GetSettings(__instance);
            if (settings != null)
                strength *= settings.brightness;
        }

        static void Postfix(AeroMesh __instance)
        {
            ReentryLayers.LayerSettings settings = ReentryLayers.GetSettings(__instance);
            if (settings != null)
                ReentryLayers.ApplyPropertyBlock(__instance, settings);
        }
    }


    // ============================================================
    // ORIGINAL LAYER SHAPE TUNING (edgeA / outerA - the real mesh)
    // ============================================================

    [HarmonyPatch(typeof(AeroModule), "FixedUpdate_Reentry_And_Heating")]
    class AeroMesh_ShapeTuningA
    {
        static void Prefix(AeroModule __instance)
        {
            if (GameManager.main == null) return;
            AeroData aeroData = GameManager.main.aeroData;
            if (aeroData == null) return;

            ReentryBaseShape.CaptureIfNeeded(aeroData);

            // Feed this rocket's real speed into edgeA/outerA before the
            // game's own SetTemperature calls happen inside the original
            // method body - ApplyPropertyBlock reads it back afterward.
            float speed = ReentryPhysics.GetVelocity(__instance).magnitude;
            ReentryLayers.SetCurrentSpeed(__instance.reentryEdge, speed);
            ReentryLayers.SetCurrentSpeed(__instance.reentryOuter, speed);

            aeroData.reentry_Edge.size = ReentryBaseShape.edgeSize * ReentryLayers.edgeA.lengthScale;
            aeroData.reentry_Edge.side_FadeX = ReentryBaseShape.edgeFadeX * ReentryLayers.edgeA.widthScale * ReentryLayers.edgeA.fadeXScale;
            aeroData.reentry_Edge.side_FadeM = ReentryBaseShape.edgeFadeM * ReentryLayers.edgeA.widthScale * ReentryLayers.edgeA.fadeMScale;

            aeroData.reentry_Outer.tail_Scale = ReentryBaseShape.tailScale * ReentryLayers.outerA.lengthScale;
            aeroData.reentry_Outer.side_FadeX = ReentryBaseShape.outerFadeX * ReentryLayers.outerA.widthScale * ReentryLayers.outerA.fadeXScale;
            aeroData.reentry_Outer.side_FadeM = ReentryBaseShape.outerFadeM * ReentryLayers.outerA.widthScale * ReentryLayers.outerA.fadeMScale;
            aeroData.reentry_Outer.tail_Acceleration = ReentryBaseShape.tailAcceleration * ReentryLayers.outerA.straightness;
            aeroData.reentry_Outer.tail_InitialSlope = ReentryBaseShape.tailInitialSlope * ReentryLayers.outerA.straightness;
        }
    }


    // ============================================================
    // DUPLICATE LAYERS - now handles however many extra layers
    // exist (List<LayerSettings>), each with its own clone
    // GameObject per original mesh and its own mesh-shape data.
    // ============================================================

    [HarmonyPatch(typeof(AeroModule), "FixedUpdate_Reentry_And_Heating")]
    class AeroMesh_DuplicateLayers
    {
        // Per extra layer: which clone belongs to which original AeroMesh.
        private static readonly Dictionary<ReentryLayers.LayerSettings, Dictionary<AeroMesh, AeroMesh>> clones =
            new Dictionary<ReentryLayers.LayerSettings, Dictionary<AeroMesh, AeroMesh>>();

        // Per extra layer: its own independent mesh-shape data.
        private static readonly Dictionary<ReentryLayers.LayerSettings, BasicMeshData> meshData =
            new Dictionary<ReentryLayers.LayerSettings, BasicMeshData>();

        static void Postfix(AeroModule __instance, float temperature, List<Surface> exposedSurfaces, float velocityAngleRad, Matrix2x2 localToWorld)
        {
            if (temperature <= 0f)
            {
                // FIX: previously this just returned, leaving every
                // already-activated clone permanently active - nothing
                // ever deactivated them again once reentry ended, unlike
                // the original meshes (which the base game explicitly
                // hides via reentryEdge/reentryOuter.SetActive(false) in
                // AeroModule.FixedUpdate once temperature drops to 0).
                // That's why extra layers kept showing indefinitely,
                // even sitting on the launch pad long after any reentry.
                foreach (Dictionary<AeroMesh, AeroMesh> perOriginal in clones.Values)
                    foreach (AeroMesh clone in perOriginal.Values)
                        if (clone != null)
                            clone.gameObject.SetActive(false);
                return;
            }
            if (GameManager.main == null) return;
            AeroData aeroData = GameManager.main.aeroData;
            if (aeroData == null) return;

            ReentryBaseShape.CaptureIfNeeded(aeroData);

            // Same speed value applies to every extra layer on this
            // rocket - computed once per tick here, fed to each clone
            // below (ApplyPropertyBlock reads it back per-mesh).
            float speed = ReentryPhysics.GetVelocity(__instance).magnitude;

            // Copy the extras list since layers can be added/removed
            // (from the UI) between ticks.
            foreach (ReentryLayers.LayerSettings layer in ReentryLayers.extraLayers.ToList())
            {
                AeroMesh original = layer.isEdge ? __instance.reentryEdge : __instance.reentryOuter;
                AeroMesh clone = GetOrCreateClone(layer, original);
                ReentryLayers.SetCurrentSpeed(clone, speed);

                if (layer.isEdge)
                {
                    StraightMesh data = (StraightMesh)GetOrCreateData(layer, aeroData);
                    data.size = ReentryBaseShape.edgeSize * layer.lengthScale;
                    data.side_FadeX = ReentryBaseShape.edgeFadeX * layer.widthScale * layer.fadeXScale;
                    data.side_FadeM = ReentryBaseShape.edgeFadeM * layer.widthScale * layer.fadeMScale;

                    AeroMesh.Data meshInput = new AeroMesh.Data
                    {
                        velocityAngle_Rad = velocityAngleRad,
                        localToWorld = localToWorld,
                        data = data,
                        curveData = data.GetCurveData()
                    };
                    clone.GenerateMesh(meshInput, exposedSurfaces);
                    clone.SetTemperature(AeroModule.GetIntensity(temperature, 1000f) * 0.7f);
                }
                else
                {
                    CurvedMesh data = (CurvedMesh)GetOrCreateData(layer, aeroData);
                    data.tail_Scale = ReentryBaseShape.tailScale * layer.lengthScale;
                    data.side_FadeX = ReentryBaseShape.outerFadeX * layer.widthScale * layer.fadeXScale;
                    data.side_FadeM = ReentryBaseShape.outerFadeM * layer.widthScale * layer.fadeMScale;
                    data.tail_Acceleration = ReentryBaseShape.tailAcceleration * layer.straightness;
                    data.tail_InitialSlope = ReentryBaseShape.tailInitialSlope * layer.straightness;

                    AeroMesh.Data meshInput = new AeroMesh.Data
                    {
                        velocityAngle_Rad = velocityAngleRad,
                        localToWorld = localToWorld,
                        data = data,
                        curveData = data.GetCurveData(exposedSurfaces.Count),
                        sampleCurve = data.SampleCurve()
                    };
                    clone.GenerateMesh(meshInput, exposedSurfaces);
                    clone.SetTemperature(AeroModule.GetIntensity(temperature, 1800f) * 0.6f);
                }

                clone.gameObject.SetActive(true);
            }
        }

        private static BasicMeshData GetOrCreateData(ReentryLayers.LayerSettings layer, AeroData aeroData)
        {
            if (meshData.TryGetValue(layer, out BasicMeshData data))
                return data;

            BasicMeshData fresh = layer.isEdge ? (BasicMeshData)new StraightMesh() : new CurvedMesh();
            CopyBasicFields(layer.isEdge ? (BasicMeshData)aeroData.reentry_Edge : aeroData.reentry_Outer, fresh);
            meshData[layer] = fresh;
            return fresh;
        }

        private static AeroMesh GetOrCreateClone(ReentryLayers.LayerSettings layer, AeroMesh original)
        {
            if (!clones.TryGetValue(layer, out Dictionary<AeroMesh, AeroMesh> perOriginal))
            {
                perOriginal = new Dictionary<AeroMesh, AeroMesh>();
                clones[layer] = perOriginal;
            }

            if (perOriginal.TryGetValue(original, out AeroMesh clone) && clone != null)
                return clone;

            GameObject cloneObj = Object.Instantiate(original.gameObject, original.transform.parent);
            cloneObj.transform.localPosition = original.transform.localPosition;
            cloneObj.transform.localRotation = original.transform.localRotation;
            cloneObj.transform.localScale = original.transform.localScale;
            cloneObj.name = original.name + " (" + layer.id + ")";

            clone = cloneObj.GetComponent<AeroMesh>();
            perOriginal[original] = clone;
            ReentryLayers.RegisterClone(clone, layer);
            return clone;
        }

        private static void CopyBasicFields(BasicMeshData src, BasicMeshData dst)
        {
            dst.top_Move = src.top_Move;
            dst.top_Fade = src.top_Fade;
            dst.extend = src.extend;
            dst.side_FadeX = src.side_FadeX;
            dst.side_FadeM = src.side_FadeM;
            dst.textureWidth = src.textureWidth;
            dst.randomizeHorizontalUV = src.randomizeHorizontalUV;
            dst.startTexAfterTopFade = src.startTexAfterTopFade;
            dst.skipSurfaces = src.skipSurfaces;
            dst.reduceIfBelow = src.reduceIfBelow;
            dst.tail_Resolution = src.tail_Resolution;

            if (src is CurvedMesh srcCurved && dst is CurvedMesh dstCurved)
            {
                dstCurved.tail_InitialSlope = srcCurved.tail_InitialSlope;
                dstCurved.tail_InitialVelocity = srcCurved.tail_InitialVelocity;
                dstCurved.tail_Acceleration = srcCurved.tail_Acceleration;
            }
        }

        /// <summary>Called by ReentryLayers.RemoveLayer to clean up a removed layer's GameObjects.</summary>
        public static void DestroyClonesFor(ReentryLayers.LayerSettings layer)
        {
            if (clones.TryGetValue(layer, out Dictionary<AeroMesh, AeroMesh> perOriginal))
            {
                foreach (AeroMesh clone in perOriginal.Values)
                    if (clone != null)
                        Object.Destroy(clone.gameObject);
                clones.Remove(layer);
            }
            meshData.Remove(layer);
        }
    }


    // ============================================================
    // CONSOLE COMMANDS
    // ============================================================

    public static class ReentryConsoleCommands
    {
        private static bool registered;

        public static void Register()
        {
            if (registered) return;
            registered = true;

            // Exact match on a distinctive string, deliberately not just
            // "reset" - the console's "commands" list is shared across
            // every loaded mod, checked in order, and the first delegate
            // to return true wins. A generic name risks colliding with
            // some other mod's looser match.
            Add("rvfixwindow", () =>
            {
                ReentryVisualsUI.ResetPosition();
                return "Reset Reentry Visuals window position";
            });

            Add("rvsave", () =>
            {
                ReentrySaveLoad.Save();
                return "Saved Reentry Visuals settings";
            });

            Add("rvload", () =>
            {
                ReentrySaveLoad.Load();
                ReentryVisualsUI.RefreshIfBuilt();
                return "Loaded Reentry Visuals settings";
            });

            Add("addedge", () =>
            {
                ReentryLayers.LayerSettings l = ReentryLayers.AddLayer(true);
                ReentryVisualsUI.RefreshIfBuilt();
                return $"Added edge layer '{l.id}'";
            });

            Add("addouter", () =>
            {
                ReentryLayers.LayerSettings l = ReentryLayers.AddLayer(false);
                ReentryVisualsUI.RefreshIfBuilt();
                return $"Added outer layer '{l.id}'";
            });

            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                Match m = Regex.Match(s, @"^removelayer (\S+)$", RegexOptions.IgnoreCase);
                if (!m.Success) return false;
                if (!ReentryLayers.byId.TryGetValue(m.Groups[1].Value, out ReentryLayers.LayerSettings layer))
                    return false;
                if (layer.isOriginal)
                {
                    ModLoader.IO.Console.main.WriteText("Can't remove an original layer");
                    return true;
                }
                ReentryLayers.RemoveLayer(layer);
                ReentryVisualsUI.RefreshIfBuilt();
                ModLoader.IO.Console.main.WriteText($"Removed layer '{layer.id}'");
                return true;
            });

            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                Match m = Regex.Match(
                    s,
                    @"^set (\S+) (hue|coldhue|huemin|huemax|brightness|opacity|offset|speed|posx|posy|width|length|fadex|fadem|straightness|order) ([\d.\-]+)$",
                    RegexOptions.IgnoreCase
                );
                if (!m.Success) return false;

                if (!ReentryLayers.byId.TryGetValue(m.Groups[1].Value, out ReentryLayers.LayerSettings layer))
                    return false;

                string parameter = m.Groups[2].Value.ToLower();
                if (!float.TryParse(m.Groups[3].Value, out float value))
                    return false;

                ReentryLayers.SetParam(layer, parameter, value);
                ModLoader.IO.Console.main.WriteText($"Set {m.Groups[1].Value}.{parameter} = {value}");
                return true;
            });

            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                Match m = Regex.Match(
                    s,
                    @"^particles (enabled|rate|velocity|spread) ([\d.\-]+)$",
                    RegexOptions.IgnoreCase
                );
                if (!m.Success) return false;

                string parameter = m.Groups[1].Value.ToLower();
                if (!float.TryParse(m.Groups[2].Value, out float value))
                    return false;

                ReentryBurnParticles.SetParam(parameter, value);
                ModLoader.IO.Console.main.WriteText($"Set particles.{parameter} = {value}");
                return true;
            });
        }

        private static void Add(string exact, Func<string> handler)
        {
            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                if (s.Trim().ToLower() != exact) return false;
                ModLoader.IO.Console.main.WriteText(handler());
                return true;
            });
        }
    }


    // ============================================================
    // VISUALS UI
    // ============================================================

    public static class ReentryVisualsUI
    {
        const int WindowId = 730104;
        const int WindowWidth = 560;
        const int WindowHeight = 750;

        const int ColumnWidth = 510;
        const int ColumnHeight = 880;

        const int LabelWidth = 105;
        const int InputWidth = 190;

        const int FieldHeight = 50;

        const float RowSpacing = 60f;
        const float ColumnSpacing = 15f;

        static GameObject _holder;
        static ClosableWindow _window;
        static Transform _columnParent;
        static bool _built;

        static readonly Dictionary<ReentryLayers.LayerSettings, Box> Columns =
            new Dictionary<ReentryLayers.LayerSettings, Box>();

        static readonly Dictionary<ReentryLayers.LayerSettings, Dictionary<string, NumberInput>> Inputs =
            new Dictionary<ReentryLayers.LayerSettings, Dictionary<string, NumberInput>>();

        static readonly Dictionary<ReentryLayers.LayerSettings, LayerDefaults> Defaults =
            new Dictionary<ReentryLayers.LayerSettings, LayerDefaults>();

        static readonly Dictionary<string, NumberInput> ParticleInputs =
            new Dictionary<string, NumberInput>();

        class LayerDefaults
        {
            public float hue, coldHue, hueMinVel, hueMaxVel;
            public float brightness, opacity, offset, speed;
            public float posX, posY;
            public float width, length, fadeX, fadeM, straightness;
            public int order;
        }

        public static void Initialize()
        {
            GameObject obj = new GameObject("ReentryVisuals Hotkey");
            Object.DontDestroyOnLoad(obj);
            obj.AddComponent<ReentryVisualsHotkey>();

            // FIX: nothing previously called Load() automatically - saved
            // settings only ever got applied via the "rvload" console
            // command or the "Load All" button, so every fresh session
            // silently started from hardcoded defaults regardless of
            // what was saved. Load() already no-ops safely if the file
            // doesn't exist yet, so it's safe to call unconditionally here.
            ReentrySaveLoad.Load();
        }

        public static void Toggle()
        {
            if (!_built)
            {
                Build();
                _built = true;
                return;
            }

            RefreshAll();

            bool willBeVisible = !_holder.activeSelf;
            _holder.SetActive(willBeVisible);

            // Auto-save on close only - not on every keystroke while open,
            // which would mean a file write per input change.
            if (!willBeVisible)
                ReentrySaveLoad.Save();
        }

        public static void RefreshIfBuilt()
        {
            if (!_built) return;

            // New layers may have appeared (added via console or loaded
            // from a save file) since the panel was built - make sure
            // every current layer has a column before refreshing values.
            bool addedAny = false;
            foreach (ReentryLayers.LayerSettings layer in ReentryLayers.AllLayers())
            {
                if (!Columns.ContainsKey(layer))
                {
                    BuildLayerColumn(_columnParent, layer);
                    addedAny = true;
                }
            }

            // FIX: BuildLayerColumn appends at the end, so newly-recreated
            // layers (e.g. from Load()) were landing AFTER the Add Layer /
            // Save All / Load All column instead of before it - this was
            // missing here even though the +Edge/+Outer button handlers
            // already do it.
            if (addedAny)
                MoveAddColumnToEnd();

            RefreshAll();

            if (addedAny)
                RebuildScrollLayout();
        }

        public static void ResetPosition()
        {
            if (_window == null) return;
            _window.Position = Vector2.zero;
            if (_holder != null) _holder.SetActive(true);
        }

        static void Build()
        {
            _holder = Builder.CreateHolder(Builder.SceneToAttach.BaseScene, "ReentryVisuals Holder");

            _window = UIToolsBuilder.CreateClosableWindow(
                _holder.transform, WindowId, WindowWidth, WindowHeight, 0, 1,
                draggable: true, savePosition: false, titleText: "Reentry Visuals"
            );

            _window.Minimized = false;

            // NOTE: intentionally not calling RegisterPermanentSaving -
            // it persisted position across sessions despite
            // savePosition: false above, which is how the window got
            // stuck reloading off-screen after a drag.

            // Switched to Vertical for both layout and scrolling - every
            // real working example found (yours included) pairs Vertical
            // layout with Vertical EnableScrolling. Horizontal scrolling
            // on this framework may just not be a supported/tested path;
            // this is the confirmed-working pattern instead. Each layer
            // now stacks top-to-bottom rather than side-by-side.
            _window.CreateLayoutGroup(
                Type.Vertical, TextAnchor.UpperLeft, ColumnSpacing,
                new RectOffset(15, 15, 20, 20)
            );

            _window.EnableScrolling(Type.Vertical);

            _columnParent = _window;

            foreach (ReentryLayers.LayerSettings layer in ReentryLayers.AllLayers())
                BuildLayerColumn(_columnParent, layer);

            BuildParticlesColumn(_columnParent);
            BuildAddColumn(_columnParent);

            RebuildScrollLayout();
        }

        // FIX: forces the scroll content to recompute its height after
        // columns are added, removed, or reordered at runtime. Without
        // this the ScrollRect's cached content size can stay smaller
        // than the actual content, capping how far down it lets you
        // scroll - short of the Add Layer / Save All / Load All column.
        static void RebuildScrollLayout()
        {
            if (_columnParent is RectTransform rt)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        static void BuildLayerColumn(Transform parent, ReentryLayers.LayerSettings layer)
        {
            // FIX: previously passed ColumnHeight (880) here directly at
            // construction time, before SetFixedWidthAutoHeight's
            // ContentSizeFitter ever ran - if CreateBox sizes its own
            // background visual from these constructor args immediately,
            // that background could stay at the old oversized height even
            // after the fitter later shrinks the RectTransform used for
            // positioning, leaving a leftover background-colored gap. A
            // small placeholder here (and in BuildParticlesColumn /
            // BuildAddColumn below) means there's nothing oversized left
            // for the fitter to disagree with.
            Box column = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.15f);
            SetFixedWidthAutoHeight(column.gameObject, ColumnWidth);
            column.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, RowSpacing, new RectOffset(10, 10, 15, 15));

            Columns[layer] = column;

            if (!Defaults.ContainsKey(layer))
            {
                Defaults[layer] = new LayerDefaults
                {
                    hue = layer.hue, coldHue = layer.coldHue, hueMinVel = layer.hueMinVel, hueMaxVel = layer.hueMaxVel,
                    brightness = layer.brightness, opacity = layer.opacity,
                    offset = layer.offset, speed = layer.animationSpeed,
                    posX = layer.posX, posY = layer.posY,
                    width = layer.widthScale, length = layer.lengthScale,
                    fadeX = layer.fadeXScale, fadeM = layer.fadeMScale,
                    straightness = layer.straightness, order = layer.sortingOrder
                };
            }

            if (!Inputs.ContainsKey(layer))
                Inputs[layer] = new Dictionary<string, NumberInput>();

            Builder.CreateLabel(column, ColumnWidth - 20, 40, 0, 0, layer.id + (layer.isOriginal ? " (real)" : ""));

            BuildRow(column, layer, "Hue", "hue", 5f, layer.hue);
            BuildRow(column, layer, "Cold Hue", "coldhue", 5f, layer.coldHue);
            BuildRow(column, layer, "Hue Min Vel", "huemin", 10f, layer.hueMinVel);
            BuildRow(column, layer, "Hue Max Vel", "huemax", 10f, layer.hueMaxVel);
            BuildRow(column, layer, "Brightness", "brightness", 0.1f, layer.brightness);
            BuildRow(column, layer, "Opacity", "opacity", 0.05f, layer.opacity);
            BuildRow(column, layer, "Offset", "offset", 0.05f, layer.offset);
            BuildRow(column, layer, "Speed", "speed", 0.1f, layer.animationSpeed);
            BuildRow(column, layer, "Pos X", "posx", 0.1f, layer.posX);
            BuildRow(column, layer, "Pos Y", "posy", 0.1f, layer.posY);
            BuildRow(column, layer, "Width", "width", 0.05f, layer.widthScale);
            BuildRow(column, layer, "Length", "length", 0.05f, layer.lengthScale);
            BuildRow(column, layer, "Fade X", "fadex", 0.05f, layer.fadeXScale);
            BuildRow(column, layer, "Fade M", "fadem", 0.05f, layer.fadeMScale);
            BuildRow(column, layer, "Straightness", "straightness", 0.05f, layer.straightness);
            BuildRow(column, layer, "Order", "order", 1f, layer.sortingOrder);

            Container buttonRow = Builder.CreateContainer(column);
            buttonRow.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 10f);

            Builder.CreateButton(buttonRow, (ColumnWidth - 40) / (layer.isOriginal ? 1 : 2), 45, 0, 0,
                () => ResetLayer(layer), "Reset");

            if (!layer.isOriginal)
            {
                Builder.CreateButton(buttonRow, (ColumnWidth - 40) / 2, 45, 0, 0,
                    () => RemoveLayerFromUI(layer), "Remove");
            }
        }

        static void BuildParticlesColumn(Transform parent)
        {
            // Compact height (4 rows, not 11 like a full layer panel) -
            // same reasoning as BuildAddColumn above.
            const int particlesBoxHeight = 380;

            Box column = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.15f);
            SetFixedWidthAutoHeight(column.gameObject, ColumnWidth);
            column.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, RowSpacing, new RectOffset(10, 10, 15, 15));

            Builder.CreateLabel(column, ColumnWidth - 20, 40, 0, 0, "Burn Particles");

            BuildParticleRow(column, "Enabled", "enabled", 1f, ReentryBurnParticles.enabled ? 1f : 0f);
            BuildParticleRow(column, "Rate/sec", "rate", 1f, ReentryBurnParticles.particlesPerSecond);
            BuildParticleRow(column, "Velocity", "velocity", 0.1f, ReentryBurnParticles.velocityRange);
            BuildParticleRow(column, "Spread", "spread", 0.1f, ReentryBurnParticles.spawnSpread);
        }

        static void BuildParticleRow(Transform parent, string label, string parameter, float step, float value)
        {
            Container row = Builder.CreateContainer(parent);
            SetFixedSize(row.gameObject, ColumnWidth - 20, FieldHeight);
            row.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 12f);

            Builder.CreateLabel(row, LabelWidth, FieldHeight, 0, 0, label);

            NumberInput input = UIToolsBuilder.CreateNumberInput(row, InputWidth, FieldHeight, value, step);
            SetFixedSize(input.gameObject, InputWidth, FieldHeight);

            ParticleInputs[parameter] = input;
            input.OnValueChangedEvent += newValue => ReentryBurnParticles.SetParam(parameter, newValue);
        }

        static void BuildAddColumn(Transform parent)
        {
            // Compact height now (not the full ColumnHeight) - it's just
            // a handful of buttons, and in a vertical stack a near-empty
            // box the same height as a full parameter panel would look odd.
            const int addBoxHeight = 300;

            Box column = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.1f);
            SetFixedWidthAutoHeight(column.gameObject, ColumnWidth);
            column.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, 20f, new RectOffset(10, 10, 15, 15));

            _addColumnObj = column.gameObject;

            Builder.CreateLabel(column, ColumnWidth - 20, 40, 0, 0, "Add Layer");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentryLayers.LayerSettings l = ReentryLayers.AddLayer(true);
                BuildLayerColumn(_columnParent, l);
                MoveAddColumnToEnd();
                RebuildScrollLayout();
            }, "+ Edge");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentryLayers.LayerSettings l = ReentryLayers.AddLayer(false);
                BuildLayerColumn(_columnParent, l);
                MoveAddColumnToEnd();
                RebuildScrollLayout();
            }, "+ Outer");

            Builder.CreateLabel(column, ColumnWidth - 20, 30, 0, 0, "");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentrySaveLoad.Save();
            }, "Save All");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentrySaveLoad.Load();
                RefreshIfBuilt();
            }, "Load All");
        }

        static GameObject _addColumnObj;

        static void MoveAddColumnToEnd()
        {
            // New columns get added via CreateBox, which appends at the
            // end of the layout group - push the "Add Layer" column back
            // to last so it doesn't end up sandwiched between layers.
            if (_addColumnObj != null)
                _addColumnObj.transform.SetAsLastSibling();
        }

        static void SetFixedSize(GameObject obj, float width, float height)
        {
            LayoutElement layout = obj.GetOrAddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.minHeight = height;
            layout.preferredHeight = height;
            layout.flexibleWidth = 0;
            layout.flexibleHeight = 0;
        }

        // FIX: previously every column Box used SetFixedSize with a
        // hardcoded height guess (ColumnHeight=880, particlesBoxHeight=380,
        // addBoxHeight=300). Those constants were never updated when
        // LayerSettings grew to 13 rows, so a box's forced height didn't
        // match what its rows actually needed - leaving blank space below
        // the real content (before the next section), which pushed
        // everything after it (including "Load All") further down than
        // the true content height, off the bottom of the scroll view.
        // This lets a box size itself to its actual content instead.
        static void SetFixedWidthAutoHeight(GameObject obj, float width)
        {
            LayoutElement layout = obj.GetOrAddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0;
            layout.minHeight = -1;
            layout.preferredHeight = -1;
            layout.flexibleHeight = 0;

            ContentSizeFitter fitter = obj.GetOrAddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        static void BuildRow(Transform parent, ReentryLayers.LayerSettings layer, string label, string parameter, float step, float value)
        {
            Container row = Builder.CreateContainer(parent);
            SetFixedSize(row.gameObject, ColumnWidth - 20, FieldHeight);

            ContentSizeFitter fitter = row.gameObject.GetComponent<ContentSizeFitter>();
            if (fitter != null) Object.Destroy(fitter);

            row.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 12f);

            Builder.CreateLabel(row, LabelWidth, FieldHeight, 0, 0, label);

            NumberInput input = UIToolsBuilder.CreateNumberInput(row, InputWidth, FieldHeight, value, step);
            SetFixedSize(input.gameObject, InputWidth, FieldHeight);

            Inputs[layer][parameter] = input;
            input.OnValueChangedEvent += newValue => ReentryLayers.SetParam(layer, parameter, newValue);
        }

        static void RemoveLayerFromUI(ReentryLayers.LayerSettings layer)
        {
            ReentryLayers.RemoveLayer(layer);

            if (Columns.TryGetValue(layer, out Box column))
            {
                Object.Destroy(column.gameObject);
                Columns.Remove(layer);
            }
            Inputs.Remove(layer);
            Defaults.Remove(layer);

            RebuildScrollLayout();
        }

        static void ResetLayer(ReentryLayers.LayerSettings layer)
        {
            if (!Defaults.TryGetValue(layer, out LayerDefaults d)) return;

            layer.hue = d.hue;
            layer.coldHue = d.coldHue;
            layer.hueMinVel = d.hueMinVel;
            layer.hueMaxVel = d.hueMaxVel;
            layer.brightness = d.brightness;
            layer.opacity = d.opacity;
            layer.offset = d.offset;
            layer.animationSpeed = d.speed;
            layer.posX = d.posX;
            layer.posY = d.posY;
            layer.widthScale = d.width;
            layer.lengthScale = d.length;
            layer.fadeXScale = d.fadeX;
            layer.fadeMScale = d.fadeM;
            layer.straightness = d.straightness;
            layer.sortingOrder = d.order;

            if (Inputs.TryGetValue(layer, out Dictionary<string, NumberInput> inputs))
                foreach (KeyValuePair<string, NumberInput> entry in inputs)
                    entry.Value.Value = ReentryLayers.GetParam(layer, entry.Key);
        }

        static void RefreshAll()
        {
            foreach (KeyValuePair<ReentryLayers.LayerSettings, Dictionary<string, NumberInput>> layerEntry in Inputs)
                foreach (KeyValuePair<string, NumberInput> inputEntry in layerEntry.Value)
                    inputEntry.Value.Value = ReentryLayers.GetParam(layerEntry.Key, inputEntry.Key);

            foreach (KeyValuePair<string, NumberInput> entry in ParticleInputs)
                entry.Value.Value = ReentryBurnParticles.GetParam(entry.Key);
        }
    }

    public class ReentryVisualsHotkey : MonoBehaviour
    {
        private readonly SFS.Input.KeybindingsPC.Key toggleKey = KeyCode.F3;

        private void Update()
        {
            SFS.Input.I_Key key = toggleKey;
            if (key.IsKeyDown())
                ReentryVisualsUI.Toggle();
        }
    }
}