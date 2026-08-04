using System;
using System.Collections.Generic;

namespace Battleship
{
    public enum Orientation { Horizontal, Vertical }

    /// <summary>Standard fleet identities. Lengths via Ships.Length.</summary>
    public enum ShipType { Carrier, Battleship, Cruiser, Submarine, Destroyer }

    public enum FireOutcome { Invalid, Miss, Hit, Sunk, Intercepted }

    public static class Ships
    {
        public static int Length(ShipType t) => t switch
        {
            ShipType.Carrier => 5,
            ShipType.Battleship => 4,
            ShipType.Cruiser => 3,
            ShipType.Submarine => 3,
            ShipType.Destroyer => 2,
            _ => 0
        };

        public static readonly ShipType[] Standard =
        {
            ShipType.Carrier, ShipType.Battleship, ShipType.Cruiser, ShipType.Submarine, ShipType.Destroyer
        };
    }

    public readonly struct FireResult
    {
        public readonly FireOutcome Outcome;
        public readonly ShipType SunkShip; // meaningful only when Outcome == Sunk

        public FireResult(FireOutcome outcome, ShipType sunk = default)
        {
            Outcome = outcome;
            SunkShip = sunk;
        }
    }

    internal sealed class Ship
    {
        public ShipType Type;
        public readonly List<(int x, int y)> Cells = new();
        public readonly List<bool> CellHit = new(); // damage per cell index — survives relocation
        public int Hits { get { int c = 0; foreach (var h in CellHit) if (h) c++; return c; } }
        public bool Sunk => Cells.Count > 0 && Hits >= Cells.Count;
    }

    /// <summary>
    /// One player's 10x10 board: ship placement, incoming fire, and query helpers for rendering.
    /// Pure C#, no Unity dependency.
    /// </summary>
    public sealed class Board
    {
        public const int Size = 10;

        private readonly int[,] shipAt = new int[Size, Size]; // -1 = water, else index into ships
        private readonly bool[,] shot = new bool[Size, Size];
        private readonly List<Ship> ships = new();

        // ---- fog-of-war view (online attacker side): the opponent's board, learned only from results ----
        // A fog board is never Place()d — no real layout is held. Outcomes reported by the defender are
        // recorded via ApplyKnownResult. Real (placed) boards leave all of this empty, so every query
        // below behaves exactly as before for single-player.
        private bool fog;                                            // true = this is a fog view
        private int fogShipCount, fogSunkCount;                      // total / sunk enemy ships
        private readonly bool[,] fogHit = new bool[Size, Size];      // cell known to hold an enemy ship
        private readonly bool[,] fogSunkCell = new bool[Size, Size]; // cell of a fully-sunk enemy ship

        public Board() => Clear();

        public void Clear()
        {
            ships.Clear();
            fog = false; fogShipCount = fogSunkCount = 0;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                { shipAt[x, y] = -1; shot[x, y] = false; fogHit[x, y] = false; fogSunkCell[x, y] = false; }
        }

        public static bool InBounds(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size;
        public bool IsShot(int x, int y) => InBounds(x, y) && shot[x, y];
        public bool HasShip(int x, int y) => InBounds(x, y) && (shipAt[x, y] >= 0 || fogHit[x, y]);
        public int ShipCount => ships.Count;

        public int ShipsRemaining
        {
            get { if (fog) return fogShipCount - fogSunkCount; int c = 0; foreach (var s in ships) if (!s.Sunk) c++; return c; }
        }

        public bool AllSunk
        {
            get
            {
                if (fog) return fogShipCount > 0 && fogSunkCount >= fogShipCount;
                if (ships.Count == 0) return false; foreach (var s in ships) if (!s.Sunk) return false; return true;
            }
        }

        public bool CanPlace(ShipType type, int x, int y, Orientation o)
        {
            int len = Ships.Length(type);
            int dx = o == Orientation.Horizontal ? 1 : 0;
            int dy = o == Orientation.Vertical ? 1 : 0;
            for (int i = 0; i < len; i++)
            {
                int cx = x + dx * i, cy = y + dy * i;
                if (!InBounds(cx, cy) || shipAt[cx, cy] >= 0) return false;
            }
            return true;
        }

        /// <summary>Cell position of ship-cell #i (0 = stern). Vertical ships face UP — bow at the TOP (enemy is topside).</summary>
        private static (int x, int y) CellPos(int i, int x, int y, Orientation o, int len)
            => o == Orientation.Horizontal ? (x + i, y) : (x, y + (len - 1 - i));

        public bool Place(ShipType type, int x, int y, Orientation o)
        {
            if (!CanPlace(type, x, y, o)) return false;
            var ship = new Ship { Type = type };
            int idx = ships.Count;
            int len = Ships.Length(type);
            for (int i = 0; i < len; i++)
            {
                var (cx, cy) = CellPos(i, x, y, o, len);
                shipAt[cx, cy] = idx;
                ship.Cells.Add((cx, cy));
                ship.CellHit.Add(false);
            }
            ships.Add(ship);
            return true;
        }

        public FireResult Fire(int x, int y)
        {
            if (!InBounds(x, y) || shot[x, y]) return new FireResult(FireOutcome.Invalid);
            shot[x, y] = true;
            int idx = shipAt[x, y];
            if (idx < 0) return new FireResult(FireOutcome.Miss);
            var s = ships[idx];
            int ci = s.Cells.IndexOf((x, y));
            if (ci >= 0 && !s.CellHit[ci]) s.CellHit[ci] = true; // relocated pre-damaged cells don't double-count
            return s.Sunk ? new FireResult(FireOutcome.Sunk, s.Type) : new FireResult(FireOutcome.Hit);
        }

        // ---- perk support: per-ship queries + relocation with traveling damage ----

        private Ship Find(ShipType t) { foreach (var s in ships) if (s.Type == t) return s; return null; }

        public bool HasShipOfType(ShipType t) => Find(t) != null;
        public int HitsOn(ShipType t) => Find(t)?.Hits ?? 0;
        public bool IsSunk(ShipType t) => Find(t)?.Sunk ?? false;

        /// <summary>Is cell #index (0 = placement origin / stern) of this ship damaged?</summary>
        public bool ShipCellHit(ShipType t, int index)
        {
            var s = Find(t);
            return s != null && index >= 0 && index < s.CellHit.Count && s.CellHit[index];
        }

        public List<(int x, int y)> ShipCells(ShipType t)
        {
            var s = Find(t);
            return s == null ? null : new List<(int x, int y)>(s.Cells);
        }

        /// <summary>True if this ship cell is damaged (works even when the shot marker was cleared by a relocate).</summary>
        public bool IsCellDamaged(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            int idx = shipAt[x, y];
            if (idx < 0) return false;
            var s = ships[idx];
            int ci = s.Cells.IndexOf((x, y));
            return ci >= 0 && s.CellHit[ci];
        }

        public bool CanRelocate(ShipType t, int x, int y, Orientation o)
        {
            var s = Find(t);
            if (s == null || s.Sunk) return false;
            int len = s.Cells.Count;
            int dx = o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx;
            for (int i = 0; i < len; i++)
            {
                int cx = x + dx * i, cy = y + dy * i;
                if (!InBounds(cx, cy)) return false;
                int occ = shipAt[cx, cy];
                if (occ >= 0 && ships[occ] != s) return false; // may overlap its own old footprint
            }
            return true;
        }

        /// <summary>
        /// Move a ship to a new spot. Damage travels with it (by cell index); shot markers on the
        /// vacated cells REMAIN as misses — the attacker sees their hits turn to empty water
        /// (the ship escaped) but not where it went. Returns the vacated cells, or null if illegal.
        /// </summary>
        public List<(int x, int y)> Relocate(ShipType t, int x, int y, Orientation o)
        {
            if (!CanRelocate(t, x, y, o)) return null;
            var s = Find(t);
            int idx = ships.IndexOf(s);
            var vacated = new List<(int x, int y)>(s.Cells);
            foreach (var (ox, oy) in s.Cells) shipAt[ox, oy] = -1; // shot state stays: hit → miss on water
            s.Cells.Clear();
            int len = s.CellHit.Count;
            for (int i = 0; i < len; i++)
            {
                var (cx, cy) = CellPos(i, x, y, o, len);
                shipAt[cx, cy] = idx;
                s.Cells.Add((cx, cy)); // CellHit[i] kept — damage travels silently (no shot marker)
            }
            return vacated;
        }

        /// <summary>True if the cell holds a ship that is now fully sunk (for reveal on enemy board).</summary>
        public bool IsSunkAt(int x, int y)
        {
            if (fog) return InBounds(x, y) && fogSunkCell[x, y];
            int idx = shipAt[x, y];
            return idx >= 0 && ships[idx].Sunk;
        }

        public void PlaceRandomFleet(System.Random rng)
        {
            Clear();
            foreach (var t in Ships.Standard)
            {
                int guard = 0;
                while (guard++ < 10000)
                {
                    var o = rng.Next(2) == 0 ? Orientation.Horizontal : Orientation.Vertical;
                    int x = rng.Next(Size), y = rng.Next(Size);
                    if (Place(t, x, y, o)) break;
                }
            }
        }

        // ---- fog-of-war API (online: the attacker's view of the opponent's board) ----

        /// <summary>Turn this board into a fog view of the opponent's fleet. No layout is placed;
        /// outcomes arrive one shot at a time through <see cref="ApplyKnownResult"/>.</summary>
        public void InitFog(int enemyShipCount = 5)
        {
            Clear();
            fog = true;
            fogShipCount = enemyShipCount;
        }

        /// <summary>Fog board: record a shot's outcome as the defender reported it. On a Sunk result the
        /// defender also passes the whole ship footprint (<paramref name="sunkCells"/>) so the sink can be
        /// drawn on every one of its already-hit cells. No ship layout is ever inferred here.</summary>
        public void ApplyKnownResult(int x, int y, FireResult result, IReadOnlyList<(int x, int y)> sunkCells = null)
        {
            if (!fog || !InBounds(x, y)) return;
            shot[x, y] = true;
            switch (result.Outcome)
            {
                case FireOutcome.Hit:
                    fogHit[x, y] = true;
                    break;
                case FireOutcome.Sunk:
                    fogHit[x, y] = true;
                    fogSunkCount++;
                    if (sunkCells != null && sunkCells.Count > 0)
                        foreach (var c in sunkCells)
                        { if (!InBounds(c.x, c.y)) continue; shot[c.x, c.y] = true; fogHit[c.x, c.y] = true; fogSunkCell[c.x, c.y] = true; }
                    else
                        fogSunkCell[x, y] = true;
                    break;
                // Miss / Intercepted / Invalid: the shot marker alone (renders as water/miss).
            }
        }

        /// <summary>Fog board: the opponent relocated a ship out of these cells — their hit markers revert
        /// to water (misses), exactly what the defender's own board shows after a Relocate.</summary>
        public void FogForget(IReadOnlyList<(int x, int y)> cells)
        {
            if (!fog || cells == null) return;
            foreach (var c in cells)
                if (InBounds(c.x, c.y)) { fogHit[c.x, c.y] = false; fogSunkCell[c.x, c.y] = false; }
        }
    }
}
