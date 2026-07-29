namespace Battleship
{
    /// <summary>
    /// Wire protocol for Pikeman multiplayer. Every transport message begins with one of these bytes;
    /// the rest of the payload is message-specific. Kept in one place so both peers agree on the format.
    /// Step 3 only exercises Ping/Pong (a connectivity round-trip); the rest are reserved for later steps.
    /// </summary>
    public enum NetMsg : byte
    {
        Ping = 1,   // connectivity probe (host -> guest)
        Pong = 2,   // probe reply (guest -> host)

        // --- reserved for later steps ---
        FleetReady = 10,   // Step 4: "my fleet is placed"
        MatchStart = 11,   // Step 4: host assigns first turn + shared config
        MoveIntent = 20,   // Step 6/7: a move I want to make (fire/torpedo/perk...)
        MoveResult = 21,   // Step 6/7: the resolved outcome from the defender
        Rematch    = 30,   // Step 7
    }
}
