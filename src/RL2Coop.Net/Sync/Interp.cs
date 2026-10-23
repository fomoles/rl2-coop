using RL2Coop.Net.Protocol;

namespace RL2Coop.Net.Sync
{
    /// <summary>Blending rules for snapshot types.</summary>
    public static class Interp
    {
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>
        /// Continuous values (position, velocity) are blended; discrete values
        /// (flags, animation state, HP) come from whichever snapshot is nearer,
        /// so an attack animation starts on the frame it should rather than
        /// being smeared across two states.
        /// </summary>
        public static PlayerStateMsg Blend(in PlayerStateMsg a, in PlayerStateMsg b, float t)
        {
            var r = t < 0.5f ? a : b;
            r.X = Lerp(a.X, b.X, t);
            r.Y = Lerp(a.Y, b.Y, t);
            r.VelX = Lerp(a.VelX, b.VelX, t);
            r.VelY = Lerp(a.VelY, b.VelY, t);
            if (a.AnimState == b.AnimState) r.AnimTime = Lerp(a.AnimTime, b.AnimTime, t);
            return r;
        }

        /// <summary>Dead-reckon a player a short time past the last snapshot.</summary>
        public static PlayerStateMsg Extrapolate(in PlayerStateMsg s, double seconds)
        {
            var r = s;
            r.X += s.VelX * (float)seconds;
            r.Y += s.VelY * (float)seconds;
            return r;
        }

        public static EnemyState Blend(in EnemyState a, in EnemyState b, float t)
        {
            var r = t < 0.5f ? a : b;
            r.X = Lerp(a.X, b.X, t);
            r.Y = Lerp(a.Y, b.Y, t);
            if (a.AnimState == b.AnimState) r.AnimTime = Lerp(a.AnimTime, b.AnimTime, t);
            return r;
        }

        /// <summary>Sample a player buffer at a render time, with bounded extrapolation.</summary>
        public static bool Sample(SnapshotBuffer<PlayerStateMsg> buf, double renderTime, InterpolationSettings s, out PlayerStateMsg result)
        {
            if (!buf.TrySample(renderTime, out var a, out var b, out var t, out var over)) { result = default; return false; }
            result = over > 0
                ? Extrapolate(b, System.Math.Min(over, s.MaxExtrapolation))
                : Blend(a, b, t);
            return true;
        }

        /// <summary>Sample an enemy buffer at a render time (enemies freeze rather than extrapolate).</summary>
        public static bool Sample(SnapshotBuffer<EnemyState> buf, double renderTime, out EnemyState result)
        {
            if (!buf.TrySample(renderTime, out var a, out var b, out var t, out _)) { result = default; return false; }
            result = Blend(a, b, t);
            return true;
        }
    }
}
