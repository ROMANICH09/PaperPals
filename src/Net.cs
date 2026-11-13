using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PaperPals
{
    /// Plain NGO named messages (no NetcodePatcher). Host -> everyone (the host receives its own copy).
    /// One name per message type for the whole library; the payload carries the pal's NetworkObjectId.
    internal static class Net
    {
        private const string VoiceMsg = "PaperPals.Voice";
        private const string TeleportMsg = "PaperPals.Teleport";
        private const int ChunkSize = 16 * 1024;
        private const int MaxPcm = 2_000_000;
        private static CustomMessagingManager registeredOn;
        private static int nextClipId;

        private sealed class Pending { public int Count, Got, Rate; public float Yaw, Volume, Started; public byte[] Pcm; }
        // Keyed per clip, so two clips of one pal in flight at once (hurt shout + death scream) both arrive.
        private static readonly Dictionary<(ulong, int), Pending> pending = new Dictionary<(ulong, int), Pending>();

        public static void EnsureRegistered()
        {
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm == null || cmm == registeredOn) return;
            cmm.RegisterNamedMessageHandler(VoiceMsg, OnVoice);
            cmm.RegisterNamedMessageHandler(TeleportMsg, OnTeleport);
            registeredOn = cmm;
            pending.Clear();
        }

        /// Host coroutine: a clip in 16 KB chunks, 50 ms apart. yaw NaN = don't turn to face anyone.
        public static IEnumerator SendVoice(ulong npcId, float yaw, float volume, VoiceClip clip)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) yield break;
            EnsureRegistered();
            int clipId = ++nextClipId;
            int total = clip.Pcm.Length;
            int count = (total + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < count; i++)
            {
                nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) yield break;
                int off = i * ChunkSize, len = Math.Min(ChunkSize, total - off);
                var w = new FastBufferWriter(len + 64, Allocator.Temp);
                try
                {
                    w.WriteValueSafe(npcId);
                    w.WriteValueSafe(clipId);
                    w.WriteValueSafe(i);
                    w.WriteValueSafe(count);
                    w.WriteValueSafe(yaw);
                    w.WriteValueSafe(volume);
                    w.WriteValueSafe(clip.Rate);
                    w.WriteValueSafe(total);
                    w.WriteValueSafe(len);
                    w.WriteBytesSafe(clip.Pcm, len, off);
                    nm.CustomMessagingManager.SendNamedMessageToAll(VoiceMsg, w, NetworkDelivery.ReliableFragmentedSequenced);
                }
                finally { w.Dispose(); }
                if (i + 1 < count) yield return new WaitForSecondsRealtime(0.05f);
            }
        }

        private static void OnVoice(ulong sender, FastBufferReader r)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || sender != NetworkManager.ServerClientId) return;
                r.ReadValueSafe(out ulong npcId);
                r.ReadValueSafe(out int clipId);
                r.ReadValueSafe(out int idx);
                r.ReadValueSafe(out int count);
                r.ReadValueSafe(out float yaw);
                r.ReadValueSafe(out float volume);
                r.ReadValueSafe(out int rate);
                r.ReadValueSafe(out int total);
                r.ReadValueSafe(out int len);
                if (total <= 0 || total > MaxPcm || rate < 4000 || rate > 96000 || count <= 0 || idx < 0 || idx >= count
                    || len < 0 || idx * ChunkSize + len > total) return;
                var key = (npcId, clipId);
                if (!pending.TryGetValue(key, out var p))
                {
                    if (pending.Count > 32) DropStale();
                    p = new Pending { Count = count, Rate = rate, Yaw = yaw, Volume = volume, Pcm = new byte[total], Started = Time.realtimeSinceStartup };
                    pending[key] = p;
                }
                var chunk = new byte[len];
                r.ReadBytesSafe(ref chunk, len);
                Buffer.BlockCopy(chunk, 0, p.Pcm, idx * ChunkSize, len);
                if (++p.Got < p.Count) return;
                pending.Remove(key);
                if (nm.SpawnManager.SpawnedObjects.TryGetValue(npcId, out var no) && no != null && no.TryGetComponent(out PaperPalAI ai))
                    ai.PlayVoice(p.Yaw, p.Volume, p.Rate, p.Pcm);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Voice message: " + e.Message); }
        }

        private static void DropStale()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var k in new List<(ulong, int)>(pending.Keys))
                if (now - pending[k].Started > 15f) pending.Remove(k);
        }

        public static void SendTeleport(ulong npcId, Vector3 pos, bool outside)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            EnsureRegistered();
            var w = new FastBufferWriter(64, Allocator.Temp);
            try
            {
                w.WriteValueSafe(npcId);
                w.WriteValueSafe(pos);
                w.WriteValueSafe(outside);
                nm.CustomMessagingManager.SendNamedMessageToAll(TeleportMsg, w, NetworkDelivery.ReliableSequenced);
            }
            finally { w.Dispose(); }
        }

        private static void OnTeleport(ulong sender, FastBufferReader r)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || sender != NetworkManager.ServerClientId) return;
                r.ReadValueSafe(out ulong npcId);
                r.ReadValueSafe(out Vector3 pos);
                r.ReadValueSafe(out bool outside);
                if (nm.SpawnManager.SpawnedObjects.TryGetValue(npcId, out var no) && no != null && no.TryGetComponent(out PaperPalAI ai))
                    ai.ApplyTeleport(pos, outside);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Teleport message: " + e.Message); }
        }
    }
}
