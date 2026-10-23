using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;
using RL2Coop.Net.Sync;
using Xunit;

namespace RL2Coop.Net.Tests
{
    public class SessionTests
    {
        [Fact]
        public void Guest_joins_and_both_sides_know_each_other()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Bob");

            Assert.Equal(1, g.LocalId);
            Assert.Equal("Hosty", g.Players[ProtocolInfo.HostPlayerId].Name);
            Assert.Equal("Bob", lb.Host.Players[1].Name);
        }

        [Fact]
        public void Second_guest_learns_about_first_and_first_about_second()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");

            Assert.True(lb.PumpUntil(() => a.Players.ContainsKey(b.LocalId)));
            Assert.Equal("Ann", b.Players[a.LocalId].Name);
            Assert.Equal(2, b.LocalId);
            Assert.Equal(2, b.Players.Count); // host and Ann; never himself
        }

        [Fact]
        public void Fifth_player_is_turned_away_with_a_readable_reason()
        {
            using var lb = new Loopback();
            lb.AddGuest("g1"); lb.AddGuest("g2"); lb.AddGuest("g3");
            var late = lb.AddGuest("g4", waitForWelcome: false);
            GuestEndReason? reason = null; string msg = null;
            late.Disconnected += (r, m) => { reason = r; msg = m; };

            Assert.True(lb.PumpUntil(() => reason.HasValue));
            Assert.Equal(GuestEndReason.Rejected, reason);
            Assert.Contains("full", msg);
            Assert.Equal(3, lb.Host.Players.Count);
        }

        [Fact]
        public void Wrong_protocol_version_is_rejected_before_any_game_data_flows()
        {
            using var lb = new Loopback();
            var listener = new EventBasedNetListener();
            DisconnectInfo? info = null;
            byte rejectCode = 255;
            listener.PeerDisconnectedEvent += (p, i) =>
            {
                info = i;
                if (i.AdditionalData != null && i.AdditionalData.TryGetByte(out var b)) rejectCode = b;
            };
            var raw = new NetManager(listener) { ChannelsCount = Channels.Count };
            raw.Start();
            var w = new NetDataWriter();
            w.Put(ProtocolInfo.Magic, 16); w.Put((ushort)999); w.Put("old", 24);
            raw.Connect("127.0.0.1", lb.Port, w);
            try
            {
                Assert.True(lb.PumpUntil(() => { raw.PollEvents(); return info.HasValue; }));
                Assert.Equal(DisconnectReason.ConnectionRejected, info.Value.Reason);
                Assert.Equal((byte)RejectReason.VersionMismatch, rejectCode);
                Assert.Empty(lb.Host.Players);
            }
            finally { raw.Stop(); }
        }

        [Fact]
        public void Garbage_packets_are_dropped_and_the_session_keeps_working()
        {
            using var lb = new Loopback();
            var listener = new EventBasedNetListener();
            var raw = new NetManager(listener) { ChannelsCount = Channels.Count };
            raw.Start();
            var w = new NetDataWriter();
            w.Put(ProtocolInfo.Magic, 16); w.Put(ProtocolInfo.Version); w.Put("evil", 24);
            var peer = raw.Connect("127.0.0.1", lb.Port, w);
            try
            {
                Assert.True(lb.PumpUntil(() => { raw.PollEvents(); return lb.Host.Players.Count == 1; }));
                peer.Send(new byte[] { (byte)MessageType.PlayerState, 1 }, Channels.Control, DeliveryMethod.ReliableOrdered); // truncated
                peer.Send(new byte[] { (byte)MessageType.Welcome, 9, 9 }, Channels.Control, DeliveryMethod.ReliableOrdered);     // wrong direction
                peer.Send(new byte[] { 200 }, Channels.Control, DeliveryMethod.ReliableOrdered);                                   // unknown type
                Assert.True(lb.PumpUntil(() => { raw.PollEvents(); return lb.HostLog.Warnings.Count >= 3; }));

                var g = lb.AddGuest("Fine"); // host still serves real players
                Assert.True(g.IsConnected);
            }
            finally { raw.Stop(); }
        }

        [Fact]
        public void Player_movement_reaches_host_and_is_relayed_to_other_guests()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");
            Assert.True(lb.PumpUntil(() => a.Players.ContainsKey(b.LocalId)));

            Assert.True(lb.PumpUntil(() =>
            {
                a.SendLocalState(new PlayerStateMsg { X = 12.5f, Y = -3f, Flags = PlayerFlags.FacingRight, Id = 99 /* lie: ignored */ });
                lb.Host.SendLocalState(new PlayerStateMsg { X = 1f, Y = 2f });
                return lb.Host.Players[a.LocalId].States.Count > 0
                    && b.Players[a.LocalId].States.Count > 0
                    && b.Players[0].States.Count > 0
                    && a.Players[0].States.Count > 0;
            }));

            var seenByBen = b.Players[a.LocalId].Latest;
            Assert.Equal(a.LocalId, seenByBen.Id);
            Assert.Equal(12.5f, seenByBen.X);
            Assert.True(seenByBen.Flags.HasFlag(PlayerFlags.FacingRight));
            Assert.Equal(1f, a.Players[0].Latest.X);
            Assert.False(b.Players.ContainsKey(99));
        }

        [Fact]
        public void Guest_clock_converges_on_the_host_timeline()
        {
            using var lb = new Loopback();
            lb.PumpFor(50); // let the host clock run ahead so the guest's offset is non-trivial
            var g = lb.AddGuest("Clock");
            lb.PumpFor(1500);

            Assert.True(g.ClockSync.HasSample);
            var error = Math.Abs(g.HostNow - lb.Host.HostNow);
            Assert.True(error < 0.02, $"clock error {error * 1000:F1} ms");
        }

        [Fact]
        public void Run_seed_and_room_reach_guests_and_late_joiners()
        {
            using var lb = new Loopback();
            var early = lb.AddGuest("Early");
            int? seed = null;
            early.RunStarted += s => seed = s;
            lb.Host.StartRun(424242);
            Assert.True(lb.PumpUntil(() => seed.HasValue));
            Assert.Equal(424242, seed);

            var rooms = new List<RoomChangeMsg>();
            early.RoomChanged += rooms.Add;
            lb.Host.ChangeRoom(17, 3.5f, -1f);
            Assert.True(lb.PumpUntil(() => rooms.Count == 1));
            Assert.Equal(17, rooms[0].RoomId);
            Assert.Equal(3.5f, rooms[0].X);
            Assert.Equal(17, early.RoomId);

            // Someone joining mid-run lands in the same castle and room.
            int? lateSeed = null; RoomChangeMsg? lateRoom = null;
            var late = new CoopGuest(new CoopOptions { PlayerName = "Late" });
            late.RunStarted += s => lateSeed = s;
            late.RoomChanged += r => lateRoom = r;
            lb.Guests.Add(late);
            late.Connect("127.0.0.1", lb.Port);
            Assert.True(lb.PumpUntil(() => lateRoom.HasValue));
            Assert.Equal(424242, lateSeed);
            Assert.Equal(17, lateRoom.Value.RoomId);
            Assert.True(float.IsNaN(lateRoom.Value.X));
        }

        [Fact]
        public void Guest_door_request_goes_to_host_which_leads_the_party()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Scout");
            lb.Host.StartRun(1);
            lb.Host.ChangeRoom(5, 0, 0);
            Assert.True(lb.PumpUntil(() => g.RoomId == 5));

            RoomRequestMsg? req = null;
            lb.Host.RoomRequested += r => { req = r; lb.Host.ChangeRoom(r.RoomId, r.X, r.Y); };
            g.RequestRoom(6, 10, 20);
            Assert.True(lb.PumpUntil(() => g.RoomId == 6));
            Assert.Equal(g.LocalId, req.Value.RequesterId);
            Assert.Equal(6, lb.Host.RoomId);
        }

        [Fact]
        public void Guest_hits_are_sent_to_the_host_and_validated()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Knight");
            lb.Host.StartRun(7);
            lb.Host.ChangeRoom(3, 0, 0);
            Assert.True(lb.PumpUntil(() => g.RoomId == 3));

            var hits = new List<HitRequestMsg>();
            lb.Host.HitRequested += hits.Add;
            g.RequestHit(4, 55f, critical: true, knockX: 1f);
            g.RequestHit(5, float.NaN, false);   // corrupt: dropped
            g.RequestHit(6, -10f, false);        // negative: dropped
            g.RequestHit(7, 1e12f, false);       // absurd: dropped
            g.RequestHit(8, 12f, false);
            Assert.True(lb.PumpUntil(() => hits.Count >= 2));
            lb.PumpFor(100);

            Assert.Equal(new ushort[] { 4, 8 }, hits.Select(h => h.EnemyNetId).ToArray());
            Assert.All(hits, h => Assert.Equal(g.LocalId, h.AttackerId));
            Assert.Equal(55f, hits[0].Damage);
            Assert.True(hits[0].Critical);
        }

        [Fact]
        public void Hits_aimed_at_the_previous_room_are_ignored()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Slow");
            lb.Host.StartRun(7);
            lb.Host.ChangeRoom(3, 0, 0);
            Assert.True(lb.PumpUntil(() => g.RoomId == 3));
            lb.Host.ChangeRoom(4, 0, 0); // host moves on; guest has not heard yet
            var hits = 0;
            lb.Host.HitRequested += _ => hits++;
            g.RequestHit(1, 10f, false); // still stamped room 3
            lb.PumpFor(150);
            Assert.Equal(0, hits);
        }

        [Fact]
        public void Enemy_snapshots_of_a_big_room_arrive_whole_and_deaths_are_reliable()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Watcher");
            lb.Host.StartRun(9);
            lb.Host.ChangeRoom(2, 0, 0);
            Assert.True(lb.PumpUntil(() => g.RoomId == 2));

            var mirror = new EnemyMirror();
            mirror.Reset(2);
            g.EnemySnapshotReceived += m => mirror.Apply(m);
            var died = new List<EnemyDiedMsg>();
            g.EnemyDied += died.Add;

            var enemies = Enumerable.Range(0, 100).Select(i => new EnemyState
            {
                NetId = (ushort)i, X = i, Y = -i, Hp = 100 - i, MaxHp = 100, Flags = EnemyFlags.Active,
            }).ToList();
            Assert.True(lb.PumpUntil(() => { lb.Host.BroadcastEnemies(enemies); return mirror.Count == 100; }));
            Assert.True(mirror.TryGet(42, g.HostNow, out var e42));
            Assert.Equal(42f, e42.X);
            Assert.Equal(58, e42.Hp);

            lb.Host.EnemyKilled(42, killerId: g.LocalId);
            Assert.True(lb.PumpUntil(() => died.Count == 1));
            Assert.Equal(g.LocalId, died[0].KillerId);
            mirror.MarkDead(42);
            Assert.True(mirror.TryGet(42, g.HostNow, out e42));
            Assert.True(e42.Flags.HasFlag(EnemyFlags.Dead));
        }

        [Fact]
        public void Run_ends_only_when_every_player_is_dead()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");
            lb.Host.StartRun(5);
            var ended = new List<RunEndReason>();
            a.RunEnded += ended.Add;
            Assert.True(lb.PumpUntil(() => a.RunActive && b.RunActive));

            a.SetLocalAlive(false);
            lb.Host.SetLocalAlive(false);
            Assert.True(lb.PumpUntil(() => !b.Players[0].Alive && !lb.Host.Players[a.LocalId].Alive));
            lb.PumpFor(100);
            Assert.Empty(ended);
            Assert.True(lb.Host.RunActive);

            b.SetLocalAlive(false);
            Assert.True(lb.PumpUntil(() => ended.Count == 1));
            Assert.Equal(RunEndReason.AllPlayersDead, ended[0]);
            Assert.False(lb.Host.RunActive);
        }

        [Fact]
        public void Last_survivor_quitting_also_ends_the_run()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Leaver");
            lb.Host.StartRun(5);
            Assert.True(lb.PumpUntil(() => g.RunActive));
            var hostEnded = false;
            lb.Host.RunEnded += _ => hostEnded = true;
            lb.Host.SetLocalAlive(false);
            lb.PumpFor(50);
            Assert.False(hostEnded);
            g.Disconnect();
            Assert.True(lb.PumpUntil(() => hostEnded));
        }

        [Fact]
        public void Spectator_follows_a_living_player_and_skips_the_dead()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");
            var c = lb.AddGuest("Cid");
            lb.Host.StartRun(5);
            Assert.True(lb.PumpUntil(() => c.RunActive && c.Players.Count == 3));

            var spec = new Spectator();
            Assert.Equal((byte)0, spec.Update(c.Players));
            Assert.Equal((byte)1, spec.Cycle(c.Players, +1));
            Assert.Equal((byte)0, spec.Cycle(c.Players, -1));

            lb.Host.SetLocalAlive(false);
            Assert.True(lb.PumpUntil(() => !c.Players[0].Alive));
            Assert.Equal((byte)1, spec.Update(c.Players));
            a.SetLocalAlive(false); b.SetLocalAlive(false);
            Assert.True(lb.PumpUntil(() => !c.Players[1].Alive && !c.Players[2].Alive));
            Assert.Null(spec.Update(c.Players));
        }

        [Fact]
        public void Loadouts_and_deaths_are_replayed_to_late_joiners()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            lb.Host.StartRun(1);
            a.SendLocalLoadout(classId: 3, appearance: new byte[] { 1, 2, 3 });
            lb.Host.SendLocalLoadout(classId: 7, appearance: new byte[] { 9 });
            Assert.True(lb.PumpUntil(() => lb.Host.Players[a.LocalId].Loadout.HasValue && a.RunActive));
            a.SetLocalAlive(false);
            Assert.True(lb.PumpUntil(() => !lb.Host.Players[a.LocalId].Alive));

            var late = lb.AddGuest("Late");
            Assert.True(lb.PumpUntil(() => late.Players[a.LocalId].Loadout.HasValue && late.Players[0].Loadout.HasValue));
            Assert.Equal(3, late.Players[a.LocalId].Loadout.Value.ClassId);
            Assert.Equal(new byte[] { 1, 2, 3 }, late.Players[a.LocalId].Loadout.Value.Appearance);
            Assert.Equal(7, late.Players[0].Loadout.Value.ClassId);
            Assert.True(lb.PumpUntil(() => !late.Players[a.LocalId].Alive));
        }

        [Fact]
        public void Guest_leaving_is_announced_to_everyone()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");
            Assert.True(lb.PumpUntil(() => b.Players.ContainsKey(a.LocalId)));
            LeaveReason? seen = null;
            b.PlayerLeft += (p, r) => { if (p.Id == a.LocalId) seen = r; };

            GuestEndReason? own = null;
            a.Disconnected += (r, _) => own = r;
            a.Disconnect();
            Assert.True(lb.PumpUntil(() => seen.HasValue && !lb.Host.Players.ContainsKey(1)));
            Assert.Equal(LeaveReason.Quit, seen);
            Assert.Equal(GuestEndReason.LocalQuit, own);
            Assert.False(b.Players.ContainsKey(a.LocalId));
        }

        [Fact]
        public void Guest_quitting_the_game_is_dropped_by_the_host()
        {
            // Quitting the game runs Leave() and then shuts the network down in the same frame.
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            var b = lb.AddGuest("Ben");
            Assert.True(lb.PumpUntil(() => b.Players.ContainsKey(a.LocalId)));
            LeaveReason? seen = null;
            b.PlayerLeft += (p, r) => { if (p.Id == a.LocalId) seen = r; };

            a.Dispose();
            lb.Guests.Remove(a);
            Assert.True(lb.PumpUntil(() => seen.HasValue && !lb.Host.Players.ContainsKey(1)));
            Assert.Equal(LeaveReason.Quit, seen);
        }

        [Fact]
        public void Freed_slot_is_reused()
        {
            using var lb = new Loopback();
            var a = lb.AddGuest("Ann");
            lb.AddGuest("Ben");
            a.Disconnect();
            Assert.True(lb.PumpUntil(() => !lb.Host.Players.ContainsKey(1)));
            var c = lb.AddGuest("Cid");
            Assert.Equal(1, c.LocalId);
        }

        [Fact]
        public void Host_closing_tells_guests_why()
        {
            var lb = new Loopback();
            var g = lb.AddGuest("Bob");
            GuestEndReason? reason = null;
            g.Disconnected += (r, _) => reason = r;
            lb.Host.Stop();
            Assert.True(lb.PumpUntil(() => reason.HasValue));
            Assert.Equal(GuestEndReason.HostClosed, reason);
            Assert.Empty(g.Players);
            lb.Dispose();
        }

        [Fact]
        public void Kicked_guest_is_told_it_was_kicked()
        {
            using var lb = new Loopback();
            var g = lb.AddGuest("Troll");
            GuestEndReason? reason = null;
            g.Disconnected += (r, _) => reason = r;
            lb.Host.Kick(g.LocalId);
            Assert.True(lb.PumpUntil(() => reason.HasValue));
            Assert.Equal(GuestEndReason.Kicked, reason);
        }

        [Fact]
        public void Nobody_listening_gives_connection_failed()
        {
            var probe = new CoopHost();
            Assert.True(probe.Start(0));
            var deadPort = probe.Port;
            probe.Stop();

            var g = new CoopGuest(new CoopOptions { PlayerName = "Lonely", DisconnectTimeoutMs = 1000 });
            GuestEndReason? reason = null;
            g.Disconnected += (r, _) => reason = r;
            g.Connect("127.0.0.1", deadPort);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!reason.HasValue && sw.ElapsedMilliseconds < 8000) { g.Poll(); System.Threading.Thread.Sleep(5); }
            g.Dispose();
            Assert.Equal(GuestEndReason.ConnectionFailed, reason);
        }

        [Fact]
        public void Sessions_survive_lag_and_packet_loss()
        {
            var bad = new CoopOptions { SimulateLatencyMs = 80, SimulateLossPercent = 15 };
            using var lb = new Loopback(new CoopOptions { SimulateLatencyMs = 80, SimulateLossPercent = 15 }, bad);
            var g = lb.AddGuest("Wifi");
            int? seed = null;
            g.RunStarted += s => seed = s;
            lb.Host.StartRun(99);
            lb.Host.ChangeRoom(8, 0, 0);
            Assert.True(lb.PumpUntil(() => seed == 99 && g.RoomId == 8 && g.ClockReady, 5000));

            // A guest walking right at 5 u/s: the host's interpolated view stays smooth and close.
            var t0 = g.HostNow;
            var maxStep = 0f; float? last = null;
            Assert.True(lb.PumpUntil(() =>
            {
                var x = (float)(g.HostNow - t0) * 5f;
                g.SendLocalState(new PlayerStateMsg { X = x, VelX = 5f });
                var buf = lb.Host.Players[g.LocalId].States;
                if (Interp.Sample(buf, lb.Host.RenderTime, lb.Host.Interpolation, out var seen))
                {
                    if (last.HasValue) maxStep = Math.Max(maxStep, Math.Abs(seen.X - last.Value));
                    last = seen.X;
                }
                return g.HostNow - t0 > 2.0;
            }, 6000));
            Assert.True(last.HasValue);
            Assert.True(maxStep < 0.5f, $"jump of {maxStep} units in one frame");
        }
    }
}
