using System;
using LiteNetLib.Utils;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Sync;
using Xunit;

namespace RL2Coop.Net.Tests
{
    public class SnapshotBufferTests
    {
        private static PlayerStateMsg At(float x, float vx = 0) => new PlayerStateMsg { X = x, VelX = vx };

        [Fact]
        public void Samples_between_two_snapshots()
        {
            var b = new SnapshotBuffer<PlayerStateMsg>();
            b.Add(1.0, At(0));
            b.Add(1.1, At(10));
            Assert.True(Interp.Sample(b, 1.025, new InterpolationSettings(), out var s));
            Assert.Equal(2.5f, s.X, 3);
        }

        [Fact]
        public void Out_of_order_arrivals_are_slotted_in_and_duplicates_dropped()
        {
            var b = new SnapshotBuffer<PlayerStateMsg>();
            Assert.True(b.Add(1.0, At(0)));
            Assert.True(b.Add(1.2, At(20)));
            Assert.True(b.Add(1.1, At(10)));
            Assert.False(b.Add(1.1, At(99)));
            Assert.False(b.Add(double.NaN, At(1)));
            Assert.Equal(3, b.Count);
            Assert.True(Interp.Sample(b, 1.15, new InterpolationSettings(), out var s));
            Assert.Equal(15f, s.X, 3);
            Assert.Equal(20f, b.Newest.X);
        }

        [Fact]
        public void Ring_keeps_only_the_newest_window()
        {
            var b = new SnapshotBuffer<PlayerStateMsg>(4);
            for (var i = 0; i < 10; i++) b.Add(i, At(i));
            Assert.Equal(4, b.Count);
            Assert.False(b.Add(5, At(-1))); // older than the whole window
            Assert.True(Interp.Sample(b, 6.5, new InterpolationSettings(), out var s));
            Assert.Equal(6.5f, s.X, 3);
        }

        [Fact]
        public void Extrapolation_is_bounded_then_freezes()
        {
            var b = new SnapshotBuffer<PlayerStateMsg>();
            b.Add(1.0, At(0, vx: 10));
            b.Add(1.1, At(1, vx: 10));
            var settings = new InterpolationSettings { MaxExtrapolation = 0.2 };
            Assert.True(Interp.Sample(b, 1.15, settings, out var s));
            Assert.Equal(1.5f, s.X, 3);
            Assert.True(Interp.Sample(b, 5.0, settings, out s));
            Assert.Equal(3f, s.X, 3); // 1 + 10 * 0.2, not 1 + 10 * 3.9
        }

        [Fact]
        public void Discrete_fields_switch_at_the_midpoint_instead_of_blending()
        {
            var a = new PlayerStateMsg { AnimState = 1, Hp = 100, Flags = PlayerFlags.Grounded };
            var c = new PlayerStateMsg { AnimState = 2, Hp = 80, Flags = PlayerFlags.Attacking };
            Assert.Equal(1, Interp.Blend(a, c, 0.4f).AnimState);
            Assert.Equal(2, Interp.Blend(a, c, 0.6f).AnimState);
            Assert.Equal(80, Interp.Blend(a, c, 0.6f).Hp);
        }

        [Fact]
        public void Empty_buffer_gives_nothing()
        {
            Assert.False(Interp.Sample(new SnapshotBuffer<PlayerStateMsg>(), 1, new InterpolationSettings(), out _));
            Assert.True(double.IsNaN(new SnapshotBuffer<EnemyState>().NewestTime));
        }
    }

    public class ClockSyncTests
    {
        [Fact]
        public void First_exchange_sets_the_offset()
        {
            var c = new ClockSync();
            // Guest clock 100 behind the host; 40 ms round trip.
            c.AddSample(localSendTime: 10.00, hostTime: 110.02, localReceiveTime: 10.04);
            Assert.Equal(100.0, c.Offset, 6);
            Assert.Equal(110.5, c.ToHost(10.5), 6);
        }

        [Fact]
        public void Slow_samples_do_not_drag_the_estimate()
        {
            var c = new ClockSync();
            c.AddSample(0.0, 100.01, 0.02);           // clean, offset 100
            c.AddSample(1.0, 101.30, 1.50);           // 500 ms queued reply, naive offset 100.05
            Assert.Equal(100.0, c.Offset, 3);
        }

        [Fact]
        public void Big_jumps_snap_small_drift_slews()
        {
            var c = new ClockSync();
            c.AddSample(0, 100.01, 0.02);
            for (var i = 1; i <= 8; i++) c.AddSample(i, 100 + i + 0.06, i + 0.02); // host now 50 ms further ahead
            Assert.InRange(c.Offset, 100.0, 100.05);
            Assert.True(c.Offset > 100.01, "should be moving toward the new offset");

            var d = new ClockSync();
            d.AddSample(0, 100.01, 0.02);
            for (var i = 1; i <= 8; i++) d.AddSample(i, 200 + i + 0.01, i + 0.02); // host restarted: 100 s jump
            Assert.Equal(200.0, d.Offset, 3);
        }

        [Fact]
        public void Nonsense_round_trips_are_ignored()
        {
            var c = new ClockSync();
            c.AddSample(5, 100, 4);     // reply before request
            c.AddSample(0, 100, 30);    // 30 s round trip
            Assert.False(c.HasSample);
        }
    }

    public class SerializationTests
    {
        private static T RoundTrip<T>(T msg) where T : struct, IMessage
        {
            var w = new NetDataWriter();
            msg.Write(w);
            var r = new NetDataReader(w.CopyData());
            var back = default(T);
            back.Read(r);
            Assert.Equal(0, r.AvailableBytes);
            return back;
        }

        [Fact]
        public void Player_state_round_trips()
        {
            var s = new PlayerStateMsg
            {
                Id = 2, Time = 123.456, RoomId = 77, X = 1.5f, Y = -2.25f, VelX = 3, VelY = -4,
                Flags = PlayerFlags.Dashing | PlayerFlags.FacingRight, AnimState = -12345, AnimTime = 0.75f, Hp = 250, MaxHp = 300,
            };
            Assert.Equal(s, RoundTrip(s));
        }

        [Fact]
        public void Welcome_round_trips_and_rejects_oversized_player_lists()
        {
            var m = new WelcomeMsg
            {
                YourId = 3, HostTime = 9.5, RunActive = true, RunSeed = -7, RoomId = 4,
                Players = new[] { new PlayerInfo { Id = 0, Name = "Хост" }, new PlayerInfo { Id = 1, Name = "Bob" } },
            };
            var back = RoundTrip(m);
            Assert.Equal("Хост", back.Players[0].Name);
            Assert.Equal(-7, back.RunSeed);

            var w = new NetDataWriter();
            w.Put((byte)1); w.Put(0.0); w.Put(false); w.Put(0); w.Put(0); w.Put((byte)50);
            Assert.Throws<FormatException>(() => default(WelcomeMsg).Read(new NetDataReader(w.CopyData())));
        }

        [Fact]
        public void Enemy_snapshot_fits_a_safe_packet_at_its_limit()
        {
            var m = new EnemySnapshotMsg
            {
                Time = 1, RoomId = 2, Count = EnemySnapshotMsg.MaxPerPacket,
                Enemies = new EnemyState[EnemySnapshotMsg.MaxPerPacket],
            };
            for (var i = 0; i < m.Count; i++) m.Enemies[i] = new EnemyState { NetId = (ushort)i, X = i, Hp = i, Flags = EnemyFlags.Boss };
            var w = new NetDataWriter();
            m.Write(w);
            Assert.Equal(8 + 4 + 1 + EnemyState.Size * EnemySnapshotMsg.MaxPerPacket, w.Length);
            Assert.True(w.Length + 1 <= 1020, $"{w.Length} bytes");
            var back = RoundTrip(m);
            Assert.Equal(EnemySnapshotMsg.MaxPerPacket - 1, back.Enemies[EnemySnapshotMsg.MaxPerPacket - 1].NetId);
            Assert.Equal(EnemyFlags.Boss, back.Enemies[5].Flags);
        }

        [Fact]
        public void Loadout_blob_is_bounded_both_ways()
        {
            var ok = RoundTrip(new PlayerLoadoutMsg { Id = 1, ClassId = 9, Appearance = new byte[] { 4, 5 } });
            Assert.Equal(new byte[] { 4, 5 }, ok.Appearance);
            Assert.Throws<ArgumentException>(() =>
                new PlayerLoadoutMsg { Appearance = new byte[PlayerLoadoutMsg.MaxAppearanceBytes + 1] }.Write(new NetDataWriter()));
        }

        [Fact]
        public void Hit_request_does_not_carry_the_attacker_on_the_wire()
        {
            var back = RoundTrip(new HitRequestMsg { AttackerId = 3, RoomId = 1, EnemyNetId = 2, Damage = 10, Critical = true, KnockX = 1, KnockY = 2 });
            Assert.Equal(0, back.AttackerId);
            Assert.Equal(10f, back.Damage);
            Assert.True(back.Critical);
        }

        [Fact]
        public void Small_messages_round_trip()
        {
            Assert.Equal(5, RoundTrip(new RoomChangeMsg { Sequence = 5, RoomId = 6, X = 1, Y = 2 }).Sequence);
            Assert.Equal(6, RoundTrip(new RoomRequestMsg { RoomId = 6, X = 1, Y = 2 }).RoomId);
            Assert.Equal(RunEndReason.Victory, RoundTrip(new RunEndMsg { Reason = RunEndReason.Victory }).Reason);
            Assert.Equal(LeaveReason.TimedOut, RoundTrip(new PlayerLeftMsg { Id = 1, Reason = LeaveReason.TimedOut }).Reason);
            Assert.Equal(7, RoundTrip(new PlayerActionMsg { Id = 1, Kind = 2, Param = 7, FacingRight = true }).Param);
            Assert.Equal(2, RoundTrip(new EnemyDiedMsg { RoomId = 1, NetId = 2, KillerId = 3 }).NetId);
            var pt = RoundTrip(new PingTableMsg { Count = 2, Ids = new byte[] { 1, 2 }, RttMs = new ushort[] { 30, 400 } });
            Assert.Equal((ushort)400, pt.RttMs[1]);
        }
    }
}
