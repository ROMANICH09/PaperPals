using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LethalLib.Modules;
using UnityEngine;
using UnityEngine.AI;

namespace PaperPals
{
    [BepInPlugin(Guid, "PaperPals", Version)]
    [BepInDependency("evaisa.lethallib")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "luver_inn.paperpals";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static string PluginDir;
        internal static readonly List<NpcDefinition> Defs = new List<NpcDefinition>();

        // global config
        internal static ConfigEntry<string> MirageFolder, FearIgnore;
        internal static ConfigEntry<bool> OnlyPaperPals, GuaranteedSpawn, SpawnNearEntrance, DebugLog, MatchPlayerEyes, PanicEnabled;
        internal static ConfigEntry<int> GuaranteedSpawnCount;
        internal static ConfigEntry<float> Volume, HearingDistance, MouthThreshold, StepTime, CooldownMin, CooldownMax,
            AloneMin, AloneMax, TalkRange, PanicSpeed, PanicAfterLanding, ShoutPauseMin, ShoutPauseMax, DoorTimeout, LieOffset,
            SneakSpeed, WitnessRange, WitnessCloseRange, GraceSeconds;

        private void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            BindGlobalConfig();

            foreach (var def in FindDefinitions())
            {
                def.BindConfig(Config);
                if (!def.Enabled.Value) { Log.LogInfo($"{def.Id}: disabled in config."); continue; }
                def.Sprites = SpriteSet.Load(def);
                if (!def.Sprites.Ok) continue;
                def.Voice = new VoiceLibrary(def);
                def.Index = Defs.Count;
                Defs.Add(def);
                try { BuildAndRegister(def); }
                catch (Exception e) { Log.LogError($"{def.Id}: registration failed: {e}"); Defs.Remove(def); }
            }

            new Harmony(Guid).PatchAll(typeof(Patches));
            Log.LogInfo($"PaperPals {Version} loaded: {Defs.Count} PaperPal(s) [{string.Join(", ", Defs.Select(d => d.Id))}]" +
                        (OnlyPaperPals.Value || GuaranteedSpawn.Value ? "  ** TEST HELPERS ON **" : ""));
        }

        private void BindGlobalConfig()
        {
            const string V = "Voice", S = "Sprite", M = "Meltdown", F = "Fear", W = "Witness", T = "Testing", H = "Health";
            MirageFolder = Config.Bind(V, "MirageFolder", "", "Mirage data folder. Empty = <game folder>/Mirage. Host only.");
            Volume = Config.Bind(V, "Volume", 1f, "0 to 1.");
            HearingDistance = Config.Bind(V, "HearingDistance", 28f, "Metres at which their voices fade out.");
            CooldownMin = Config.Bind(V, "CooldownMin", 20f, "Minimum seconds between lines to a player. Host only.");
            CooldownMax = Config.Bind(V, "CooldownMax", 60f, "Maximum seconds between lines to a player. Host only.");
            AloneMin = Config.Bind(V, "AloneMin", 90f, "Lines said to nobody: minimum seconds between them. 0 = never. Host only.");
            AloneMax = Config.Bind(V, "AloneMax", 240f, "Lines said to nobody: maximum seconds between them. Host only.");
            TalkRange = Config.Bind(V, "TalkRange", 12f, "They talk to a player in view within this many metres. Host only.");
            MouthThreshold = Config.Bind(S, "MouthThreshold", 0.02f, "Audio loudness (RMS) above which the mouth opens.");
            StepTime = Config.Bind(S, "StepTime", 0.32f, "Seconds each walking frame is shown at normal speed. Live.");
            MatchPlayerEyes = Config.Bind(S, "MatchPlayerEyeHeight", true,
                "Size every pal from a standing player's eye height (a 180 cm pal has his eyes level with yours).");
            PanicEnabled = Config.Bind(M, "Enabled", true, "Pulling the apparatus makes every pal panic and run to the ship. Host only.");
            PanicSpeed = Config.Bind(M, "RunSpeed", 6.5f, "Running speed (meltdown and fleeing).");
            ShoutPauseMin = Config.Bind(M, "ShoutPauseMin", 1.5f, "Seconds between panic/fear lines (min).");
            ShoutPauseMax = Config.Bind(M, "ShoutPauseMax", 4f, "Seconds between panic/fear lines (max).");
            DoorTimeout = Config.Bind(M, "DoorTimeout", 40f, "If a pal can't reach the main entrance in time, he goes through it anyway.");
            LieOffset = Config.Bind(H, "LieOffset", 0.12f, "Dead pals lie this far above the floor so clutter doesn't poke through.");
            SneakSpeed = Config.Bind(F, "SneakSpeed", 1.4f, "Crouched sneaking speed (m/s).");
            FearIgnore = Config.Bind(F, "FearIgnore", "Manticoil,Docile Locust Bees",
                "Monsters (enemyName, comma-separated) that never scare pals. Other PaperPals never do.");
            WitnessRange = Config.Bind(W, "WitnessRange", 30f, "Outside, monsters may hurt a pal only while a player within this range can see him...");
            WitnessCloseRange = Config.Bind(W, "WitnessCloseRange", 15f, "...or a player is within this range (no sight needed).");
            GraceSeconds = Config.Bind(W, "GraceSeconds", 10f, "Full immunity from monsters after spawning and after first stepping outside.");
            OnlyPaperPals = Config.Bind(T, "OnlyPaperPals", false, "TEST ONLY (host): every inside monster spawn becomes a random pal; no other monsters.");
            GuaranteedSpawn = Config.Bind(T, "GuaranteedSpawn", false, "TEST ONLY (host): spawn pals on every landing.");
            GuaranteedSpawnCount = Config.Bind(T, "GuaranteedSpawnCount", 1, "How many (cycles through the installed pals).");
            SpawnNearEntrance = Config.Bind(T, "SpawnNearEntrance", false, "TEST ONLY: the first guaranteed pal spawns by the main entrance.");
            PanicAfterLanding = Config.Bind(T, "PanicAfterLanding", 0f,
                "TEST ONLY (host): start the meltdown panic this many seconds after landing, without pulling the apparatus. 0 = off.");
            DebugLog = Config.Bind(T, "DebugLog", false, "Log state every 10 s and every ignored monster hit (host).");
        }

        /// Every file named exactly "npc.json" under BepInEx/plugins. "Template" folders inside PaperPals are skipped.
        private static IEnumerable<NpcDefinition> FindDefinitions()
        {
            var files = new List<string>();
            try
            {
                files = Directory.GetFiles(Paths.PluginPath, "npc.json", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f) == "npc.json")              // exact, case-sensitive
                    .Where(f => !InTemplateFolder(f))
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) { Log.LogError("Scanning for npc.json failed: " + e.Message); }
            Log.LogInfo($"{files.Count} npc.json file(s) found.");

            var byId = new Dictionary<string, NpcDefinition>();
            foreach (var f in files)
            {
                var def = NpcDefinition.Load(f);
                if (def == null) continue;
                if (byId.TryGetValue(def.Id, out var first))
                { Log.LogError($"Duplicate PaperPal id '{def.Id}' in {f}; keeping {first.JsonPath}."); continue; }
                byId[def.Id] = def;
            }
            return byId.Values.OrderBy(d => d.Id, StringComparer.Ordinal); // same order on every PC
        }

        /// Only the template shipped inside the PaperPals package is skipped (it also has no npc.json, just npc.template.json).
        private static bool InTemplateFolder(string file)
        {
            if (!file.StartsWith(PluginDir, StringComparison.OrdinalIgnoreCase)) return false;
            string rel = file.Substring(PluginDir.Length);
            bool skip = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == "Template");
            if (skip) Log.LogInfo($"Skipping template file {file}.");
            return skip;
        }

        private static void BuildAndRegister(NpcDefinition def)
        {
            float s = def.HeightScale; // physical size relative to a 180 cm pal (Amirkhan 1.1.0's numbers)
            GameObject prefab = NetworkPrefabs.CreateNetworkPrefab("PaperPal_" + def.Id);

            var agent = prefab.AddComponent<NavMeshAgent>();
            agent.speed = def.WalkSpeed;
            agent.angularSpeed = 360f;
            agent.acceleration = 8f;
            agent.stoppingDistance = 0.3f;
            agent.radius = Mathf.Max(0.1f, 0.35f * s);
            agent.height = Mathf.Max(0.3f, 1.8f * s);
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            agent.avoidancePriority = 99;

            var eye = new GameObject("Eye").transform;
            eye.SetParent(prefab.transform, false);
            eye.localPosition = new Vector3(0f, 1.6f * s, 0f);

            var board = new GameObject("Billboard");
            board.transform.SetParent(prefab.transform, false);
            board.AddComponent<MeshFilter>().sharedMesh = def.Sprites.Quad;
            var mr = board.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;

            var voiceGo = new GameObject("Voice");
            voiceGo.transform.SetParent(prefab.transform, false);
            voiceGo.transform.localPosition = new Vector3(0f, 1.6f * s, 0f);
            var src = voiceGo.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2f;
            src.maxDistance = HearingDistance.Value;
            src.dopplerLevel = 0f;
            voiceGo.AddComponent<AudioLowPassFilter>();
            voiceGo.AddComponent<OccludeAudio>();

            var ai = prefab.AddComponent<PaperPalAI>();
            ai.DefIndex = def.Index;
            ai.eye = eye;
            ai.enemyHP = def.Hp;
            ai.updatePositionThreshold = 0.25f;
            ai.syncMovementSpeed = 0.15f;
            ai.exitVentAnimationTime = 0.1f;
            ai.enemyBehaviourStates = PaperPalAI.MakeStates();

            // Scan node (red, like monsters). HUD sphere-casts layer 22 for this component.
            var scan = new GameObject("ScanNode");
            scan.transform.SetParent(prefab.transform, false);
            scan.transform.localPosition = new Vector3(0f, 1.3f * s, 0f);
            scan.layer = 22;
            var scanBox = scan.AddComponent<BoxCollider>();
            scanBox.isTrigger = true;
            scanBox.size = new Vector3(1f, 2.2f, 1f) * Mathf.Max(0.3f, s);
            var node = scan.AddComponent<ScanNodeProperties>();
            node.headerText = def.DisplayName;
            node.subText = def.J.scanSubtext ?? "";
            node.nodeType = 1;
            node.minRange = 1;
            node.maxRange = 25;
            node.requiresLineOfSight = true;
            node.creatureScanID = -1;

            // Enemy trigger: doors open for him; players and monsters can hit him; players walk through.
            var body = new GameObject("Collision");
            body.transform.SetParent(prefab.transform, false);
            body.tag = "Enemy";
            body.layer = 19;
            var cap = body.AddComponent<CapsuleCollider>();
            cap.isTrigger = true;
            cap.radius = Mathf.Max(0.1f, 0.35f * s);
            cap.height = Mathf.Max(0.3f, 1.8f * s);
            cap.center = new Vector3(0f, 0.9f * s, 0f);
            var rb = body.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            body.AddComponent<EnemyAICollisionDetect>().mainScript = ai;

            var type = ScriptableObject.CreateInstance<EnemyType>();
            type.name = "PaperPal_" + def.Id;
            type.enemyName = def.DisplayName;
            type.enemyPrefab = prefab;
            type.PowerLevel = 0f;
            type.MaxCount = 1;
            type.DiversityPowerLevel = 0;
            type.isOutsideEnemy = false;
            type.isDaytimeEnemy = false;
            type.probabilityCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
            type.numberSpawnedFalloff = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));
            type.EnemySize = EnemySize.Medium; // default is Tiny, which Giant Kiwis kill instantly (bypassing the witness rule)
            type.canDie = true;
            type.canBeStunned = false;
            type.canBeDestroyed = true;
            type.destroyOnDeath = false;
            type.disableAnimatorWhenFar = false;
            type.pushPlayerForce = 0f;
            type.pushPlayerDistance = 0f;
            type.loudnessMultiplier = 0f;
            type.audioClips = new AudioClip[0];
            type.miscAnimations = new MiscAnimation[0];
            ai.enemyType = type;
            def.Type = type;

            Enemies.RegisterEnemy(type, def.Rarity, Levels.LevelTypes.All, Enemies.SpawnType.Default,
                (TerminalNode)null, (TerminalKeyword)null);
            Log.LogInfo($"Registered PaperPal '{def.Id}' ({def.DisplayName}, {def.HeightCm} cm, eyeLevel {def.EyeLevel}, " +
                        $"rarity {def.Rarity}, voice '{def.TargetPlayer}', hp {def.Hp}, walk {def.WalkSpeed}, fear {def.FearEnabled}/{def.FearRadius}/{def.FearRunDistance}/{def.CalmDelay}).");
        }
    }
}
