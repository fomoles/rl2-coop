using System;
using System.Collections.Generic;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;
using RL2Coop.Net.Sync;

namespace RL2Coop.Net.Game
{
    public enum CoopMode
    {
        Offline,
        Hosting,
        Connecting,
        Joined,
    }

    /// <summary>
    /// The co-op rules, independent of any engine. The adapter calls
    /// <see cref="Tick"/> once per frame and reports what the local hero does
    /// through the Local* methods; the controller decides what is sent, what
    /// the game is allowed to do on its own, and what it must show.
    ///
    /// Rules it enforces:
    /// - the host leads: guests follow the host's seed and rooms, and cannot
    ///   walk into a room the host is not in (unless <see cref="GuestsCanLead"/>);
    /// - the host owns every enemy: a guest's hit becomes a request, the host
    ///   applies it, and guests only mirror what the host reports;
    /// - a dead hero watches a living one; the run ends when nobody is left.
    /// </summary>
    public sealed class CoopController : IDisposable
    {
        private readonly IGameWorld _game;
        private readonly ILog _log;
        private readonly IClock _clock;
        private readonly List<EnemyState> _enemyScratch = new List<EnemyState>(64);
        private readonly HashSet<byte> _visiblePuppets = new HashSet<byte>();
        private readonly List<byte> _scratchIds = new List<byte>(4);
        private double _nextStateSend, _nextEnemySend;
        private byte? _spectating;
        private bool _spectateApplied;

        public CoopController(IGameWorld game, ILog log = null, IClock clock = null)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _log = log ?? NullLog.Instance;
            _clock = clock ?? new StopwatchClock();
        }

        /// <summary>How often the local hero is sent, per second.</summary>
        public double StateSendRate = 30;

        /// <summary>How often the host sends the room's enemies, per second.</summary>
        public double EnemySendRate = 20;

        /// <summary>When true, a guest stepping through a door takes everyone there; otherwise only the host opens rooms.</summary>
        public bool GuestsCanLead;

        public CoopMode Mode { get; private set; }
        public CoopSession Session { get; private set; }
        public CoopHost Host => Session as CoopHost;
        public CoopGuest Guest => Session as CoopGuest;
        public bool IsHost => Mode == CoopMode.Hosting;
        public bool IsGuest => Mode == CoopMode.Joined || Mode == CoopMode.Connecting;

        /// <summary>Guest's mirror of the host's enemies (empty on the host).</summary>
        public EnemyMirror Enemies { get; } = new EnemyMirror();

        public Spectator Spectator { get; } = new Spectator();

        /// <summary>Short human-readable messages for the overlay ("Bob joined", "Only the host opens new rooms").</summary>
        public event Action<string> Notice;

        /// <summary>The session closed (left, host gone, kicked, failed to connect). The argument says why, for the overlay.</summary>
        public event Action<string> Ended;

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Open a session others can join. Returns false if the port is taken.</summary>
        public bool StartHost(CoopOptions options, int port = ProtocolInfo.DefaultPort)
        {
            Leave();
            var host = new CoopHost(options, _clock, _log);
            if (!host.Start(port))
            {
                host.Dispose();
                Say($"Could not open port {port}: is another copy already hosting?");
                return false;
            }
            Attach(host);
            host.HitRequested += OnHitRequested;
            host.RoomRequested += OnRoomRequested;
            Mode = CoopMode.Hosting;
            SendLoadout();
            Say($"Hosting on port {host.Port}");
            return true;
        }

        /// <summary>Connect to a host. Progress and failure arrive through <see cref="Notice"/> and <see cref="Ended"/>.</summary>
        public void Join(CoopOptions options, string address, int port = ProtocolInfo.DefaultPort)
        {
            Leave();
            var guest = new CoopGuest(options, _clock, _log);
            Attach(guest);
            guest.Connected += OnGuestConnected;
            guest.Disconnected += OnGuestDisconnected;
            Mode = CoopMode.Connecting;
            Say($"Connecting to {address}:{port}…");
            try
            {
                guest.Connect(address, port);
            }
            catch (Exception e) when (e is InvalidOperationException || e is System.Net.Sockets.SocketException || e is ArgumentException)
            {
                // A typo in the address must not reach the game as an exception.
                Session = null;
                Mode = CoopMode.Offline;
                guest.Dispose();
                Cleanup();
                var reason = $"Could not connect to \"{address}\": check the address";
                _log.Warn($"{reason} ({e.Message})");
                Say(reason);
                Ended?.Invoke(reason);
            }
        }

        /// <summary>Close the session and remove every trace of other players from the game.</summary>
        public void Leave()
        {
            if (Session == null) return;
            var s = Session;
            Session = null;
            var wasMode = Mode;
            Mode = CoopMode.Offline;
            s.Dispose();
            Cleanup();
            if (wasMode != CoopMode.Offline) Ended?.Invoke("You left the session");
        }

        public void Dispose() => Leave();

        private void Attach(CoopSession s)
        {
            Session = s;
            s.PlayerJoined += OnPlayerJoined;
            s.PlayerLeft += OnPlayerLeft;
            s.PlayerLoadoutChanged += OnLoadoutChanged;
            s.PlayerActed += OnPlayerActed;
            s.PlayerLifeChanged += OnPlayerLifeChanged;
            s.RunStarted += OnRunStarted;
            s.RoomChanged += OnRoomChanged;
            s.RunEnded += OnRunEnded;
            s.EnemySnapshotReceived += m => Enemies.Apply(m);
            s.EnemyDied += OnEnemyDied;
        }

        private void Cleanup()
        {
            foreach (var id in _visiblePuppets) _game.HidePuppet(id);
            _visiblePuppets.Clear();
            Enemies.Reset(int.MinValue);
            Spectator.Clear();
            if (_spectating.HasValue || _spectateApplied) _game.Spectate(null);
            _spectating = null;
            _spectateApplied = false;
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Call once per rendered frame on the game's main thread.</summary>
        public void Tick()
        {
            var s = Session;
            if (s == null) return;
            s.Poll();
            if (Session != s) return; // an event handler ended the session
            if (Mode == CoopMode.Connecting) return;

            var now = _clock.Now;
            if (now >= _nextStateSend)
            {
                _nextStateSend = Math.Max(_nextStateSend + 1.0 / StateSendRate, now - 0.5);
                if (_game.TryReadLocalPlayer(out var me)) s.SendLocalState(me);
            }

            if (IsHost && s.RunActive && now >= _nextEnemySend)
            {
                _nextEnemySend = Math.Max(_nextEnemySend + 1.0 / EnemySendRate, now - 0.5);
                _enemyScratch.Clear();
                _game.ReadEnemies(_enemyScratch);
                if (_enemyScratch.Count > 0) Host.BroadcastEnemies(_enemyScratch);
            }

            DrawPuppets(s);
            if (IsGuest) DrawEnemies(s);
            UpdateSpectator(s);
        }

        private void DrawPuppets(CoopSession s)
        {
            var renderTime = s.RenderTime;
            foreach (var p in s.Players.Values)
            {
                var show = Interp.Sample(p.States, renderTime, s.Interpolation, out var state)
                           && (!s.RunActive || state.RoomId == s.RoomId);
                if (show)
                {
                    if (_visiblePuppets.Add(p.Id)) _game.ShowPuppet(p);
                    if (!p.Alive) state.Flags |= PlayerFlags.Dead;
                    _game.MovePuppet(p.Id, state);
                }
                else if (_visiblePuppets.Remove(p.Id))
                {
                    _game.HidePuppet(p.Id);
                }
            }
        }

        private void DrawEnemies(CoopSession s)
        {
            var renderTime = s.RenderTime;
            foreach (var id in Enemies.Known)
                if (Enemies.TryGet(id, renderTime, out var e)) _game.MoveEnemy(e);
        }

        private void UpdateSpectator(CoopSession s)
        {
            byte? target = null;
            if (s.RunActive && !s.LocalAlive) target = Spectator.Update(s.Players);
            if (target == _spectating && _spectateApplied == target.HasValue) return;
            _spectating = target;
            _spectateApplied = target.HasValue;
            _game.Spectate(target);
        }

        /// <summary>Dead player pressed next/previous: watch someone else.</summary>
        public void CycleSpectate(int direction)
        {
            var s = Session;
            if (s == null || s.LocalAlive) return;
            _spectating = Spectator.Cycle(s.Players, direction);
            _spectateApplied = _spectating.HasValue;
            _game.Spectate(_spectating);
        }

        // ---------------------------------------------------------------- reports from the game

        /// <summary>
        /// Host started a new run from the manor. Guests are pulled into the same castle.
        /// On a guest this is ignored: a guest's run only ever starts from the host.
        /// </summary>
        public void LocalRunStarted(int seed)
        {
            if (!IsHost) return;
            Host.StartRun(seed);
            SendLoadout();
        }

        /// <summary>
        /// The hero is about to go through a door into <paramref name="roomId"/>.
        /// Returns whether the game may go ahead. The host always may; a guest may
        /// only enter the room the host is already in.
        /// </summary>
        public bool LocalWantsRoom(int roomId, float x, float y)
        {
            var s = Session;
            if (s == null || IsHost || !s.RunActive) return true;
            if (roomId == s.RoomId) return true;
            if (GuestsCanLead && Mode == CoopMode.Joined)
            {
                Guest.RequestRoom(roomId, x, y);
                return false; // move when the host confirms it
            }
            Say("Only the host opens new rooms");
            return false;
        }

        /// <summary>The hero arrived in a room. On the host this takes everyone along.</summary>
        public void LocalEnteredRoom(int roomId, float x, float y)
        {
            if (!IsHost || !Host.RunActive || roomId == Host.RoomId) return;
            Host.ChangeRoom(roomId, x, y);
            Enemies.Reset(roomId);
        }

        /// <summary>
        /// The local hero's attack connected with an enemy. Returns whether the
        /// game should apply the damage itself: yes on the host (it owns the
        /// enemies), no on a guest (the hit is sent to the host instead).
        /// </summary>
        public bool LocalHitsEnemy(ushort netId, float damage, bool critical, float knockX = 0, float knockY = 0)
        {
            if (!IsGuest) return true;
            if (Mode == CoopMode.Joined && Session.LocalAlive) Guest.RequestHit(netId, damage, critical, knockX, knockY);
            return false;
        }

        /// <summary>Host only: an enemy died (to anyone's hit). Guests play its death.</summary>
        public void LocalEnemyKilled(ushort netId, byte killerId = ProtocolInfo.HostPlayerId)
        {
            if (IsHost) Host.EnemyKilled(netId, killerId);
        }

        /// <summary>A one-shot move others should see: attack, spell, talent.</summary>
        public void LocalAction(byte kind, int param, float x, float y, bool facingRight)
            => Session?.SendLocalAction(kind, param, x, y, facingRight);

        public void LocalDied()
        {
            var s = Session;
            if (s == null || !s.LocalAlive) return;
            s.SetLocalAlive(false);
            if (s.RunActive && FirstAlive(s) == null) return; // the host ends the run; nobody to watch
            Say("You died. Watching your team — [ / ] to switch");
        }

        /// <summary>The heir or its look changed (new run, class swap).</summary>
        public void LocalLoadoutChanged() => SendLoadout();

        private void SendLoadout()
        {
            if (Session == null) return;
            _game.ReadLocalLoadout(out var classId, out var appearance);
            Session.SendLocalLoadout(classId, appearance ?? Array.Empty<byte>());
        }

        // ---------------------------------------------------------------- session events

        private void OnGuestConnected()
        {
            Mode = CoopMode.Joined;
            SendLoadout();
            Say($"Joined as player {Session.LocalId + 1}");
        }

        private void OnGuestDisconnected(GuestEndReason reason, string message)
        {
            if (Session == null) return;
            var text = reason switch
            {
                GuestEndReason.ConnectionFailed => "Could not reach the host (wrong address, closed port or firewall)",
                GuestEndReason.HostNotFound => $"Could not find the host \"{message}\": check the address",
                GuestEndReason.Rejected => "The host refused: " + message,
                GuestEndReason.HostClosed => "The host closed the session",
                GuestEndReason.Kicked => "The host removed you from the session",
                GuestEndReason.TimedOut => "Lost connection to the host",
                _ => "Disconnected",
            };
            var s = Session;
            Session = null;
            Mode = CoopMode.Offline;
            s.Dispose();
            Cleanup();
            Say(text);
            Ended?.Invoke(text);
        }

        private void OnPlayerJoined(RemotePlayer p) => Say($"{p.Name} joined");

        private void OnPlayerLeft(RemotePlayer p, LeaveReason reason)
        {
            if (_visiblePuppets.Remove(p.Id)) _game.HidePuppet(p.Id);
            Say(reason == LeaveReason.TimedOut ? $"{p.Name} lost connection" : $"{p.Name} left");
        }

        private void OnLoadoutChanged(RemotePlayer p)
        {
            if (_visiblePuppets.Contains(p.Id)) _game.ShowPuppet(p);
        }

        private void OnPlayerActed(RemotePlayer p, PlayerActionMsg a)
        {
            if (_visiblePuppets.Contains(p.Id)) _game.PlayPuppetAction(p.Id, a);
        }

        private void OnPlayerLifeChanged(RemotePlayer p)
        {
            if (!p.Alive) Say($"{p.Name} died");
        }

        private void OnRunStarted(int seed)
        {
            Enemies.Reset(Session.RoomId);
            _spectating = null;
            if (_spectateApplied) { _spectateApplied = false; _game.Spectate(null); }
            if (IsGuest) _game.StartRun(seed);
        }

        private void OnRoomChanged(RoomChangeMsg m)
        {
            Enemies.Reset(m.RoomId);
            if (IsGuest) _game.GoToRoom(m.RoomId, m.X, m.Y);
        }

        private void OnRunEnded(RunEndReason reason)
        {
            _game.EndRun(reason);
            Say(reason switch
            {
                RunEndReason.AllPlayersDead => "Everyone has fallen. The run is over",
                RunEndReason.Victory => "Victory!",
                _ => "The host ended the run",
            });
        }

        private void OnEnemyDied(EnemyDiedMsg m)
        {
            Enemies.MarkDead(m.NetId);
            _game.KillEnemy(m.NetId);
        }

        private void OnHitRequested(HitRequestMsg hit) => _game.ApplyHit(hit);

        private void OnRoomRequested(RoomRequestMsg m)
        {
            if (!GuestsCanLead) return;
            _game.GoToRoom(m.RoomId, m.X, m.Y);
            LocalEnteredRoom(m.RoomId, m.X, m.Y);
        }

        /// <summary>Where the host stands now, if they are in <paramref name="roomId"/>: a player joining mid-run appears there.</summary>
        public bool TryHostPosition(int roomId, out float x, out float y)
        {
            x = y = float.NaN;
            if (!IsGuest || !Session.Players.TryGetValue(ProtocolInfo.HostPlayerId, out var host) || host.States.Count == 0) return false;
            var s = host.Latest;
            if (s.RoomId != roomId) return false;
            x = s.X;
            y = s.Y;
            return true;
        }

        /// <summary>
        /// True when at least one other player is connected and every other
        /// player's latest state is in the given room.
        /// </summary>
        public bool OthersInRoom(int roomId)
        {
            var s = Session;
            if (s == null || s.Players.Count == 0) return false;
            foreach (var p in s.Players.Values)
                if (p.States.Count == 0 || p.Latest.RoomId != roomId) return false;
            return true;
        }

        // ---------------------------------------------------------------- overlay helpers

        /// <summary>One line per player for the overlay, the local one first.</summary>
        public void DescribePlayers(List<string> into)
        {
            into.Clear();
            var s = Session;
            if (s == null) return;
            var me = s.LocalAlive ? "" : " (dead)";
            into.Add($"#{s.LocalId + 1} {s.LocalName} (you{(IsHost ? ", host" : "")}){me}");
            _scratchIds.Clear();
            foreach (var id in s.Players.Keys) _scratchIds.Add(id);
            _scratchIds.Sort();
            foreach (var id in _scratchIds)
            {
                var p = s.Players[id];
                var ping = IsGuest && id == ProtocolInfo.HostPlayerId ? Guest.HostRttMs : p.PingMs;
                var away = s.RunActive && p.States.Count > 0 && p.Latest.RoomId != s.RoomId ? " (other room)" : "";
                into.Add($"#{p.Id + 1} {p.Name}{(id == ProtocolInfo.HostPlayerId ? " (host)" : "")} {ping} ms{(p.Alive ? "" : " (dead)")}{away}");
            }
        }

        private static RemotePlayer FirstAlive(CoopSession s)
        {
            foreach (var p in s.Players.Values) if (p.Alive) return p;
            return null;
        }

        private void Say(string text)
        {
            _log.Info(text);
            Notice?.Invoke(text);
        }
    }
}
