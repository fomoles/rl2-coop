using System;

namespace RL2Coop.Net.Sync
{
    /// <summary>Monotonic time source in seconds. Swappable so tests can drive time.</summary>
    public interface IClock
    {
        double Now { get; }
    }

    /// <summary>Wall-clock-independent monotonic clock.</summary>
    public sealed class StopwatchClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public double Now => _sw.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Estimates the host's clock on a guest, NTP style. Every state packet is
    /// stamped with host time, so all players share one timeline and remote
    /// motion can be replayed at the right moment regardless of whose machine
    /// sent it.
    /// <para>
    /// Each ping/pong gives an offset sample. The sample with the lowest round
    /// trip in a short window is the most trustworthy (least queueing), and the
    /// estimate slews toward it gradually so the timeline never jumps backwards.
    /// </para>
    /// </summary>
    public sealed class ClockSync
    {
        private const int Window = 8;
        private const double SnapThreshold = 0.25; // seconds; larger errors are corrected at once
        private const double SlewRate = 0.1;       // fraction of the error corrected per sample

        private readonly double[] _offsets = new double[Window];
        private readonly double[] _rtts = new double[Window];
        private int _samples;
        private int _next;

        /// <summary>hostTime ≈ localTime + Offset.</summary>
        public double Offset { get; private set; }

        /// <summary>Smoothed round-trip time in seconds.</summary>
        public double Rtt { get; private set; }

        public bool HasSample => _samples > 0;

        /// <summary>Coarse initial estimate from a one-way message (no RTT known).</summary>
        public void Seed(double hostTime, double localReceiveTime, double oneWayGuess)
        {
            if (HasSample) return;
            Offset = hostTime + oneWayGuess - localReceiveTime;
        }

        /// <summary>Feed one ping/pong exchange.</summary>
        public void AddSample(double localSendTime, double hostTime, double localReceiveTime)
        {
            var rtt = localReceiveTime - localSendTime;
            if (rtt < 0 || rtt > 5) return; // clock went backwards or stale reply
            var offset = hostTime + rtt * 0.5 - localReceiveTime;

            _offsets[_next] = offset;
            _rtts[_next] = rtt;
            _next = (_next + 1) % Window;
            var first = _samples == 0;
            if (_samples < Window) _samples++;

            Rtt = first ? rtt : Rtt + (rtt - Rtt) * 0.2;

            var best = 0;
            for (var i = 1; i < _samples; i++) if (_rtts[i] < _rtts[best]) best = i;
            var target = _offsets[best];

            var error = target - Offset;
            if (first || Math.Abs(error) > SnapThreshold) Offset = target;
            else Offset += error * SlewRate;
        }

        public double ToHost(double localTime) => localTime + Offset;
    }
}
