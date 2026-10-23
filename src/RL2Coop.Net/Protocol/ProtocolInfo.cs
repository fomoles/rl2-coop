namespace RL2Coop.Net.Protocol
{
    /// <summary>
    /// Constants every peer must agree on. Bump <see cref="Version"/> whenever
    /// the byte layout of any message changes; the host rejects mismatches
    /// during the handshake instead of letting garbage reach the game.
    /// </summary>
    public static class ProtocolInfo
    {
        /// <summary>Wire format version. Checked in the connection request.</summary>
        public const ushort Version = 1;

        /// <summary>Magic string at the start of every connection request.</summary>
        public const string Magic = "RL2COOP";

        /// <summary>Default UDP port the host listens on.</summary>
        public const int DefaultPort = 26950;

        /// <summary>Host plus three guests.</summary>
        public const int MaxPlayers = 4;

        /// <summary>The host always has player id 0.</summary>
        public const byte HostPlayerId = 0;

        /// <summary>Longest player name accepted; longer names are truncated.</summary>
        public const int MaxNameLength = 24;
    }

    /// <summary>
    /// Logical channels. Reliable traffic (joins, room changes, hits, deaths)
    /// and high-rate state snapshots travel on separate channels so a resent
    /// reliable packet never stalls movement updates.
    /// </summary>
    public static class Channels
    {
        /// <summary>Reliable, ordered: session control and gameplay events.</summary>
        public const byte Control = 0;

        /// <summary>Unreliable, sequenced: host enemy snapshots (latest wins).</summary>
        public const byte Enemies = 1;

        /// <summary>
        /// First player-state channel. Player <c>id</c> streams on
        /// <c>PlayerStateBase + id</c>: sequencing is per channel, so if the
        /// host relays two players over one channel a late packet from one
        /// would be thrown away just for arriving after the other's.
        /// </summary>
        public const byte PlayerStateBase = 2;

        /// <summary>Sequenced channel carrying a given player's state.</summary>
        public static byte PlayerState(byte playerId) => (byte)(PlayerStateBase + playerId);

        /// <summary>Number of channels the transport must be configured with.</summary>
        public const byte Count = PlayerStateBase + ProtocolInfo.MaxPlayers;
    }
}
