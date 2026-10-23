using System;
using LiteNetLib.Utils;

namespace RL2Coop.Net.Protocol
{
    /// <summary>First byte of every packet.</summary>
    public enum MessageType : byte
    {
        // --- session control (reliable) ---
        Welcome = 1,
        PlayerJoined = 2,
        PlayerLeft = 3,
        PlayerLoadout = 4,

        // --- clock (unreliable) ---
        TimePing = 10,
        TimePong = 11,
        PingTable = 12, // host -> guests, everyone's latency for the overlay

        // --- run flow (reliable) ---
        RunStart = 20,
        RoomChange = 21,
        RunEnd = 22,
        RoomRequest = 23,

        // --- players ---
        PlayerState = 30,   // unreliable sequenced
        PlayerAction = 31,  // reliable
        PlayerDied = 32,    // reliable
        PlayerRevived = 33, // reliable

        // --- enemies (host authoritative) ---
        EnemySnapshot = 40, // unreliable sequenced, host -> guests
        HitRequest = 41,    // reliable, guest -> host
        EnemyDied = 42,     // reliable, host -> guests
    }

    /// <summary>A message that knows its own wire layout.</summary>
    public interface IMessage
    {
        MessageType Type { get; }
        void Write(NetDataWriter w);
        void Read(NetDataReader r);
    }

    /// <summary>Why a player left the session.</summary>
    public enum LeaveReason : byte
    {
        Quit = 0,
        TimedOut = 1,
        Kicked = 2,
        HostClosed = 3,
    }

    /// <summary>Why the host refused a connection. Sent as reject data.</summary>
    public enum RejectReason : byte
    {
        BadRequest = 0,
        VersionMismatch = 1,
        SessionFull = 2,
    }

    /// <summary>Why a run is over.</summary>
    public enum RunEndReason : byte
    {
        AllPlayersDead = 0,
        HostLeftRun = 1,
        Victory = 2,
    }

    /// <summary>Bit flags packed into <see cref="PlayerStateMsg.Flags"/>.</summary>
    [Flags]
    public enum PlayerFlags : byte
    {
        None = 0,
        FacingRight = 1 << 0,
        Grounded = 1 << 1,
        Dead = 1 << 2,
        Attacking = 1 << 3,
        Dashing = 1 << 4,
        Invincible = 1 << 5,
        Spectating = 1 << 6,
    }

    /// <summary>Bit flags packed into <see cref="EnemyState.Flags"/>.</summary>
    [Flags]
    public enum EnemyFlags : byte
    {
        None = 0,
        FacingRight = 1 << 0,
        Dead = 1 << 1,
        Active = 1 << 2,
        Boss = 1 << 3,
    }

    /// <summary>Identity of a player as shown to everyone else.</summary>
    public struct PlayerInfo
    {
        public byte Id;
        public string Name;

        public void Write(NetDataWriter w) { w.Put(Id); w.Put(Name ?? string.Empty, ProtocolInfo.MaxNameLength); }
        public void Read(NetDataReader r) { Id = r.GetByte(); Name = r.GetString(ProtocolInfo.MaxNameLength); }
    }

    /// <summary>Host -> new guest, once, right after the connection is accepted.</summary>
    public struct WelcomeMsg : IMessage
    {
        public MessageType Type => MessageType.Welcome;
        public byte YourId;
        /// <summary>Host clock at send time, seconds. Seeds the guest's clock sync.</summary>
        public double HostTime;
        /// <summary>True when a run is in progress; the guest should start it with <see cref="RunSeed"/>.</summary>
        public bool RunActive;
        public int RunSeed;
        public int RoomId;
        public PlayerInfo[] Players;

        public void Write(NetDataWriter w)
        {
            w.Put(YourId); w.Put(HostTime); w.Put(RunActive); w.Put(RunSeed); w.Put(RoomId);
            var n = Players?.Length ?? 0;
            w.Put((byte)n);
            for (var i = 0; i < n; i++) Players[i].Write(w);
        }

        public void Read(NetDataReader r)
        {
            YourId = r.GetByte(); HostTime = r.GetDouble(); RunActive = r.GetBool(); RunSeed = r.GetInt(); RoomId = r.GetInt();
            var n = r.GetByte();
            if (n > ProtocolInfo.MaxPlayers) throw new FormatException("too many players in welcome");
            Players = new PlayerInfo[n];
            for (var i = 0; i < n; i++) Players[i].Read(r);
        }
    }

    /// <summary>Host -> everyone: a player connected.</summary>
    public struct PlayerJoinedMsg : IMessage
    {
        public MessageType Type => MessageType.PlayerJoined;
        public PlayerInfo Player;
        public void Write(NetDataWriter w) => Player.Write(w);
        public void Read(NetDataReader r) => Player.Read(r);
    }

    /// <summary>Host -> everyone: a player is gone.</summary>
    public struct PlayerLeftMsg : IMessage
    {
        public MessageType Type => MessageType.PlayerLeft;
        public byte Id;
        public LeaveReason Reason;
        public void Write(NetDataWriter w) { w.Put(Id); w.Put((byte)Reason); }
        public void Read(NetDataReader r) { Id = r.GetByte(); Reason = (LeaveReason)r.GetByte(); }
    }

    /// <summary>
    /// What a player looks like: class plus an opaque appearance blob the game
    /// adapter defines (weapon, colours, traits that change the model). Sent
    /// when it changes, relayed by the host, cached for late joiners.
    /// </summary>
    public struct PlayerLoadoutMsg : IMessage
    {
        public const int MaxAppearanceBytes = 256;
        public MessageType Type => MessageType.PlayerLoadout;
        public byte Id;
        public int ClassId;
        public byte[] Appearance;

        public void Write(NetDataWriter w)
        {
            w.Put(Id); w.Put(ClassId);
            var a = Appearance ?? Array.Empty<byte>();
            if (a.Length > MaxAppearanceBytes) throw new ArgumentException("appearance blob too large");
            w.PutBytesWithLength(a);
        }

        public void Read(NetDataReader r)
        {
            Id = r.GetByte(); ClassId = r.GetInt();
            Appearance = r.GetBytesWithLength();
            if (Appearance.Length > MaxAppearanceBytes) throw new FormatException("appearance blob too large");
        }
    }

    /// <summary>Guest -> host clock probe (NTP style).</summary>
    public struct TimePingMsg : IMessage
    {
        public MessageType Type => MessageType.TimePing;
        public double ClientTime;
        public void Write(NetDataWriter w) => w.Put(ClientTime);
        public void Read(NetDataReader r) => ClientTime = r.GetDouble();
    }

    /// <summary>Host -> guest answer to <see cref="TimePingMsg"/>.</summary>
    public struct TimePongMsg : IMessage
    {
        public MessageType Type => MessageType.TimePong;
        public double ClientTime;
        public double HostTime;
        public void Write(NetDataWriter w) { w.Put(ClientTime); w.Put(HostTime); }
        public void Read(NetDataReader r) { ClientTime = r.GetDouble(); HostTime = r.GetDouble(); }
    }

    /// <summary>Host -> everyone: a new run with this castle seed begins.</summary>
    public struct RunStartMsg : IMessage
    {
        public MessageType Type => MessageType.RunStart;
        public int Seed;
        public void Write(NetDataWriter w) => w.Put(Seed);
        public void Read(NetDataReader r) => Seed = r.GetInt();
    }

    /// <summary>
    /// Host -> everyone: the party moves to another room. The host leads; the
    /// guests follow and appear at the given position.
    /// </summary>
    public struct RoomChangeMsg : IMessage
    {
        public MessageType Type => MessageType.RoomChange;
        /// <summary>Increases with every change so stale messages can be ignored.</summary>
        public int Sequence;
        public int RoomId;
        public float X, Y;
        public void Write(NetDataWriter w) { w.Put(Sequence); w.Put(RoomId); w.Put(X); w.Put(Y); }
        public void Read(NetDataReader r) { Sequence = r.GetInt(); RoomId = r.GetInt(); X = r.GetFloat(); Y = r.GetFloat(); }
    }

    /// <summary>
    /// Guest -> host: "I walked out of the room into this one". The host
    /// decides; if it agrees, it moves itself and answers with a
    /// <see cref="RoomChangeMsg"/>, so whoever reaches a door first leads.
    /// </summary>
    public struct RoomRequestMsg : IMessage
    {
        public MessageType Type => MessageType.RoomRequest;
        /// <summary>Filled in by the host from the connection.</summary>
        public byte RequesterId;
        public int RoomId;
        public float X, Y;
        public void Write(NetDataWriter w) { w.Put(RoomId); w.Put(X); w.Put(Y); }
        public void Read(NetDataReader r) { RoomId = r.GetInt(); X = r.GetFloat(); Y = r.GetFloat(); }
    }

    /// <summary>Host -> everyone: the run is over.</summary>
    public struct RunEndMsg : IMessage
    {
        public MessageType Type => MessageType.RunEnd;
        public RunEndReason Reason;
        public void Write(NetDataWriter w) => w.Put((byte)Reason);
        public void Read(NetDataReader r) => Reason = (RunEndReason)r.GetByte();
    }

    /// <summary>
    /// One player's body at one moment, on the host's timeline. Guests send
    /// their own ~30 times a second; the host relays to the others.
    /// </summary>
    public struct PlayerStateMsg : IMessage
    {
        public MessageType Type => MessageType.PlayerState;
        public byte Id;
        /// <summary>Host-clock time this state was sampled, seconds.</summary>
        public double Time;
        public int RoomId;
        public float X, Y, VelX, VelY;
        public PlayerFlags Flags;
        /// <summary>Animator state hash of the base layer (adapter-defined).</summary>
        public int AnimState;
        /// <summary>Normalized time inside <see cref="AnimState"/>.</summary>
        public float AnimTime;
        public int Hp, MaxHp;

        public void Write(NetDataWriter w)
        {
            w.Put(Id); w.Put(Time); w.Put(RoomId);
            w.Put(X); w.Put(Y); w.Put(VelX); w.Put(VelY);
            w.Put((byte)Flags); w.Put(AnimState); w.Put(AnimTime);
            w.Put(Hp); w.Put(MaxHp);
        }

        public void Read(NetDataReader r)
        {
            Id = r.GetByte(); Time = r.GetDouble(); RoomId = r.GetInt();
            X = r.GetFloat(); Y = r.GetFloat(); VelX = r.GetFloat(); VelY = r.GetFloat();
            Flags = (PlayerFlags)r.GetByte(); AnimState = r.GetInt(); AnimTime = r.GetFloat();
            Hp = r.GetInt(); MaxHp = r.GetInt();
        }
    }

    /// <summary>
    /// A discrete thing a player did that the others should see happen
    /// (swing, spell, talent). Kinds and params are adapter-defined.
    /// </summary>
    public struct PlayerActionMsg : IMessage
    {
        public MessageType Type => MessageType.PlayerAction;
        public byte Id;
        public byte Kind;
        public int Param;
        public float X, Y;
        public bool FacingRight;
        public void Write(NetDataWriter w) { w.Put(Id); w.Put(Kind); w.Put(Param); w.Put(X); w.Put(Y); w.Put(FacingRight); }
        public void Read(NetDataReader r) { Id = r.GetByte(); Kind = r.GetByte(); Param = r.GetInt(); X = r.GetFloat(); Y = r.GetFloat(); FacingRight = r.GetBool(); }
    }

    /// <summary>A player died (or was revived). Sent by that player, relayed by the host.</summary>
    public struct PlayerLifeMsg : IMessage
    {
        public MessageType Type => Alive ? MessageType.PlayerRevived : MessageType.PlayerDied;
        public bool Alive;
        public byte Id;
        public void Write(NetDataWriter w) => w.Put(Id);
        public void Read(NetDataReader r) => Id = r.GetByte();
    }

    /// <summary>One enemy inside an <see cref="EnemySnapshotMsg"/>.</summary>
    public struct EnemyState
    {
        /// <summary>Wire size, used to split snapshots under the MTU.</summary>
        public const int Size = 2 + 4 + 4 + 1 + 4 + 4 + 4 + 4;

        /// <summary>Stable id of the enemy inside its room (spawn order).</summary>
        public ushort NetId;
        public float X, Y;
        public EnemyFlags Flags;
        public int AnimState;
        public float AnimTime;
        public int Hp, MaxHp;

        public void Write(NetDataWriter w)
        {
            w.Put(NetId); w.Put(X); w.Put(Y); w.Put((byte)Flags);
            w.Put(AnimState); w.Put(AnimTime); w.Put(Hp); w.Put(MaxHp);
        }

        public void Read(NetDataReader r)
        {
            NetId = r.GetUShort(); X = r.GetFloat(); Y = r.GetFloat(); Flags = (EnemyFlags)r.GetByte();
            AnimState = r.GetInt(); AnimTime = r.GetFloat(); Hp = r.GetInt(); MaxHp = r.GetInt();
        }
    }

    /// <summary>
    /// Host -> guests: where every enemy in the current room is, ~20 times a
    /// second. Large rooms are split over several packets with the same time.
    /// </summary>
    public struct EnemySnapshotMsg : IMessage
    {
        /// <summary>
        /// Keeps one packet under LiteNetLib's unfragmented limit before MTU discovery
        /// (1020 bytes): 1 type + 8 time + 4 room + 1 count + 36 x 27 = 986.
        /// </summary>
        public const int MaxPerPacket = 36;
        public MessageType Type => MessageType.EnemySnapshot;
        public double Time;
        public int RoomId;
        public EnemyState[] Enemies;
        /// <summary>How many entries of <see cref="Enemies"/> are valid (array may be pooled).</summary>
        public int Count;

        public void Write(NetDataWriter w)
        {
            if (Count > MaxPerPacket) throw new ArgumentException("split snapshots above MaxPerPacket");
            w.Put(Time); w.Put(RoomId); w.Put((byte)Count);
            for (var i = 0; i < Count; i++) Enemies[i].Write(w);
        }

        public void Read(NetDataReader r)
        {
            Time = r.GetDouble(); RoomId = r.GetInt(); Count = r.GetByte();
            if (Count > MaxPerPacket) throw new FormatException("enemy snapshot too large");
            Enemies = new EnemyState[Count];
            for (var i = 0; i < Count; i++) Enemies[i].Read(r);
        }
    }

    /// <summary>
    /// Guest -> host: "my attack connected with this enemy". The guest does not
    /// change the enemy itself; the host applies the damage and the result
    /// comes back in the next snapshot, so all screens agree on every HP bar.
    /// </summary>
    public struct HitRequestMsg : IMessage
    {
        public MessageType Type => MessageType.HitRequest;
        /// <summary>Filled in by the host from the connection, never trusted from the wire.</summary>
        public byte AttackerId;
        public int RoomId;
        public ushort EnemyNetId;
        public float Damage;
        public bool Critical;
        public float KnockX, KnockY;

        public void Write(NetDataWriter w) { w.Put(RoomId); w.Put(EnemyNetId); w.Put(Damage); w.Put(Critical); w.Put(KnockX); w.Put(KnockY); }
        public void Read(NetDataReader r) { RoomId = r.GetInt(); EnemyNetId = r.GetUShort(); Damage = r.GetFloat(); Critical = r.GetBool(); KnockX = r.GetFloat(); KnockY = r.GetFloat(); }
    }

    /// <summary>Host -> guests: an enemy died. Reliable, so a lost snapshot never leaves a ghost.</summary>
    public struct EnemyDiedMsg : IMessage
    {
        public MessageType Type => MessageType.EnemyDied;
        public int RoomId;
        public ushort NetId;
        public byte KillerId;
        public void Write(NetDataWriter w) { w.Put(RoomId); w.Put(NetId); w.Put(KillerId); }
        public void Read(NetDataReader r) { RoomId = r.GetInt(); NetId = r.GetUShort(); KillerId = r.GetByte(); }
    }

    /// <summary>
    /// Host -> guests, about once a second: every guest's round trip to the
    /// host, so each overlay can show everyone's connection quality.
    /// </summary>
    public struct PingTableMsg : IMessage
    {
        public MessageType Type => MessageType.PingTable;
        public byte Count;
        public byte[] Ids;
        public ushort[] RttMs;

        public void Write(NetDataWriter w)
        {
            w.Put(Count);
            for (var i = 0; i < Count; i++) { w.Put(Ids[i]); w.Put(RttMs[i]); }
        }

        public void Read(NetDataReader r)
        {
            Count = r.GetByte();
            if (Count > ProtocolInfo.MaxPlayers) throw new FormatException("ping table too large");
            Ids = new byte[Count];
            RttMs = new ushort[Count];
            for (var i = 0; i < Count; i++) { Ids[i] = r.GetByte(); RttMs[i] = r.GetUShort(); }
        }
    }
}
