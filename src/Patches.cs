using System.Linq;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace PaperPals
{
    [HarmonyPatch]
    internal static class Patches
    {
        private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        // ------------------------------------------------------------------ landing: reset panic, test spawns
        [HarmonyPatch(typeof(StartOfRound), "OnShipLandedMiscEvents")]
        [HarmonyPostfix]
        private static void OnLanded()
        {
            PaperPalAI.PanicActive = false;
            panicTestAt = Plugin.PanicAfterLanding.Value > 0f && IsHost ? Time.time + Plugin.PanicAfterLanding.Value : -1f;
            try
            {
                if (!Plugin.GuaranteedSpawn.Value || !IsHost || Plugin.Defs.Count == 0) return;
                var rm = RoundManager.Instance;
                GameObject[] nodes = GameObject.FindGameObjectsWithTag("AINode");
                if (rm == null || nodes == null || nodes.Length == 0) { Plugin.Log.LogInfo("No interior on this moon; no guaranteed PaperPals."); return; }
                int want = Mathf.Max(1, Plugin.GuaranteedSpawnCount.Value);
                var order = Plugin.Defs.OrderBy(_ => Random.value).ToList(); // cycle through every installed pal
                for (int i = 0; i < want; i++)
                {
                    var def = order[i % order.Count];
                    Vector3 pos = nodes[Random.Range(0, nodes.Length)].transform.position;
                    if (i == 0 && Plugin.SpawnNearEntrance.Value) pos = NearMainEntrance(nodes, pos);
                    pos = rm.GetNavMeshPosition(pos, default, 5f);
                    rm.SpawnEnemyGameObject(pos, Random.Range(0f, 360f), -1, def.Type);
                    Plugin.Log.LogInfo($"Guaranteed spawn: {def.Id} at {pos}.");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError("Guaranteed spawn failed: " + e); }
        }

        private static Vector3 NearMainEntrance(GameObject[] nodes, Vector3 fallback)
        {
            EntranceTeleport inside = Object.FindObjectsOfType<EntranceTeleport>().FirstOrDefault(t => !t.isEntranceToBuilding && t.entranceId == 0);
            if (inside == null) return fallback;
            Vector3 door = inside.entrancePoint != null ? inside.entrancePoint.position : inside.transform.position;
            Vector3 pos = fallback;
            float best = float.MaxValue;
            foreach (var n in nodes)
            {
                Vector3 np = n.transform.position;
                float flat = Vector2.Distance(new Vector2(np.x, np.z), new Vector2(door.x, door.z));
                if (flat < 2f) continue;
                float score = flat + Mathf.Abs(np.y - door.y) * 6f; // same floor as the door
                if (score < best) { best = score; pos = np; }
            }
            return pos;
        }

        private static float panicTestAt = -1f;

        /// Test helper: PanicAfterLanding seconds after landing, act as if the apparatus was pulled.
        [HarmonyPatch(typeof(StartOfRound), "Update")]
        [HarmonyPostfix]
        private static void PanicTestTimer()
        {
            if (panicTestAt < 0f || Time.time < panicTestAt) return;
            panicTestAt = -1f;
            if (!IsHost || PaperPalAI.PanicActive || Plugin.Defs.Count == 0) return;
            PaperPalAI.PanicActive = true;
            Plugin.Log.LogInfo("[Test] PanicAfterLanding: PaperPals panic (no apparatus pulled).");
        }

        // ------------------------------------------------------------------ meltdown trigger
        /// Apparatus pulled (FacilityMeltdown starts from the same method). Prefix: isLungDocked is still true here.
        [HarmonyPatch(typeof(LungProp), nameof(LungProp.EquipItem))]
        [HarmonyPrefix]
        private static void ApparatusPulled(LungProp __instance)
        {
            try
            {
                if (!IsHost || !__instance.isLungDocked || PaperPalAI.PanicActive || Plugin.Defs.Count == 0) return;
                PaperPalAI.PanicActive = true;
                Plugin.Log.LogInfo("Apparatus pulled: PaperPals panic.");
            }
            catch (System.Exception e) { Plugin.Log.LogError("Apparatus hook failed: " + e); }
        }

        // ------------------------------------------------------------------ witness rule: sand worm instakill
        /// The worm kills through KillEnemyOnOwnerClient and skips HitEnemy, so it gets its own witness check.
        [HarmonyPatch(typeof(SandWormAI), nameof(SandWormAI.OnCollideWithEnemy))]
        [HarmonyPrefix]
        private static bool WormWitness(EnemyAI enemyScript)
        {
            if (!(enemyScript is PaperPalAI pal) || pal.MonsterDamageAllowed(out string why)) return true;
            if (Plugin.DebugLog.Value && IsHost) Plugin.Log.LogInfo($"[Debug] {pal.Def?.Id}: sand worm ignored ({why}).");
            return false;
        }

        /// Witness rule for instant kills that skip HitEnemy (spike roof traps, death pits, sand worm, mods):
        /// a pal only dies this way outside, after the grace time, with a player close or watching.
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemyOnOwnerClient))]
        [HarmonyPrefix]
        private static bool KillGuard(EnemyAI __instance)
        {
            if (!(__instance is PaperPalAI pal) || pal.SelfKilling || pal.isEnemyDead) return true;
            if (pal.MonsterDamageAllowed(out string why)) return true;
            if (Plugin.DebugLog.Value) Plugin.Log.LogInfo($"[Debug] {pal.Def?.Id}: instant kill ignored ({why}).");
            return false;
        }

        // ------------------------------------------------------------------ test mode: only PaperPals
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemyGameObject))]
        [HarmonyPrefix]
        private static bool ReplaceSpawn(RoundManager __instance, ref int enemyNumber, ref EnemyType enemyType, ref NetworkObjectReference __result)
        {
            if (!Plugin.OnlyPaperPals.Value || !__instance.IsServer || Plugin.Defs.Count == 0) return true;
            EnemyType original = enemyType;
            if (original != null && Plugin.Defs.Any(d => d.Type == original)) return true;
            if (original == null)
            {
                if (enemyNumber == -2 || enemyNumber == -3) original = null;
                else if (enemyNumber >= 0 && enemyNumber < __instance.currentLevel.Enemies.Count) original = __instance.currentLevel.Enemies[enemyNumber].enemyType;
                else original = Plugin.Defs[0].Type;
            }
            if (original != null && Plugin.Defs.Any(d => d.Type == original)) return true; // the game picked a pal: keep it
            if (original == null || original.isOutsideEnemy || original.isDaytimeEnemy)
            {
                Plugin.Log.LogInfo($"[OnlyPaperPals] cancelled outside spawn ({original?.enemyName ?? "outside/daytime"}).");
                __result = default;
                return false;
            }
            var def = Plugin.Defs[Random.Range(0, Plugin.Defs.Count)];
            Plugin.Log.LogInfo($"[OnlyPaperPals] {original.enemyName} -> {def.Id}");
            enemyType = def.Type;
            return true;
        }

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnEnemiesOutside))]
        [HarmonyPrefix]
        private static bool NoOutside() => !Plugin.OnlyPaperPals.Value;

        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnDaytimeEnemiesOutside))]
        [HarmonyPrefix]
        private static bool NoDaytime() => !Plugin.OnlyPaperPals.Value;

        private static float nextSweep;

        [HarmonyPatch(typeof(RoundManager), "Update")]
        [HarmonyPostfix]
        private static void Sweep(RoundManager __instance)
        {
            if (!Plugin.OnlyPaperPals.Value || Time.time < nextSweep || !__instance.IsServer) return;
            nextSweep = Time.time + 2f;
            try
            {
                foreach (var e in Object.FindObjectsOfType<EnemyAI>())
                {
                    if (e == null || e is PaperPalAI) continue;
                    var no = e.NetworkObject;
                    if (no == null || !no.IsSpawned) continue;
                    Plugin.Log.LogInfo($"[OnlyPaperPals] despawned {e.enemyType?.enemyName ?? e.name}.");
                    __instance.SpawnedEnemies.Remove(e);
                    no.Despawn(true);
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[OnlyPaperPals] sweep: " + ex.Message); }
        }
    }
}
