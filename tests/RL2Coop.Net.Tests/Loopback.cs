using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RL2Coop.Net.Session;

namespace RL2Coop.Net.Tests
{
    /// <summary>Real UDP sessions on 127.0.0.1, pumped from the test thread like a game loop would.</summary>
    internal sealed class Loopback : IDisposable
    {
        public readonly CoopHost Host;
        public readonly List<CoopGuest> Guests = new List<CoopGuest>();
        public readonly TestLog HostLog = new TestLog("host");
        private readonly CoopOptions _guestTemplate;

        public Loopback(CoopOptions hostOptions = null, CoopOptions guestOptions = null)
        {
            hostOptions = hostOptions ?? new CoopOptions();
            if (hostOptions.PlayerName == "Player") hostOptions.PlayerName = "Hosty";
            _guestTemplate = guestOptions ?? new CoopOptions();
            Host = new CoopHost(hostOptions, null, HostLog);
            if (!Host.Start(0)) throw new InvalidOperationException("host failed to bind");
        }

        public int Port => Host.Port;

        public CoopGuest AddGuest(string name, bool waitForWelcome = true)
        {
            var o = new CoopOptions
            {
                PlayerName = name,
                DisconnectTimeoutMs = _guestTemplate.DisconnectTimeoutMs,
                SimulateLatencyMs = _guestTemplate.SimulateLatencyMs,
                SimulateLossPercent = _guestTemplate.SimulateLossPercent,
            };
            var g = new CoopGuest(o, null, new TestLog(name));
            Guests.Add(g);
            g.Connect("127.0.0.1", Port);
            if (waitForWelcome && !PumpUntil(() => g.IsConnected && Host.Players.ContainsKey(g.LocalId)))
                throw new TimeoutException($"{name} never joined");
            return g;
        }

        public void PumpOnce()
        {
            Host.Poll();
            foreach (var g in Guests) g.Poll();
        }

        public bool PumpUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                PumpOnce();
                if (condition()) return true;
                Thread.Sleep(2);
            }
            return condition();
        }

        public void PumpFor(int ms) => PumpUntil(() => false, ms);

        public void Dispose()
        {
            foreach (var g in Guests) g.Dispose();
            Host.Dispose();
        }
    }

    internal sealed class TestLog : ILog
    {
        private readonly string _who;
        public readonly List<string> Warnings = new List<string>();
        public TestLog(string who) { _who = who; }
        public void Info(string message) => Console.WriteLine($"[{_who}] {message}");
        public void Warn(string message) { Warnings.Add(message); Console.WriteLine($"[{_who}] WARN {message}"); }
    }
}
