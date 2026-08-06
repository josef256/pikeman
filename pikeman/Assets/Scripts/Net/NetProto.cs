using System.Collections.Generic;

namespace Battleship
{
    /// <summary>
    /// Wire protocol for Pikeman multiplayer. Every transport message begins with one of these bytes;
    /// the rest of the payload is message-specific. Kept in one place so both peers agree on the format.
    /// Step 3 exercises Ping/Pong; Step 4 FleetReady/MatchStart; Step 6 MoveIntent/MoveResult.
    /// </summary>
    public enum NetMsg : byte
    {
        Ping = 1,   // connectivity probe (host -> guest)
        Pong = 2,   // probe reply (guest -> host)

        FleetReady = 10,   // Step 4: "my fleet is placed"
        MatchStart = 11,   // Step 4: host assigns first turn + shared config
        MoveIntent = 20,   // Step 6/7: a move I want to make (attacker -> defender)
        MoveResult = 21,   // Step 6/7: the resolved outcome (defender -> attacker)
        Rematch    = 30,   // Step 7
    }

    /// <summary>The kind of move an attacker sends in a <see cref="NetMsg.MoveIntent"/> body (first byte).
    /// Step 6 only uses Fire and Pass; the perk kinds are reserved for Step 7.</summary>
    public enum MoveKind : byte
    {
        Fire = 0,   // shoot one cell (Step 6)
        Pass = 1,   // end my turn without firing (Step 6)
        Torpedo  = 2,   // Step 7
        Relocate = 3,   // Step 7
        Radar    = 4,   // Step 7
    }

    /// <summary>One resolved shot as the defender reports it back to the attacker. Plain data only —
    /// no Board reference — so the codec stays free of game-state lookups.</summary>
    public struct ShotResult
    {
        public int X, Y;
        public FireOutcome Outcome;
        public ShipType SunkShip;                 // meaningful only when Outcome == Sunk
        public List<(int x, int y)> Footprint;    // sunk ship's full cell list (Sunk only), else null
    }

    /// <summary>
    /// Serializes move intents and results to/from raw bytes. Coordinates are 0..9 and ship/outcome
    /// enums are tiny, so every field fits in a single byte. The leading <see cref="NetMsg"/> tag is
    /// added by PikeNet, not here — these bodies are tag-free.
    /// </summary>
    public static class NetCodec
    {
        // ---- MoveIntent bodies (attacker -> defender) ----

        public static byte[] EncodeFire(int x, int y)
            => new byte[] { (byte)MoveKind.Fire, (byte)x, (byte)y };

        public static byte[] EncodePass()
            => new byte[] { (byte)MoveKind.Pass };

        public static byte[] EncodeTorpedo(int col)
            => new byte[] { (byte)MoveKind.Torpedo, (byte)col };

        // ---- MoveResult body (defender -> attacker) ----
        // Layout: [shotCount] then per shot [x][y][outcome][sunkShip][fpCount] [fx][fy] * fpCount

        public static byte[] EncodeResult(List<ShotResult> shots)
        {
            var buf = new List<byte> { (byte)(shots?.Count ?? 0) };
            if (shots != null)
                foreach (var s in shots)
                {
                    buf.Add((byte)s.X);
                    buf.Add((byte)s.Y);
                    buf.Add((byte)s.Outcome);
                    buf.Add((byte)s.SunkShip);
                    int fp = s.Footprint?.Count ?? 0;
                    buf.Add((byte)fp);
                    if (s.Footprint != null)
                        foreach (var c in s.Footprint) { buf.Add((byte)c.x); buf.Add((byte)c.y); }
                }
            return buf.ToArray();
        }

        public static List<ShotResult> DecodeResult(byte[] body)
        {
            var list = new List<ShotResult>();
            if (body == null || body.Length == 0) return list;
            int i = 0;
            int count = body[i++];
            for (int s = 0; s < count && i + 5 <= body.Length; s++)
            {
                var r = new ShotResult
                {
                    X = body[i++],
                    Y = body[i++],
                    Outcome = (FireOutcome)body[i++],
                    SunkShip = (ShipType)body[i++],
                };
                int fp = body[i++];
                if (fp > 0)
                {
                    r.Footprint = new List<(int x, int y)>(fp);
                    for (int c = 0; c < fp && i + 2 <= body.Length; c++)
                        r.Footprint.Add((body[i++], body[i++]));
                }
                list.Add(r);
            }
            return list;
        }
    }
}
