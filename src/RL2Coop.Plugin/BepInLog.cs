using BepInEx.Logging;
using RL2Coop.Net.Session;

namespace RL2Coop.Plugin
{
    /// <summary>Routes the network core's log lines into BepInEx/LogOutput.log.</summary>
    internal sealed class BepInLog : ILog
    {
        private readonly ManualLogSource _source;
        public BepInLog(ManualLogSource source) => _source = source;
        public void Info(string message) => _source.LogInfo(message);
        public void Warn(string message) => _source.LogWarning(message);
    }
}
