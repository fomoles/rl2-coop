using System.Collections.Generic;
using RL2Coop.Net.Protocol;

namespace RL2Coop.Net.Sync
{
    /// <summary>
    /// A guest's view of the host's enemies in the current room: one snapshot
    /// buffer per enemy, keyed by its net id. The game adapter feeds it the
    /// incoming snapshots and asks it where to draw each enemy this frame.
    /// </summary>
    public sealed class EnemyMirror
    {
        private readonly Dictionary<ushort, SnapshotBuffer<EnemyState>> _buffers = new Dictionary<ushort, SnapshotBuffer<EnemyState>>();
        private readonly HashSet<ushort> _dead = new HashSet<ushort>();

        /// <summary>Room the mirrored enemies belong to.</summary>
        public int RoomId { get; private set; } = int.MinValue;

        /// <summary>Enemies the host has told us about in this room.</summary>
        public IEnumerable<ushort> Known => _buffers.Keys;

        public int Count => _buffers.Count;

        /// <summary>Forget everything (new room).</summary>
        public void Reset(int roomId)
        {
            RoomId = roomId;
            _buffers.Clear();
            _dead.Clear();
        }

        public void Apply(in EnemySnapshotMsg msg)
        {
            if (msg.RoomId != RoomId) return;
            for (var i = 0; i < msg.Count; i++)
            {
                var e = msg.Enemies[i];
                if (!_buffers.TryGetValue(e.NetId, out var buf))
                {
                    buf = new SnapshotBuffer<EnemyState>(16);
                    _buffers[e.NetId] = buf;
                }
                buf.Add(msg.Time, e);
                if ((e.Flags & EnemyFlags.Dead) != 0) _dead.Add(e.NetId);
            }
        }

        /// <summary>The host's reliable death notice: never leave a ghost even if snapshots were lost.</summary>
        public void MarkDead(ushort netId) => _dead.Add(netId);

        public bool IsDead(ushort netId) => _dead.Contains(netId);

        /// <summary>Where the enemy should be drawn at <paramref name="renderTime"/> (host timeline).</summary>
        public bool TryGet(ushort netId, double renderTime, out EnemyState state)
        {
            if (!_buffers.TryGetValue(netId, out var buf)) { state = default; return false; }
            if (!Interp.Sample(buf, renderTime, out state)) return false;
            if (_dead.Contains(netId)) state.Flags |= EnemyFlags.Dead;
            return true;
        }

        /// <summary>Host time of the newest snapshot for this enemy, NaN if unknown.</summary>
        public double LastSeen(ushort netId) => _buffers.TryGetValue(netId, out var b) ? b.NewestTime : double.NaN;
    }
}
