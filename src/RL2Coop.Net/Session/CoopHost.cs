using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Sync;

namespace RL2Coop.Net.Session
{
    /// <summary>
    /// The host: owns the run. It picks the seed, leads room changes, runs the
    /// only real copy of every enemy and relays player traffic between guests
    /// (star topology, so guests only ever need to reach one address).
    /// </summary>
    public sealed class CoopHost : CoopSession
    {
        /// <summary>Per-connection bookkeeping, stored in <see cref="NetPeer.Tag"/>.</summary>
        private sealed class Slot
        {
            public byte Id;
            public string Name;
            public bool Joined; // welcome sent, other players told
        }

        private readonly Dictionary<byte, NetPeer> _peers = new Dictionary<byte, NetPeer>();
        private EnemySnapshotMsg _enemyPacket = new EnemySnapshotMsg { Enemies = new EnemyState[EnemySnapshotMsg.MaxPerPacket] };
        private double _nextPingTable;
        private bool _stopping;

        public CoopHost(CoopOptions options = null, IClock clock = null, ILog log = null)
            : base(options, clock, log)
        {
            LocalId = ProtocolInfo.HostPlayerId;
        }

        public override bool IsHost => true;
        public override bool IsConnected => Net.IsRunning;
        public override double HostNow => Clock.Now;

        /// <summary>UDP port actually bound (useful when started on port 0).</summary>
        public int Port => Net.LocalPort;

        /// <summary>
        /// A guest says its attack hit an enemy. Already checked for the right
        /// room and a sane damage value; the adapter applies it to the enemy.
        /// </summary>
        public event Action<HitRequestMsg> HitRequested;

        /// <summary>
        /// A guest walked into another room. The adapter decides whether the
        /// party follows; if so it moves the host and calls <see cref="ChangeRoom"/>.
        /// </summary>
        public event Action<RoomRequestMsg> RoomRequested;

        public bool Start(int port = ProtocolInfo.DefaultPort)
        {
            if (!Net.Start(port))
            {
                Log.Warn($"could not listen on UDP port {port} (already in use?)");
                return false;
            }
            Log.Info($"hosting on UDP port {Net.LocalPort}");
            return true;
        }

        /// <summary>Close the session; every guest is told the host left.</summary>
        public void Stop()
        {
            if (!Net.IsRunning) return;
            _stopping = true;
            var w = new NetDataWriter();
            w.Put((byte)LeaveReason.HostClosed);
            foreach (var peer in _peers.Values) peer.Disconnect(w);
            Net.PollEvents();
            Net.Stop(true);
            _peers.Clear();
            ClearPlayers();
            _stopping = false;
        }

        public override void Dispose() => Stop();

        /// <summary>Remove a guest from the session.</summary>
        public void Kick(byte id)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            var w = new NetDataWriter();
            w.Put((byte)LeaveReason.Kicked);
            peer.Disconnect(w);
        }

        /// <summary>Current round trip to a guest in milliseconds, or -1.</summary>
        public int RttTo(byte id) => _peers.TryGetValue(id, out var p) ? p.RoundTripTime : -1;

        // ------------------------------------------------------------------
        // run control (host only)

        /// <summary>Begin a run: everyone generates the castle from <paramref name="seed"/>.</summary>
        public void StartRun(int seed)
        {
            RunActive = true;
            RunSeed = seed;
            RoomId = 0;
            RoomSequence = 0;
            ResetLives();
            SendAll(new RunStartMsg { Seed = seed }, Channels.Control, DeliveryMethod.ReliableOrdered);
            Log.Info($"run started, seed {seed}");
        }

        /// <summary>The host entered a room; guests follow and appear at (x, y).</summary>
        public void ChangeRoom(int roomId, float x, float y)
        {
            RoomSequence++;
            RoomId = roomId;
            SendAll(new RoomChangeMsg { Sequence = RoomSequence, RoomId = roomId, X = x, Y = y },
                Channels.Control, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>End the run for everyone.</summary>
        public void EndRun(RunEndReason reason)
        {
            if (!RunActive) return;
            RunActive = false;
            SendAll(new RunEndMsg { Reason = reason }, Channels.Control, DeliveryMethod.ReliableOrdered);
            Log.Info($"run ended: {reason}");
            RaiseRunEnded(reason);
        }

        /// <summary>
        /// Publish the state of every enemy in the current room. Call ~20 times
        /// a second; large rooms are split into several packets.
        /// </summary>
        public void BroadcastEnemies(IReadOnlyList<EnemyState> enemies)
        {
            if (_peers.Count == 0) return;
            _enemyPacket.Time = HostNow;
            _enemyPacket.RoomId = RoomId;
            var i = 0;
            do
            {
                var n = Math.Min(EnemySnapshotMsg.MaxPerPacket, enemies.Count - i);
                for (var k = 0; k < n; k++) _enemyPacket.Enemies[k] = enemies[i + k];
                _enemyPacket.Count = n;
                SendAll(_enemyPacket, Channels.Enemies, DeliveryMethod.Sequenced);
                i += n;
            } while (i < enemies.Count);
        }

        /// <summary>An enemy died on the host (whoever landed the blow).</summary>
        public void EnemyKilled(ushort netId, byte killerId)
            => SendAll(new EnemyDiedMsg { RoomId = RoomId, NetId = netId, KillerId = killerId },
                Channels.Control, DeliveryMethod.ReliableOrdered);

        // ------------------------------------------------------------------

        protected override void SendFromLocal<T>(T msg, byte channel, DeliveryMethod method)
            => SendAll(msg, channel, method);

        protected override void OnLocalLifeChanged() => CheckAllDead();

        protected override void OnUpdate()
        {
            var now = Clock.Now;
            if (now < _nextPingTable || _peers.Count == 0) return;
            _nextPingTable = now + 1.0;
            DropSilentPeers();
            if (_peers.Count == 0) return;

            var table = new PingTableMsg { Ids = new byte[_peers.Count], RttMs = new ushort[_peers.Count] };
            foreach (var kv in _peers)
            {
                var rtt = kv.Value.RoundTripTime;
                table.Ids[table.Count] = kv.Key;
                table.RttMs[table.Count] = (ushort)Math.Min(rtt, ushort.MaxValue);
                table.Count++;
                var p = FindPlayer(kv.Key);
                if (p != null) p.PingMs = rtt;
            }
            SendAll(table, Channels.Control, DeliveryMethod.Unreliable);
        }

        /// <summary>When nobody is left standing the run is over for the party.</summary>
        private void CheckAllDead()
        {
            if (!RunActive || LocalAlive) return;
            foreach (var p in Players.Values) if (p.Alive) return;
            EndRun(RunEndReason.AllPlayersDead);
        }

        private byte? FreeId()
        {
            for (byte id = 1; id < ProtocolInfo.MaxPlayers; id++)
                if (!_peers.ContainsKey(id)) return id;
            return null;
        }

        // ------------------------------------------------------------------
        // handshake

        public override void OnConnectionRequest(ConnectionRequest request)
        {
            string name;
            try
            {
                var magic = request.Data.GetString(16);
                var version = request.Data.GetUShort();
                name = request.Data.GetString(ProtocolInfo.MaxNameLength);
                if (magic != ProtocolInfo.Magic) { Reject(request, RejectReason.BadRequest, "not an RL2 Co-op client"); return; }
                if (version != ProtocolInfo.Version)
                {
                    Reject(request, RejectReason.VersionMismatch, $"host runs protocol {ProtocolInfo.Version}, you run {version}: update the mod");
                    return;
                }
            }
            catch (Exception)
            {
                Reject(request, RejectReason.BadRequest, "malformed request");
                return;
            }

            var id = FreeId();
            if (id == null) { Reject(request, RejectReason.SessionFull, $"session is full ({ProtocolInfo.MaxPlayers} players)"); return; }

            var peer = request.Accept();
            peer.Tag = new Slot { Id = id.Value, Name = SanitizeName(name, id.Value) };
            _peers[id.Value] = peer;
        }

        private void Reject(ConnectionRequest request, RejectReason reason, string message)
        {
            Log.Info($"rejected {request.RemoteEndPoint}: {message}");
            var w = new NetDataWriter();
            w.Put((byte)reason);
            w.Put(message, 200);
            request.Reject(w);
        }

        private static string SanitizeName(string name, byte id)
        {
            name = (name ?? string.Empty).Trim();
            var clean = new System.Text.StringBuilder(name.Length);
            foreach (var c in name) if (!char.IsControl(c)) clean.Append(c);
            return clean.Length == 0 ? $"Player {id + 1}" : clean.ToString();
        }

        public override void OnPeerConnected(NetPeer peer)
        {
            if (!(peer.Tag is Slot slot)) return;

            // Tell the newcomer who is here and where the run stands...
            var infos = new List<PlayerInfo> { new PlayerInfo { Id = LocalId, Name = Options.PlayerName } };
            foreach (var p in Players.Values) infos.Add(new PlayerInfo { Id = p.Id, Name = p.Name });
            Send(peer, new WelcomeMsg
            {
                YourId = slot.Id,
                HostTime = HostNow,
                RunActive = RunActive,
                RunSeed = RunSeed,
                RoomId = RoomId,
                Players = infos.ToArray(),
            }, Channels.Control, DeliveryMethod.ReliableOrdered);

            // ...what everyone looks like, and who is already dead.
            if (LocalLoadout.HasValue) Send(peer, LocalLoadout.Value, Channels.Control, DeliveryMethod.ReliableOrdered);
            if (!LocalAlive) Send(peer, new PlayerLifeMsg { Id = LocalId, Alive = false }, Channels.Control, DeliveryMethod.ReliableOrdered);
            foreach (var p in Players.Values)
            {
                if (p.Loadout.HasValue) Send(peer, p.Loadout.Value, Channels.Control, DeliveryMethod.ReliableOrdered);
                if (!p.Alive) Send(peer, new PlayerLifeMsg { Id = p.Id, Alive = false }, Channels.Control, DeliveryMethod.ReliableOrdered);
            }

            // Then tell the others about the newcomer.
            var joined = AddPlayer(slot.Id, slot.Name);
            joined.PingMs = peer.RoundTripTime;
            slot.Joined = true;
            SendAll(new PlayerJoinedMsg { Player = new PlayerInfo { Id = slot.Id, Name = slot.Name } },
                Channels.Control, DeliveryMethod.ReliableOrdered, peer);
            Log.Info($"{joined} joined from {peer}");
            RaiseJoined(joined);
        }

        public override void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (_stopping) { peer.Tag = null; return; }
            var reason = info.Reason == DisconnectReason.Timeout ? LeaveReason.TimedOut
                : info.Reason == DisconnectReason.DisconnectPeerCalled ? LeaveReason.Kicked
                : LeaveReason.Quit;
            DropPeer(peer, reason, info.Reason.ToString());
        }

        /// <summary>Forget a guest's connection and tell everyone the player left.</summary>
        private void DropPeer(NetPeer peer, LeaveReason reason, string why)
        {
            if (!(peer.Tag is Slot slot)) return;
            peer.Tag = null;
            if (_peers.TryGetValue(slot.Id, out var known) && known == peer) _peers.Remove(slot.Id);
            if (!slot.Joined || !RemovePlayer(slot.Id, out var player)) return;

            SendAll(new PlayerLeftMsg { Id = slot.Id, Reason = reason }, Channels.Control, DeliveryMethod.ReliableOrdered);
            Log.Info($"{player} left ({why})");
            RaiseLeft(player, reason);
            CheckAllDead();
        }

        private readonly List<NetPeer> _silent = new List<NetPeer>();

        /// <summary>
        /// A guest whose game quit was once kept on the host for good: the
        /// connection went quiet but no disconnect event ever came, so the slot
        /// stayed taken and the shared fight waited for a player who was gone.
        /// Anyone silent for longer than the timeout is dropped here as well.
        /// </summary>
        private void DropSilentPeers()
        {
            var limit = Net.DisconnectTimeout + 2000;
            foreach (var peer in _peers.Values)
                if (peer.TimeSinceLastPacket > limit)
                    _silent.Add(peer);
            foreach (var peer in _silent)
            {
                Log.Warn($"dropping a silent connection from {peer} (state {peer.ConnectionState}, quiet {peer.TimeSinceLastPacket} ms)");
                DropPeer(peer, LeaveReason.TimedOut, "silent, no disconnect event");
                Net.DisconnectPeerForce(peer);
            }
            _silent.Clear();
        }

        // ------------------------------------------------------------------
        // traffic from guests

        protected override void Dispatch(NetPeer peer, MessageType type, NetDataReader r)
        {
            if (!(peer.Tag is Slot slot) || !slot.Joined) return;
            var id = slot.Id;

            switch (type)
            {
                case MessageType.TimePing:
                {
                    var ping = ReadMsg<TimePingMsg>(r);
                    Send(peer, new TimePongMsg { ClientTime = ping.ClientTime, HostTime = HostNow },
                        Channels.Control, DeliveryMethod.Unreliable);
                    break;
                }
                case MessageType.PlayerState:
                {
                    var s = ReadMsg<PlayerStateMsg>(r);
                    s.Id = id; // never trust the sender's claim about who it is
                    var now = HostNow;
                    if (double.IsNaN(s.Time) || s.Time > now + 0.5 || s.Time < now - 5) s.Time = now;
                    ApplyState(s);
                    SendAll(s, Channels.PlayerState(id), DeliveryMethod.Sequenced, peer);
                    break;
                }
                case MessageType.PlayerLoadout:
                {
                    var m = ReadMsg<PlayerLoadoutMsg>(r);
                    m.Id = id;
                    ApplyLoadout(m);
                    SendAll(m, Channels.Control, DeliveryMethod.ReliableOrdered, peer);
                    break;
                }
                case MessageType.PlayerAction:
                {
                    var m = ReadMsg<PlayerActionMsg>(r);
                    m.Id = id;
                    ApplyAction(m);
                    SendAll(m, Channels.Control, DeliveryMethod.ReliableOrdered, peer);
                    break;
                }
                case MessageType.PlayerDied:
                case MessageType.PlayerRevived:
                {
                    var m = ReadMsg<PlayerLifeMsg>(r);
                    m.Id = id;
                    m.Alive = type == MessageType.PlayerRevived;
                    if (!ApplyLife(m)) break;
                    SendAll(m, Channels.Control, DeliveryMethod.ReliableOrdered, peer);
                    if (!m.Alive) CheckAllDead();
                    break;
                }
                case MessageType.HitRequest:
                {
                    var m = ReadMsg<HitRequestMsg>(r);
                    m.AttackerId = id;
                    if (!RunActive || m.RoomId != RoomId) break; // late hit from the previous room
                    if (!IsFinite(m.Damage) || m.Damage <= 0 || m.Damage > Options.MaxHitDamage) break;
                    if (!IsFinite(m.KnockX) || !IsFinite(m.KnockY)) { m.KnockX = 0; m.KnockY = 0; }
                    var attacker = FindPlayer(id);
                    if (attacker != null && !attacker.Alive) break; // the dead do not swing
                    HitRequested?.Invoke(m);
                    break;
                }
                case MessageType.RoomRequest:
                {
                    var m = ReadMsg<RoomRequestMsg>(r);
                    m.RequesterId = id;
                    if (!RunActive || m.RoomId == RoomId) break;
                    if (!IsFinite(m.X) || !IsFinite(m.Y)) break;
                    RoomRequested?.Invoke(m);
                    break;
                }
                default:
                    Log.Warn($"guest {id} sent unexpected {type}");
                    break;
            }
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
