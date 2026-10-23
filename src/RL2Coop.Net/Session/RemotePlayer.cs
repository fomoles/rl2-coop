using RL2Coop.Net.Protocol;
using RL2Coop.Net.Sync;

namespace RL2Coop.Net.Session
{
    /// <summary>Everything this machine knows about another player.</summary>
    public sealed class RemotePlayer
    {
        internal RemotePlayer(byte id, string name)
        {
            Id = id;
            Name = name;
        }

        public byte Id { get; }
        public string Name { get; internal set; }

        public bool Alive { get; internal set; } = true;

        /// <summary>Last loadout received; null until the player sends one.</summary>
        public PlayerLoadoutMsg? Loadout { get; internal set; }

        /// <summary>Recent states on the host timeline, for interpolation.</summary>
        public SnapshotBuffer<PlayerStateMsg> States { get; } = new SnapshotBuffer<PlayerStateMsg>();

        /// <summary>Latest state received (not interpolated).</summary>
        public PlayerStateMsg Latest => States.Newest;

        /// <summary>
        /// Round-trip time to this player in milliseconds as seen by this
        /// machine: direct for host↔guest, host-relayed estimate otherwise.
        /// </summary>
        public int PingMs { get; internal set; }

        public override string ToString() => $"#{Id} {Name}";
    }

    /// <summary>Minimal logging seam so the core does not depend on Unity or BepInEx.</summary>
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
    }

    internal sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Info(string message) { }
        public void Warn(string message) { }
    }
}
