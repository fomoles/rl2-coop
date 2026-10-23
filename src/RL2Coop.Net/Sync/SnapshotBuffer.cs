using System;

namespace RL2Coop.Net.Sync
{
    /// <summary>
    /// Time-ordered ring of received snapshots for one entity. Remote entities
    /// are drawn slightly in the past (see <see cref="InterpolationSettings"/>)
    /// so there are almost always two snapshots around the render time to
    /// blend between; network jitter then turns into nothing worse than a few
    /// milliseconds of extra delay instead of visible stutter.
    /// </summary>
    public sealed class SnapshotBuffer<T> where T : struct
    {
        private readonly double[] _times;
        private readonly T[] _values;
        private int _start;
        private int _count;

        public SnapshotBuffer(int capacity = 32)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
            _times = new double[capacity];
            _values = new T[capacity];
        }

        public int Count => _count;

        /// <summary>Time of the newest snapshot, or NaN when empty.</summary>
        public double NewestTime => _count == 0 ? double.NaN : _times[Index(_count - 1)];

        /// <summary>The newest snapshot (default when empty).</summary>
        public T Newest => _count == 0 ? default : _values[Index(_count - 1)];

        public void Clear() { _start = 0; _count = 0; }

        /// <summary>
        /// Insert a snapshot. Out-of-order arrivals are placed correctly;
        /// duplicates and anything older than the whole window are dropped.
        /// </summary>
        /// <returns>False if the snapshot was discarded.</returns>
        public bool Add(double time, in T value)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return false;

            if (_count > 0)
            {
                var newest = _times[Index(_count - 1)];
                if (time < newest)
                {
                    // Rare (sequenced channel already drops most), but keep it correct.
                    if (_count == _times.Length && time <= _times[Index(0)]) return false;
                    var pos = _count;
                    while (pos > 0 && _times[Index(pos - 1)] > time) pos--;
                    if (pos > 0 && _times[Index(pos - 1)] == time) return false;
                    InsertAt(pos, time, value);
                    return true;
                }
                if (time == newest) return false;
            }

            if (_count == _times.Length) { _start = (_start + 1) % _times.Length; _count--; }
            var slot = Index(_count);
            _times[slot] = time;
            _values[slot] = value;
            _count++;
            return true;
        }

        /// <summary>
        /// Find the two snapshots around <paramref name="renderTime"/>.
        /// </summary>
        /// <param name="renderTime">Host-timeline time to sample at.</param>
        /// <param name="from">Snapshot at or before the render time.</param>
        /// <param name="to">Snapshot after the render time (equals <paramref name="from"/> at the edges).</param>
        /// <param name="t">Blend factor 0..1 between them.</param>
        /// <param name="overshoot">Seconds the render time is past the newest snapshot (0 when inside the window).</param>
        /// <returns>False when the buffer is empty.</returns>
        public bool TrySample(double renderTime, out T from, out T to, out float t, out double overshoot)
        {
            overshoot = 0;
            t = 0;
            if (_count == 0) { from = to = default; return false; }

            var first = Index(0);
            if (_count == 1 || renderTime <= _times[first])
            {
                from = to = _values[first];
                if (_count == 1) overshoot = Math.Max(0, renderTime - _times[first]);
                return true;
            }

            var last = Index(_count - 1);
            if (renderTime >= _times[last])
            {
                from = to = _values[last];
                t = 1;
                overshoot = renderTime - _times[last];
                return true;
            }

            // Newest-first scan: render time is almost always near the end.
            for (var i = _count - 2; i >= 0; i--)
            {
                var a = Index(i);
                if (_times[a] <= renderTime)
                {
                    var b = Index(i + 1);
                    from = _values[a];
                    to = _values[b];
                    var span = _times[b] - _times[a];
                    t = span <= 0 ? 1f : (float)((renderTime - _times[a]) / span);
                    return true;
                }
            }

            from = to = _values[first];
            return true;
        }

        private int Index(int logical) => (_start + logical) % _times.Length;

        private void InsertAt(int pos, double time, in T value)
        {
            if (_count == _times.Length)
            {
                // Drop the oldest to make room; pos shifts left by one.
                _start = (_start + 1) % _times.Length;
                _count--;
                pos--;
            }
            for (var i = _count; i > pos; i--)
            {
                _times[Index(i)] = _times[Index(i - 1)];
                _values[Index(i)] = _values[Index(i - 1)];
            }
            _times[Index(pos)] = time;
            _values[Index(pos)] = value;
            _count++;
        }
    }

    /// <summary>How far in the past remote entities are drawn.</summary>
    public sealed class InterpolationSettings
    {
        /// <summary>
        /// Render delay in seconds. Three 30 Hz snapshots: survives one lost
        /// packet plus normal jitter without falling back to extrapolation.
        /// </summary>
        public double Delay = 0.1;

        /// <summary>Longest time a remote entity is predicted past its last snapshot before it freezes.</summary>
        public double MaxExtrapolation = 0.2;
    }
}
