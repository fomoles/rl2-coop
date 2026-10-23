using System.Collections.Generic;
using RL2Coop.Net.Session;

namespace RL2Coop.Net.Sync
{
    /// <summary>
    /// Who a dead player's camera follows. Picks a living player and lets the
    /// spectator cycle between them; if the target dies the camera jumps to
    /// the next one instead of staying on a corpse.
    /// </summary>
    public sealed class Spectator
    {
        private readonly List<byte> _alive = new List<byte>(4);

        /// <summary>Player being watched, or null when nobody is alive.</summary>
        public byte? Target { get; private set; }

        /// <summary>Re-check the target against the current player table. Call every frame while dead.</summary>
        public byte? Update(IReadOnlyDictionary<byte, RemotePlayer> players)
        {
            Collect(players);
            if (Target.HasValue && _alive.Contains(Target.Value)) return Target;
            Target = _alive.Count > 0 ? _alive[0] : (byte?)null;
            return Target;
        }

        /// <summary>Switch to the next (+1) or previous (-1) living player.</summary>
        public byte? Cycle(IReadOnlyDictionary<byte, RemotePlayer> players, int direction)
        {
            Collect(players);
            if (_alive.Count == 0) { Target = null; return null; }
            var i = Target.HasValue ? _alive.IndexOf(Target.Value) : -1;
            if (i < 0) i = direction >= 0 ? -1 : 0;
            var n = _alive.Count;
            i = ((i + (direction >= 0 ? 1 : -1)) % n + n) % n;
            Target = _alive[i];
            return Target;
        }

        public void Clear() => Target = null;

        private void Collect(IReadOnlyDictionary<byte, RemotePlayer> players)
        {
            _alive.Clear();
            foreach (var p in players.Values) if (p.Alive) _alive.Add(p.Id);
            _alive.Sort();
        }
    }
}
