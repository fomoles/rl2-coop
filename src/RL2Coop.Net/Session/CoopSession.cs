using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Sync;

namespace RL2Coop.Net.Session
{
    /// <summary>Tunables shared by host and guest.</summary>
    public sealed class CoopOptions
    {
        /// <summary>Silence after which a peer counts as gone. Generous, because game loads can stall the main thread.</summary>
        public int DisconnectTimeoutMs = 8000;

        /// <summary>How often a guest re-measures the host clock once it has settled.</summary>
        public double ClockPingInterval = 1.0;

        /// <summary>How far in the past remote players and enemies are drawn.</summary>
        public InterpolationSettings Interpolation = new InterpolationSettings();

        /// <summary>A guest hit above this is treated as corrupt and dropped by the host.</summary>
        public float MaxHitDamage = 1_000_000f;

        /// <summary>Artificial latency for testing, milliseconds (0 = off).</summary>
        public int SimulateLatencyMs;

        /// <summary>Artificial packet loss for testing, percent (0 = off).</summary>
        public int SimulateLossPercent;

        /// <summary>Name shown to the other players.</summary>
        public string PlayerName = "Player";

        /// <summary>Turns a host name into addresses. Runs off the game thread; replaceable in tests.</summary>
        public Func<string, IPAddress[]> ResolveHostName = Dns.GetHostAddresses;
    }

    /// <summary>
    /// Everything host and guest have in common: transport setup, message
    /// framing, the remote-player table and the events the game adapter
    /// listens to.
    /// <para>
    /// Threading: LiteNetLib keeps the socket alive on its own thread (so a
    /// long room load does not drop the connection), but every event here is
    /// raised from <see cref="Poll"/>, i.e. on the game's main thread.
    /// </para>
    /// </summary>
    public abstract class CoopSession : INetEventListener, IDisposable
    {
        protected readonly NetManager Net;
        protected readonly NetDataWriter Writer = new NetDataWriter();
        protected readonly IClock Clock;
        protected readonly ILog Log;
        protected readonly CoopOptions Options;
        private readonly Dictionary<byte, RemotePlayer> _players = new Dictionary<byte, RemotePlayer>();

        protected CoopSession(CoopOptions options, IClock clock, ILog log)
        {
            Options = options ?? new CoopOptions();
            Clock = clock ?? new StopwatchClock();
            Log = log ?? NullLog.Instance;
            Net = new NetManager(this)
            {
                ChannelsCount = Channels.Count,
                DisconnectTimeout = Options.DisconnectTimeoutMs,
                AutoRecycle = true,
                UnsyncedEvents = false,
                UpdateTime = 10,
                PingInterval = 500,
                IPv6Enabled = false,
            };
            if (Options.SimulateLatencyMs > 0)
            {
                Net.SimulateLatency = true;
                Net.SimulationMinLatency = Options.SimulateLatencyMs / 2;
                Net.SimulationMaxLatency = Options.SimulateLatencyMs;
            }
            if (Options.SimulateLossPercent > 0)
            {
                Net.SimulatePacketLoss = true;
                Net.SimulationPacketLossChance = Options.SimulateLossPercent;
            }
        }

        // ------------------------------------------------------------------
        // state

        /// <summary>This machine's player id (host is always 0; a guest learns its id from the welcome).</summary>
        public byte LocalId { get; protected set; }

        /// <summary>Name this player shows to others.</summary>
        public string LocalName => Options.PlayerName;

        public abstract bool IsHost { get; }

        /// <summary>True once the session can carry gameplay (host listening / guest welcomed).</summary>
        public abstract bool IsConnected { get; }

        /// <summary>Current time on the shared host timeline, seconds.</summary>
        public abstract double HostNow { get; }

        /// <summary>
        /// True once <see cref="HostNow"/> rests on a measured round trip rather
        /// than a guess. Always true on the host.
        /// </summary>
        public virtual bool ClockReady => true;

        /// <summary>Time remote entities should be drawn at this frame.</summary>
        public double RenderTime => HostNow - Options.Interpolation.Delay;

        public InterpolationSettings Interpolation => Options.Interpolation;

        public bool RunActive { get; protected set; }
        public int RunSeed { get; protected set; }
        public int RoomId { get; protected set; }
        public int RoomSequence { get; protected set; }

        public bool LocalAlive { get; private set; } = true;

        /// <summary>Other players, keyed by id. Never contains the local player.</summary>
        public IReadOnlyDictionary<byte, RemotePlayer> Players => _players;

        // ------------------------------------------------------------------
        // events (all raised from Poll, on the caller's thread)

        public event Action<RemotePlayer> PlayerJoined;
        public event Action<RemotePlayer, LeaveReason> PlayerLeft;
        public event Action<RemotePlayer> PlayerLoadoutChanged;
        public event Action<RemotePlayer, PlayerActionMsg> PlayerActed;
        public event Action<RemotePlayer> PlayerLifeChanged;
        public event Action<int> RunStarted;
        /// <summary>The party moved. On the host this is raised only for changes it did not make itself.</summary>
        public event Action<RoomChangeMsg> RoomChanged;
        public event Action<RunEndReason> RunEnded;
        public event Action<EnemySnapshotMsg> EnemySnapshotReceived;
        public event Action<EnemyDiedMsg> EnemyDied;

        // ------------------------------------------------------------------
        // main loop

        /// <summary>Call once per game frame. Processes network events and timers.</summary>
        public void Poll()
        {
            Net.PollEvents();
            OnUpdate();
        }

        protected virtual void OnUpdate() { }

        public virtual void Dispose()
        {
            if (Net.IsRunning) Net.Stop(true);
        }

        // ------------------------------------------------------------------
        // local player -> others

        /// <summary>Publish the local player's body. Call ~30 times a second.</summary>
        public void SendLocalState(PlayerStateMsg state)
        {
            // A state stamped with a guessed time would land at the wrong moment on
            // everyone's timeline and show as a jerk once the clock settles.
            if (!ClockReady) return;
            state.Id = LocalId;
            state.Time = HostNow;
            state.RoomId = RoomId;
            if (!LocalAlive) state.Flags |= PlayerFlags.Dead;
            SendFromLocal(state, Channels.PlayerState(LocalId), DeliveryMethod.Sequenced);
        }

        /// <summary>Publish what the local player looks like. Call when it changes.</summary>
        public void SendLocalLoadout(int classId, byte[] appearance)
        {
            var msg = new PlayerLoadoutMsg { Id = LocalId, ClassId = classId, Appearance = appearance };
            LocalLoadout = msg;
            SendFromLocal(msg, Channels.Control, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Last loadout published by the local player (re-sent to late joiners by the host).</summary>
        public PlayerLoadoutMsg? LocalLoadout { get; private set; }

        /// <summary>Publish a discrete action (attack swing, spell) so the others can play it.</summary>
        public void SendLocalAction(byte kind, int param, float x, float y, bool facingRight)
        {
            var msg = new PlayerActionMsg { Id = LocalId, Kind = kind, Param = param, X = x, Y = y, FacingRight = facingRight };
            SendFromLocal(msg, Channels.Control, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>The local player died or came back.</summary>
        public void SetLocalAlive(bool alive)
        {
            if (LocalAlive == alive) return;
            LocalAlive = alive;
            SendFromLocal(new PlayerLifeMsg { Id = LocalId, Alive = alive }, Channels.Control, DeliveryMethod.ReliableOrdered);
            OnLocalLifeChanged();
        }

        protected virtual void OnLocalLifeChanged() { }

        /// <summary>Guest: send to the host. Host: send to every guest.</summary>
        protected abstract void SendFromLocal<T>(T msg, byte channel, DeliveryMethod method) where T : struct, IMessage;

        // ------------------------------------------------------------------
        // helpers for subclasses

        protected NetDataWriter Frame<T>(T msg) where T : struct, IMessage
        {
            Writer.Reset();
            Writer.Put((byte)msg.Type);
            msg.Write(Writer);
            return Writer;
        }

        protected void Send<T>(NetPeer peer, T msg, byte channel, DeliveryMethod method) where T : struct, IMessage
            => peer.Send(Frame(msg), channel, method);

        protected void SendAll<T>(T msg, byte channel, DeliveryMethod method, NetPeer except = null) where T : struct, IMessage
            => Net.SendToAll(Frame(msg), channel, method, except);

        protected static T ReadMsg<T>(NetDataReader r) where T : struct, IMessage
        {
            var m = default(T);
            m.Read(r);
            return m;
        }

        /// <summary>Wipe per-run alive flags (new run: everyone starts alive).</summary>
        protected void ResetLives()
        {
            LocalAlive = true;
            foreach (var p in _players.Values) p.Alive = true;
        }

        protected RemotePlayer AddPlayer(byte id, string name)
        {
            if (_players.TryGetValue(id, out var existing)) { existing.Name = name; return existing; }
            var p = new RemotePlayer(id, name);
            _players[id] = p;
            return p;
        }

        protected bool RemovePlayer(byte id, out RemotePlayer player)
        {
            if (!_players.TryGetValue(id, out player)) return false;
            _players.Remove(id);
            return true;
        }

        protected void ClearPlayers() => _players.Clear();

        protected RemotePlayer FindPlayer(byte id) => _players.TryGetValue(id, out var p) ? p : null;

        /// <summary>Apply a relayed or direct state update from another player.</summary>
        protected void ApplyState(in PlayerStateMsg s)
        {
            var p = FindPlayer(s.Id);
            p?.States.Add(s.Time, s);
        }

        protected void ApplyLoadout(in PlayerLoadoutMsg m)
        {
            var p = FindPlayer(m.Id);
            if (p == null) return;
            p.Loadout = m;
            PlayerLoadoutChanged?.Invoke(p);
        }

        protected void ApplyAction(in PlayerActionMsg m)
        {
            var p = FindPlayer(m.Id);
            if (p != null) PlayerActed?.Invoke(p, m);
        }

        protected bool ApplyLife(in PlayerLifeMsg m)
        {
            var p = FindPlayer(m.Id);
            if (p == null || p.Alive == m.Alive) return false;
            p.Alive = m.Alive;
            PlayerLifeChanged?.Invoke(p);
            return true;
        }

        protected void RaiseJoined(RemotePlayer p) => PlayerJoined?.Invoke(p);
        protected void RaiseLeft(RemotePlayer p, LeaveReason r) => PlayerLeft?.Invoke(p, r);
        protected void RaiseRunStarted(int seed) => RunStarted?.Invoke(seed);
        protected void RaiseRoomChanged(RoomChangeMsg m) => RoomChanged?.Invoke(m);
        protected void RaiseRunEnded(RunEndReason r) => RunEnded?.Invoke(r);
        protected void RaiseEnemySnapshot(EnemySnapshotMsg m) => EnemySnapshotReceived?.Invoke(m);
        protected void RaiseEnemyDied(EnemyDiedMsg m) => EnemyDied?.Invoke(m);

        // ------------------------------------------------------------------
        // transport callbacks

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            if (reader.AvailableBytes < 1) return;
            var type = (MessageType)reader.GetByte();
            try
            {
                Dispatch(peer, type, reader);
            }
            catch (Exception e)
            {
                // A malformed or truncated packet must never take the game down.
                Log.Warn($"dropped bad {type} packet from {peer}: {e.GetType().Name}: {e.Message}");
            }
        }

        protected abstract void Dispatch(NetPeer peer, MessageType type, NetDataReader r);

        public abstract void OnPeerConnected(NetPeer peer);
        public abstract void OnPeerDisconnected(NetPeer peer, DisconnectInfo info);
        public abstract void OnConnectionRequest(ConnectionRequest request);

        public virtual void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
            => Log.Warn($"socket error {socketError} ({endPoint})");

        public virtual void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    }
}
