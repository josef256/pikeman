using System;
using System.Collections.Generic;

namespace Battleship
{
    /// <summary>One perk per ship. Perks die silently when the ship takes specific damage.</summary>
    public enum PerkKind { Relocate, Radar, Scorch, Intercept, Torpedo }

    public static class Perks
    {
        public static PerkKind Of(ShipType t) => t switch
        {
            ShipType.Destroyer => PerkKind.Relocate,
            ShipType.Carrier => PerkKind.Radar,
            ShipType.Battleship => PerkKind.Scorch,
            ShipType.Cruiser => PerkKind.Intercept,
            ShipType.Submarine => PerkKind.Torpedo,
            _ => PerkKind.Relocate
        };

        // Cooldowns in the owner's turns. Tunable (pushed from the controller Inspector).
        public static int RelocateCd = 7, RadarCd = 10, ScorchCd = 5, InterceptCd = 3, TorpedoCd = 9;

        public static int Cooldown(ShipType t) => Of(t) switch
        {
            PerkKind.Relocate => RelocateCd,
            PerkKind.Radar => RadarCd,
            PerkKind.Scorch => ScorchCd,
            PerkKind.Intercept => InterceptCd,
            PerkKind.Torpedo => TorpedoCd,
            _ => 7
        };

        /// <summary>
        /// Perk-loss rules (evaluated live from the owner's board, silent to the enemy):
        /// Destroyer — bow cell (index 1) hit → can't move. Carrier — 2+ cells lost → no planes.
        /// Battleship — BOTH middle cells (1,2) hit → no scorch. Cruiser — center cell (1) hit → no intercept.
        /// Submarine — conning tower (center, 1) hit → no torpedoes. Sunk always disables.
        /// </summary>
        public static bool IsDisabled(Board own, ShipType t)
        {
            if (own == null || !own.HasShipOfType(t) || own.IsSunk(t)) return true;
            return t switch
            {
                ShipType.Destroyer => own.ShipCellHit(t, 1),
                ShipType.Carrier => own.HitsOn(t) >= 2,
                ShipType.Battleship => own.ShipCellHit(t, 1) && own.ShipCellHit(t, 2),
                ShipType.Cruiser => own.ShipCellHit(t, 1),
                ShipType.Submarine => own.ShipCellHit(t, 1),
                _ => false
            };
        }
    }

    /// <summary>One player's perk tracker: cooldowns (in own turns) + the cruiser's protected cell.</summary>
    public sealed class PerkSide
    {
        private readonly int[] readyAt = new int[5]; // owner turn number when each perk becomes usable
        public int TurnCount;                        // completed turns by this side

        // Cruiser protection: one cell shielded until a shot is intercepted (or the perk dies).
        public int ProtectX = -1, ProtectY = -1;
        public bool HasProtection => ProtectX >= 0;

        // All perks start READY — cooldown only kicks in after the first use.

        public int CooldownLeft(ShipType t) => Math.Max(0, readyAt[(int)t] - TurnCount);

        public bool Ready(Board own, ShipType t)
        {
            if (Perks.IsDisabled(own, t)) return false;
            return TurnCount >= readyAt[(int)t];
        }

        public void Use(ShipType t) => readyAt[(int)t] = TurnCount + Perks.Cooldown(t);

        /// <summary>DEBUG: clear all cooldowns — every (non-disabled) perk becomes ready right now.</summary>
        public void RefreshAll() { for (int i = 0; i < readyAt.Length; i++) readyAt[i] = TurnCount; }

        public void SetProtection(int x, int y) { ProtectX = x; ProtectY = y; }
        public void ClearProtection() { ProtectX = -1; ProtectY = -1; }

        /// <summary>Intercept an incoming shot if it lands on the protected cell (and the cruiser can still guide it).</summary>
        public bool TryIntercept(Board own, int x, int y)
        {
            if (!HasProtection || ProtectX != x || ProtectY != y) return false;
            if (Perks.IsDisabled(own, ShipType.Cruiser)) { ClearProtection(); return false; }
            ClearProtection(); // one counter-missile per placement
            return true;
        }
    }
}
