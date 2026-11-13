using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GameNetcodeStuff;
using UnityEngine;

namespace PaperPals
{
    internal sealed class VoiceClip
    {
        public int Rate;
        public byte[] Pcm;      // 16-bit signed little-endian, mono
        public string File;
        public float Seconds => Pcm.Length / 2f / Rate;
    }

    /// Host only. One per PaperPal. Reads MirageVoces' per-player clips:
    /// Mirage/Centralizado/Efectos/<SteamID64 or "yo">/<category>__<time>.wav. Missing MirageVoces = silent, no errors.
    internal sealed class VoiceLibrary
    {
        private const float MaxSeconds = 15f;
        private static readonly System.Random Rng = new System.Random();
        private readonly NpcDefinition def;
        private readonly Dictionary<string, Bag> bags = new Dictionary<string, Bag>();
        private readonly HashSet<string> warnings = new HashSet<string>();
        private readonly Dictionary<string, VoiceClip> cache = new Dictionary<string, VoiceClip>(); // decoded clips (main-thread decode once)
        private readonly Queue<string> cacheOrder = new Queue<string>();
        private const int CacheSize = 40;

        private sealed class Bag
        {
            public string Key, LastPlayed;
            public List<string> Files = new List<string>();
            public readonly HashSet<string> Played = new HashSet<string>();
            public float LastScan = -999f;
        }

        public VoiceLibrary(NpcDefinition def) { this.def = def; }

        internal static string MirageRoot =>
            string.IsNullOrWhiteSpace(Plugin.MirageFolder.Value)
                ? Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Mirage")
                : Plugin.MirageFolder.Value.Trim();

        public VoiceClip PickNormal() => Pick(def.J.voice.categories);
        public VoiceClip PickPanic() => Pick(def.J.voice.panicCategories);
        public VoiceClip PickFear() => Pick(def.J.voice.fearCategories);
        public VoiceClip PickHurt() => Pick(def.J.voice.hurtCategories);

        /// Clips from the given comma-separated categories (empty = all). No repeats until all were used.
        public VoiceClip Pick(string categories)
        {
            string key = ResolveKey();
            if (key == null) return null;
            string cats = (categories ?? "").Trim().ToLowerInvariant();
            if (!bags.TryGetValue(cats, out var bag)) bags[cats] = bag = new Bag();
            if (key != bag.Key || Time.realtimeSinceStartup - bag.LastScan > 60f) Refill(bag, key, cats);
            for (int tries = 0; tries < 5 && bag.Files.Count > 0; tries++)
            {
                int i = Rng.Next(bag.Files.Count);
                string f = bag.Files[i];
                if (f == bag.LastPlayed && bag.Files.Count > 1) continue;
                bag.Files.RemoveAt(i);
                var clip = Cached(f);
                if (clip == null) continue;
                bag.LastPlayed = f;
                bag.Played.Add(f);
                if (bag.Files.Count == 0) bag.LastScan = -999f; // refill next time
                return clip;
            }
            if (bag.Files.Count == 0)
                WarnOnce($"{def.Id}: no usable clips ({(cats.Length > 0 ? cats : "all")}) in {Path.Combine(MirageRoot, "Centralizado", "Efectos", key)}");
            return null;
        }

        private void Refill(Bag bag, string key, string cats)
        {
            if (key != bag.Key) { bag.Played.Clear(); bag.Key = key; }
            bag.LastScan = Time.realtimeSinceStartup;
            string dir = Path.Combine(MirageRoot, "Centralizado", "Efectos", key);
            var allowed = new HashSet<string>(cats.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            List<string> all;
            try
            {
                all = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.wav").Where(f =>
                    {
                        if (f.EndsWith(".tmp48.wav", StringComparison.OrdinalIgnoreCase)) return false;
                        if (allowed.Count == 0) return true;
                        string n = Path.GetFileName(f);
                        int cut = n.IndexOf("__", StringComparison.Ordinal);
                        return cut > 0 && allowed.Contains(n.Substring(0, cut).ToLowerInvariant());
                    }).ToList()
                    : new List<string>();
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{def.Id}: cannot list {dir}: {e.Message}"); all = new List<string>(); }
            var fresh = all.Where(f => !bag.Played.Contains(f)).ToList();
            if (fresh.Count == 0) { bag.Played.Clear(); fresh = all.Where(f => f != bag.LastPlayed || all.Count == 1).ToList(); }
            bag.Files = fresh;
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"{def.Id}: {bag.Files.Count} clips ({(cats.Length > 0 ? cats : "all")}) for '{key}'");
        }

        /// Efectos folder name: a SteamID64 or "yo" (host's own voice). SteamID64 is matched first (names can change).
        private string ResolveKey()
        {
            string target = def.TargetPlayer;
            if (string.IsNullOrEmpty(target)) { WarnOnce($"{def.Id}: no TargetPlayer set; {def.DisplayName} stays silent."); return null; }
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;

            if (Regex.IsMatch(target, "^[0-9]{15,20}$") && ulong.TryParse(target, out ulong sid))
                return local != null && local.playerSteamId == sid ? "yo" : target;
            if (target.Equals("yo", StringComparison.OrdinalIgnoreCase) || target.Equals("host", StringComparison.OrdinalIgnoreCase))
                return "yo";

            ulong id = 0;
            List<string> known = null;
            if (local != null && NameEquals(local.playerUsername, target)) return "yo";
            if (StartOfRound.Instance != null)
                foreach (var p in StartOfRound.Instance.allPlayerScripts)
                    if (p != null && p.playerSteamId != 0 && NameEquals(p.playerUsername, target)) { id = p.playerSteamId; break; }
            if (id == 0) id = LookupJugadores(target, out known);
            if (id == 0)
            {
                WarnOnce($"{def.Id}: TargetPlayer '{target}' not found in the lobby or in Mirage/Centralizado/jugadores.json" +
                         (known != null && known.Count > 0 ? $" (known: {string.Join(", ", known)})" : "") + $". {def.DisplayName} stays silent.");
                return null;
            }
            if (local != null && local.playerSteamId == id) return "yo";
            return id.ToString();
        }

        private VoiceClip Cached(string path)
        {
            if (cache.TryGetValue(path, out var c)) return c;
            c = ReadWav(path);
            if (c == null) return null;
            cache[path] = c;
            cacheOrder.Enqueue(path);
            while (cacheOrder.Count > CacheSize) cache.Remove(cacheOrder.Dequeue());
            return c;
        }

        private static bool NameEquals(string a, string b) =>
            a != null && (a == b || string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase));

        private static ulong LookupJugadores(string name, out List<string> known)
        {
            known = new List<string>();
            try
            {
                string path = Path.Combine(MirageRoot, "Centralizado", "jugadores.json");
                if (!File.Exists(path)) return 0;
                ulong fallback = 0;
                foreach (Match m in Regex.Matches(File.ReadAllText(path), "\"([0-9]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    string n = Regex.Unescape(m.Groups[2].Value);
                    known.Add(n);
                    if (n == name) return ulong.Parse(m.Groups[1].Value);
                    if (fallback == 0 && NameEquals(n, name)) fallback = ulong.Parse(m.Groups[1].Value);
                }
                return fallback;
            }
            catch (Exception e) { Plugin.Log.LogWarning("jugadores.json: " + e.Message); return 0; }
        }

        private static VoiceClip ReadWav(string path)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') return null;
                int pos = 12, channels = 0, rate = 0, bits = 0, fmt = 0, dataPos = -1, dataLen = 0;
                while (pos + 8 <= b.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                    int len = BitConverter.ToInt32(b, pos + 4);
                    if (id == "fmt ")
                    {
                        fmt = BitConverter.ToInt16(b, pos + 8);
                        channels = BitConverter.ToInt16(b, pos + 10);
                        rate = BitConverter.ToInt32(b, pos + 12);
                        bits = BitConverter.ToInt16(b, pos + 22);
                    }
                    else if (id == "data")
                    {
                        dataPos = pos + 8;
                        dataLen = Math.Min(len, b.Length - dataPos);
                        break;
                    }
                    pos += 8 + len + (len & 1);
                }
                if (fmt != 1 || bits != 16 || channels < 1 || rate < 4000 || dataPos < 0) return null;
                int step = rate > 24000 ? rate / 16000 : 1;
                int inFrames = Math.Min(dataLen / (2 * channels), (int)(rate * MaxSeconds));
                int frames = inFrames / step;
                rate /= step;
                var pcm = new byte[frames * 2];
                for (int f = 0; f < frames; f++)
                {
                    int sum = 0;
                    for (int k = 0; k < step; k++)
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(b, dataPos + ((f * step + k) * channels + c) * 2);
                    short s = (short)(sum / (channels * step));
                    pcm[f * 2] = (byte)(s & 0xFF);
                    pcm[f * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                return frames < rate / 4 ? null : new VoiceClip { Rate = rate, Pcm = pcm, File = path };
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Cannot read {path}: {e.Message}"); return null; }
        }

        private void WarnOnce(string msg)
        {
            if (warnings.Add(msg)) Plugin.Log.LogWarning(msg);
        }
    }
}
