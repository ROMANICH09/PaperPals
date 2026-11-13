using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace PaperPals
{
    /// One PaperPal. The host decides movement, behaviour state and lines; every client draws the sprite from the
    /// synced position/rotation/behaviour state and the audio it plays. Never hurts anyone.
    public class PaperPalAI : EnemyAI
    {
        public int DefIndex;
        internal NpcDefinition Def => DefIndex >= 0 && DefIndex < Plugin.Defs.Count ? Plugin.Defs[DefIndex] : null;

        // Behaviour states (synced by EnemyAI.SwitchToBehaviourState). Priority: dead > panic > fear > talk > wander.
        internal const int Normal = 0, Panic = 1, FearSneak = 2, FearHold = 3, FearRun = 4, IdleCrouch = 5, InShip = 6;
        internal static EnemyBehaviourState[] MakeStates() => new[] { "Normal", "Panic", "FearSneak", "FearHold", "FearRun", "IdleCrouch", "InShip" }
            .Select(n => new EnemyBehaviourState { name = n }).ToArray();

        // ---- visuals (all clients)
        private Transform board;
        private MeshRenderer rend;
        private AudioSource voice;
        private AudioSource[] allSources;
        private Vector3 lastPos;
        private Vector3 facing = Vector3.forward;
        private float speed, stepTimer, mouthCloseAt, talkFaceUntil, talkYaw, sourcesRefreshAt, hitCrouchUntil;
        private int step;
        private bool walking, mouthOpen;
        private Material shown;
        private readonly float[] rmsBuf = new float[256];

        // ---- AI (host)
        private float nextTalkAt, busyUntil, pauseUntil, wanderDeadline, lastMovedAt, nextAloneAt, cooldownUntil, hitPauseUntil;
        private bool wandering;
        internal static bool PanicActive; // set by the apparatus patch (host), cleared on landing
        private bool panicking, arrivedShip, shipTargetSet;
        private float panicMoveAt, nextShoutAt, doorStartedAt, nextHurtShoutAt, shipStartedAt, shipLastMovedAt;
        private Vector3 shipTarget, insideDoor, outsideDoor;
        private int shipNudges;
        // fear
        private bool inFear, hasSpot, fearRunning;
        private float lastThreatAt, spotPickedAt;
        private Vector3 threatPos, spot;
        // witness rule
        private float graceUntil;
        private bool everOutside;
        internal bool SelfKilling; // true while he dies from counted hits (lets the kill guard through)

        public override void Awake()
        {
            base.Awake();
            currentSearch ??= new AISearchRoutine();
        }

        public override void Start()
        {
            if (enemyBehaviourStates == null || enemyBehaviourStates.Length < 7) enemyBehaviourStates = MakeStates();
            base.Start();
            var def = Def;
            board = transform.Find("Billboard");
            rend = board != null ? board.GetComponent<MeshRenderer>() : null;
            voice = transform.Find("Voice")?.GetComponent<AudioSource>();
            if (voice != null) voice.maxDistance = Plugin.HearingDistance.Value;
            allSources = GetComponentsInChildren<AudioSource>(true);
            lastPos = transform.position;
            facing = Flat(transform.forward, Vector3.forward);
            Net.EnsureRegistered();
            if (def != null)
            {
                if (agent != null) agent.speed = def.WalkSpeed;
                enemyHP = def.Hp;
            }
            graceUntil = Time.time + Mathf.Max(0f, Plugin.GraceSeconds.Value);
            if (IsServer)
            {
                nextTalkAt = Time.time + Random.Range(6f, 12f);
                pauseUntil = Time.time + Random.Range(0.5f, 2f);
            }
            if (Plugin.DebugLog.Value)
                Plugin.Log.LogInfo($"[Debug] {def?.Id} start: pos={transform.position} server={IsServer} onNavMesh={agent != null && agent.isOnNavMesh} outside={isOutside}");
        }

        private float lastStateAt = -10f;

        /// Synced state change, at most every 0.6 s (avoids RPC spam when a monster hovers at a threshold).
        private void SetState(int s, bool force = false)
        {
            if (currentBehaviourStateIndex == s) return;
            if (!force && Time.time - lastStateAt < 0.6f) return;
            lastStateAt = Time.time;
            SwitchToBehaviourState(s);
        }

        // ================================================================= AI (owner = host)
        private float nextDebugAt;

        public override void DoAIInterval()
        {
            base.DoAIInterval();
            var def = Def;
            if (!IsServer || isEnemyDead || agent == null || def == null) return;
            float now = Time.time;
            DebugTick(def, now);

            if (PanicActive && !panicking && Plugin.PanicEnabled.Value && def.J.features.meltdownPanic) BeginPanic(now);
            if (panicking) { if (!arrivedShip) SetState(Panic, true); PanicTick(now); return; }
            if (!agent.enabled || !agent.isOnNavMesh) return;

            if (now < hitPauseUntil) return; // crouched a moment after a hit
            if (FearTick(def, now)) return;

            if (now < busyUntil) return;
            if (now >= nextTalkAt)
            {
                PlayerControllerB p = FindListener();
                if (p != null && TrySpeakTo(p)) return;
                nextTalkAt = now + 2f;
            }
            if (Plugin.AloneMin.Value > 0f)
            {
                if (nextAloneAt <= 0f) ScheduleAlone(now);
                else if (now >= nextAloneAt && now >= cooldownUntil) { ScheduleAlone(now); if (TrySpeakTo(null)) return; }
            }

            if (now < pauseUntil) return;
            if (wandering)
            {
                bool stuck = !agent.pathPending && agent.pathStatus == UnityEngine.AI.NavMeshPathStatus.PathInvalid;
                if (agent.velocity.sqrMagnitude > 0.04f) lastMovedAt = now;
                else if (now - lastMovedAt > 3f) stuck = true;
                bool arrived = !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.4f;
                if (!arrived && !stuck && now < wanderDeadline) return;
                wandering = false;
                StandStill();
                pauseUntil = now + Random.Range(2f, 7f);
                SetState(def.CrouchChance > 0f && Random.value < def.CrouchChance ? IdleCrouch : Normal);
                return;
            }
            SetState(Normal);
            PickWanderTarget();
        }

        private void DebugTick(NpcDefinition def, float now)
        {
            if (!Plugin.DebugLog.Value || now < nextDebugAt) return;
            nextDebugAt = now + 10f;
            Plugin.Log.LogInfo($"[Debug] {def.Id} pos={transform.position} state={currentBehaviourStateIndex} outside={isOutside} hp={enemyHP} " +
                               $"panic={panicking} fear={inFear} onNavMesh={agent.isOnNavMesh} vel={agent.velocity.magnitude:0.0} " +
                               $"sprite: enabled={rend != null && rend.enabled} active={board != null && board.gameObject.activeInHierarchy} " +
                               $"layer={(board != null ? board.gameObject.layer : -1)} seen={rend != null && rend.isVisible} " +
                               $"h={(board != null ? board.localScale.y : 0f):0.00} mat={(shown != null ? shown.name : "none")} " +
                               $"you={(GameNetworkManager.Instance?.localPlayerController != null ? Vector3.Distance(GameNetworkManager.Instance.localPlayerController.transform.position, transform.position) : -1f):0} m");
        }

        /// SetDestinationToPosition, but also re-issues the order to the agent when it has no route. The base
        /// DoAIInterval only calls agent.SetDestination when the destination CHANGES, so after a pause (ResetPath),
        /// a door teleport or a Warp with the same destination the agent would otherwise never move again.
        private bool MoveTo(Vector3 target, bool checkForPath = false)
        {
            if (!SetDestinationToPosition(target, checkForPath)) return false;
            moveTowardsDestination = true;
            if (agent.enabled && agent.isOnNavMesh && !agent.pathPending
                && (!agent.hasPath || (agent.destination - destination).sqrMagnitude > 1f))
                agent.SetDestination(destination);
            return true;
        }

        private void StandStill()
        {
            moveTowardsDestination = false;
            movingTowardsTargetPlayer = false;
            if (agent.enabled && agent.isOnNavMesh) agent.ResetPath();
        }

        private void PickWanderTarget()
        {
            agent.speed = Def.WalkSpeed;
            if (allAINodes == null || allAINodes.Length == 0) { pauseUntil = Time.time + 5f; return; }
            for (int i = 0; i < 8; i++)
            {
                GameObject node = allAINodes[Random.Range(0, allAINodes.Length)];
                if (node == null) continue;
                float d = Vector3.Distance(node.transform.position, transform.position);
                if (d < 6f || d > 40f) continue;
                if (MoveTo(node.transform.position, true))
                {
                    moveTowardsDestination = true;
                    wandering = true;
                    lastMovedAt = Time.time + 1f;
                    wanderDeadline = Time.time + d / Mathf.Max(0.5f, agent.speed) * 2f + 5f;
                    return;
                }
            }
            pauseUntil = Time.time + 3f;
        }

        private PlayerControllerB FindListener()
        {
            PlayerControllerB best = null;
            float bestD = Plugin.TalkRange.Value;
            int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
            foreach (var p in StartOfRound.Instance.allPlayerScripts)
            {
                if (p == null || !p.isPlayerControlled || p.isPlayerDead || p.isInsideFactory == isOutside) continue;
                Vector3 head = p.gameplayCamera != null ? p.gameplayCamera.transform.position : p.transform.position + Vector3.up * 1.5f;
                float d = Vector3.Distance(eye.position, head);
                if (d > bestD) continue;
                if (Physics.Linecast(eye.position, head, mask, QueryTriggerInteraction.Ignore)) continue;
                best = p; bestD = d;
            }
            return best;
        }

        private void ScheduleAlone(float now) =>
            nextAloneAt = now + Random.Range(Plugin.AloneMin.Value, Mathf.Max(Plugin.AloneMin.Value, Plugin.AloneMax.Value));

        private bool TrySpeakTo(PlayerControllerB p)
        {
            VoiceClip clip = Def.Voice.PickNormal();
            if (clip == null) { nextTalkAt = Time.time + 60f; return false; }
            Vector3 dir = p != null ? Flat(p.transform.position - transform.position, facing) : facing;
            float yaw = Quaternion.LookRotation(dir).eulerAngles.y;
            StandStill();
            wandering = false;
            busyUntil = Time.time + clip.Seconds + clip.Pcm.Length / (16f * 1024f) * 0.05f + 1.5f;
            pauseUntil = busyUntil + Random.Range(1f, 3f);
            nextTalkAt = busyUntil + Random.Range(Plugin.CooldownMin.Value, Mathf.Max(Plugin.CooldownMin.Value, Plugin.CooldownMax.Value));
            cooldownUntil = nextTalkAt;
            if (p != null) ScheduleAlone(Time.time);
            Plugin.Log.LogInfo($"{Def.Id}: saying {System.IO.Path.GetFileName(clip.File)} ({clip.Seconds:0.0} s) to {(p != null ? p.playerUsername : "nobody")}.");
            StartCoroutine(Net.SendVoice(NetworkObjectId, yaw, 1f, clip));
            return true;
        }

        // ----------------------------------------------------------------- fear (host)
        /// True while fear is in control this interval.
        private bool FearTick(NpcDefinition def, float now)
        {
            if (!def.FearEnabled || def.FearRadius <= 0f) { if (inFear) EndFear(); return false; }
            EnemyAI threat = FindThreat(def, out float dist);
            if (threat != null) { lastThreatAt = now; threatPos = threat.transform.position; if (!inFear) StartFear(threat); }
            if (!inFear) return false;

            if (threat == null)
            {
                if (now - lastThreatAt > def.CalmDelay) { EndFear(); return false; }
                if (!hasSpot || Arrived(spot)) HoldFacing(); // hidden: hold the crouch, face back
                FearShout(def, now, 0.6f);
                return true;
            }

            fearRunning = dist < def.FearRunDistance || (fearRunning && dist < def.FearRunDistance + 1f); // 1 m margin to stop running
            if (fearRunning)
            {
                SetState(FearRun);
                agent.speed = Mathf.Max(1f, Plugin.PanicSpeed.Value);
                if ((!hasSpot && now - spotPickedAt > 1f) || (hasSpot && Arrived(spot)) || now - spotPickedAt > 3f) PickSpot(threat, 6f);
                FearShout(def, now, 1f);
                return true;
            }

            if (hasSpot && Arrived(spot) && !CanSee(threat, eye.position)) HoldFacing();
            else
            {
                SetState(FearSneak);
                agent.speed = Mathf.Max(0.3f, Plugin.SneakSpeed.Value);
                if ((!hasSpot && now - spotPickedAt > 1f) || (hasSpot && Arrived(spot)) || now - spotPickedAt > 6f) PickSpot(threat, 2f);
            }
            FearShout(def, now, 0.6f);
            return true;
        }

        private void StartFear(EnemyAI threat)
        {
            inFear = true;
            hasSpot = false;
            fearRunning = false;
            wandering = false;
            StandStill(); // drop the old wander path
            busyUntil = 0f;
            nextShoutAt = Time.time + Random.Range(0.2f, 0.8f);
            Plugin.Log.LogInfo($"{Def.Id}: scared of {threat.enemyType?.enemyName} at {Vector3.Distance(transform.position, threat.transform.position):0.0} m.");
        }

        private void EndFear()
        {
            inFear = false;
            hasSpot = false;
            fearRunning = false;
            agent.speed = Def.WalkSpeed;
            StandStill();
            SetState(Normal, true);
            pauseUntil = Time.time + Random.Range(1f, 3f);
            Plugin.Log.LogInfo($"{Def.Id}: calmed down.");
        }

        private void HoldFacing()
        {
            SetState(FearHold);
            StandStill();
            Vector3 back = Flat(threatPos - transform.position, -transform.forward); // the way he came, towards the monster
            transform.rotation = Quaternion.LookRotation(back, Vector3.up);         // rotation is synced by EnemyAI
        }

        private bool Arrived(Vector3 p) => Vector3.Distance(Flat3(transform.position), Flat3(p)) < 1.2f;
        private static Vector3 Flat3(Vector3 v) { v.y = 0f; return v; }

        /// Nearest living, visible monster within the fear radius (same side of the facility door), else null.
        private EnemyAI FindThreat(NpcDefinition def, out float dist)
        {
            dist = float.MaxValue;
            EnemyAI best = null;
            var rm = RoundManager.Instance;
            if (rm == null) return null;
            var ignore = (Plugin.FearIgnore.Value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
            foreach (var e in rm.SpawnedEnemies)
            {
                if (e == null || e == this || e.isEnemyDead || e is PaperPalAI || e.isOutside != isOutside) continue;
                string n = e.enemyType != null ? e.enemyType.enemyName : "";
                if (ignore.Any(x => string.Equals(x, n, System.StringComparison.OrdinalIgnoreCase))) continue;
                float d = Vector3.Distance(transform.position, e.transform.position);
                if (d > def.FearRadius || d >= dist) continue;
                Vector3 target = e.eye != null ? e.eye.position : e.transform.position + Vector3.up;
                if (Physics.Linecast(eye.position, target, mask, QueryTriggerInteraction.Ignore)) continue;
                best = e; dist = d;
            }
            return best;
        }

        private bool CanSee(EnemyAI threat, Vector3 point)
        {
            Vector3 from = threat.eye != null ? threat.eye.position : threat.transform.position + Vector3.up;
            return !Physics.Linecast(from, point, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore);
        }

        /// Hide spot: the nearest AI node that is further from the monster and out of its sight (else the furthest one).
        private void PickSpot(EnemyAI threat, float minGain)
        {
            spotPickedAt = Time.time;
            if (allAINodes == null || allAINodes.Length == 0) { hasSpot = false; return; }
            float curD = Vector3.Distance(transform.position, threat.transform.position);
            var cands = allAINodes.Where(n => n != null)
                .Select(n => n.transform.position)
                .Where(p => Vector3.Distance(p, transform.position) < 30f && Vector3.Distance(p, threat.transform.position) > curD + minGain)
                .OrderBy(p => Vector3.Distance(p, transform.position)).Take(14).ToList();
            foreach (var hidden in new[] { true, false })
                foreach (var p in hidden ? cands.Where(c => !CanSee(threat, c + Vector3.up)) : cands.OrderByDescending(c => Vector3.Distance(c, threat.transform.position)))
                    if (MoveTo(p, true)) { spot = p; hasSpot = true; return; }
            hasSpot = false; // cornered: stop and crouch where he is
            StandStill();
        }

        private void FearShout(NpcDefinition def, float now, float volume)
        {
            if (now < nextShoutAt) return;
            VoiceClip clip = def.Voice.PickFear();
            if (clip == null) { nextShoutAt = now + 6f; return; }
            nextShoutAt = now + clip.Seconds + Random.Range(Plugin.ShoutPauseMin.Value, Mathf.Max(Plugin.ShoutPauseMin.Value, Plugin.ShoutPauseMax.Value));
            StartCoroutine(Net.SendVoice(NetworkObjectId, float.NaN, volume, clip));
        }

        // ----------------------------------------------------------------- meltdown panic (host)
        private void BeginPanic(float now)
        {
            panicking = true; inFear = false; wandering = false; arrivedShip = false; shipTargetSet = false;
            busyUntil = 0f;
            panicMoveAt = now + Random.Range(0.3f, 1.5f);
            nextShoutAt = panicMoveAt;
            doorStartedAt = now;
            insideDoor = RoundManager.FindMainEntrancePosition(true, false);
            outsideDoor = RoundManager.FindMainEntrancePosition(true, true);
            Plugin.Log.LogInfo($"{Def.Id}: panics (meltdown), outside={isOutside}.");
        }

        private void PanicTick(float now)
        {
            if (now < panicMoveAt) return;
            agent.speed = Mathf.Max(1f, Plugin.PanicSpeed.Value);
            agent.acceleration = 14f;
            if (now >= nextShoutAt) Shout(arrivedShip ? 3f : 1f);
            if (arrivedShip) return;
            bool onMesh = agent.enabled && agent.isOnNavMesh;

            if (!isOutside)
            {
                if (insideDoor == Vector3.zero || outsideDoor == Vector3.zero) { arrivedShip = true; StandStill(); return; }
                bool there = Vector3.Distance(transform.position, insideDoor) < 1.6f;
                bool stuck = now - doorStartedAt > Mathf.Max(10f, Plugin.DoorTimeout.Value);
                if (there || stuck)
                {
                    if (stuck && !there) Plugin.Log.LogInfo($"{Def.Id}: could not reach the main entrance in time; using it anyway.");
                    Net.SendTeleport(NetworkObjectId, outsideDoor, true);
                    doorStartedAt = now;
                    return;
                }
                if (!onMesh) return;
                MoveTo(insideDoor);
                moveTowardsDestination = true;
                return;
            }

            var sor = StartOfRound.Instance;
            if (sor == null || sor.shipIsLeaving || sor.inShipPhase) { StandStill(); return; }
            if (!shipTargetSet)
            {
                shipTargetSet = true;
                shipNudges = 0;
                shipStartedAt = shipLastMovedAt = now;
                shipTarget = sor.insideShipPositions != null && sor.insideShipPositions.Length > 0
                    ? sor.insideShipPositions[Random.Range(0, sor.insideShipPositions.Length)].position
                    : sor.middleOfShipNode != null ? sor.middleOfShipNode.position : sor.shipLandingPosition.position;
                if (onMesh && !MoveTo(shipTarget, true) && sor.middleOfShipNode != null)
                    shipTarget = sor.middleOfShipNode.position;
            }
            if (onMesh && agent.velocity.sqrMagnitude > 0.25f) shipLastMovedAt = now;
            if (now - shipLastMovedAt > 4f && shipNudges < 3 && now - shipStartedAt <= 45f)
            {
                // Stuck (e.g. right at the outside door, where entrance mods block monsters): hop a few metres
                // toward the ship onto the NavMesh and try again.
                shipNudges++;
                Vector3 dir = Flat(shipTarget - transform.position, transform.forward);
                Vector3 probe = transform.position + dir * (4f + 2f * shipNudges);
                Plugin.Log.LogInfo($"{Def.Id}: stuck on the way to the ship (path {agent.pathStatus}); nudging toward it (try {shipNudges}).");
                if (UnityEngine.AI.NavMesh.SamplePosition(probe, out var hit, 5f, agent.areaMask))
                {
                    agent.Warp(hit.position);
                    serverPosition = hit.position;
                }
                shipLastMovedAt = now;
                MoveTo(shipTarget);
                moveTowardsDestination = true;
                return;
            }
            if (now - shipStartedAt > 45f || now - shipLastMovedAt > 4f)
            {
                arrivedShip = true; StandStill();
                Plugin.Log.LogInfo($"{Def.Id}: could not reach the ship; staying at {transform.position}.");
                return;
            }
            if (!onMesh) return;
            if (Vector3.Distance(transform.position, shipTarget) < 1.6f
                || (sor.shipInnerRoomBounds != null && sor.shipInnerRoomBounds.bounds.Contains(transform.position)
                    && Vector3.Distance(transform.position, shipTarget) < 4f))
            {
                arrivedShip = true; isInsidePlayerShip = true; StandStill();
                SetState(InShip, true); // every client pins him to the ship from now on (rides the takeoff)
                Plugin.Log.LogInfo($"{Def.Id}: reached the ship.");
                return;
            }
            MoveTo(shipTarget);
            moveTowardsDestination = true;
        }

        private void Shout(float intervalScale)
        {
            float now = Time.time;
            VoiceClip clip = Def.Voice.PickPanic();
            if (clip == null) { nextShoutAt = now + 5f; return; }
            nextShoutAt = now + clip.Seconds + Random.Range(Plugin.ShoutPauseMin.Value,
                Mathf.Max(Plugin.ShoutPauseMin.Value, Plugin.ShoutPauseMax.Value)) * intervalScale;
            StartCoroutine(Net.SendVoice(NetworkObjectId, float.NaN, 1f, clip));
            if (isOutside && RoundManager.Instance != null)
                RoundManager.Instance.PlayAudibleNoise(transform.position, 25f, 0.9f, 0,
                    isInsidePlayerShip && StartOfRound.Instance.hangarDoorsClosed, 7531);
        }

        /// All machines: jump through a door (sent by the host).
        internal void ApplyTeleport(Vector3 pos, bool outside)
        {
            if (isEnemyDead) return;
            Vector3 p = RoundManager.Instance != null ? RoundManager.Instance.GetNavMeshPosition(pos) : pos;
            if (IsOwner && agent != null)
            {
                agent.enabled = false; transform.position = p; agent.enabled = true;
                if (!agent.isOnNavMesh) agent.Warp(p);
                agent.ResetPath();
            }
            else transform.position = p;
            serverPosition = p;
            lastPos = p;
            SetEnemyOutside(outside);
            if (outside && !everOutside) { everOutside = true; graceUntil = Time.time + Mathf.Max(0f, Plugin.GraceSeconds.Value); }
            var door = RoundManager.FindMainEntranceScript(outside);
            if (door != null && door.entrancePointAudio != null && door.doorAudios != null && door.doorAudios.Length > 0)
                door.entrancePointAudio.PlayOneShot(door.doorAudios[0]);
            if (IsServer) { shipTargetSet = false; doorStartedAt = Time.time; }
        }

        // ----------------------------------------------------------------- damage, witness rule, death
        /// Witness rule: monsters may hurt a pal only outside, after the grace time, and only while a living player is
        /// close (WitnessCloseRange) or can see him (WitnessRange). Computed on each machine from synced positions.
        internal bool MonsterDamageAllowed(out string why)
        {
            if (!isOutside) { why = "inside"; return false; }
            if (Time.time < graceUntil) { why = "grace"; return false; }
            Vector3 me = eye != null ? eye.position : transform.position + Vector3.up;
            int mask = StartOfRound.Instance.collidersAndRoomMaskAndDefault;
            foreach (var p in StartOfRound.Instance.allPlayerScripts)
            {
                if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
                Vector3 head = p.gameplayCamera != null ? p.gameplayCamera.transform.position : p.transform.position + Vector3.up * 1.5f;
                float d = Vector3.Distance(head, me);
                if (d <= Plugin.WitnessCloseRange.Value) { why = "close witness"; return true; }
                if (d <= Plugin.WitnessRange.Value && !Physics.Linecast(head, me, mask, QueryTriggerInteraction.Ignore)) { why = "witness in sight"; return true; }
            }
            why = "no witness";
            return false;
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if (isEnemyDead) return;
            if (playerWhoHit == null && !MonsterDamageAllowed(out string why))
            {
                if (Plugin.DebugLog.Value && IsServer) Plugin.Log.LogInfo($"[Debug] {Def?.Id}: monster hit ignored ({why}).");
                return;
            }
            enemyHP -= force;
            hitCrouchUntil = Time.time + 1.5f;
            if (IsServer)
            {
                if (!panicking && currentBehaviourStateIndex != FearRun) { hitPauseUntil = Time.time + 1.5f; StandStill(); }
                if (enemyHP > 0 && Time.time >= nextHurtShoutAt) // every counted hit (0.3 s guard against double hits)
                {
                    nextHurtShoutAt = Time.time + 0.3f;
                    VoiceClip clip = Def?.Voice.PickHurt();
                    if (clip != null) StartCoroutine(Net.SendVoice(NetworkObjectId, float.NaN, 1f, clip));
                }
            }
            if (enemyHP <= 0 && IsOwner)
            {
                SelfKilling = true;
                try { KillEnemyOnOwnerClient(); } finally { SelfKilling = false; }
            }
        }

        public override void KillEnemy(bool destroy = false)
        {
            base.KillEnemy(destroy);
            if (!isEnemyDead) return;
            if (voice != null) voice.Stop();
            panicking = false; inFear = false;
            Plugin.Log.LogInfo($"{Def?.Id}: died at {transform.position}.");
            if (IsServer && Def != null && Def.J.features.deathScream)
            {
                VoiceClip clip = Def.Voice.Pick("grito");
                if (clip != null) StartCoroutine(Net.SendVoice(NetworkObjectId, float.NaN, 1f, clip));
            }
        }

        public override void OnCollideWithPlayer(Collider other) { }

        // ================================================================= voice (all clients)
        internal void PlayVoice(float yaw, float volume, int rate, byte[] pcm)
        {
            if (voice == null) return;
            int n = pcm.Length / 2;
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
            var clip = AudioClip.Create("PaperPalLine", n, 1, rate, false);
            clip.SetData(data, 0);
            if (voice.clip != null) { voice.Stop(); Destroy(voice.clip); }
            voice.clip = clip;
            voice.volume = Mathf.Clamp01(Plugin.Volume.Value * Mathf.Clamp01(volume <= 0f ? 1f : volume));
            voice.Play();
            if (float.IsNaN(yaw)) return;
            talkYaw = yaw;
            talkFaceUntil = Time.time + clip.length + 0.5f;
        }

        // ================================================================= sprite (all clients)
        private void LateUpdate()
        {
            var def = Def;
            if (board == null || rend == null || def == null) return;
            float h = SpriteHeight(def);
            if (!Mathf.Approximately(board.localScale.y, h)) board.localScale = new Vector3(h, h, h);
            EnsureVisible();
            RideShip();
            if (isEnemyDead) { LieDown(def, h); return; }
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            int st = currentBehaviourStateIndex;

            Vector3 pos = transform.position;
            Vector3 delta = pos - lastPos; delta.y = 0f;
            lastPos = pos;
            float inst = delta.magnitude / dt;
            if (inst > 20f) inst = 0f;
            speed = Mathf.Lerp(speed, inst, 1f - Mathf.Exp(-10f * dt));

            Vector3 want = facing;
            if (st == FearHold && speed < 0.4f) want = Flat(transform.forward, facing); // host-set "face back"
            else if (Time.time < talkFaceUntil) want = Quaternion.Euler(0f, talkYaw, 0f) * Vector3.forward;
            else if (inst > 0.3f) want = delta.normalized;
            facing = Vector3.RotateTowards(facing, want, 8f * dt, 0f);

            bool nowWalking = speed > 0.3f;
            if (nowWalking)
            {
                if (!walking) { step = 0; stepTimer = 0f; }
                stepTimer += dt * Mathf.Clamp(speed / Mathf.Max(0.5f, def.WalkSpeed), 0.4f, 2.5f);
                float stepTime = Mathf.Clamp(Plugin.StepTime.Value, 0.05f, 2f);
                if (stepTimer >= stepTime) { stepTimer -= stepTime; step ^= 1; }
            }
            walking = nowWalking;

            var sp = def.Sprites;
            Pose pose;
            if (walking)
            {
                if (st == FearSneak) pose = sp.HasSneak ? (step == 0 ? Pose.CrouchA : Pose.CrouchB) : sp.HasCrouch ? Pose.Crouch : (step == 0 ? Pose.StepA : Pose.StepB);
                else if ((st == Panic || st == FearRun) && sp.HasRun) pose = step == 0 ? Pose.RunA : Pose.RunB;
                else pose = step == 0 ? Pose.StepA : Pose.StepB;
            }
            else
            {
                bool crouch = Time.time < hitCrouchUntil || st == FearHold || st == FearSneak || st == IdleCrouch;
                pose = crouch && sp.HasCrouch ? Pose.Crouch : Pose.Idle;
            }

            if (Time.time > sourcesRefreshAt) { allSources = GetComponentsInChildren<AudioSource>(true); sourcesRefreshAt = Time.time + 2f; }
            float rms = 0f;
            foreach (var s in allSources)
            {
                if (s == null || !s.isPlaying || s.mute) continue;
                s.GetOutputData(rmsBuf, 0);
                float sum = 0f;
                for (int i = 0; i < rmsBuf.Length; i++) sum += rmsBuf[i] * rmsBuf[i];
                rms = Mathf.Max(rms, Mathf.Sqrt(sum / rmsBuf.Length) / Mathf.Max(0.05f, s.volume));
            }
            if (rms > Plugin.MouthThreshold.Value) { mouthOpen = true; mouthCloseAt = Time.time + 0.08f; }
            else if (Time.time > mouthCloseAt) mouthOpen = false;

            Camera cam = StartOfRound.Instance != null && StartOfRound.Instance.activeCamera != null ? StartOfRound.Instance.activeCamera : Camera.main;
            if (cam == null) return;
            Vector3 toCam = Flat(cam.transform.position - pos, -facing);
            float a = Vector3.SignedAngle(facing, toCam, Vector3.up);
            int view = Mathf.Abs(a) <= 45f ? 0 : Mathf.Abs(a) >= 135f ? 1 : a > 0f ? 3 : 2; // front, back, right, left
            board.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
            Show(sp.Get(view, pose, mouthOpen ? 1 : 0));
        }

        private bool riding;
        private Vector3 rideOffset;

        /// In the ship: keep his position fixed relative to the ship, so he flies with it instead of falling off.
        /// Runs on every machine (state InShip is synced); the agent stops driving the transform while riding.
        private void RideShip()
        {
            var ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (ship == null || (currentBehaviourStateIndex != InShip && !riding)) return;
            if (!riding)
            {
                riding = true;
                rideOffset = ship.InverseTransformPoint(transform.position);
                if (agent != null) agent.updatePosition = false;
            }
            Vector3 p = ship.TransformPoint(rideOffset);
            transform.position = p;
            serverPosition = p;
            lastPos = p;
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.nextPosition = p;
        }

        private int hiddenReports;

        /// The game (or a mod) can hide enemy meshes by moving them to layer 23 or disabling the renderer, e.g. when an
        /// enemy changes side of the facility door. A pal is a flat picture with no other body, so he must always be
        /// drawn: put it back every frame and report what had hidden it (first few times).
        private void EnsureVisible()
        {
            bool fixedSomething = false;
            int oldLayer = board.gameObject.layer;
            if (oldLayer != 19) { board.gameObject.layer = 19; fixedSomething = true; }
            if (!rend.enabled) { rend.enabled = true; fixedSomething = true; }
            if (!board.gameObject.activeSelf) { board.gameObject.SetActive(true); fixedSomething = true; }
            if (fixedSomething && hiddenReports < 5)
            {
                hiddenReports++;
                Plugin.Log.LogInfo($"{Def?.Id}: sprite had been hidden (layer {oldLayer}, outside={isOutside}, " +
                                   $"you inside={GameNetworkManager.Instance?.localPlayerController?.isInsideFactory}); shown again.");
            }
        }

        private void Show(Material m)
        {
            if (m != null && m != shown) { rend.sharedMaterial = m; shown = m; }
        }

        /// Dead: face-down on the back sprite, centred where he fell, lifted a little above the floor.
        private void LieDown(NpcDefinition def, float h)
        {
            Vector3 f = Flat(facing, Vector3.forward);
            board.rotation = Quaternion.LookRotation(f, Vector3.up) * Quaternion.Euler(90f, 0f, 0f);
            board.position = transform.position + Vector3.up * Mathf.Clamp(Plugin.LieOffset.Value, 0f, 1f) - f * (h * 0.5f);
            Show(def.Sprites.Get(1, Pose.Idle, 0));
        }

        // ----------------------------------------------------------------- size
        private static float playerEyeHeight, loggedEye;
        private const float DefaultPlayerEye = 2.35f; // measured in game (2026-11-11)

        /// sprite height = (player eye height / eyeLevel) x heightCm/180. 180 cm = eyes level with a standing player.
        private static float SpriteHeight(NpcDefinition def)
        {
            float eyeH = Plugin.MatchPlayerEyes.Value ? MeasurePlayerEye() : DefaultPlayerEye;
            return Mathf.Clamp(eyeH / def.EyeLevel * def.HeightScale, 0.02f, 8f);
        }

        private static float MeasurePlayerEye()
        {
            var lp = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (lp != null && lp.gameplayCamera != null && !lp.isPlayerDead && !lp.isCrouching && !lp.isClimbingLadder
                && !lp.inSpecialInteractAnimation && lp.thisController != null && lp.thisController.isGrounded
                && lp.thisController.velocity.sqrMagnitude < 0.01f && !lp.isInHangarShipRoom)
            {
                float e = lp.gameplayCamera.transform.position.y - lp.transform.position.y;
                if (e > 0.8f && e < 5f)
                {
                    playerEyeHeight = playerEyeHeight <= 0f ? e : Mathf.Lerp(playerEyeHeight, e, 0.02f);
                    if (Mathf.Abs(playerEyeHeight - loggedEye) > 0.02f)
                    {
                        loggedEye = playerEyeHeight;
                        Plugin.Log.LogInfo($"Player eye height {playerEyeHeight:0.00}.");
                    }
                }
            }
            return playerEyeHeight > 0f ? playerEyeHeight : DefaultPlayerEye;
        }

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-6f) { fallback.y = 0f; return fallback.sqrMagnitude < 1e-6f ? Vector3.forward : fallback.normalized; }
            return v.normalized;
        }

        public override void OnDestroy()
        {
            if (voice != null && voice.clip != null) Destroy(voice.clip);
            base.OnDestroy();
        }
    }
}
