using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RL2Coop.Net.Game;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;
using Xunit;

namespace RL2Coop.Net.Tests
{
    /// <summary>A stand-in for the game: records what the co-op layer asked it to do.</summary>
    internal sealed class FakeWorld : IGameWorld
    {
        public PlayerStateMsg Me = new PlayerStateMsg { Hp = 100, MaxHp = 100, Flags = PlayerFlags.Grounded };
        public bool HasHero = true;
        public int ClassId = 3;
        public int? StartedSeed;
        public int Room = -1;
        public float RoomX, RoomY;
        public RunEndReason? Ended;
        public readonly Dictionary<byte, PlayerStateMsg> Puppets = new Dictionary<byte, PlayerStateMsg>();
        public readonly HashSet<byte> Shown = new HashSet<byte>();
        public readonly List<PlayerActionMsg> Actions = new List<PlayerActionMsg>();
        public byte? Watching;
        public readonly List<EnemyState> HostEnemies = new List<EnemyState>();
        public readonly Dictionary<ushort, EnemyState> Mirrored = new Dictionary<ushort, EnemyState>();
        public readonly HashSet<ushort> Killed = new HashSet<ushort>();
        public readonly List<HitRequestMsg> Hits = new List<HitRequestMsg>();

        public bool TryReadLocalPlayer(out PlayerStateMsg state) { state = Me; return HasHero; }
        public void ReadLocalLoadout(out int classId, out byte[] appearance) { classId = ClassId; appearance = new byte[] { 7 }; }
        public void StartRun(int seed) { StartedSeed = seed; Ended = null; }
        public void GoToRoom(int roomId, float x, float y) { Room = roomId; RoomX = x; RoomY = y; }
        public void EndRun(RunEndReason reason) => Ended = reason;
        public void ShowPuppet(RemotePlayer player) => Shown.Add(player.Id);
        public void MovePuppet(byte playerId, in PlayerStateMsg state) => Puppets[playerId] = state;
        public void PlayPuppetAction(byte playerId, in PlayerActionMsg action) => Actions.Add(action);
        public void HidePuppet(byte playerId) { Shown.Remove(playerId); Puppets.Remove(playerId); }
        public void Spectate(byte? playerId) => Watching = playerId;
        public void ReadEnemies(List<EnemyState> into) => into.AddRange(HostEnemies);
        public void MoveEnemy(in EnemyState state) => Mirrored[state.NetId] = state;
        public void KillEnemy(ushort netId) => Killed.Add(netId);
        public void ApplyHit(in HitRequestMsg hit) => Hits.Add(hit);
    }

    /// <summary>A host and any number of guests, each with its own fake game, over real UDP on 127.0.0.1.</summary>
    internal sealed class Table : IDisposable
    {
        public readonly FakeWorld HostWorld = new FakeWorld();
        public readonly CoopController Host;
        public readonly List<(CoopController c, FakeWorld w)> Guests = new List<(CoopController, FakeWorld)>();
        public readonly List<string> HostNotices = new List<string>();

        public Table(bool guestsCanLead = false)
        {
            Host = new CoopController(HostWorld, new TestLog("host")) { GuestsCanLead = guestsCanLead };
            Host.Notice += HostNotices.Add;
            Assert.True(Host.StartHost(new CoopOptions { PlayerName = "Hosty" }, 0));
        }

        public (CoopController c, FakeWorld w) Join(string name, bool guestsCanLead = false)
        {
            var w = new FakeWorld();
            var c = new CoopController(w, new TestLog(name)) { GuestsCanLead = guestsCanLead };
            Guests.Add((c, w));
            c.Join(new CoopOptions { PlayerName = name }, "127.0.0.1", Host.Host.Port);
            Assert.True(Until(() => c.Mode == CoopMode.Joined && Host.Session.Players.ContainsKey(c.Session.LocalId)), $"{name} never joined");
            return (c, w);
        }

        public bool Until(Func<bool> condition, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Host.Tick();
                foreach (var g in Guests) g.c.Tick();
                if (condition()) return true;
                Thread.Sleep(2);
            }
            return condition();
        }

        public void For(int ms) => Until(() => false, ms);

        public void Dispose()
        {
            foreach (var g in Guests) g.c.Dispose();
            Host.Dispose();
        }
    }

    public class ControllerTests
    {
        private static void StartRunInRoom(Table t, int seed, int room)
        {
            t.Host.LocalRunStarted(seed);
            t.Host.LocalEnteredRoom(room, 10, 2);
        }

        [Fact]
        public void Guests_follow_the_hosts_seed_and_rooms()
        {
            using var t = new Table();
            var (_, w) = t.Join("Gus");
            StartRunInRoom(t, 777, 3);
            Assert.True(t.Until(() => w.StartedSeed == 777 && w.Room == 3));
            Assert.Equal(10f, w.RoomX);

            t.Host.LocalEnteredRoom(4, -5, 0);
            Assert.True(t.Until(() => w.Room == 4 && w.RoomX == -5));
        }

        [Fact]
        public void A_guest_cannot_open_a_room_on_its_own()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            var notices = new List<string>();
            g.Notice += notices.Add;
            StartRunInRoom(t, 1, 3);
            Assert.True(t.Until(() => w.Room == 3));

            Assert.False(g.LocalWantsRoom(9, 0, 0));
            Assert.Contains(notices, n => n.Contains("Only the host"));
            Assert.True(g.LocalWantsRoom(3, 0, 0)); // the host's room is always fine
            Assert.True(t.Host.LocalWantsRoom(9, 0, 0));
            t.For(150);
            Assert.Equal(-1, t.HostWorld.Room);
            Assert.Equal(3, t.Host.Session.RoomId);
        }

        [Fact]
        public void When_guests_may_lead_everyone_moves_to_the_guests_room()
        {
            using var t = new Table(guestsCanLead: true);
            var (g, w) = t.Join("Gus", guestsCanLead: true);
            StartRunInRoom(t, 1, 3);
            Assert.True(t.Until(() => w.Room == 3));

            Assert.False(g.LocalWantsRoom(9, 4, 5)); // not yet: the host confirms first
            Assert.True(t.Until(() => t.HostWorld.Room == 9 && w.Room == 9));
            Assert.Equal(9, t.Host.Session.RoomId);
        }

        [Fact]
        public void Heroes_appear_as_puppets_on_each_others_screens()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            var gid = g.Session.LocalId;
            w.Me.X = 5; w.Me.Y = 1; w.Me.Flags |= PlayerFlags.FacingRight;
            t.HostWorld.Me.X = -3;
            Assert.True(t.Until(() =>
                t.HostWorld.Puppets.TryGetValue(gid, out var p) && Math.Abs(p.X - 5) < 0.01f &&
                w.Puppets.TryGetValue(ProtocolInfo.HostPlayerId, out var h) && Math.Abs(h.X + 3) < 0.01f));
            Assert.True((t.HostWorld.Puppets[gid].Flags & PlayerFlags.FacingRight) != 0);
            Assert.Contains(gid, t.HostWorld.Shown);

            g.LocalAction(1, 42, 5, 1, true);
            Assert.True(t.Until(() => t.HostWorld.Actions.Count == 1));
            Assert.Equal(42, t.HostWorld.Actions[0].Param);
        }

        [Fact]
        public void A_puppet_in_another_room_is_hidden()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            var gid = g.Session.LocalId;
            StartRunInRoom(t, 1, 3);
            Assert.True(t.Until(() => t.HostWorld.Shown.Contains(gid) && w.Room == 3));

            // A guest whose game has not caught up yet still reports from its old room: no ghost in ours.
            w.HasHero = false;
            t.Host.LocalEnteredRoom(4, 0, 0);
            Assert.True(t.Until(() => !t.HostWorld.Shown.Contains(gid)));
            w.HasHero = true;
            Assert.True(t.Until(() => t.HostWorld.Shown.Contains(gid)));
        }

        [Fact]
        public void Guests_see_the_hosts_enemies_and_their_movement()
        {
            using var t = new Table();
            var (_, w) = t.Join("Gus");
            StartRunInRoom(t, 1, 3);
            t.HostWorld.HostEnemies.Add(new EnemyState { NetId = 0, X = 1, Hp = 50, MaxHp = 50, Flags = EnemyFlags.Active });
            t.HostWorld.HostEnemies.Add(new EnemyState { NetId = 1, X = 2, Hp = 900, MaxHp = 900, Flags = EnemyFlags.Active | EnemyFlags.Boss });
            Assert.True(t.Until(() => w.Mirrored.Count == 2));
            Assert.True((w.Mirrored[1].Flags & EnemyFlags.Boss) != 0);

            t.HostWorld.HostEnemies[0] = new EnemyState { NetId = 0, X = 4, Hp = 20, MaxHp = 50, Flags = EnemyFlags.Active };
            Assert.True(t.Until(() => Math.Abs(w.Mirrored[0].X - 4) < 0.01f && w.Mirrored[0].Hp == 20));
        }

        [Fact]
        public void A_guests_hit_is_decided_by_the_host()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            StartRunInRoom(t, 1, 3);
            Assert.True(t.Until(() => w.Room == 3));

            Assert.False(g.LocalHitsEnemy(1, 25, true, 2, 0)); // the guest's game must not apply it
            Assert.True(t.Host.LocalHitsEnemy(1, 10, false));  // the host's game does
            Assert.True(t.Until(() => t.HostWorld.Hits.Count == 1));
            var hit = t.HostWorld.Hits[0];
            Assert.Equal(g.Session.LocalId, hit.AttackerId);
            Assert.Equal(1, hit.EnemyNetId);
            Assert.Equal(25f, hit.Damage);
            Assert.True(hit.Critical);

            t.Host.LocalEnemyKilled(1, hit.AttackerId);
            Assert.True(t.Until(() => w.Killed.Contains(1)));
        }

        [Fact]
        public void A_dead_hero_watches_a_living_one_and_the_run_ends_when_all_fall()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            var (g2, w2) = t.Join("Ann");
            StartRunInRoom(t, 1, 3);
            Assert.True(t.Until(() => w.Room == 3 && w2.Room == 3));

            g.LocalDied();
            Assert.True(t.Until(() => w.Watching == ProtocolInfo.HostPlayerId));
            Assert.False(g.LocalHitsEnemy(0, 5, false));
            t.For(100);
            Assert.Empty(t.HostWorld.Hits); // the dead do not hit

            g.CycleSpectate(+1);
            Assert.Equal(g2.Session.LocalId, w.Watching);

            g2.LocalDied();
            Assert.True(t.Until(() => w.Watching == ProtocolInfo.HostPlayerId && w2.Watching == ProtocolInfo.HostPlayerId));
            Assert.Null(t.HostWorld.Ended);

            t.Host.LocalDied();
            Assert.True(t.Until(() => t.HostWorld.Ended == RunEndReason.AllPlayersDead
                                      && w.Ended == RunEndReason.AllPlayersDead
                                      && w2.Ended == RunEndReason.AllPlayersDead));

            t.Host.LocalRunStarted(2); // next heir: everyone is back and watching themselves
            Assert.True(t.Until(() => w.StartedSeed == 2 && w.Watching == null));
            Assert.True(g.Session.LocalAlive);
        }

        [Fact]
        public void A_late_joiner_is_dropped_into_the_running_castle()
        {
            using var t = new Table();
            StartRunInRoom(t, 4242, 7);
            var (_, w) = t.Join("Late");
            Assert.True(t.Until(() => w.StartedSeed == 4242 && w.Room == 7));
            Assert.True(float.IsNaN(w.RoomX)); // the room's own entrance, not the host's door
        }

        [Fact]
        public void Leaving_and_host_shutdown_clean_up_both_sides()
        {
            using var t = new Table();
            var (g, w) = t.Join("Gus");
            var (g2, w2) = t.Join("Ann");
            var gid = g.Session.LocalId;
            Assert.True(t.Until(() => t.HostWorld.Shown.Contains(gid) && w2.Shown.Contains(gid)));

            string ended = null;
            g.Ended += r => ended = r;
            g.Leave();
            Assert.Equal(CoopMode.Offline, g.Mode);
            Assert.NotNull(ended);
            Assert.Empty(w.Shown);
            Assert.True(t.Until(() => !t.HostWorld.Shown.Contains(gid) && !w2.Shown.Contains(gid)));
            Assert.Contains(t.HostNotices, n => n.Contains("Gus left"));

            string ended2 = null;
            g2.Ended += r => ended2 = r;
            t.Host.Leave();
            Assert.True(t.Until(() => ended2 != null));
            Assert.Contains("closed", ended2);
            Assert.Empty(w2.Shown);
            Assert.Equal(CoopMode.Offline, g2.Mode);
        }

        [Fact]
        public void A_guest_that_cannot_reach_the_host_says_so()
        {
            var w = new FakeWorld();
            using var g = new CoopController(w, new TestLog("lost"));
            string ended = null;
            g.Ended += r => ended = r;
            g.Join(new CoopOptions { PlayerName = "Lost", DisconnectTimeoutMs = 1000 }, "127.0.0.1", 1);
            var sw = Stopwatch.StartNew();
            while (ended == null && sw.ElapsedMilliseconds < 6000) { g.Tick(); Thread.Sleep(5); }
            Assert.NotNull(ended);
            Assert.Contains("Could not reach", ended);
            Assert.Equal(CoopMode.Offline, g.Mode);
        }

        [Fact]
        public void A_mistyped_address_is_a_message_not_a_crash()
        {
            using var g = new CoopController(new FakeWorld(), new TestLog("typo"));
            string ended = null;
            g.Ended += r => ended = r;
            g.Join(new CoopOptions { PlayerName = "Typo" }, "no such host .invalid", 9050);
            // A real lookup of a bad name: the answer can take seconds, and the game keeps running meanwhile.
            for (var i = 0; i < 1000 && ended == null; i++) { g.Tick(); Thread.Sleep(10); }
            Assert.NotNull(ended);
            Assert.Contains("Could not", ended);
            Assert.Equal(CoopMode.Offline, g.Mode);
        }

        [Fact]
        public void Joining_by_host_name_does_not_freeze_the_game()
        {
            using var t = new Table();
            var w = new FakeWorld();
            var g = new CoopController(w, new TestLog("named"));
            t.Guests.Add((g, w));
            var options = new CoopOptions
            {
                PlayerName = "Named",
                ResolveHostName = name => { Thread.Sleep(1500); return new[] { IPAddress.Loopback }; },
            };
            var sw = Stopwatch.StartNew();
            g.Join(options, "friend.example", t.Host.Host.Port);
            Assert.True(sw.ElapsedMilliseconds < 300, $"Join blocked the frame for {sw.ElapsedMilliseconds} ms");
            Assert.Equal(CoopMode.Connecting, g.Mode);
            Assert.True(t.Until(() => g.Mode == CoopMode.Joined, 6000), "never joined after the name was looked up");
        }

        [Fact]
        public void A_host_name_that_does_not_exist_is_a_message()
        {
            using var g = new CoopController(new FakeWorld(), new TestLog("unknown"));
            string ended = null;
            g.Ended += r => ended = r;
            var options = new CoopOptions
            {
                PlayerName = "Unknown",
                ResolveHostName = name => throw new SocketException((int)SocketError.HostNotFound),
            };
            g.Join(options, "nobody.example", 9050);
            for (var i = 0; i < 100 && ended == null; i++) { g.Tick(); Thread.Sleep(10); }
            Assert.NotNull(ended);
            Assert.Contains("Could not find", ended);
            Assert.Equal(CoopMode.Offline, g.Mode);
        }

        [Fact]
        public void The_overlay_lists_everyone_with_ping()
        {
            using var t = new Table();
            var (g, _) = t.Join("Gus");
            t.For(1200); // one ping table round
            var lines = new List<string>();
            g.DescribePlayers(lines);
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("#2 Gus (you)", lines[0]);
            Assert.Contains("Hosty (host)", lines[1]);
            t.Host.DescribePlayers(lines);
            Assert.StartsWith("#1 Hosty (you, host)", lines[0]);
            Assert.Contains(" ms", lines[1]);
        }
    }
}
