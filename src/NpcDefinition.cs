using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;

namespace PaperPals
{
    // ------------------------------------------------------------------ npc.json, format 1
    // Field initialisers are the defaults (Amirkhan_NPC 1.1.0's values) used when a key is missing.
    [Serializable] public class NpcJson
    {
        public int format = 1;
        public string id = "";
        public string displayName = "";
        public string scanSubtext = "";
        public VoiceJson voice = new VoiceJson();
        public SpriteJson sprite = new SpriteJson();
        public PosesJson poses = new PosesJson();
        public FearJson fear = new FearJson();
        public StatsJson stats = new StatsJson();
        public FeaturesJson features = new FeaturesJson();
    }
    [Serializable] public class VoiceJson
    {
        public string targetPlayer = "";
        public string categories = "frase,afirmacion,risa,llamada,seguir,aviso,nave,botin,ayuda,nombre,respuesta";
        public string panicCategories = "grito,ayuda,aviso";
        public string fearCategories = "ayuda,aviso,grito";
        public string hurtCategories = "grito,ayuda"; // panic scream or call for help, on every counted hit
    }
    [Serializable] public class SpriteJson { public float eyeLevel = 0.912f; public float heightCm = 180f; }
    [Serializable] public class PosesJson { public float crouchChance = 0f; }
    [Serializable] public class FearJson { public bool enabled = true; public float radius = 10f; public float calmDelay = 3f; public float runDistance = 4f; }
    [Serializable] public class StatsJson { public int hp = 6; public float walkSpeed = 2.6f; public int rarity = 25; }
    [Serializable] public class FeaturesJson { public bool meltdownPanic = true; public bool deathScream = true; }

    /// One PaperPal: the npc.json values, the host's per-NPC config overrides, its frames and its voice.
    internal sealed class NpcDefinition
    {
        public NpcJson J;
        public string JsonPath, Dir;
        public string Id => J.id;
        public SpriteSet Sprites;
        public VoiceLibrary Voice;
        public EnemyType Type;
        public int Index;

        // Per-NPC config. Overrides use "empty" / -1 as "use npc.json", so an addon update that changes npc.json
        // still reaches PCs that already have a .cfg (BepInEx keeps the first written default forever).
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> TargetPlayerCfg, ScanNameCfg, FearEnabledCfg;
        public ConfigEntry<int> RarityCfg, HpCfg;
        public ConfigEntry<float> WalkSpeedCfg, HeightCmCfg, FearRadiusCfg, FearRunCfg;

        public string DisplayName => !string.IsNullOrWhiteSpace(ScanNameCfg?.Value) ? ScanNameCfg.Value.Trim()
            : !string.IsNullOrWhiteSpace(J.displayName) ? J.displayName : J.id;
        public string TargetPlayer => !string.IsNullOrWhiteSpace(TargetPlayerCfg?.Value) ? TargetPlayerCfg.Value.Trim() : (J.voice.targetPlayer ?? "").Trim();
        public int Rarity => RarityCfg != null && RarityCfg.Value >= 0 ? RarityCfg.Value : Math.Max(0, J.stats.rarity);
        public int Hp => HpCfg != null && HpCfg.Value > 0 ? HpCfg.Value : Math.Max(1, J.stats.hp);
        public float WalkSpeed => WalkSpeedCfg != null && WalkSpeedCfg.Value > 0f ? WalkSpeedCfg.Value : Mathf.Max(0.5f, J.stats.walkSpeed);
        public float HeightCm => ClampHeight(HeightCmCfg != null && HeightCmCfg.Value >= 0f ? HeightCmCfg.Value : J.sprite.heightCm, false);
        /// 1 = a 180 cm pal = eyes level with a standing player.
        public float HeightScale => HeightCm / 180f;
        public float EyeLevel => J.sprite.eyeLevel > 0.05f && J.sprite.eyeLevel <= 1f ? J.sprite.eyeLevel : 0.912f;
        public bool FearEnabled => FearEnabledCfg != null && bool.TryParse(FearEnabledCfg.Value?.Trim(), out bool b) ? b : J.fear.enabled;
        public float FearRadius => FearRadiusCfg != null && FearRadiusCfg.Value >= 0f ? FearRadiusCfg.Value : Mathf.Max(0f, J.fear.radius);
        public float FearRunDistance => FearRunCfg != null && FearRunCfg.Value >= 0f ? FearRunCfg.Value : Mathf.Max(0f, J.fear.runDistance);
        public float CalmDelay => Mathf.Max(0.5f, J.fear.calmDelay);
        public float CrouchChance => Mathf.Clamp01(J.poses.crouchChance);

        public static float ClampHeight(float cm, bool warn, string who = null)
        {
            if (float.IsNaN(cm) || float.IsInfinity(cm)) cm = 180f;
            float c = Mathf.Clamp(cm, 1f, 200f);
            if (warn && !Mathf.Approximately(c, cm)) Plugin.Log.LogWarning($"{who}: heightCm {cm} clamped to {c} (allowed 1-200).");
            return c;
        }

        /// Parses npc.json. Returns null (with a log line) when the file can't be used.
        public static NpcDefinition Load(string path)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { Plugin.Log.LogError($"Cannot read {path}: {e.Message}"); return null; }

            var j = new NpcJson();
            try { JsonUtility.FromJsonOverwrite(text, j); }
            catch (Exception e) { Plugin.Log.LogError($"Skipping {path}: invalid JSON ({e.Message})."); return null; }

            if (j.format != 1) { Plugin.Log.LogError($"Skipping {path}: unknown npc.json format {j.format} (this PaperPals reads format 1). Update PaperPals."); return null; }
            if (string.IsNullOrEmpty(j.id) || !Regex.IsMatch(j.id, "^[a-z0-9_]+$"))
            { Plugin.Log.LogError($"Skipping {path}: \"id\" must be lowercase letters, digits or _ (got \"{j.id}\")."); return null; }
            j.voice ??= new VoiceJson(); j.sprite ??= new SpriteJson(); j.poses ??= new PosesJson();
            j.fear ??= new FearJson(); j.stats ??= new StatsJson(); j.features ??= new FeaturesJson();

            if (!Regex.IsMatch(text, "\"heightCm\"\\s*:\\s*-?[0-9]"))
            { Plugin.Log.LogWarning($"{j.id}: sprite.heightCm missing or not a number, using 180."); j.sprite.heightCm = 180f; }
            j.sprite.heightCm = ClampHeight(j.sprite.heightCm, true, j.id);
            if (j.sprite.eyeLevel <= 0.05f || j.sprite.eyeLevel > 1f)
            { Plugin.Log.LogWarning($"{j.id}: sprite.eyeLevel {j.sprite.eyeLevel} is not between 0.05 and 1, using 0.912."); j.sprite.eyeLevel = 0.912f; }

            return new NpcDefinition { J = j, JsonPath = path, Dir = Path.GetDirectoryName(path) };
        }

        public void BindConfig(ConfigFile cfg)
        {
            string s = J.id;
            Enabled = cfg.Bind(s, "Enabled", true, $"Spawn {J.displayName} at all. Needs a restart.");
            TargetPlayerCfg = cfg.Bind(s, "TargetPlayer", "",
                $"Whose MirageVoces clips he speaks (Steam name, SteamID64 or 'yo' for the host). Empty = use npc.json (\"{J.voice.targetPlayer}\"). Host only.");
            ScanNameCfg = cfg.Bind(s, "ScanName", "", $"Scanner name. Empty = use npc.json (\"{J.displayName}\").");
            RarityCfg = cfg.Bind(s, "Rarity", -1, $"Indoor spawn weight. -1 = use npc.json ({J.stats.rarity}). Needs a restart.");
            HpCfg = cfg.Bind(s, "HP", -1, $"Hits he survives. -1 = use npc.json ({J.stats.hp}).");
            WalkSpeedCfg = cfg.Bind(s, "WalkSpeed", -1f, $"Walking speed (m/s). -1 = use npc.json ({J.stats.walkSpeed}).");
            HeightCmCfg = cfg.Bind(s, "HeightCm", -1f, $"How tall he looks, 1-200 (180 = eyes level with a player). -1 = use npc.json ({J.sprite.heightCm}). The picture updates live; hitbox and scan node after a restart. Every player should use the same value.");
            FearEnabledCfg = cfg.Bind(s, "FearEnabled", "", $"true/false. Empty = use npc.json ({J.fear.enabled.ToString().ToLowerInvariant()}). Host only.");
            FearRadiusCfg = cfg.Bind(s, "FearRadius", -1f, $"Metres at which a visible monster scares him. -1 = use npc.json ({J.fear.radius}). Host only.");
            FearRunCfg = cfg.Bind(s, "FearRunDistance", -1f, $"Closer than this he runs. -1 = use npc.json ({J.fear.runDistance}). Host only.");
        }
    }
}
