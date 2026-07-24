using System;
using System.Collections.Generic;

namespace Battleship
{
    /// <summary>
    /// Targeting AI. Easy = random; Medium = hunt (parity) + target around hits / extend lines;
    /// Hard = probability-density targeting (near-optimal). Tracks its own shot results only.
    /// </summary>
    public sealed class BattleshipAI
    {
        public enum Level { Easy, Medium, Hard }

        private const int N = Board.Size;
        private static readonly (int dx, int dy)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private readonly Level level;
        private readonly System.Random rng;
        private readonly bool[,] tried = new bool[N, N];
        private readonly bool[,] hit = new bool[N, N];
        private readonly List<(int x, int y)> openHits = new(); // hits not yet attributed to a sunk ship
        private readonly List<int> remaining = new() { 5, 4, 3, 3, 2 };
        private readonly Queue<(int x, int y)> intel = new();   // radar-revealed enemy ship cells

        public BattleshipAI(Level level, int seed)
        {
            this.level = level;
            rng = new System.Random(seed);
        }

        /// <summary>Radar scout result: a confirmed enemy ship cell — shoot it when convenient.</summary>
        public void AddIntel(int x, int y) => intel.Enqueue((x, y));

        /// <summary>The enemy relocated — our hits on these cells are now misses on empty water.</summary>
        public void ForgetCells(List<(int x, int y)> cells)
        {
            if (cells == null) return;
            foreach (var (x, y) in cells)
            {
                if (!Board.InBounds(x, y)) continue;
                tried[x, y] = true; hit[x, y] = false; // fired there, nothing there anymore
                openHits.RemoveAll(h => h.x == x && h.y == y);
            }
        }

        public (int x, int y) NextShot()
        {
            while (intel.Count > 0)
            {
                var c = intel.Peek();
                if (!tried[c.x, c.y]) return intel.Dequeue();
                intel.Dequeue();
            }
            if (level == Level.Easy) return RandomUntried();
            if (level == Level.Hard) return BestByProbability();
            var t = TargetShot();
            return t ?? RandomUntried();
        }

        public void Report(int x, int y, FireResult r)
        {
            tried[x, y] = true;
            if (r.Outcome == FireOutcome.Hit || r.Outcome == FireOutcome.Sunk)
            {
                hit[x, y] = true;
                openHits.Add((x, y));
            }
            if (r.Outcome == FireOutcome.Sunk)
            {
                remaining.Remove(Ships.Length(r.SunkShip));
                RemoveConnected(x, y);
            }
        }

        // --- Medium: target around open hits, preferring to extend a line ---
        private (int x, int y)? TargetShot()
        {
            if (openHits.Count == 0) return null;
            (int x, int y)? plain = null;
            foreach (var (hx, hy) in openHits)
            {
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = hx + dx, ny = hy + dy;
                    if (!Board.InBounds(nx, ny) || tried[nx, ny]) continue;
                    // If the cell on the opposite side is also a hit, this extends a line -> take it now.
                    if (Board.InBounds(hx - dx, hy - dy) && hit[hx - dx, hy - dy]) return (nx, ny);
                    plain ??= (nx, ny);
                }
            }
            return plain;
        }

        private (int x, int y) HuntParity()
        {
            var cells = new List<(int, int)>();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if (!tried[x, y] && ((x + y) & 1) == 0) cells.Add((x, y));
            if (cells.Count == 0) return RandomUntried();
            return cells[rng.Next(cells.Count)];
        }

        private (int x, int y) RandomUntried()
        {
            var cells = new List<(int, int)>();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if (!tried[x, y]) cells.Add((x, y));
            return cells.Count == 0 ? (0, 0) : cells[rng.Next(cells.Count)];
        }

        // --- Hard: probability density over all legal placements of remaining ships ---
        private (int x, int y) BestByProbability()
        {
            var heat = new double[N, N];
            bool targeting = openHits.Count > 0;

            foreach (int len in remaining)
            {
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                        foreach (var horiz in new[] { true, false })
                        {
                            int dx = horiz ? 1 : 0, dy = horiz ? 0 : 1;
                            bool fits = true; int coversHit = 0;
                            for (int i = 0; i < len; i++)
                            {
                                int cx = x + dx * i, cy = y + dy * i;
                                if (!Board.InBounds(cx, cy) || (tried[cx, cy] && !hit[cx, cy])) { fits = false; break; }
                                if (hit[cx, cy]) coversHit++;
                            }
                            if (!fits) continue;
                            if (targeting && coversHit == 0) continue; // focus known ships
                            double weight = 1 + coversHit * 50;
                            for (int i = 0; i < len; i++)
                            {
                                int cx = x + dx * i, cy = y + dy * i;
                                if (!tried[cx, cy]) heat[cx, cy] += weight;
                            }
                        }
            }

            double best = -1; (int x, int y) pick = (-1, -1);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if (!tried[x, y] && heat[x, y] > best) { best = heat[x, y]; pick = (x, y); }

            return best > 0 ? pick : RandomUntried();
        }

        private void RemoveConnected(int sx, int sy)
        {
            // Remove the orthogonally-connected group of hits containing (sx,sy) from openHits.
            var stack = new Stack<(int x, int y)>();
            stack.Push((sx, sy));
            var group = new HashSet<(int, int)>();
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (!group.Add((x, y))) continue;
                foreach (var (dx, dy) in Dirs)
                {
                    int nx = x + dx, ny = y + dy;
                    if (Board.InBounds(nx, ny) && hit[nx, ny] && !group.Contains((nx, ny)) && Contains(openHits, nx, ny))
                        stack.Push((nx, ny));
                }
            }
            openHits.RemoveAll(h => group.Contains(h));
        }

        private static bool Contains(List<(int x, int y)> list, int x, int y)
        {
            foreach (var c in list) if (c.x == x && c.y == y) return true;
            return false;
        }
    }
}
