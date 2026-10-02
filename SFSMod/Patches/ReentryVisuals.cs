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
using Button = SFS.UI.ModGUI.Button;

namespace SFSMod.Patches
{
    // ============================================================
    // SHARED PHYSICS/LOCATION HELPER - one place for "what is this
    // rocket's current velocity" and "which planet is it at right
    // now", used by both the particle code and the per-planet
    // profile resolution below.
    // ============================================================

    public static class ReentryPhysics
    {
        public static Vector2 GetVelocity(AeroModule instance)
        {
            return (instance is Aero_Rocket aeroRocket) ? aeroRocket.rocket.rb2d.linearVelocity : Vector2.zero;
        }

        // Falls back to "Earth" if anything along the chain isn't ready
        // yet (very first frame, non-Aero_Rocket instance, etc.) rather
        // than throwing - a missing planet reference shouldn't crash
        // reentry rendering.
        public static string GetPlanetCodeName(AeroModule instance)
        {
            if (instance is Aero_Rocket aeroRocket && aeroRocket.rocket != null && aeroRocket.rocket.location != null)
            {
                SFS.WorldBase.Planet planet = aeroRocket.rocket.location.planet.Value;
                if (planet != null && !string.IsNullOrEmpty(planet.codeName))
                    return planet.codeName;
            }
            return "Earth";
        }
    }


    // ============================================================
    // BASE SHAPE - the game's own stock values, captured once.
    // Every layer (original or extra) scales FROM these, so nothing
    // compounds across ticks regardless of how many layers exist.
    // Shared across every planet's profile - it's just capturing the
    // game's own unmodified defaults once, not per-planet data.
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
    // REENTRY LAYER SETTINGS - pure per-layer data plus the shared
    // utilities that operate on a LayerSettings/AeroMesh pair. The
    // layer LIST itself (edgeA/outerA/extraLayers) now lives on
    // ReentryProfile below, one per planet, instead of as fixed
    // globals here.
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

        public static LayerSettings MakeDefaultEdgeA() => new LayerSettings
        {
            id = "edgeA", isEdge = true, isOriginal = true,
            hue = 20f, coldHue = 20f, hueMinVel = 200f, hueMaxVel = 1500f,
            brightness = 3f, opacity = 1f, offset = 0f, animationSpeed = 0f,
            widthScale = 0.7f, lengthScale = 0.7f, fadeXScale = 1f, fadeMScale = 1f,
            straightness = 1f, sortingOrder = 1
        };

        public static LayerSettings MakeDefaultOuterA() => new LayerSettings
        {
            id = "outerA", isEdge = false, isOriginal = true,
            hue = 260f, coldHue = 20f, hueMinVel = 200f, hueMaxVel = 1500f,
            brightness = 3f, opacity = 1f, offset = 0f, animationSpeed = 0f,
            widthScale = 0.7f, lengthScale = 0.6f, fadeXScale = 1f, fadeMScale = 1f,
            straightness = 1f, sortingOrder = 1
        };

        public static LayerSettings MakeExtraDefault(string id, bool isEdge) => new LayerSettings
        {
            id = id, isEdge = isEdge, isOriginal = false,
            hue = isEdge ? 45f : 300f, coldHue = isEdge ? 45f : 20f, hueMinVel = 200f, hueMaxVel = 1500f,
            brightness = 1.2f, opacity = 0.5f, offset = 0f, animationSpeed = 0f,
            widthScale = 1.1f, lengthScale = 1.1f, fadeXScale = 1f, fadeMScale = 1f,
            straightness = 1f, sortingOrder = 0
        };

        // --------------------------------------------------------
        // Which LayerSettings a given AeroMesh is CURRENTLY showing.
        // Re-registered every tick (see AeroMesh_ShapeTuningA /
        // AeroMesh_DuplicateLayers), same pattern as currentSpeed
        // below - so a rocket that moves to a different planet picks
        // up that planet's profile immediately, with nothing stale
        // left over from a one-time name-based match.
        // --------------------------------------------------------

        private static readonly Dictionary<AeroMesh, LayerSettings> meshOwner = new Dictionary<AeroMesh, LayerSettings>();

        public static void SetOwner(AeroMesh mesh, LayerSettings settings) => meshOwner[mesh] = settings;

        public static LayerSettings GetSettings(AeroMesh mesh) =>
            meshOwner.TryGetValue(mesh, out LayerSettings s) ? s : null;

        // Current speed per mesh instance, set right before that mesh's
        // GenerateMesh/SetTemperature runs each tick, read back inside
        // ApplyPropertyBlock to drive the cold/hot hue blend.
        private static readonly Dictionary<AeroMesh, float> currentSpeed = new Dictionary<AeroMesh, float>();

        public static void SetCurrentSpeed(AeroMesh mesh, float speed) => currentSpeed[mesh] = speed;

        private static float GetCurrentSpeed(AeroMesh mesh) =>
            currentSpeed.TryGetValue(mesh, out float s) ? s : 0f;

        static readonly int HueProp = Shader.PropertyToID("_Hue");
        static readonly int AlphaMultProp = Shader.PropertyToID("_AlphaMultiplier");
        static readonly int OffsetProp = Shader.PropertyToID("_Offset");

        public static void ApplyPropertyBlock(AeroMesh mesh, LayerSettings settings)
        {
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            mesh.meshRenderer.GetPropertyBlock(block);

            // Blend between coldHue (at/below hueMinVel) and hue (at/above
            // hueMaxVel) based on this mesh's current speed. Set
            // hueMinVel == hueMaxVel (or leave hueMaxVel <= hueMinVel) to
            // disable the blend and just always show "hue" as before.
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
    // REENTRY PROFILE - everything that's now PER PLANET: the two
    // original layers, every extra layer, and the burn-particle
    // settings. One of these exists per planet codeName, created
    // lazily by ReentryProfiles below.
    // ============================================================

    public class ReentryProfile
    {
        public ReentryLayers.LayerSettings edgeA = ReentryLayers.MakeDefaultEdgeA();
        public ReentryLayers.LayerSettings outerA = ReentryLayers.MakeDefaultOuterA();
        public readonly List<ReentryLayers.LayerSettings> extraLayers = new List<ReentryLayers.LayerSettings>();
        private int nextExtraId = 1;

        public bool particlesEnabled;
        public float particlesRate = 20f;
        public float particlesVelocity = 3f;
        public float particlesSpread = 1.2f;

        public static readonly string[] ParticleParamNames = { "enabled", "rate", "velocity", "spread" };

        public void SetParticleParam(string parameter, float value)
        {
            switch (parameter)
            {
                case "enabled": particlesEnabled = value >= 0.5f; break;
                case "rate": particlesRate = value; break;
                case "velocity": particlesVelocity = value; break;
                case "spread": particlesSpread = value; break;
            }
        }

        public float GetParticleParam(string parameter)
        {
            switch (parameter)
            {
                case "enabled": return particlesEnabled ? 1f : 0f;
                case "rate": return particlesRate;
                case "velocity": return particlesVelocity;
                case "spread": return particlesSpread;
                default: return 0f;
            }
        }

        public IEnumerable<ReentryLayers.LayerSettings> AllLayers()
        {
            yield return edgeA;
            yield return outerA;
            foreach (ReentryLayers.LayerSettings l in extraLayers)
                yield return l;
        }

        public Dictionary<string, ReentryLayers.LayerSettings> ById =>
            AllLayers().ToDictionary(l => l.id, l => l, StringComparer.OrdinalIgnoreCase);

        /// <summary>Used by the "+" UI buttons / "addedge"/"addouter" console commands.</summary>
        public ReentryLayers.LayerSettings AddLayer(bool isEdge)
        {
            string id = (isEdge ? "edge" : "outer") + nextExtraId;
            nextExtraId++;
            ReentryLayers.LayerSettings l = ReentryLayers.MakeExtraDefault(id, isEdge);
            extraLayers.Add(l);
            return l;
        }

        /// <summary>Used when loading from file to recreate saved extra layers with their original ids.</summary>
        public ReentryLayers.LayerSettings EnsureLayer(string id, bool isEdge)
        {
            if (ById.TryGetValue(id, out ReentryLayers.LayerSettings existing))
                return existing;

            string digits = new string(id.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int n) && n >= nextExtraId)
                nextExtraId = n + 1;

            ReentryLayers.LayerSettings l = ReentryLayers.MakeExtraDefault(id, isEdge);
            extraLayers.Add(l);
            return l;
        }

        public void RemoveLayer(ReentryLayers.LayerSettings layer)
        {
            if (layer.isOriginal) return; // never remove the real meshes
            extraLayers.Remove(layer);
            AeroMesh_DuplicateLayers.DestroyClonesFor(layer);
        }
    }


    // ============================================================
    // REENTRY PROFILES - the per-planet registry. Loads profiles
    // lazily from disk (one file per planet) and tracks which
    // planet's profile the UI is currently showing/editing, which is
    // DELIBERATELY separate from whichever planet a given rocket is
    // actually at during gameplay - editing Moon's preset doesn't
    // change what renders on a rocket currently re-entering at Earth.
    // The rendering patches resolve each rocket's own current planet
    // independently every tick (see ReentryPhysics.GetPlanetCodeName).
    // ============================================================

    public static class ReentryProfiles
    {
        private static readonly Dictionary<string, ReentryProfile> loaded =
            new Dictionary<string, ReentryProfile>(StringComparer.OrdinalIgnoreCase);

        public static string EditingPlanet { get; private set; } = "Earth";

        public static ReentryProfile GetOrLoad(string planetCodeName)
        {
            if (string.IsNullOrEmpty(planetCodeName)) planetCodeName = "Earth";
            if (!loaded.TryGetValue(planetCodeName, out ReentryProfile profile))
            {
                profile = ReentrySaveLoad.Load(planetCodeName);
                loaded[planetCodeName] = profile;
            }
            return profile;
        }

        public static ReentryProfile Editing => GetOrLoad(EditingPlanet);

        public static void SwitchEditingPlanet(string planetCodeName)
        {
            if (string.IsNullOrEmpty(planetCodeName)) return;
            if (string.Equals(planetCodeName, EditingPlanet, StringComparison.OrdinalIgnoreCase)) return;
            SaveEditing();
            EditingPlanet = planetCodeName;
            GetOrLoad(planetCodeName);
        }

        public static void SaveEditing() => ReentrySaveLoad.Save(EditingPlanet, Editing);

        /// <summary>Re-reads the editing planet's file from disk, replacing whatever was cached in memory.</summary>
        public static void ReloadEditing() => loaded[EditingPlanet] = ReentrySaveLoad.Load(EditingPlanet);

        // All planets currently loaded by the game, vanilla and custom
        // alike - planetLoader.planets is populated from whatever solar
        // system data is active, so a custom solar system's planets show
        // up here automatically too.
        public static List<string> AllPlanetCodeNames()
        {
            List<string> names = new List<string>();
            if (SFS.Base.planetLoader != null && SFS.Base.planetLoader.planets != null)
                names.AddRange(SFS.Base.planetLoader.planets.Keys);
            // Do not invent a vanilla Earth entry when the active system
            // has not populated its planet list yet. The picker is rebuilt
            // when opened, so it will use the active system once loaded.
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }


    // ============================================================
    // BURN PARTICLES - reuses the game's own "effects/Burn"
    // WorldParticle prefab (already used for things like separator
    // sparks/debris). The prefab reference/load-failed flag/spawn
    // accumulator are shared across every planet (loading the same
    // resource once makes sense regardless of profile); rate/
    // velocity/spread/enabled now come from whichever profile is
    // passed in per tick.
    // ============================================================

    public static class ReentryBurnParticles
    {
        private static WorldParticle prefab;
        private static bool loadFailed;
        private static float accumulator;

        public static void Tick(AeroModule instance, float temperature, ReentryProfile profile)
        {
            if (!profile.particlesEnabled || temperature <= 0f || loadFailed)
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

            accumulator += profile.particlesRate * Time.fixedDeltaTime;
            int count = Mathf.FloorToInt(accumulator);
            if (count <= 0) return;
            accumulator -= count;

            Vector3 basePos = instance.reentryEdge.transform.position;
            Vector2 rocketVelocity = ReentryPhysics.GetVelocity(instance);

            (Vector3, Vector3)[] particles = new (Vector3, Vector3)[count];
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * profile.particlesSpread;
                Vector2 vel = rocketVelocity + UnityEngine.Random.insideUnitCircle * profile.particlesVelocity;
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
            ReentryProfile profile = ReentryProfiles.GetOrLoad(ReentryPhysics.GetPlanetCodeName(__instance));
            ReentryBurnParticles.Tick(__instance, temperature, profile);
        }
    }


    // ============================================================
    // SAVE / LOAD - one file per planet now: ReentryVisuals_<planet>.txt
    // ============================================================

    public static class ReentrySaveLoad
    {
        private static string FilePath(string planetCodeName) =>
            System.IO.Path.Combine(SFSMod.MyMod.Main.ModFolder, $"ReentryVisuals_{SanitizeFileName(planetCodeName)}.txt");

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // Defaults for a planet with no ReentryVisuals_<planet>.txt yet come
        // from BuiltInPresets (Patches/BuiltInPresets.cs). A planet with no
        // built-in preset starts from the plain hardcoded LayerSettings
        // defaults with no extra layers. A saved file always overrides these.

        public static void Save(string planetCodeName, ReentryProfile profile)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("layers=" + string.Join(",", profile.extraLayers.Select(l => l.id)));

                foreach (ReentryLayers.LayerSettings layer in profile.AllLayers())
                {
                    if (!layer.isOriginal)
                        lines.Add($"{layer.id}.kind={(layer.isEdge ? "edge" : "outer")}");

                    foreach (string param in ReentryLayers.ParamNames)
                    {
                        float value = ReentryLayers.GetParam(layer, param);
                        lines.Add($"{layer.id}.{param}=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                foreach (string param in ReentryProfile.ParticleParamNames)
                {
                    float value = profile.GetParticleParam(param);
                    lines.Add($"particles.{param}=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                string path = FilePath(planetCodeName);
                System.IO.File.WriteAllLines(path, lines);
                Debug.Log($"[ReentryMod] Saved {planetCodeName} settings to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReentryMod] Failed to save {planetCodeName} settings: {e}");
            }
        }

        public static ReentryProfile Load(string planetCodeName)
        {
            ReentryProfile profile = new ReentryProfile();
            string path = FilePath(planetCodeName);
            bool hasSaveFile = false;

            try
            {
                hasSaveFile = System.IO.File.Exists(path);
                string[] rawLines = hasSaveFile
                    ? System.IO.File.ReadAllLines(path)
                    : BuiltInPresets.Get(planetCodeName);

                // First pass: recreate any saved extra layers before
                // setting their params, so ById lookups below succeed.
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
                            profile.EnsureLayer(id, isEdge);
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
                            profile.SetParticleParam(param, pv);
                        continue;
                    }

                    if (!profile.ById.TryGetValue(scope, out ReentryLayers.LayerSettings layer))
                        continue;

                    if (!float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float value))
                        continue;

                    ReentryLayers.SetParam(layer, param, value);
                }

                Debug.Log(hasSaveFile
                    ? $"[ReentryMod] Loaded {planetCodeName} settings from {path}"
                    : $"[ReentryMod] No saved settings for {planetCodeName} - using {(BuiltInPresets.Has(planetCodeName) ? "built-in" : "plain")} defaults");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReentryMod] Failed to load {planetCodeName} settings: {e}");
            }

            return profile;
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
    // call, original or clone, since GetSettings resolves both via
    // the same per-mesh ownership map (now re-registered every tick
    // against whichever profile is active for that mesh's rocket).
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
    // Resolves THIS rocket's actual current planet every tick and
    // uses that planet's profile - independent of whatever planet
    // the UI happens to be editing right now.
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

            ReentryProfile profile = ReentryProfiles.GetOrLoad(ReentryPhysics.GetPlanetCodeName(__instance));

            // Feed this rocket's real speed and active profile into
            // edgeA/outerA before the game's own SetTemperature calls
            // happen inside the original method body - ApplyPropertyBlock
            // reads both back afterward.
            float speed = ReentryPhysics.GetVelocity(__instance).magnitude;
            ReentryLayers.SetOwner(__instance.reentryEdge, profile.edgeA);
            ReentryLayers.SetOwner(__instance.reentryOuter, profile.outerA);
            ReentryLayers.SetCurrentSpeed(__instance.reentryEdge, speed);
            ReentryLayers.SetCurrentSpeed(__instance.reentryOuter, speed);

            aeroData.reentry_Edge.size = ReentryBaseShape.edgeSize * profile.edgeA.lengthScale;
            aeroData.reentry_Edge.side_FadeX = ReentryBaseShape.edgeFadeX * profile.edgeA.widthScale * profile.edgeA.fadeXScale;
            aeroData.reentry_Edge.side_FadeM = ReentryBaseShape.edgeFadeM * profile.edgeA.widthScale * profile.edgeA.fadeMScale;

            aeroData.reentry_Outer.tail_Scale = ReentryBaseShape.tailScale * profile.outerA.lengthScale;
            aeroData.reentry_Outer.side_FadeX = ReentryBaseShape.outerFadeX * profile.outerA.widthScale * profile.outerA.fadeXScale;
            aeroData.reentry_Outer.side_FadeM = ReentryBaseShape.outerFadeM * profile.outerA.widthScale * profile.outerA.fadeMScale;
            aeroData.reentry_Outer.tail_Acceleration = ReentryBaseShape.tailAcceleration * profile.outerA.straightness;
            aeroData.reentry_Outer.tail_InitialSlope = ReentryBaseShape.tailInitialSlope * profile.outerA.straightness;
        }
    }


    // ============================================================
    // DUPLICATE LAYERS - iterates whichever profile is active for
    // THIS rocket's current planet, each tick.
    // ============================================================

    [HarmonyPatch(typeof(AeroModule), "FixedUpdate_Reentry_And_Heating")]
    class AeroMesh_DuplicateLayers
    {
        // Per extra layer: which clone belongs to which original AeroMesh.
        // Keyed by the LayerSettings OBJECT, so different planets' layers
        // (even ones that happen to share an id like "outer1") never
        // collide here - each profile has its own distinct objects.
        private static readonly Dictionary<ReentryLayers.LayerSettings, Dictionary<AeroMesh, AeroMesh>> clones =
            new Dictionary<ReentryLayers.LayerSettings, Dictionary<AeroMesh, AeroMesh>>();

        // Per extra layer: its own independent mesh-shape data.
        private static readonly Dictionary<ReentryLayers.LayerSettings, BasicMeshData> meshData =
            new Dictionary<ReentryLayers.LayerSettings, BasicMeshData>();

        // Last time each source AeroMesh was actually processed by the
        // game's re-entry/heating method. This is deliberately separate
        // from temperature: teleporting can interrupt the game's normal
        // re-entry update before temperature reaches zero.
        private static readonly Dictionary<AeroMesh, float> lastProcessedTime =
            new Dictionary<AeroMesh, float>();

        internal static void MarkProcessed(AeroMesh original)
        {
            if (original != null)
                lastProcessedTime[original] = Time.unscaledTime;
        }

        internal static bool WasProcessedRecently(AeroMesh original)
        {
            if (original == null) return false;

            float last;
            if (!lastProcessedTime.TryGetValue(original, out last))
                return false;

            return Time.unscaledTime - last <= 0.5f;
        }

        static void Postfix(AeroModule __instance, float temperature, List<Surface> exposedSurfaces, float velocityAngleRad, Matrix2x2 localToWorld)
        {
            // Record that this particular re-entry mesh is still being
            // updated. If teleportation stops this method from running,
            // the watchdog attached to its clones will hide them.
            if (__instance.reentryEdge != null)
                MarkProcessed(__instance.reentryEdge);
            if (__instance.reentryOuter != null)
                MarkProcessed(__instance.reentryOuter);

            if (temperature <= 0f)
            {
                // Normal end-of-reentry cleanup.
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

            ReentryProfile profile = ReentryProfiles.GetOrLoad(ReentryPhysics.GetPlanetCodeName(__instance));

            // Same speed value applies to every extra layer on this
            // rocket - computed once per tick here, fed to each clone
            // below (ApplyPropertyBlock reads it back per-mesh).
            float speed = ReentryPhysics.GetVelocity(__instance).magnitude;

            // Copy the extras list since layers can be added/removed
            // (from the UI) between ticks.
            foreach (ReentryLayers.LayerSettings layer in profile.extraLayers.ToList())
            {
                AeroMesh original = layer.isEdge ? __instance.reentryEdge : __instance.reentryOuter;
                AeroMesh clone = GetOrCreateClone(layer, original);
                ReentryLayers.SetOwner(clone, layer);
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

            // The clone gets its own watchdog because the source
            // AeroModule can stop receiving re-entry updates abruptly
            // when a craft is teleported, recovered, switched scenes,
            // or otherwise moved out of the game's normal heating loop.
            ReentryCloneWatchdog watchdog = cloneObj.GetComponent<ReentryCloneWatchdog>();
            if (watchdog == null)
                watchdog = cloneObj.AddComponent<ReentryCloneWatchdog>();
            watchdog.Initialize(original);

            perOriginal[original] = clone;
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

        /// <summary>
        /// Hides all duplicates belonging to one original SFS reentry mesh.
        /// </summary>
        internal static void HideClonesForOriginal(AeroMesh original)
        {
            if (original == null)
                return;

            foreach (Dictionary<AeroMesh, AeroMesh> perOriginal in clones.Values)
            {
                if (!perOriginal.TryGetValue(original, out AeroMesh clone))
                    continue;

                if (clone != null)
                    clone.gameObject.SetActive(false);
            }
        }

        /// <summary>Called by ReentryProfile.RemoveLayer to clean up a removed layer's GameObjects.</summary>
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
    // STOCK REENTRY STATE CLEANUP
    // ============================================================
    // SFS itself turns the original reentry meshes off at the end of
    // AeroModule.FixedUpdate when the craft is no longer in reentry.
    // Our duplicate meshes are separate GameObjects, so they do not
    // receive that cleanup automatically.
    //
    // This is the authoritative cleanup path for teleporting: moving
    // from mid-reentry directly into orbit can skip the normal
    // temperature -> 0 transition, but FixedUpdate still sets the real
    // reentry meshes inactive. We mirror that state for every clone.

    [HarmonyPatch(typeof(AeroModule), "FixedUpdate")]
    class AeroModule_ReentryCloneCleanup
    {
        static void Postfix(AeroModule __instance)
        {
            if (__instance == null)
                return;

            if (__instance.reentryEdge != null &&
                !__instance.reentryEdge.gameObject.activeSelf)
            {
                AeroMesh_DuplicateLayers.HideClonesForOriginal(__instance.reentryEdge);
            }

            if (__instance.reentryOuter != null &&
                !__instance.reentryOuter.gameObject.activeSelf)
            {
                AeroMesh_DuplicateLayers.HideClonesForOriginal(__instance.reentryOuter);
            }
        }
    }


    // ============================================================
    // CLONE WATCHDOG
    // ============================================================
    // A teleport can bypass the normal temperature -> 0 transition.
    // In that case the source AeroModule may stop running its re-entry
    // update while the duplicated GameObject is still active.
    //
    // The watchdog is intentionally lightweight: it only checks whether
    // the source mesh still exists and whether the re-entry patch has
    // processed it recently. If not, the visual clone is immediately
    // hidden. The next real re-entry update can safely reactivate it.

    public sealed class ReentryCloneWatchdog : MonoBehaviour
    {
        private AeroMesh source;

        public void Initialize(AeroMesh sourceMesh)
        {
            source = sourceMesh;
        }

        private void Update()
        {
            if (source == null || !source.gameObject.activeInHierarchy ||
                !AeroMesh_DuplicateLayers.WasProcessedRecently(source))
            {
                gameObject.SetActive(false);
            }
        }
    }


    // ============================================================
    // CONSOLE COMMANDS - all operate on ReentryProfiles.Editing (the
    // planet currently selected in the UI/via "editplanet").
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
                ReentryProfiles.SaveEditing();
                return $"Saved {ReentryProfiles.EditingPlanet} settings";
            });

            Add("rvload", () =>
            {
                ReentryVisualsUI.ReloadEditingProfile();
                return $"Loaded {ReentryProfiles.EditingPlanet} settings";
            });

            Add("addedge", () =>
            {
                ReentryLayers.LayerSettings l = ReentryProfiles.Editing.AddLayer(true);
                ReentryVisualsUI.RefreshIfBuilt();
                return $"Added edge layer '{l.id}' to {ReentryProfiles.EditingPlanet}";
            });

            Add("addouter", () =>
            {
                ReentryLayers.LayerSettings l = ReentryProfiles.Editing.AddLayer(false);
                ReentryVisualsUI.RefreshIfBuilt();
                return $"Added outer layer '{l.id}' to {ReentryProfiles.EditingPlanet}";
            });

            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                Match m = Regex.Match(s, @"^editplanet (\S+)$", RegexOptions.IgnoreCase);
                if (!m.Success) return false;
                ReentryVisualsUI.SwitchEditingPlanet(m.Groups[1].Value);
                ModLoader.IO.Console.main.WriteText($"Now editing {ReentryProfiles.EditingPlanet}");
                return true;
            });

            ModLoader.IO.Console.commands.Add(delegate (string s)
            {
                Match m = Regex.Match(s, @"^removelayer (\S+)$", RegexOptions.IgnoreCase);
                if (!m.Success) return false;
                if (!ReentryProfiles.Editing.ById.TryGetValue(m.Groups[1].Value, out ReentryLayers.LayerSettings layer))
                    return false;
                if (layer.isOriginal)
                {
                    ModLoader.IO.Console.main.WriteText("Can't remove an original layer");
                    return true;
                }
                ReentryProfiles.Editing.RemoveLayer(layer);
                ReentryVisualsUI.RebuildAllLayerColumns();
                ModLoader.IO.Console.main.WriteText($"Removed layer '{layer.id}' from {ReentryProfiles.EditingPlanet}");
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

                if (!ReentryProfiles.Editing.ById.TryGetValue(m.Groups[1].Value, out ReentryLayers.LayerSettings layer))
                    return false;

                string parameter = m.Groups[2].Value.ToLower();
                if (!float.TryParse(m.Groups[3].Value, out float value))
                    return false;

                ReentryLayers.SetParam(layer, parameter, value);
                ModLoader.IO.Console.main.WriteText($"Set {m.Groups[1].Value}.{parameter} = {value} ({ReentryProfiles.EditingPlanet})");
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

                ReentryProfiles.Editing.SetParticleParam(parameter, value);
                ModLoader.IO.Console.main.WriteText($"Set particles.{parameter} = {value} ({ReentryProfiles.EditingPlanet})");
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
        const int WindowWidth = 640;
        const int WindowHeight = 750;

        const int ColumnWidth = 620;

        const int LabelWidth = 105;
        const int InputWidth = 190;

        const int FieldHeight = 50;

        const float RowSpacing = 60f;
        const float ColumnSpacing = 15f;

        static GameObject _holder;
        static ClosableWindow _window;
        static Transform _columnParent;
        static bool _built;

        static Button _planetSwitchButton;
        static Box _planetListBox;
        static readonly Dictionary<string, Button> _planetButtons = new Dictionary<string, Button>();

        static GameObject _particlesColumnObj;

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

            // Pre-loads Earth's profile (applying built-in defaults if no
            // save file exists yet) so settings are already in memory
            // before the player ever opens the panel.
            ReentryProfiles.GetOrLoad("Earth");
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
                ReentryProfiles.SaveEditing();
        }

        public static void RefreshIfBuilt()
        {
            if (!_built) return;

            // New layers may have appeared (added via console or loaded
            // from a save file) since the panel was built - make sure
            // every current layer in the EDITING profile has a column
            // before refreshing values.
            bool addedAny = false;
            foreach (ReentryLayers.LayerSettings layer in ReentryProfiles.Editing.AllLayers())
            {
                if (!Columns.ContainsKey(layer))
                {
                    BuildLayerColumn(_columnParent, layer);
                    addedAny = true;
                }
            }

            if (addedAny)
                MoveAddColumnToEnd();

            RefreshAll();

            if (addedAny)
                RebuildScrollLayout();
        }

        /// <summary>Re-reads the editing planet's file from disk and rebuilds the whole layer/particle section to match.</summary>
        public static void ReloadEditingProfile()
        {
            ReentryProfiles.ReloadEditing();
            RebuildAllLayerColumns();
        }

        /// <summary>Saves whatever's currently being edited, switches to a different planet's profile, and rebuilds the UI for it.</summary>
        public static void SwitchEditingPlanet(string planetCodeName)
        {
            if (!_built)
            {
                ReentryProfiles.SwitchEditingPlanet(planetCodeName);
                return;
            }

            ReentryProfiles.SwitchEditingPlanet(planetCodeName);

            if (_planetSwitchButton != null)
                _planetSwitchButton.Text = $"Editing: {ReentryProfiles.EditingPlanet}  (tap to switch)";
            HighlightEditingPlanetButton();

            RebuildAllLayerColumns();
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

            _window.CreateLayoutGroup(
                Type.Vertical, TextAnchor.UpperLeft, ColumnSpacing,
                new RectOffset(15, 15, 20, 20)
            );

            _window.EnableScrolling(Type.Vertical);

            _columnParent = _window;

            BuildPlanetSwitcher(_columnParent);

            foreach (ReentryLayers.LayerSettings layer in ReentryProfiles.Editing.AllLayers())
                BuildLayerColumn(_columnParent, layer);

            BuildParticlesColumn(_columnParent, ReentryProfiles.Editing);
            BuildAddColumn(_columnParent);

            RebuildScrollLayout();
        }

        // --------------------------------------------------------
        // PLANET SWITCHER - one button at the top ("Editing: X (tap
        // to switch)") that toggles a collapsible list of every
        // planet currently loaded by the game (vanilla and custom),
        // sourced live from SFS.Base.planetLoader.planets so a custom
        // solar system's planets appear automatically.
        // --------------------------------------------------------

        static void BuildPlanetSwitcher(Transform parent)
        {
            _planetSwitchButton = Builder.CreateButton(parent, ColumnWidth, 50, 0, 0,
                TogglePlanetPicker, $"Editing: {ReentryProfiles.EditingPlanet}  (tap to switch)");
            SetFixedSize(_planetSwitchButton.gameObject, ColumnWidth, 50);

            _planetListBox = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.2f);
            SetFixedWidthAutoHeight(_planetListBox.gameObject, ColumnWidth);
            _planetListBox.CreateLayoutGroup(
                Type.Vertical,
                TextAnchor.UpperCenter,
                6f,
                new RectOffset(10, 10, 10, 10)
            );
            _planetListBox.gameObject.SetActive(false);

            RebuildPlanetPicker();
        }

        /// <summary>
        /// Rebuilds the picker from the currently loaded SFS planets.
        /// This runs whenever the picker is opened so entering a custom
        /// solar system after the UI was created cannot leave a stale list.
        /// </summary>
        static void RebuildPlanetPicker()
        {
            if (_planetListBox == null)
                return;

            foreach (Transform child in _planetListBox.gameObject.transform)
                Object.Destroy(child.gameObject);

            _planetButtons.Clear();

            foreach (string codeName in ReentryProfiles.AllPlanetCodeNames())
            {
                string planetCodeName = codeName;

                Button b = Builder.CreateButton(
                    _planetListBox,
                    ColumnWidth - 20,
                    36,
                    0,
                    0,
                    () => SelectPlanet(planetCodeName),
                    planetCodeName
                );

                SetFixedSize(b.gameObject, ColumnWidth - 20, 36);
                _planetButtons[planetCodeName] = b;
            }

            HighlightEditingPlanetButton();
            RebuildScrollLayout();
        }

        static void TogglePlanetPicker()
        {
            // Re-query the active solar system every time the button is clicked.
            RebuildPlanetPicker();

            _planetListBox.gameObject.SetActive(
                !_planetListBox.gameObject.activeSelf
            );

            RebuildScrollLayout();
        }

        static void SelectPlanet(string codeName)
        {
            _planetListBox.gameObject.SetActive(false);
            SwitchEditingPlanet(codeName);
        }

        static void HighlightEditingPlanetButton()
        {
            foreach (KeyValuePair<string, Button> entry in _planetButtons)
                entry.Value.TextColor = string.Equals(entry.Key, ReentryProfiles.EditingPlanet, StringComparison.OrdinalIgnoreCase)
                    ? Color.yellow
                    : Color.white;
        }

        /// <summary>Tears down and rebuilds every layer column + the particles column from ReentryProfiles.Editing - used after switching or reloading a profile.</summary>
        public static void RebuildAllLayerColumns()
        {
            if (!_built) return;

            foreach (Box column in Columns.Values)
                if (column != null) Object.Destroy(column.gameObject);
            Columns.Clear();
            Inputs.Clear();
            Defaults.Clear();
            ParticleInputs.Clear();

            if (_particlesColumnObj != null) Object.Destroy(_particlesColumnObj);

            ReentryProfile profile = ReentryProfiles.Editing;
            foreach (ReentryLayers.LayerSettings layer in profile.AllLayers())
                BuildLayerColumn(_columnParent, layer);

            BuildParticlesColumn(_columnParent, profile);
            MoveAddColumnToEnd();
            RebuildScrollLayout();
        }

        // Forces the scroll content to recompute its height after columns
        // are added, removed, or reordered at runtime. Without this the
        // ScrollRect's cached content size can stay smaller than the
        // actual content, capping how far down it lets you scroll.
        static void RebuildScrollLayout()
        {
            if (_columnParent is RectTransform rt)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        static void BuildLayerColumn(Transform parent, ReentryLayers.LayerSettings layer)
        {
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

        static void BuildParticlesColumn(Transform parent, ReentryProfile profile)
        {
            Box column = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.15f);
            SetFixedWidthAutoHeight(column.gameObject, ColumnWidth);
            column.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, RowSpacing, new RectOffset(10, 10, 15, 15));
            _particlesColumnObj = column.gameObject;

            Builder.CreateLabel(column, ColumnWidth - 20, 40, 0, 0, "Burn Particles");

            BuildParticleRow(column, profile, "Enabled", "enabled", 1f, profile.particlesEnabled ? 1f : 0f);
            BuildParticleRow(column, profile, "Rate/sec", "rate", 1f, profile.particlesRate);
            BuildParticleRow(column, profile, "Velocity", "velocity", 0.1f, profile.particlesVelocity);
            BuildParticleRow(column, profile, "Spread", "spread", 0.1f, profile.particlesSpread);
        }

        static void BuildParticleRow(Transform parent, ReentryProfile profile, string label, string parameter, float step, float value)
        {
            Container row = Builder.CreateContainer(parent);
            SetFixedSize(row.gameObject, ColumnWidth - 20, FieldHeight);
            row.CreateLayoutGroup(Type.Horizontal, TextAnchor.MiddleCenter, 12f);

            Builder.CreateLabel(row, LabelWidth, FieldHeight, 0, 0, label);

            NumberInput input = UIToolsBuilder.CreateNumberInput(row, InputWidth, FieldHeight, value, step);
            SetFixedSize(input.gameObject, InputWidth, FieldHeight);

            ParticleInputs[parameter] = input;
            input.OnValueChangedEvent += newValue => profile.SetParticleParam(parameter, newValue);
        }

        static void BuildAddColumn(Transform parent)
        {
            const int addBoxHeight = 300;

            Box column = Builder.CreateBox(parent, ColumnWidth, 10, opacity: 0.1f);
            SetFixedWidthAutoHeight(column.gameObject, ColumnWidth);
            column.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, 20f, new RectOffset(10, 10, 15, 15));

            _addColumnObj = column.gameObject;

            Builder.CreateLabel(column, ColumnWidth - 20, 40, 0, 0, "Add Layer");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentryLayers.LayerSettings l = ReentryProfiles.Editing.AddLayer(true);
                BuildLayerColumn(_columnParent, l);
                MoveAddColumnToEnd();
                RebuildScrollLayout();
            }, "+ Edge");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentryLayers.LayerSettings l = ReentryProfiles.Editing.AddLayer(false);
                BuildLayerColumn(_columnParent, l);
                MoveAddColumnToEnd();
                RebuildScrollLayout();
            }, "+ Outer");

            Builder.CreateLabel(column, ColumnWidth - 20, 30, 0, 0, "");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReentryProfiles.SaveEditing();
            }, "Save All");

            Builder.CreateButton(column, ColumnWidth - 20, 50, 0, 0, () =>
            {
                ReloadEditingProfile();
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
            ReentryProfiles.Editing.RemoveLayer(layer);

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

            ReentryProfile profile = ReentryProfiles.Editing;
            foreach (KeyValuePair<string, NumberInput> entry in ParticleInputs)
                entry.Value.Value = profile.GetParticleParam(entry.Key);
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
    