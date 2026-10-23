LiteNetLib 1.2.0 (tag v1.2.0, commit 07c089a22639ba64a414eb5816e4bd805cc5a684)
https://github.com/RevenantX/LiteNetLib — MIT, see LICENSE.txt.
Vendored and compiled into RL2Coop.Net.dll so the mod ships as one
assembly and does not depend on netstandard facades being present in the game.

One change, in `Utils/NetSerializer.cs`: the `[IgnoreDataMember]` check matches
the attribute by name instead of naming its type. The game's Mono has no
`System.Runtime.Serialization.dll`, so naming the type threw
`FileNotFoundException` the first time a `NetManager` was created. Marked
`RL2Coop:` in the source.

Second change, in `NetManager.cs`: the latency and packet-loss simulation is
compiled when `RL2COOP_SIMULATE` is defined, not only in Debug builds, so the
mod's `Debug.SimulateLatencyMs` / `Debug.SimulateLossPercent` settings work in
the released plugin. Marked `RL2Coop:` in the source.
