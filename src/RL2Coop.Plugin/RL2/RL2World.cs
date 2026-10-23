// Rogue Legacy 2 side of the co-op layer. Compiled only when the game's
// assemblies are present (RL2_GAME, see RL2Coop.Plugin.csproj).
//
// Every game member used here is listed in docs/RL2-API.md with where it was
// confirmed (the decompiled Assembly-CSharp of the Steam build).
#if RL2_GAME
using System;
using System.Collections.Generic;
using RL2Coop.Net;
using RL2Coop.Net.Game;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;
using Cinemachine;
using RL_Windows;
using SceneManagement_RL;
using UnityEngine;

namespace RL2Coop.Plugin.RL2
{
    internal sealed class RL2World : IGameWorld
    {
        // Summoned enemies get ids above the spawn-controller range so both
        // sides number them the same way: in the order they were summoned.
        private const ushort FirstSummonedId = 1000;

        private readonly ILog _log;
        private CoopController _controller;

        private BaseRoom _room;
        private int _roomId;
        private readonly List<EnemyController> _enemies = new List<EnemyController>();
        private readonly Dictionary<EnemyController, ushort> _enemyIds = new Dictionary<EnemyController, ushort>();
        private ushort _nextSummonedId = FirstSummonedId;

        private readonly Dictionary<byte, Puppet> _puppets = new Dictionary<byte, Puppet>();
        private readonly Dictionary<byte, string> _puppetNames = new Dictionary<byte, string>();
        private Vector2 _lastPos;
        private float _lastPosTime = -1;
        private byte? _spectating;

        // Teleports wait for the next frame: they are asked for from inside the
        // game's own room-enter event, and re-entering a room from there would
        // run the room setup twice in one call stack.
        private bool _teleportPending;
        private int _teleportRoom;
        private Vector2 _teleportPos;
        private readonly Dictionary<int, Vector2> _entryPos = new Dictionary<int, Vector2>();

        /// <summary>Set while the adapter itself changes an enemy's health, so the guest hit filter lets it through.</summary>
        internal static bool ApplyingHostState;

        /// <summary>Seed the next castle should be built from, set on a guest by the host's run start.</summary>
        internal static int? PendingSeed;

        internal static RL2World Current { get; private set; }
        internal CoopController Controller => _controller;

        public RL2World(ILog log)
        {
            _log = log;
            Current = this;
        }

        internal void Attach(CoopController controller)
        {
            _controller = controller;
            Messenger<GameMessenger, GameEvent>.AddListener(GameEvent.PlayerEnterRoom, OnPlayerEnterRoom);
            Messenger<GameMessenger, GameEvent>.AddListener(GameEvent.EnemySummoned, OnEnemySummoned);
            Messenger<GameMessenger, GameEvent>.AddListener(GameEvent.EnemyDeath, OnEnemyDeath);
            Messenger<GameMessenger, GameEvent>.AddListener(GameEvent.WorldCreationComplete, OnWorldCreated);
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (_, to) => _log.Info($"Scene: {to.name}");
        }

        // ------------------------------------------------------------ rooms

        /// <summary>
        /// A room id both machines agree on. The castle is laid out from the
        /// shared seed, so the same room sits at the same world position with
        /// the same biome everywhere; the id is built from those.
        /// </summary>
        internal static int RoomKey(BaseRoom room)
        {
            var p = room.transform.position;
            unchecked
            {
                var h = (int)room.BiomeType;
                h = h * 397 ^ Mathf.RoundToInt(p.x);
                h = h * 397 ^ Mathf.RoundToInt(p.y);
                return h == 0 ? 1 : h;
            }
        }

        private void OnWorldCreated(MonoBehaviour sender, EventArgs args)
        {
            if (!_controller.IsHost) return;
            _announcedSeed = CurrentSeed();
            _controller.LocalRunStarted(_announcedSeed.Value);
        }

        private void OnPlayerEnterRoom(MonoBehaviour sender, EventArgs args)
        {
            if (!(args is RoomViaDoorEventArgs roomArgs) || roomArgs.Room == null) return;
            var room = roomArgs.Room;
            if (room.BiomeType == BiomeType.HubTown) return; // the manor is everyone's own
            var id = RoomKey(room);
            var pos = (Vector2)PlayerManager.GetPlayerController().transform.position;

            if (_controller.Mode != CoopMode.Offline && _roomId != 0 && id != _roomId && !_controller.LocalWantsRoom(id, pos.x, pos.y))
            {
                // A guest walked through a door the host did not open: send them back.
                GoToRoom(_roomId, float.NaN, float.NaN);
                return;
            }
            if (!_entryPos.ContainsKey(id)) _entryPos[id] = pos;
            EnterRoom(room, id);
            _controller.LocalEnteredRoom(id, pos.x, pos.y);
        }

        private void EnterRoom(BaseRoom room, int id)
        {
            _room = room;
            _roomId = id;
            _enemies.Clear();
            _enemyIds.Clear();
            _nextSummonedId = FirstSummonedId;
            var spawners = room.SpawnControllerManager?.EnemySpawnControllers;
            ushort next = 1;
            foreach (var spawner in spawners ?? Array.Empty<EnemySpawnController>())
            {
                var id16 = next++;
                if (spawner == null || !spawner.ShouldSpawn || spawner.EnemyInstance == null) continue;
                Track(spawner.EnemyInstance, id16);
            }
            _log.Info($"Room {id}: {_enemies.Count} enemies{(_controller.IsGuest ? ", moved by the host" : "")}");
        }

        private void Track(EnemyController enemy, ushort netId)
        {
            _enemies.Add(enemy);
            _enemyIds[enemy] = netId;
            if (_controller.IsGuest) Puppetize(enemy);
        }

        private void OnEnemySummoned(MonoBehaviour sender, EventArgs args)
        {
            if (args is EnemySummonedEventArgs e && e.SummonedEnemy != null && !_enemyIds.ContainsKey(e.SummonedEnemy))
                Track(e.SummonedEnemy, _nextSummonedId++);
        }

        private void OnEnemyDeath(MonoBehaviour sender, EventArgs args)
        {
            if (!(args is EnemyDeathEventArgs e) || e.Victim == null) return;
            if (!_controller.IsHost) return;
            if (_enemyIds.TryGetValue(e.Victim, out var netId))
            {
                _log.Info($"Enemy {netId} died; telling the guests");
                _controller.LocalEnemyKilled(netId);
            }
            else _log.Info($"Untracked enemy {e.Victim.name} died in room {_roomId}");
        }

        public void GoToRoom(int roomId, float x, float y)
        {
            if (!_teleportPending) _teleportAskedAt = Time.realtimeSinceStartup;
            _teleportPending = true;
            _teleportRoom = roomId;
            _teleportPos = new Vector2(x, y);
        }

        /// <summary>Per frame, from the game's own update (see <see cref="Hooks"/>).</summary>
        internal void Tick()
        {
            SyncRunWithSession();
            if (_teleportPending && !SceneLoader_RL.IsRunningTransitionWithLogic && PlayerManager.IsInstantiated)
            {
                _teleportPending = false;
                Teleport(_teleportRoom, _teleportPos);
            }
            else if (_teleportPending && Time.realtimeSinceStartup - _teleportAskedAt > 5f)
            {
                _teleportAskedAt = float.MaxValue; // once per wait
                _log.Warn($"Move to room {_teleportRoom} waiting for over 5 s (transition {SceneLoader_RL.IsRunningTransitionWithLogic}, hero {PlayerManager.IsInstantiated})");
            }
            if (_spectating.HasValue && _puppets.TryGetValue(_spectating.Value, out var p) && p != null) FollowWithCamera(p.transform);
        }

        private float _teleportAskedAt;
        private (int room, Vector2 at)? _pendingMove;
        private int? _announcedSeed;
        private int? _rebuiltFor;

        internal static bool InBuiltCastle =>
            !SceneLoader_RL.IsLoading && PlayerManager.IsInstantiated
            && SceneLoader_RL.CurrentScene == SceneLoadingUtility.GetSceneName(SceneID.World);

        /// <summary>
        /// Keeps the castle in step with the session when the session started
        /// after the castle did: a host that starts hosting mid-run announces
        /// the castle it is already in, and a guest standing in a different
        /// castle when the host's seed arrives reloads into the host's.
        /// </summary>
        private void SyncRunWithSession()
        {
            if (_controller.IsHost)
            {
                if (InBuiltCastle && _announcedSeed != CurrentSeed())
                {
                    _announcedSeed = CurrentSeed();
                    _controller.LocalRunStarted(_announcedSeed.Value);
                }
                return;
            }
            _announcedSeed = null;
            var seed = PendingSeed;
            if (_pendingMove.HasValue && InBuiltCastle && (!seed.HasValue || CurrentSeed() == seed.Value))
                Teleport(_pendingMove.Value.room, _pendingMove.Value.at);
            if (!_controller.IsGuest || !seed.HasValue || _rebuiltFor == seed || !InBuiltCastle) return;
            if (CurrentSeed() == seed.Value) { _rebuiltFor = seed; return; }
            _rebuiltFor = seed;
            _log.Info($"This castle ({RNGSeedManager.GetSeedAsHex(CurrentSeed())}) is not the host's; rebuilding from {RNGSeedManager.GetSeedAsHex(seed.Value)}.");
            WindowManager.CloseAllOpenWindows();
            SceneLoader_RL.LoadScene(SceneID.World, TransitionID.FadeToBlackWithLoading);
        }

        private void Teleport(int roomId, Vector2 at)
        {
            if (!InBuiltCastle)
            {
                // Still on the title screen, in the manor or loading: go there once the castle stands.
                _pendingMove = (roomId, at);
                return;
            }
            _pendingMove = null;
            var room = FindRoom(roomId);
            if (room == null)
            {
                _log.Warn($"Room {roomId} is not in this castle; the castles have diverged (different seed or mods).");
                return;
            }
            Vector3 target = !float.IsNaN(at.x) ? (Vector3)at
                : _controller.TryHostPosition(roomId, out var hx, out var hy) ? new Vector3(hx, hy)
                : _entryPos.TryGetValue(roomId, out var entry) ? (Vector3)entry
                : DefaultEntrance(room);
            _log.Info($"Moving to room {roomId} at {target.x:0.0},{target.y:0.0}");
            // The same call the game's own tunnels use: it activates the room,
            // places the hero, sets the camera bounds and fires PlayerEnterRoom,
            // which lands in OnPlayerEnterRoom with the room already agreed.
            _roomId = roomId;
            room.PlacePlayerInRoom(room.transform.InverseTransformPoint(target));
        }

        /// <summary>
        /// Debug.AutoFight on the host: go through the nearest room's door
        /// that has enemies waiting, the way walking there would, so the
        /// guests follow into a fight.
        /// </summary>
        internal bool GoToNearestFight()
        {
            if (_room == null) return false;
            BaseRoom best = null;
            var bestDistance = float.MaxValue;
            foreach (var room in Resources.FindObjectsOfTypeAll<BaseRoom>())
            {
                if (!room.gameObject.scene.IsValid() || room == _room || room.BiomeType == BiomeType.HubTown) continue;
                var spawners = room.SpawnControllerManager?.EnemySpawnControllers;
                if (spawners == null || spawners.Length == 0 || room.Doors.Count == 0) continue;
                var d = (room.transform.position - _room.transform.position).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = room; }
            }
            if (best == null) return false;
            _log.Info($"AutoFight: going to room {RoomKey(best)}, {best.SpawnControllerManager.EnemySpawnControllers.Length} enemy spawner(s)");
            best.PlacePlayerInRoom(best.Doors[0]);
            return true;
        }

        /// <summary>
        /// Debug.AutoFight: put the local hero a step from the nearest living
        /// enemy, facing it, so the next swing lands. On a guest the enemies
        /// stand where the host's snapshots put them, so both copies pick the
        /// same one. False when the room has none left.
        /// </summary>
        internal bool StepToNearestEnemy()
        {
            var player = PlayerManager.GetPlayerController();
            if (player == null) return false;
            var from = player.transform.position;
            EnemyController best = null;
            var bestDistance = float.MaxValue;
            foreach (var e in _enemies)
            {
                if (e == null || e.IsDead || !e.gameObject.activeInHierarchy) continue;
                var d = (e.transform.position - from).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = e; }
            }
            if (best == null) return false;
            if (bestDistance > 4f)
            {
                var side = from.x <= best.transform.position.x ? -1.5f : 1.5f;
                player.transform.position = new Vector3(best.transform.position.x + side, best.transform.position.y, from.z);
            }
            player.SetFacing(player.transform.position.x < best.transform.position.x);
            return true;
        }

        private static BaseRoom FindRoom(int roomId)
        {
            foreach (var room in Resources.FindObjectsOfTypeAll<BaseRoom>())
                if (room.gameObject.scene.IsValid() && RoomKey(room) == roomId) return room;
            return null;
        }

        /// <summary>Fallback when nobody has stood in the room yet: the centre of its bounds.</summary>
        private static Vector3 DefaultEntrance(BaseRoom room)
        {
            var b = room.GetComponentInChildren<Collider2D>();
            return b != null ? b.bounds.center : room.transform.position;
        }

        // ------------------------------------------------------------ run

        /// <summary>The seed the castle in this scene was built from (RL2 shows it as hex on the pause screen).</summary>
        private static int CurrentSeed() => RNGSeedManager.GetCurrentSeed("World");

        public void StartRun(int seed)
        {
            PendingSeed = seed;
            _roomId = 0;
            _entryPos.Clear();
            _log.Info($"Next castle will be built from the host's seed {RNGSeedManager.GetSeedAsHex(seed)}. Enter the castle from the manor.");
        }

        public void EndRun(RunEndReason reason)
        {
            PendingSeed = null;
            var player = PlayerManager.GetPlayerController();
            Hooks.LocalDown = false;
            _spectating = null;
            RestoreCamera();
            if (player == null || player.IsDead) return;
            player.gameObject.SetActive(true);
            Hooks.AllowRealDeath = true;
            try { player.KillCharacter(null, broadcastEvent: true); }
            finally { Hooks.AllowRealDeath = false; }
        }

        // ------------------------------------------------------------ local hero

        public bool TryReadLocalPlayer(out PlayerStateMsg s)
        {
            s = default;
            if (!PlayerManager.IsInstantiated) return false;
            var player = PlayerManager.GetPlayerController();
            if (player == null) return false;

            var pos = (Vector2)player.transform.position;
            var now = Time.time;
            var vel = _lastPosTime >= 0 && now > _lastPosTime ? (pos - _lastPos) / (now - _lastPosTime) : Vector2.zero;
            _lastPos = pos;
            _lastPosTime = now;

            s.RoomId = _roomId;
            s.X = pos.x;
            s.Y = pos.y;
            s.VelX = vel.x;
            s.VelY = vel.y;
            s.Hp = Mathf.RoundToInt(player.CurrentHealth);
            s.MaxHp = Mathf.RoundToInt(player.ActualMaxHealth);
            if (player.IsFacingRight) s.Flags |= PlayerFlags.FacingRight;
            if (player.IsDead || Hooks.LocalDown) s.Flags |= PlayerFlags.Dead;
            var anim = player.Animator;
            if (anim != null)
            {
                var st = anim.GetCurrentAnimatorStateInfo(0);
                s.AnimState = st.fullPathHash;
                s.AnimTime = st.normalizedTime;
            }
            return true;
        }

        public void ReadLocalLoadout(out int classId, out byte[] appearance)
        {
            var c = SaveManager.PlayerSaveData.CurrentCharacter;
            classId = (int)c.ClassType;
            appearance = Array.Empty<byte>();
        }

        // ------------------------------------------------------------ puppets

        public void ShowPuppet(RemotePlayer player)
        {
            _puppetNames[player.Id] = player.Name;
            var p = PuppetFor(player.Id);
            if (p == null) return;
            p.Name = player.Name;
            p.gameObject.SetActive(true);
        }

        public void MovePuppet(byte playerId, in PlayerStateMsg state)
        {
            PuppetFor(playerId)?.Apply(state);
        }

        /// <summary>The player's puppet, made on first use. Creation waits for the local hero (it is copied from it), so it is retried every frame until the hero exists.</summary>
        private Puppet PuppetFor(byte playerId)
        {
            if (_puppets.TryGetValue(playerId, out var p) && p != null) return p;
            if (!_puppetNames.TryGetValue(playerId, out var name)) return null;
            p = Puppet.Create(playerId, name);
            if (p != null)
            {
                _puppets[playerId] = p;
                _log.Info($"Showing {name} (#{playerId + 1})");
            }
            return p;
        }

        public void PlayPuppetAction(byte playerId, in PlayerActionMsg action)
        {
            // Swings are carried by the animation state in every PlayerState
            // message; one-shot actions only need to cover what that misses.
        }

        public void HidePuppet(byte playerId)
        {
            if (_puppets.TryGetValue(playerId, out var p) && p != null) p.gameObject.SetActive(false);
        }

        public void Spectate(byte? playerId)
        {
            _spectating = playerId;
            if (playerId == null) RestoreCamera();
        }

        /// <summary>
        /// RL2's camera is a Cinemachine virtual camera per room that follows the
        /// hero; spectating points the active one at a puppet instead. Done every
        /// frame because each room switches to its own virtual camera.
        /// </summary>
        private static void FollowWithCamera(Transform target)
        {
            if (!CameraController.IsInstantiated) return;
            var vcam = CameraController.CinemachineBrain != null ? CameraController.CinemachineBrain.ActiveVirtualCamera : null;
            if (vcam != null && vcam.Follow != target) vcam.Follow = target;
        }

        private static void RestoreCamera()
        {
            var player = PlayerManager.IsInstantiated ? PlayerManager.GetPlayerController() : null;
            if (player != null) FollowWithCamera(player.transform);
        }

        internal IEnumerable<Puppet> Puppets => _puppets.Values;

        // ------------------------------------------------------------ enemies

        public void ReadEnemies(List<EnemyState> into)
        {
            foreach (var e in _enemies)
            {
                if (e == null || !_enemyIds.TryGetValue(e, out var id)) continue;
                var p = e.transform.position;
                var s = new EnemyState
                {
                    NetId = id,
                    X = p.x,
                    Y = p.y,
                    Hp = Mathf.RoundToInt(e.CurrentHealth),
                    MaxHp = Mathf.RoundToInt(e.ActualMaxHealth),
                };
                if (e.IsFacingRight) s.Flags |= EnemyFlags.FacingRight;
                if (e.IsDead) s.Flags |= EnemyFlags.Dead;
                if (e.gameObject.activeInHierarchy) s.Flags |= EnemyFlags.Active;
                if (e.IsBoss) s.Flags |= EnemyFlags.Boss;
                var anim = e.Animator;
                if (anim != null)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(0);
                    s.AnimState = st.fullPathHash;
                    s.AnimTime = st.normalizedTime;
                }
                into.Add(s);
            }
        }

        public void MoveEnemy(in EnemyState state)
        {
            var e = Find(state.NetId);
            if (e == null || e.IsDead) return;
            if ((state.Flags & EnemyFlags.Dead) != 0)
            {
                // The host's snapshot says it is dead: do not wait for the death notice.
                KillEnemy(state.NetId);
                return;
            }
            if (e.LogicController != null && e.LogicController.enabled) Puppetize(e); // respawned or reset by the game
            e.transform.position = new Vector3(state.X, state.Y, e.transform.position.z);
            var right = (state.Flags & EnemyFlags.FacingRight) != 0;
            if (e.IsFacingRight != right) e.SetFacing(right);

            if (state.AnimState != 0 && e.Animator != null)
            {
                var cur = e.Animator.GetCurrentAnimatorStateInfo(0);
                if (cur.fullPathHash != state.AnimState) e.Animator.Play(state.AnimState, 0, state.AnimTime % 1f);
            }

            var diff = state.Hp - e.CurrentHealth;
            if (Mathf.Abs(diff) >= 1)
            {
                ApplyingHostState = true;
                try { e.SetHealth(diff, true, false); }
                finally { ApplyingHostState = false; }
            }
        }

        public void KillEnemy(ushort netId)
        {
            var e = Find(netId);
            if (e == null || e.IsDead) return;
            _log.Info($"Host says enemy {netId} died");
            ApplyingHostState = true;
            try { e.KillCharacter(Killer(), broadcastEvent: true); }
            finally { ApplyingHostState = false; }
        }

        public void ApplyHit(in HitRequestMsg hit)
        {
            if (hit.RoomId != _roomId) { DropHit($"room {hit.RoomId}, host is in {_roomId}"); return; }
            var e = Find(hit.EnemyNetId);
            if (e == null || e.IsDead) { DropHit($"enemy {hit.EnemyNetId} is {(e == null ? "unknown" : "already dead")}"); return; }
            _log.Info($"Guest hit enemy {hit.EnemyNetId} for {hit.Damage:0}");
            e.SetHealth(-hit.Damage, true, true);
            if (e.CurrentHealth <= 0 && !e.IsDead) e.KillCharacter(Killer(), broadcastEvent: true);
        }

        private string _lastDrop;

        /// <summary>Says once why a guest's hits are being ignored, not once per hit.</summary>
        private void DropHit(string why)
        {
            if (why == _lastDrop) return;
            _lastDrop = why;
            _log.Info($"Guest hit ignored: {why}");
        }

        /// <summary>
        /// EnemyController.KillCharacter reads the killer's tag, so it needs a
        /// real object: the local hero stands in for whoever landed the blow.
        /// </summary>
        private static GameObject Killer() => PlayerManager.GetPlayerController()?.gameObject;

        internal bool TryGetNetId(EnemyController e, out ushort netId) => _enemyIds.TryGetValue(e, out netId);

        private EnemyController Find(ushort netId)
        {
            foreach (var e in _enemies)
                if (e != null && _enemyIds.TryGetValue(e, out var id) && id == netId) return e;
            return null;
        }

        /// <summary>
        /// On a guest, an enemy is moved by the host's snapshots, so its own
        /// brain must not move or attack it: its logic is stopped and switched
        /// off (EnemyController.FixedUpdate only ticks an enabled
        /// LogicController) and its character physics is switched off so
        /// gravity and knockback do not fight the host's positions.
        /// </summary>
        private static void Puppetize(EnemyController enemy)
        {
            var logic = enemy.LogicController;
            if (logic != null)
            {
                logic.StopAllLogic(resetCurrentState: true);
                logic.enabled = false;
            }
            if (enemy.ControllerCorgi != null) enemy.ControllerCorgi.enabled = false;
        }
    }
}
#endif
