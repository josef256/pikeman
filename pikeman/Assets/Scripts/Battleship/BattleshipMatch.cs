using System;
using System.Collections.Generic;

namespace Battleship
{
    /// <summary>
    /// Drives a single-player match: the player fires at EnemyBoard, the AI fires at PlayerBoard.
    /// Classic rules plus ship perks (see Perks). Relocate/Radar consume the turn, Scorch rides on
    /// the shot, Torpedo IS the shot, Intercept placement is free. Pure C#.
    /// </summary>
    public sealed class BattleshipMatch
    {
        public struct ShotEvent { public int X, Y; public FireResult Result; }

        public sealed class AiTurn
        {
            public readonly List<ShotEvent> Shots = new();
            public bool Relocated;                    // enemy destroyer moved — attacker markers vanished
            public bool Quiet;                        // enemy scouted instead of firing
            public List<(int x, int y)> Vacated;      // cells whose markers vanished (for UI cleanup)
            public int TorpedoCol = -1;               // >= 0: this turn was a torpedo run down that column
            public (int x, int y)? Revealed;          // online radar: a cell of MY fleet revealed to the opponent
        }

        public Board PlayerBoard { get; } = new();
        public Board EnemyBoard { get; } = new();
        public BattleshipAI Ai { get; private set; }
        public bool PlayerTurn { get; private set; } = true;

        public PerkSide PlayerPerks { get; } = new();
        public PerkSide EnemyPerks { get; } = new();
        public bool ScorchArmed { get; private set; }

        /// <summary>Everything the player's last action fired (main shot + scorch splash).</summary>
        public readonly List<ShotEvent> LastPlayerShots = new();

        private System.Random rng;
        private readonly HashSet<(int, int)> playerRadarSeen = new();
        private (int x, int y)? debugShot;

        /// <summary>DEBUG: hand the AI the turn and force its next shot onto this exact cell (bypasses its perk logic).</summary>
        public void DebugAiShoot(int x, int y)
        {
            if (Over || !Board.InBounds(x, y)) return;
            debugShot = (x, y);
            PlayerTurn = false;
        }

        public bool Over => PlayerBoard.AllSunk || EnemyBoard.AllSunk;
        public bool PlayerWon => EnemyBoard.AllSunk;

        /// <summary>Place the AI fleet and create the AI brain. The player fleet is placed separately.</summary>
        public void SetupEnemy(BattleshipAI.Level level, int seed)
        {
            EnemyBoard.PlaceRandomFleet(new System.Random(seed));
            Ai = new BattleshipAI(level, seed + 9973);
            rng = new System.Random(seed * 31 + 7);
        }

        /// <summary>Online 1v1 start: set which side shoots first, from THIS client's perspective
        /// (true = my turn). No AI is driven in online mode — BattleshipController skips the AI pump
        /// when online, and opponent moves arrive over the network instead.</summary>
        public void SetOnlineStart(bool myTurn)
        {
            PlayerTurn = myTurn;
            // Online never calls SetupEnemy, so seed the match RNG here. It only picks THIS client's own
            // radar-reveal cells (chosen locally by the defender, then sent explicitly) — nothing about it
            // needs to match the peer, so any seed is fine.
            rng ??= new System.Random(0x50494B45);   // "PIKE"
            EnemyBoard.InitFog();                     // my view of the opponent's fleet starts as pure fog
        }

        private void EndPlayerTurn() { PlayerPerks.TurnCount++; PlayerTurn = false; }

        /// <summary>Player ran out the turn clock: no shot fired, the turn just passes.</summary>
        public void PlayerPass()
        {
            if (Over || !PlayerTurn) return;
            LastPlayerShots.Clear();
            EndPlayerTurn();
        }

        private static readonly (int dx, int dy)[] Orth = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        private FireResult Resolve(Board board, PerkSide defender, int x, int y)
        {
            if (!board.IsShot(x, y) && defender.TryIntercept(board, x, y))
                return new FireResult(FireOutcome.Intercepted);
            return board.Fire(x, y);
        }

        // ---------------- player actions ----------------

        /// <summary>Player fires at the enemy board (scorch splash included if armed). Turn passes on a valid shot.</summary>
        public FireResult PlayerFire(int x, int y)
        {
            if (Over || !PlayerTurn) return new FireResult(FireOutcome.Invalid);
            var r = Resolve(EnemyBoard, EnemyPerks, x, y);
            if (r.Outcome == FireOutcome.Invalid) return r;
            LastPlayerShots.Clear();
            LastPlayerShots.Add(new ShotEvent { X = x, Y = y, Result = r });
            if (ScorchArmed)
            {
                ScorchArmed = false;
                PlayerPerks.Use(ShipType.Battleship);
                var s = PickSplash(EnemyBoard, x, y);
                if (s.HasValue)
                {
                    var sr = Resolve(EnemyBoard, EnemyPerks, s.Value.x, s.Value.y);
                    if (sr.Outcome != FireOutcome.Invalid)
                        LastPlayerShots.Add(new ShotEvent { X = s.Value.x, Y = s.Value.y, Result = sr });
                }
            }
            EndPlayerTurn();
            return r;
        }

        public bool ArmScorch(bool on)
        {
            if (on && (!PlayerTurn || Over || !PlayerPerks.Ready(PlayerBoard, ShipType.Battleship))) return false;
            ScorchArmed = on;
            return true;
        }

        /// <summary>Move the destroyer (damage travels, AI forgets its markers there). Consumes the turn.</summary>
        public bool PlayerRelocate(int x, int y, Orientation o)
        {
            if (Over || !PlayerTurn || !PlayerPerks.Ready(PlayerBoard, ShipType.Destroyer)) return false;
            var vacated = PlayerBoard.Relocate(ShipType.Destroyer, x, y, o);
            if (vacated == null) return false;
            Ai.ForgetCells(vacated);
            PlayerPerks.Use(ShipType.Destroyer);
            EndPlayerTurn();
            return true;
        }

        /// <summary>Carrier radar: reveal one random enemy ship cell. Consumes the turn. Null = nothing new to find.</summary>
        public (int x, int y)? PlayerRadar()
        {
            if (Over || !PlayerTurn || !PlayerPerks.Ready(PlayerBoard, ShipType.Carrier)) return null;
            var cell = PickRadarCell(EnemyBoard, playerRadarSeen);
            if (cell == null) return null; // nothing left to reveal — don't waste the perk
            playerRadarSeen.Add(cell.Value);
            PlayerPerks.Use(ShipType.Carrier);
            EndPlayerTurn();
            return cell;
        }

        /// <summary>Cruiser protection on any own cell (water bluffs allowed) except the cruiser's own center. FREE action.</summary>
        public bool PlayerSetIntercept(int x, int y)
        {
            if (Over || !Board.InBounds(x, y) || !PlayerPerks.Ready(PlayerBoard, ShipType.Cruiser)) return false;
            if (PlayerBoard.IsShot(x, y)) return false;
            var cc = PlayerBoard.ShipCells(ShipType.Cruiser);
            if (cc != null && cc.Count > 1 && cc[1] == (x, y)) return false; // can't protect its own brain
            PlayerPerks.SetProtection(x, y);
            PlayerPerks.Use(ShipType.Cruiser);
            return true;
        }

        /// <summary>
        /// Torpedo from an edge cell straight across; hits the FIRST undamaged ship cell in its path.
        /// An empty run reveals NOTHING (no miss markers). Consumes the turn either way.
        /// </summary>
        /// <summary>
        /// Player torpedo: pick a COLUMN — it always swims UP from the player's waters (bottom edge)
        /// and hits the FIRST undamaged ship cell it meets; destroyed cells are passed through.
        /// An empty run reveals NOTHING (no miss markers). Consumes the turn either way.
        /// </summary>
        public bool PlayerTorpedo(int col)
        {
            if (Over || !PlayerTurn || !PlayerPerks.Ready(PlayerBoard, ShipType.Submarine)) return false;
            if (col < 0 || col >= Board.Size) return false;
            LastPlayerShots.Clear();
            var t = TorpedoTarget(EnemyBoard, col, Board.Size - 1, (0, -1));
            if (t.HasValue)
            {
                var r = Resolve(EnemyBoard, EnemyPerks, t.Value.x, t.Value.y);
                if (r.Outcome != FireOutcome.Invalid)
                    LastPlayerShots.Add(new ShotEvent { X = t.Value.x, Y = t.Value.y, Result = r });
            }
            PlayerPerks.Use(ShipType.Submarine);
            EndPlayerTurn();
            return true;
        }

        private static (int x, int y)? TorpedoTarget(Board b, int ex, int ey, (int dx, int dy) d)
        {
            int x = ex, y = ey;
            while (Board.InBounds(x, y))
            {
                if (b.HasShip(x, y) && !b.IsCellDamaged(x, y)) return (x, y);
                x += d.dx; y += d.dy;
            }
            return null;
        }

        private (int x, int y)? PickSplash(Board b, int x, int y)
        {
            var opts = new List<(int, int)>();
            foreach (var (dx, dy) in Orth)
            {
                int nx = x + dx, ny = y + dy;
                if (Board.InBounds(nx, ny) && !b.IsShot(nx, ny)) opts.Add((nx, ny));
            }
            return opts.Count == 0 ? null : opts[rng.Next(opts.Count)];
        }

        private (int x, int y)? PickRadarCell(Board b, HashSet<(int, int)> seen)
        {
            var opts = new List<(int x, int y)>();
            for (int y = 0; y < Board.Size; y++)
                for (int x = 0; x < Board.Size; x++)
                    if (b.HasShip(x, y) && !b.IsShot(x, y) && !b.IsCellDamaged(x, y) && (seen == null || !seen.Contains((x, y))))
                        opts.Add((x, y));
            return opts.Count == 0 ? null : opts[rng.Next(opts.Count)];
        }

        // ---------------- AI turn ----------------

        /// <summary>AI takes its turn: may place protection (free), then relocate / scout / shoot (with scorch).</summary>
        public AiTurn AiFire()
        {
            var turn = new AiTurn();
            if (Over || PlayerTurn) return turn;

            if (debugShot != null)
            {
                var (fx, fy) = debugShot.Value; debugShot = null;
                var fr = Resolve(PlayerBoard, PlayerPerks, fx, fy);
                Ai.Report(fx, fy, fr);
                turn.Shots.Add(new ShotEvent { X = fx, Y = fy, Result = fr });
                EndAiTurn(); return turn;
            }

            AiMaybeProtect();

            // Damaged destroyer runs for it.
            if (EnemyBoard.HitsOn(ShipType.Destroyer) > 0 && EnemyPerks.Ready(EnemyBoard, ShipType.Destroyer) && AiRelocate(turn))
            { EndAiTurn(); return turn; }

            // Scout when planes are ready — but not robotically the moment they are (40% per turn).
            if (EnemyPerks.Ready(EnemyBoard, ShipType.Carrier) && rng.Next(100) < 40)
            {
                var cell = PickRadarCell(PlayerBoard, null);
                if (cell != null)
                {
                    Ai.AddIntel(cell.Value.x, cell.Value.y);
                    EnemyPerks.Use(ShipType.Carrier);
                    turn.Quiet = true;
                    EndAiTurn(); return turn;
                }
            }

            // Torpedo run (30% when ready): dives from the AI's side (top) down a random column.
            if (EnemyPerks.Ready(EnemyBoard, ShipType.Submarine) && rng.Next(100) < 30)
            {
                int col = rng.Next(Board.Size);
                EnemyPerks.Use(ShipType.Submarine);
                turn.TorpedoCol = col;
                var tt = TorpedoTarget(PlayerBoard, col, 0, (0, 1));
                if (tt.HasValue)
                {
                    var tr = Resolve(PlayerBoard, PlayerPerks, tt.Value.x, tt.Value.y);
                    if (tr.Outcome != FireOutcome.Invalid)
                    {
                        Ai.Report(tt.Value.x, tt.Value.y, tr);
                        turn.Shots.Add(new ShotEvent { X = tt.Value.x, Y = tt.Value.y, Result = tr });
                    }
                }
                EndAiTurn(); return turn;
            }

            bool scorch = EnemyPerks.Ready(EnemyBoard, ShipType.Battleship) && rng.Next(100) < 60;
            var (x, y) = Ai.NextShot();
            var r = Resolve(PlayerBoard, PlayerPerks, x, y);
            Ai.Report(x, y, r);
            turn.Shots.Add(new ShotEvent { X = x, Y = y, Result = r });
            if (scorch)
            {
                EnemyPerks.Use(ShipType.Battleship);
                var s = PickSplash(PlayerBoard, x, y);
                if (s.HasValue)
                {
                    var sr = Resolve(PlayerBoard, PlayerPerks, s.Value.x, s.Value.y);
                    if (sr.Outcome != FireOutcome.Invalid)
                    {
                        Ai.Report(s.Value.x, s.Value.y, sr);
                        turn.Shots.Add(new ShotEvent { X = s.Value.x, Y = s.Value.y, Result = sr });
                    }
                }
            }
            EndAiTurn();
            return turn;
        }

        private void EndAiTurn() { EnemyPerks.TurnCount++; PlayerTurn = true; }

        private void AiMaybeProtect()
        {
            if (!EnemyPerks.Ready(EnemyBoard, ShipType.Cruiser)) return;
            var cc = EnemyBoard.ShipCells(ShipType.Cruiser);
            var opts = new List<(int x, int y)>();
            // Prefer undamaged cells of a ship the player is already chewing on.
            foreach (ShipType t in Ships.Standard)
            {
                if (EnemyBoard.HitsOn(t) == 0 || EnemyBoard.IsSunk(t)) continue;
                foreach (var c in EnemyBoard.ShipCells(t))
                    if (!EnemyBoard.IsShot(c.x, c.y) && !EnemyBoard.IsCellDamaged(c.x, c.y) && (cc == null || cc.Count < 2 || cc[1] != c))
                        opts.Add(c);
            }
            if (opts.Count == 0)
            {
                foreach (ShipType t in Ships.Standard)
                {
                    if (EnemyBoard.IsSunk(t)) continue;
                    foreach (var c in EnemyBoard.ShipCells(t))
                        if (!EnemyBoard.IsShot(c.x, c.y) && !EnemyBoard.IsCellDamaged(c.x, c.y) && (cc == null || cc.Count < 2 || cc[1] != c))
                            opts.Add(c);
                }
            }
            if (opts.Count == 0) return;
            var pick = opts[rng.Next(opts.Count)];
            EnemyPerks.SetProtection(pick.x, pick.y);
            EnemyPerks.Use(ShipType.Cruiser);
        }

        private bool AiRelocate(AiTurn turn)
        {
            for (int g = 0; g < 200; g++)
            {
                var o = rng.Next(2) == 0 ? Orientation.Horizontal : Orientation.Vertical;
                int x = rng.Next(Board.Size), y = rng.Next(Board.Size);
                if (!EnemyBoard.CanRelocate(ShipType.Destroyer, x, y, o)) continue;
                var vacated = EnemyBoard.Relocate(ShipType.Destroyer, x, y, o);
                if (vacated == null) continue;
                EnemyPerks.Use(ShipType.Destroyer);
                turn.Relocated = true;
                turn.Vacated = vacated;
                return true;
            }
            return false;
        }

        // ---------------- online: apply an opponent's decided move (defender side) ----------------
        // Online the remote player takes the AI's structural seat: they attack my PlayerBoard and their
        // perk state lives in EnemyPerks. These methods take the attacker's DECISION (received over the
        // wire) and resolve it against my real board, returning the same AiTurn the render path already
        // animates. No AI runs and no RNG crosses the wire — every random attacker choice (scorch splash
        // cell, torpedo column) arrives explicitly in the message. Their mirror image on the outgoing
        // side is EnemyBoard's fog (RecordMyShot / ApplyKnownResult).

        /// <summary>Opponent fired at me, with an optional scorch splash cell THEY chose. Resolves against
        /// my board — my Intercept may counter it — and returns the shots for rendering.</summary>
        public AiTurn ApplyOpponentFire(int x, int y, bool scorch, (int x, int y)? splash)
        {
            var turn = new AiTurn();
            if (Over || PlayerTurn) return turn;
            var r = Resolve(PlayerBoard, PlayerPerks, x, y);
            if (r.Outcome != FireOutcome.Invalid)
                turn.Shots.Add(new ShotEvent { X = x, Y = y, Result = r });
            if (scorch)
            {
                EnemyPerks.Use(ShipType.Battleship);
                if (splash.HasValue)
                {
                    var sr = Resolve(PlayerBoard, PlayerPerks, splash.Value.x, splash.Value.y);
                    if (sr.Outcome != FireOutcome.Invalid)
                        turn.Shots.Add(new ShotEvent { X = splash.Value.x, Y = splash.Value.y, Result = sr });
                }
            }
            EndAiTurn();
            return turn;
        }

        /// <summary>Opponent torpedoed a column of my board. I own the layout, so I resolve which cell the
        /// fish strikes (first undamaged cell from the top — the opponent's side).</summary>
        public AiTurn ApplyOpponentTorpedo(int col)
        {
            var turn = new AiTurn();
            if (Over || PlayerTurn) return turn;
            EnemyPerks.Use(ShipType.Submarine);
            turn.TorpedoCol = col;
            if (col >= 0 && col < Board.Size)
            {
                var tt = TorpedoTarget(PlayerBoard, col, 0, (0, 1));
                if (tt.HasValue)
                {
                    var tr = Resolve(PlayerBoard, PlayerPerks, tt.Value.x, tt.Value.y);
                    if (tr.Outcome != FireOutcome.Invalid)
                        turn.Shots.Add(new ShotEvent { X = tt.Value.x, Y = tt.Value.y, Result = tr });
                }
            }
            EndAiTurn();
            return turn;
        }

        /// <summary>Opponent relocated their damaged destroyer. It moved on their real board; on my fog the
        /// vacated cells' hit markers revert to water — I never learn where it went.</summary>
        public AiTurn ApplyOpponentRelocate(List<(int x, int y)> vacated)
        {
            var turn = new AiTurn();
            if (Over || PlayerTurn) return turn;
            EnemyPerks.Use(ShipType.Destroyer);
            EnemyBoard.FogForget(vacated);
            turn.Relocated = true;
            turn.Vacated = vacated;
            EndAiTurn();
            return turn;
        }

        /// <summary>Opponent scouted my fleet. I own that info, so I pick the revealed cell here (in
        /// turn.Revealed, to send back for their fog). Consumes the turn as a quiet recon turn.</summary>
        public AiTurn ApplyOpponentRadar()
        {
            var turn = new AiTurn();
            if (Over || PlayerTurn) return turn;
            EnemyPerks.Use(ShipType.Carrier);
            turn.Quiet = true;
            turn.Revealed = PickRadarCell(PlayerBoard, null);
            EndAiTurn();
            return turn;
        }

        // ---- online: fold MY resolved move into the fog board and pass the turn ----

        /// <summary>Online: record the result of MY shot (resolved by the opponent) onto the fog board.
        /// For a sunk ship the opponent also sends the footprint. Does NOT end the turn — call
        /// <see cref="EndMyOnlineTurn"/> once all cells of the move are recorded (main + scorch splash).</summary>
        public void RecordMyShot(int x, int y, FireResult result, List<(int x, int y)> sunkCells = null)
            => EnemyBoard.ApplyKnownResult(x, y, result, sunkCells);

        /// <summary>Online: end MY turn after my move's results have been folded into the fog board.</summary>
        public void EndMyOnlineTurn() => EndPlayerTurn();
    }
}
