using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Sync;

namespace RL2Coop.Net.Session
{
    /// <summary>Why a guest's session ended, in terms a player understands.</summary>
    public enum GuestEndReason
    {
        /// <summary>Nobody answered at that address (wrong IP, host not started, firewall).</summary>
        ConnectionFailed,
        /// <summary>The host name did not turn into an address.</summary>
        HostNotFound,
        /// <summary>The host refused us; see the message (full, wrong version).</summary>
        Rejected,
        /// <summary>The host closed the session.</summary>
        HostClosed,
        /// <summary>The host removed us.</summary>
        Kicked,
        /// <summary>The connection went silent.</summary>
        TimedOut,
        /// <summary>We left on purpose.</summary>
        LocalQuit,
    }

    /// <summary>
    /// A guest: connects to one host, follows its run and room changes, sees
    /// enemies through the host's snapshots and sends its hits to the host.
    /// </summary>
    public sealed class CoopGuest : CoopSession
    {
        private readonly ClockSync _clockSync = new ClockSync();
        private NetPeer _host;
        private bool _welcomed;
        private bool _leaving;
        private double _nextPing;
        private int _pingsSent;
        private Task<IPAddress[]> _lookup;
        private string _lookupName;
        private int _lookupPort;
        private NetDataWriter _connectData;

        public CoopGuest(CoopOptions options = null, IClock clock = null, ILog log = null)
            : base(options, clock, log) { }

        public override bool IsHost => false;
        public override bool IsConnected => _welcomed && _host != null && _host.ConnectionState == ConnectionState.Connected;
        public override double HostNow => _clockSync.ToHost(Clock.Now);
        public override bool ClockReady => _clockSync.HasSample;

        /// <summary>True while a connection attempt is underway or open.</summary>
        public bool IsActive => _host != null || _lookup != null;

        /// <summary>Round trip to the host in milliseconds.</summary>
        public int HostRttMs => _host?.RoundTripTime ?? 0;

        /// <summary>Clock estimate quality, for diagnostics.</summary>
        public ClockSync ClockSync => _clockSync;

        /// <summary>The host accepted us and sent the welcome; <see cref="CoopSession.Players"/> is filled.</summary>
        public event Action Connected;

        /// <summary>The session is over (or never started). The message is human readable.</summary>
        public event Action<GuestEndReason, string> Disconnected;

        public void Connect(string address, int port = ProtocolInfo.DefaultPort)
        {
            if (IsActive) throw new InvalidOperationException("already connected or connecting");
            if (!Net.IsRunning && !Net.Start()) throw new InvalidOperationException("could not open a UDP socket");

            var w = new NetDataWriter();
            w.Put(ProtocolInfo.Magic, 16);
            w.Put(ProtocolInfo.Version);
            w.Put(Options.PlayerName ?? string.Empty, ProtocolInfo.MaxNameLength);
            _connectData = w;
            _leaving = false;
            _welcomed = false;
            if (IPAddress.TryParse(address, out var ip))
            {
                _host = Net.Connect(new IPEndPoint(ip, port), w);
                if (_host == null) throw new InvalidOperationException($"could not start a connection to {address}:{port}");
                Log.Info($"connecting to {address}:{port}");
                return;
            }
            // A name lookup can take seconds; done here it would freeze the game.
            _lookupName = address;
            _lookupPort = port;
            var resolve = Options.ResolveHostName;
            _lookup = Task.Run(() => resolve(address));
            Log.Info($"looking up {address}");
        }

        private void FinishLookup()
        {
            var task = _lookup;
            _lookup = null;
            var ip = task.Status == TaskStatus.RanToCompletion
                ? task.Result?.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                : null;
            if (ip == null)
            {
                var why = task.Exception?.GetBaseException().Message ?? "no IPv4 address";
                Log.Info($"lookup of {_lookupName} failed: {why}");
                Disconnected?.Invoke(GuestEndReason.HostNotFound, _lookupName);
                return;
            }
            _host = Net.Connect(new IPEndPoint(ip, _lookupPort), _connectData);
            if (_host == null)
            {
                Disconnected?.Invoke(GuestEndReason.ConnectionFailed, $"could not start a connection to {ip}:{_lookupPort}");
                return;
            }
            Log.Info($"connecting to {_lookupName} ({ip}):{_lookupPort}");
        }

        /// <summary>Leave the session.</summary>
        public void Disconnect()
        {
            _lookup = null; // a pending lookup finishes on its own; its answer is ignored
            if (_host == null) return;
            _leaving = true;
            _host.Disconnect();
            Net.PollEvents();
        }

        public override void Dispose()
        {
            Disconnect();
            base.Dispose();
        }

        /// <summary>Tell the host this guest's attack hit an enemy.</summary>
        public void RequestHit(ushort enemyNetId, float damage, bool critical, float knockX = 0, float knockY = 0)
        {
            if (!IsConnected) return;
            Send(_host, new HitRequestMsg
            {
                RoomId = RoomId,
                EnemyNetId = enemyNetId,
                Damage = damage,
                Critical = critical,
                KnockX = knockX,
                KnockY = knockY,
            }, Channels.Control, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Tell the host this guest went through a door into another room.</summary>
        public void RequestRoom(int roomId, float x, float y)
        {
            if (!IsConnected) return;
            Send(_host, new RoomRequestMsg { RoomId = roomId, X = x, Y = y }, Channels.Control, DeliveryMethod.ReliableOrdered);
        }

        protected override void SendFromLocal<T>(T msg, byte channel, DeliveryMethod method)
        {
            if (IsConnected) Send(_host, msg, channel, method);
        }

        protected override void OnUpdate()
        {
            if (_lookup != null && _lookup.IsCompleted) FinishLookup();
            if (!IsConnected) return;
            var now = Clock.Now;
            if (now < _nextPing) return;
            // A quick burst first so the timeline is right before gameplay starts, then a slow trickle.
            _pingsSent++;
            _nextPing = now + (_pingsSent < 6 ? 0.2 : Options.ClockPingInterval);
            Send(_host, new TimePingMsg { ClientTime = now }, Channels.Control, DeliveryMethod.Unreliable);
        }

        // ------------------------------------------------------------------

        public override void OnConnectionRequest(ConnectionRequest request) => request.Reject();

        public override void OnPeerConnected(NetPeer peer)
        {
            // Nothing yet: the session counts as connected once the welcome arrives.
        }

        public override void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != _host) return;
            var wasWelcomed = _welcomed;
            _host = null;
            _welcomed = false;
            ClearPlayers();
            RunActive = false;

            GuestEndReason reason;
            string message;
            switch (info.Reason)
            {
                case DisconnectReason.ConnectionRejected:
                    reason = GuestEndReason.Rejected;
                    message = "the host refused the connection";
                    if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0
                        && info.AdditionalData.TryGetByte(out var why))
                    {
                        message = info.AdditionalData.TryGetString(out var text) && !string.IsNullOrEmpty(text)
                            ? text : ((RejectReason)why).ToString();
                    }
                    break;
                case DisconnectReason.RemoteConnectionClose:
                    reason = GuestEndReason.HostClosed;
                    message = "the host closed the session";
                    if (info.AdditionalData != null && info.AdditionalData.TryGetByte(out var leave) && leave == (byte)LeaveReason.Kicked)
                    {
                        reason = GuestEndReason.Kicked;
                        message = "the host removed you from the session";
                    }
                    break;
                case DisconnectReason.Timeout:
                    reason = wasWelcomed ? GuestEndReason.TimedOut : GuestEndReason.ConnectionFailed;
                    message = wasWelcomed ? "lost connection to the host" : "no answer from the host";
                    break;
                case DisconnectReason.DisconnectPeerCalled:
                    reason = GuestEndReason.LocalQuit;
                    message = "you left the session";
                    break;
                default:
                    reason = wasWelcomed ? GuestEndReason.TimedOut : GuestEndReason.ConnectionFailed;
                    message = $"connection lost ({info.Reason})";
                    break;
            }
            if (_leaving) { reason = GuestEndReason.LocalQuit; message = "you left the session"; }
            Log.Info($"disconnected: {reason}: {message}");
            Disconnected?.Invoke(reason, message);
        }

        protected override void Dispatch(NetPeer peer, MessageType type, NetDataReader r)
        {
            if (peer != _host) return;
            if (!_welcomed && type != MessageType.Welcome) return;

            switch (type)
            {
                case MessageType.Welcome:
                {
                    if (_welcomed) break;
                    var m = ReadMsg<WelcomeMsg>(r);
                    LocalId = m.YourId;
                    _clockSync.Seed(m.HostTime, Clock.Now, peer.Ping / 1000.0);
                    ClearPlayers();
                    foreach (var info in m.Players)
                        if (info.Id != LocalId) AddPlayer(info.Id, info.Name);
                    RunActive = m.RunActive;
                    RunSeed = m.RunSeed;
                    RoomId = m.RoomId;
                    RoomSequence = 0;
                    _welcomed = true;
                    _nextPing = 0;
                    Log.Info($"joined as #{LocalId}; {Players.Count} other player(s); run {(RunActive ? "in progress" : "not started")}");
                    Connected?.Invoke();
                    foreach (var p in Players.Values) RaiseJoined(p);
                    if (RunActive)
                    {
                        RaiseRunStarted(RunSeed);
                        // Mid-run join: no entry position, the adapter uses the room's own spawn.
                        RaiseRoomChanged(new RoomChangeMsg { Sequence = 0, RoomId = RoomId, X = float.NaN, Y = float.NaN });
                    }
                    break;
                }
                case MessageType.TimePong:
                {
                    var m = ReadMsg<TimePongMsg>(r);
                    _clockSync.AddSample(m.ClientTime, m.HostTime, Clock.Now);
                    break;
                }
                case MessageType.PingTable:
                {
                    var m = ReadMsg<PingTableMsg>(r);
                    for (var i = 0; i < m.Count; i++)
                    {
                        var p = FindPlayer(m.Ids[i]);
                        // Another guest is reached through the host: its leg plus ours.
                        if (p != null) p.PingMs = m.RttMs[i] + HostRttMs;
                    }
                    var host = FindPlayer(ProtocolInfo.HostPlayerId);
                    if (host != null) host.PingMs = HostRttMs;
                    break;
                }
                case MessageType.PlayerJoined:
                {
                    var m = ReadMsg<PlayerJoinedMsg>(r);
                    if (m.Player.Id == LocalId) break;
                    var p = AddPlayer(m.Player.Id, m.Player.Name);
                    Log.Info($"{p} joined");
                    RaiseJoined(p);
                    break;
                }
                case MessageType.PlayerLeft:
                {
                    var m = ReadMsg<PlayerLeftMsg>(r);
                    if (RemovePlayer(m.Id, out var p))
                    {
                        Log.Info($"{p} left ({m.Reason})");
                        RaiseLeft(p, m.Reason);
                    }
                    break;
                }
                case MessageType.PlayerLoadout:
                {
                    var m = ReadMsg<PlayerLoadoutMsg>(r);
                    if (m.Id != LocalId) ApplyLoadout(m);
                    break;
                }
                case MessageType.PlayerState:
                {
                    var m = ReadMsg<PlayerStateMsg>(r);
                    if (m.Id != LocalId) ApplyState(m);
                    break;
                }
                case MessageType.PlayerAction:
                {
                    var m = ReadMsg<PlayerActionMsg>(r);
                    if (m.Id != LocalId) ApplyAction(m);
                    break;
                }
                case MessageType.PlayerDied:
                case MessageType.PlayerRevived:
                {
                    var m = ReadMsg<PlayerLifeMsg>(r);
                    m.Alive = type == MessageType.PlayerRevived;
                    if (m.Id != LocalId) ApplyLife(m);
                    break;
                }
                case MessageType.RunStart:
                {
                    var m = ReadMsg<RunStartMsg>(r);
                    RunActive = true;
                    RunSeed = m.Seed;
                    RoomId = 0;
                    RoomSequence = 0;
                    ResetLives();
                    RaiseRunStarted(m.Seed);
                    break;
                }
                case MessageType.RoomChange:
                {
                    var m = ReadMsg<RoomChangeMsg>(r);
                    if (m.Sequence <= RoomSequence) break; // stale
                    RoomSequence = m.Sequence;
                    RoomId = m.RoomId;
                    RaiseRoomChanged(m);
                    break;
                }
                case MessageType.RunEnd:
                {
                    var m = ReadMsg<RunEndMsg>(r);
                    RunActive = false;
                    RaiseRunEnded(m.Reason);
                    break;
                }
                case MessageType.EnemySnapshot:
                {
                    var m = ReadMsg<EnemySnapshotMsg>(r);
                    if (m.RoomId == RoomId) RaiseEnemySnapshot(m);
                    break;
                }
                case MessageType.EnemyDied:
                {
                    var m = ReadMsg<EnemyDiedMsg>(r);
                    if (m.RoomId == RoomId) RaiseEnemyDied(m);
                    break;
                }
                default:
                    Log.Warn($"host sent unexpected {type}");
                    break;
            }
        }
    }
}
