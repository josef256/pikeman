using UnityEngine;
using Battleship;

namespace Battleship
{
    /// <summary>
    /// Battleship vs AI, two-screen flow, with pixel-art ship sprites (Resources/Ships).
    /// Setup: click a ship, move, click your grid to drop (R / right-click / Rotate to turn; drag a
    /// placed ship to move it). Play: YOUR FLEET ↔ TARGETING via a button; fire, then watch the reply.
    /// </summary>
    public class BattleshipController : MonoBehaviour
    {
        private enum Phase { Menu, Setup, Playing, Over }
        private enum PerkMode { None, Relocate, Intercept, Torpedo }
        private enum Lang { EN, FR }
        private struct FleetShip { public ShipType type; public bool placed; public int x, y; public Orientation o; }

        private BattleshipMatch match;
        private Phase phase = Phase.Setup;
        private BattleshipAI.Level difficulty = BattleshipAI.Level.Medium;
        private int seed = 1;
        private bool targeting;

        private FleetShip[] fleet;
        private string menuNote = "";       // transient home-menu status line (e.g. "coming soon")
        private float menuNoteUntil = -1f;
        private bool settingsOpen = false;  // settings panel overlaid on the home menu
        private int activeSlider = -1;      // which settings slider is being dragged (0=music, 1=sfx, -1=none)
        private bool netOpen = false;       // multiplayer connect overlay (Step 2)
        private PikeNet net;                // UGS connection layer (sign-in + 2-player session)
        private string netCodeEntry = "";   // room code the guest is typing in the JOIN field
        private bool online = false;        // true while in a networked 1v1 (disables the AI)
        private bool onlineWaiting = false; // pressed READY, waiting for the opponent's fleet
        private bool awaitingResult = false;// online: fired an intent, waiting for the defender's result
        private int pendingFireX, pendingFireY; // online: the cell my in-flight shot targeted
        private MoveKind pendingKind = MoveKind.Fire; // online: which move I'm awaiting a result for
        private int pendingCol;             // online: the column of an in-flight torpedo run
        private Lang lang = Lang.EN;        // UI language; persisted in PlayerPrefs, switched from Settings
        private int dragIdx = -1;
        private Orientation orient = Orientation.Horizontal;

        private string message = "";
        private float aiTimer;
        private float turnDeadline = -1f; // when the player's turn clock expires
        private bool prevPlayerTurn;
        private int aiShotX = -1, aiShotY = -1;
        private float lockUntil;                       // input locked while enemy action plays out on screen
        private bool InputLocked => Time.time < lockUntil;

        private GUIStyle big, hdr, mark, small, btn;
        private Font uiFont; // Press Start 2P — the official game font
        private Texture2D[] shipTex;
        private Texture2D waterTex;
        private Texture2D[] waterFrames;

        private bool showGrid = true;
        private bool debugHit = false;
        [System.NonSerialized] public bool debugMenu = false; // ESC toggles a debug window during play
        [Header("Water")]
        [Range(0f, 30f)] public float waterFps = 12f;
        [Range(4f, 40f)] public float explFps = 22f;
        [Header("Ship bob")]
        [Range(0f, 0.15f)] public float bobAmount = 0.05f;
        [Range(0f, 5f)] public float bobSpeed = 1.6f;
        [Header("Ship damage")]
        [Range(2f, 30f)] public float dmgFps = 12f;
        private Texture2D[][][] dmgFrames;
        private Texture2D[][][] dmgLoopFrames; // fire+smoke burn loop, plays forever once the hit anim ends
        [Range(2f, 30f)] public float dmgLoopFps = 10f;
        private float[,] hitT;

        [Header("Perk cooldowns (turns)")]
        public int relocateCd = 7;
        public int radarCd = 10;
        public int scorchCd = 5;
        public int interceptCd = 3;
        public int torpedoCd = 9;
        private PerkMode perkMode = PerkMode.None;
        private readonly System.Collections.Generic.HashSet<(int, int)> radarIntel = new();
        private int ctrPX, ctrPY; private float ctrPT = -1f; // counter-missile boom on own board
        private int ctrEX, ctrEY; private float ctrET = -1f; // your shot intercepted on enemy board

        [Header("Counter-missile")]
        [Range(2f, 30f)] public float mslSpeed = 12f;         // flight speed in cells/second
        [Range(4f, 30f)] public float lnchFps = 14f;          // cruiser hatch-launch anim fps
        private Texture2D mslNorth;                           // the one missile sprite, rotated to any headinger E,NE,N,NW,W,SW,S,SE
        private Texture2D[] lnchFrames;                       // Perk/CruiserLaunch/lNN — plays on cruiser mid cell
        private Texture2D[] boomFrames;                       // Perk/InterceptBoom/xNN — mid-air detonation
        private Vector2 mslFrom, mslTo;
        private float mslT0 = -1f, mslDur, mslAngle;
        [Header("Torpedo visual")]
        [Range(2f, 40f)] public float torpSpeed = 8f;         // swim speed, cells/second (slow enough to SEE)
        [Range(0.2f, 1f)] public float torpAlpha = 0.7f;      // underwater ghosting
        private Vector2 torpVisFrom, torpVisTo;
        private float torpVisT0 = -1f, torpVisDur;
        [Range(3f, 30f)] public float shellSpeed = 10f;       // radar shell blip, cells/second
        [Range(0f, 2f)] public float fireDelay = 0.55f;       // beat between the muzzle sound and the shell appearing (both sides)
        private struct ShellVis { public Vector2 from, to; public float t0, dur; } // one blip per shot (scorch = salvo of two)
        private readonly System.Collections.Generic.List<ShellVis> shellVis = new System.Collections.Generic.List<ShellVis>();
        private float[,] revealAt;                                // enemy-cell markers stay hidden until the shell lands
        private int torpHX = -1, torpHY; private float torpRevealT; // torpedo hit result hidden until the fish arrives
        private Vector2 eTorpFrom, eTorpTo;               // ENEMY torpedo crossing the player's fleet (top → bottom)
        private float eTorpT0 = -1f, eTorpDur;
        private float torpLoopStart = -1f, torpLoopEnd;   // torpedo audio loop window (runs until fish + trail are gone)
        [Header("Radar scope")]
        [Range(10f, 180f)] public float sweepSpeed = 65f;   // beam rotation, degrees/second
        [Range(0f, 1f)] public float radarBeepVol = 0.35f;
        [Range(0f, 0.3f)] public float crtBarAlpha = 0.09f; // rolling CRT refresh bar intensity (0 = off)
        [Range(0f, 0.9f)] public float glassDim = 0.50f;    // darkens the baked glass toward deep phosphor (0 = raw art)
        [Range(0f, 1f)] public float phosphorGlow = 0.45f;  // long phosphor persistence behind the sweep (0 = off)
        [Range(60f, 300f)] public float phosphorArc = 150f; // how far around the decaying glow reaches, degrees
        [Range(2f, 20f)] public float crtBarTime = 5f;      // seconds for the bar to climb the screen
        [Range(0f, 0.6f)] public float crtWarp = 0.22f;     // horizontal distortion strength (× cell) inside the band
        [Range(0.02f, 0.3f)] public float crtBarHeight = 0.06f; // band height as a fraction of the scope
        [Range(0f, 1f)] public float crtBlur = 0.7f;        // how smeared the content looks inside the band
        private Texture2D ringTex;                          // procedural thin circle
        private Texture2D bandTex;                          // procedural soft horizontal band
        private int debugMark; // ESC-menu marker painter: 0 off · 1 hit · 2 destroyed · 3 miss · 4 recon (screenshot staging)
        private bool paused;   // SPACE hard-freeze (timeScale 0) — every Time.time-driven anim holds its frame for screenshots
        private readonly System.Collections.Generic.List<(int x, int y, int kind)> debugMarks = new System.Collections.Generic.List<(int, int, int)>();
        private Texture2D markHit, markDestroyed, markMiss, markRecon; // PixelLab legend markers
        private Texture2D consoleTex; // UI/final — the COMPLETE console (header, frame, glass, slots baked in)
        private Texture2D menuTex;    // UI/MainMenu — home-screen backdrop (title, ships, radar, empty button plates baked in)
        private Texture2D fleetSetupTex; // UI/FleetPlacement — setup-screen backdrop (board frame, 5 empty berths, chips + button plates)
        private Texture2D frameTex;   // UI/finalframe — same console with the glass CUT OUT (top overlay, catches spills)
        // the scope glass inside the console art, as fractions (measured from the art)
        private const float HoleX = 0.0889f, HoleY = 0.1348f, HoleW = 0.6475f, HoleH = 0.6489f;
        private const float FleetHoleL = 0.0835f; // hole's TRUE left edge (art x342/4096) — fleet grid starts here
        private Rect glassRect, consoleRect; // fullscreen console layout
        private bool glassOn;                // radar glass wider than the grid this frame
        private GUIStyle radarTitle;         // header plate text
        private GUIStyle lgTitle, lgName, lgDesc; // legend panel typography
        private GUIStyle stVal;                   // status values, right-aligned
        private GUIStyle fleetBtnLbl;             // fleet-view button label
        private GUIStyle pkName, pkDesc, pkStat;  // perk card typography
        private Texture2D[] perkIconTex;          // Perk/Icons — scout, intercept, scorch, torpedo, relocate
                // right-column slot rects, as fractions of the shell art
        private static readonly Rect Slot1F = new Rect(0.8032f, 0.1025f, 0.1733f, 0.4077f); // LEGEND
        private static readonly Rect Slot2F = new Rect(0.8032f, 0.5176f, 0.1733f, 0.1855f); // STATUS
        private static readonly Rect Slot3F = new Rect(0.8098f, 0.7302f, 0.1577f, 0.0933f); // baked amber button plate
        private static readonly Rect Slot4F = new Rect(0.7871f, 0.8799f, 0.1807f, 0.0830f); // bottom-right amber plate → END TURN
        private static readonly Rect TimerF = new Rect(0.6079f, 0.8789f, 0.1367f, 0.0708f); // dark LCD slot → auto-end countdown
        private static readonly Rect TimerTabF = new Rect(0.6226f, 0.9625f, 0.1050f, 0.0220f); // little tab under it → TURN-END
        private static readonly Rect FleetLedsF = new Rect(0.0310f, 0.8782f, 0.1838f, 0.0911f); // bottom-left slot → fleet LED status
        private static readonly Rect MsgStripF = new Rect(0.236f, 0.891f, 0.332f, 0.070f);     // bottom-center strip → commentary
        private GUIStyle msgStyle;
        private string pendingMsg; private float pendingMsgAt; // result text held back until the shot actually lands
        private float fleetSqueeze = 1f; // fleet-on-glass h-stretch (grid+ships fill the glass; water samples corrected)
        private Texture2D[] wedgeFrames;                    // procedural sweep afterglow sector (dither shimmer frames)
        private Texture2D[] phosphorFrames;                 // long phosphor persistence behind the sweep
        private float phosphorArcBaked = -1f;               // arc the frames were generated with
        private GUIStyle radarLbl;                          // coordinate labels on the scope
        private float prevSweepA;
        private AudioClip clipBeep;

        [Header("Recon plane")]
        [Range(0.8f, 4f)] public float planeTime = 1.8f;      // flyover duration (s), reveal when it exits the top
        private Texture2D planeTex;
        private Texture2D planeRadarTex; // white silhouette of the plane, tinted phosphor on the scope
        private float planeT0 = -1f;
        private float ePlaneT0 = -1f; // ENEMY spotter plane sweeping the player's fleet (top → bottom)
        private int pendX, pendY; private float pendReveal = -1f; // radar contact waiting on the flyover
        [Header("Audio")]
        [Range(0f, 1f)] public float sfxVolume = 0.8f;
        [Range(0f, 1f)] public float musicVolume = 0.5f;  // home-menu theme level (independent of SFX)
        [Range(10f, 300f)] public float turnTime = 60f;   // YOUR turn clock — hits zero and the turn is skipped
        [Range(2f, 20f)] public float autoEndTurn = 8f;   // seconds after your action settles before the turn auto-ends
        private float actionEndT;                         // when the player's action visuals/audio are done
        private float planeSfxEndT;                       // when the recon plane engine audio finishes
        private AudioSource audioSrc, audioLoop, musicSrc; // one-shots + looping SFX channel (torpedo run) + music channel
        private AudioClip clipFire, clipSplash, clipSunk, clipTorpedo, clipTorpLaunch, clipTorpLoop, clipMissile, clipIntercept;
        private AudioClip clipPlane, clipPing, clipPeriscope, clipSail, clipClick, clipFlip, clipWin, clipLose;
        private AudioClip clipMenuTheme;                   // Sfx/MenuTheme — home-menu soundtrack (loops)
        private AudioClip[] clipHits;
        private readonly System.Collections.Generic.List<(float t, AudioClip c, float vol)> pendingSfx = new();

        [Header("Screen shake")]
        [Range(0f, 3f)] public float shakeAmount = 1f;    // master intensity (0 = off)
        [Range(0.1f, 1f)] public float shakeTime = 0.35f; // how long one shake lasts
        private readonly System.Collections.Generic.List<(float t, float mag)> shakes = new(); // scheduled to impact moments
        private float shakeT0 = -1f, shakeMag;

        [Header("Relocate sail")]
        [Range(2f, 15f)] public float relocSpeed = 7f;        // destroyer sail speed, cells/second
        [Range(0.05f, 0.6f)] public float relocTurnTime = 0.22f; // seconds per 90° turn
        private float relocT0 = -1f, relocDur;
        // sail path keyframe segments: ship CENTER (cell units) + heading angle per segment
        private readonly System.Collections.Generic.List<float> segDur = new();
        private readonly System.Collections.Generic.List<Vector2> segC0 = new(), segC1 = new();
        private readonly System.Collections.Generic.List<float> segA0 = new(), segA1 = new();
        private readonly System.Collections.Generic.List<bool> segHide = new(); // over-the-horizon stretch
        private float lnchT0 = -1f;
        // enemy shells: missiles dropping in from the top edge (enemy is always topside)
        private readonly System.Collections.Generic.List<(Vector2 from, Vector2 to, float t0, float dur)> incoming = new();
        // smoke trail: procedural soft puffs spawned behind every flying missile
        [Header("Missile trail")]
        [Range(5f, 60f)] public float trailRate = 28f;        // puffs spawned per second
        [Range(0.15f, 2f)] public float trailLife = 0.55f;    // dissipation time (s) per puff
        [Range(0.05f, 0.6f)] public float trailSize = 0.22f;  // puff start size (× cell)
        [Range(0f, 1f)] public float trailGrow = 0.4f;        // extra size gained while fading (× cell)
        [Range(0.1f, 1f)] public float trailAlpha = 0.55f;    // puff opacity
        private Texture2D puffTex;
        private Texture2D cursorTex; // procedural software cursor (OS cursor hidden — invisible to screen recorders)
        private readonly System.Collections.Generic.List<(Vector2 g, float t0)> puffs = new();
        private float puffTimer;


        private Texture2D[] explFrames;
        private float[,] eImpact, pImpact;



        private static readonly Color Water = new(0.16f, 0.34f, 0.52f);
        private static readonly Color ShipCol = new(0.42f, 0.45f, 0.50f);
        private static readonly Color HitCol = new(0.85f, 0.25f, 0.20f);
        private static readonly Color MissCol = new(0.72f, 0.82f, 0.92f);
        private static readonly Color SunkCol = new(0.45f, 0.10f, 0.10f);
        private static readonly Color OkTint = new(0.55f, 1f, 0.55f);
        private static readonly Color BadTint = new(1f, 0.5f, 0.5f);
        private static readonly Color Grid = new(0.05f, 0.10f, 0.16f);

        private float cs, m, boardY, boardX;
        private float csX, csY; // radar cells may be rectangular (grid stretched to fill the glass)

        private void Awake()
        {
            audioSrc = GetComponent<AudioSource>();
            if (audioSrc == null) audioSrc = gameObject.AddComponent<AudioSource>();
            audioSrc.playOnAwake = false;
            audioLoop = gameObject.AddComponent<AudioSource>();
            audioLoop.playOnAwake = false; audioLoop.loop = true;
            musicSrc = gameObject.AddComponent<AudioSource>();
            musicSrc.playOnAwake = false; musicSrc.loop = true;
            // restore player-chosen levels from a prior session (fall back to the Inspector defaults)
            sfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat("pk_sfxVol", sfxVolume));
            musicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat("pk_musicVol", musicVolume));
            lang = (Lang)Mathf.Clamp(PlayerPrefs.GetInt("pk_lang", 0), 0, 1);
            net = new PikeNet();
            net.OnMatchStart += BeginOnlineMatch;   // both fleets ready -> drop into Playing in sync
            net.OnMoveIntent += OnNetMoveIntent;    // opponent's decided move -> resolve on my board (defender)
            net.OnMoveResult += OnNetMoveResult;    // my shot's outcome -> fold into fog + animate (attacker)
            NewGame();
            phase = Phase.Menu; // first screen on launch is the home menu, not fleet setup
        }

        private void Sfx(AudioClip c, float vol = 1f)
        {
            if (c != null && audioSrc != null && sfxVolume > 0f) audioSrc.PlayOneShot(c, vol * sfxVolume);
        }
        private void QueueMsg(string m, float at) { pendingMsg = m; pendingMsgAt = at; }
        private void QueueSfx(float when, AudioClip c, float vol = 1f) { if (c != null) pendingSfx.Add((when, c, vol)); }
        private AudioClip RandomHit() => (clipHits == null || clipHits.Length == 0) ? null : clipHits[Random.Range(0, clipHits.Length)];

        private void NewGame()
        {
            match = new BattleshipMatch();
            match.SetupEnemy(difficulty, seed);
            online = false; onlineWaiting = false;   // single-player by default; StartOnlineSetup re-arms online
            awaitingResult = false;
            fleet = new FleetShip[Ships.Standard.Length];
            for (int i = 0; i < fleet.Length; i++) fleet[i] = new FleetShip { type = Ships.Standard[i], placed = false };
            dragIdx = -1; orient = Orientation.Horizontal; targeting = false;
            phase = Phase.Setup;
            aiShotX = aiShotY = -1;
            perkMode = PerkMode.None; radarIntel.Clear(); ctrPT = -1f; ctrET = -1f; mslT0 = -1f; lnchT0 = -1f;
            debugMark = 0; debugMarks.Clear();
            paused = false; Time.timeScale = 1f; AudioListener.pause = false; // never carry a screenshot freeze into a new game
            incoming.Clear(); puffs.Clear(); torpVisT0 = -1f; eTorpT0 = -1f; torpLoopStart = -1f; torpHX = -1; planeT0 = -1f; ePlaneT0 = -1f; pendReveal = -1f; relocT0 = -1f; shellVis.Clear();
            turnDeadline = -1f; prevPlayerTurn = false;
            pendingMsg = null;
            shakes.Clear(); shakeT0 = -1f; pendingSfx.Clear(); lockUntil = 0f; actionEndT = 0f;
            if (audioLoop != null) audioLoop.Stop();
            Perks.RelocateCd = relocateCd; Perks.RadarCd = radarCd; Perks.ScorchCd = scorchCd;
            Perks.InterceptCd = interceptCd; Perks.TorpedoCd = torpedoCd;
            eImpact = new float[Board.Size, Board.Size];
            pImpact = new float[Board.Size, Board.Size];
            hitT = new float[Board.Size, Board.Size];
            revealAt = new float[Board.Size, Board.Size];
            for (int x = 0; x < Board.Size; x++) for (int y = 0; y < Board.Size; y++) { eImpact[x, y] = -1f; pImpact[x, y] = -1f; hitT[x, y] = -1f; }
            message = "Click a ship, move, click the grid to drop it.";
        }

        /// <summary>DEBUG: skip setup — random fleet, start playing, open the radar. For automated screenshots.</summary>
        public void DebugJumpToRadar()
        {
            RandomizeFleet();
            SyncBoard();
            phase = Phase.Playing;
            targeting = true;
        }

        public void DebugJumpToFleet() // same, but landing on the fleet glass (screenshot verification)
        {
            RandomizeFleet();
            SyncBoard();
            phase = Phase.Playing;
            targeting = false;
        }

        // ── trailer scenarios (ESC menu): hand-placed fleet + scripted enemy volley through the
        // REAL incoming pipeline (launch sfx → diving missile → impact/splash/burn at arrival).
        // Purely visual — the match state is untouched, so takes are repeatable.
        private int scenIdx;

        public void DebugPlayScenario(int s)
        {
            for (int x = 0; x < Board.Size; x++)
                for (int y = 0; y < Board.Size; y++)
                { hitT[x, y] = -1f; pImpact[x, y] = -1f; eImpact[x, y] = -1f; }
            incoming.Clear(); shakes.Clear(); pendingSfx.Clear();

            void Place(ShipType t, int px, int py, Orientation o)
            { int i = FleetIndex(t); fleet[i].placed = true; fleet[i].x = px; fleet[i].y = py; fleet[i].o = o; }

            var shots = new System.Collections.Generic.List<(int x, int y, float at)>();
            switch (s)
            {
                case 0: // battleship pounding — repeated hits on one hull, brackets in the water
                    Place(ShipType.Carrier, 2, 2, Orientation.Horizontal);
                    Place(ShipType.Battleship, 3, 5, Orientation.Horizontal);
                    Place(ShipType.Cruiser, 8, 3, Orientation.Vertical);
                    Place(ShipType.Submarine, 1, 7, Orientation.Horizontal);
                    Place(ShipType.Destroyer, 6, 7, Orientation.Vertical);
                    shots.Add((4, 5, 0f)); shots.Add((2, 4, 1.8f)); shots.Add((5, 5, 3.6f));
                    shots.Add((4, 2, 5.4f)); shots.Add((7, 6, 7.2f)); shots.Add((3, 5, 9f));
                    break;
                case 1: // spread carnage — one hit on almost every ship
                    Place(ShipType.Carrier, 2, 3, Orientation.Vertical);
                    Place(ShipType.Battleship, 4, 2, Orientation.Horizontal);
                    Place(ShipType.Cruiser, 4, 7, Orientation.Horizontal);
                    Place(ShipType.Submarine, 8, 4, Orientation.Vertical);
                    Place(ShipType.Destroyer, 5, 4, Orientation.Horizontal);
                    shots.Add((2, 4, 0f)); shots.Add((6, 2, 1.8f)); shots.Add((3, 6, 3.6f));
                    shots.Add((5, 7, 5.4f)); shots.Add((8, 5, 7.2f)); shots.Add((7, 5, 9f));
                    break;
                case 2: // near misses — the enemy brackets the fleet, water everywhere, one lucky hit
                    Place(ShipType.Carrier, 3, 6, Orientation.Horizontal);
                    Place(ShipType.Battleship, 1, 2, Orientation.Vertical);
                    Place(ShipType.Cruiser, 4, 3, Orientation.Horizontal);
                    Place(ShipType.Submarine, 3, 8, Orientation.Horizontal);
                    Place(ShipType.Destroyer, 8, 1, Orientation.Vertical);
                    shots.Add((4, 5, 0f)); shots.Add((2, 3, 1.6f)); shots.Add((5, 4, 3.2f));
                    shots.Add((6, 7, 4.8f)); shots.Add((8, 2, 6.6f)); shots.Add((4, 6, 8.4f));
                    break;
                default: // devastation — heavy volley, the whole fleet burns
                    Place(ShipType.Carrier, 2, 4, Orientation.Horizontal);
                    Place(ShipType.Battleship, 4, 6, Orientation.Horizontal);
                    Place(ShipType.Cruiser, 8, 5, Orientation.Vertical);
                    Place(ShipType.Submarine, 2, 2, Orientation.Horizontal);
                    Place(ShipType.Destroyer, 2, 7, Orientation.Horizontal);
                    shots.Add((3, 4, 0f)); shots.Add((5, 4, 1.5f)); shots.Add((4, 6, 3f));
                    shots.Add((6, 6, 4.5f)); shots.Add((8, 6, 6f)); shots.Add((3, 2, 7.5f));
                    shots.Add((1, 6, 9f)); shots.Add((2, 7, 10.5f));
                    break;
            }
            SyncBoard();

            float t0 = Time.time + 1f; // a breath before the first launch — clean recording head
            foreach (var sh in shots)
            {
                float start = t0 + sh.at * 1.7f; // stretched spacing — each impact gets room to breathe
                float dur = Mathf.Max(0.25f, (sh.y + 2.5f) / Mathf.Max(1f, mslSpeed));
                float launch = start + fireDelay;
                float arr = launch + dur;
                incoming.Add((new Vector2(sh.x, -2.5f), new Vector2(sh.x, sh.y), launch, dur));
                QueueSfx(start, clipFire, 0.5f);
                if (match.PlayerBoard.HasShip(sh.x, sh.y)) // hit or splash decided by the actual layout
                { hitT[sh.x, sh.y] = arr; QueueShake(arr, 0.2f); QueueSfx(arr, RandomHit()); }
                else
                { pImpact[sh.x, sh.y] = arr; QueueShake(arr, 0.08f); QueueSfx(arr, clipSplash); }
            }
            phase = Phase.Playing; targeting = false; debugMenu = false;
            message = "🚀 INCOMING FIRE...";
        }


        private void LoadArt()
        {
            shipTex = new Texture2D[Ships.Standard.Length];
            foreach (ShipType t in System.Enum.GetValues(typeof(ShipType)))
                shipTex[(int)t] = Resources.Load<Texture2D>("Ships/" + t);
            waterTex = Resources.Load<Texture2D>("Ships/Water");
            var frames = new System.Collections.Generic.List<Texture2D>();
            for (int i = 0; i < 64; i++) { var t = Resources.Load<Texture2D>("Water/w" + i.ToString("00")); if (t == null) break; frames.Add(t); }
            waterFrames = frames.ToArray();
            var ef = new System.Collections.Generic.List<Texture2D>();
            for (int i = 0; i < 64; i++) { var t = Resources.Load<Texture2D>("Explosion/e" + i.ToString("00")); if (t == null) break; ef.Add(t); }
            explFrames = ef.ToArray();
            planeTex = Resources.Load<Texture2D>("Plane/plane");
            planeRadarTex = Resources.Load<Texture2D>("Plane/plane_radar");
            string[] pkIcons = { "scout", "intercept", "scorch", "torpedo", "relocate" };
            perkIconTex = new Texture2D[pkIcons.Length];
            for (int i = 0; i < pkIcons.Length; i++) perkIconTex[i] = Resources.Load<Texture2D>("Perk/Icons/" + pkIcons[i]);
            clipFire = Resources.Load<AudioClip>("Sfx/fire");
            clipSplash = Resources.Load<AudioClip>("Sfx/splash");
            clipHits = new[] { Resources.Load<AudioClip>("Sfx/hit1"), Resources.Load<AudioClip>("Sfx/hit2"), Resources.Load<AudioClip>("Sfx/hit3") };
            clipSunk = Resources.Load<AudioClip>("Sfx/sunk");
            clipTorpedo = Resources.Load<AudioClip>("Sfx/torpedo");
            clipTorpLaunch = Resources.Load<AudioClip>("Sfx/torplaunch");
            clipTorpLoop = Resources.Load<AudioClip>("Sfx/torploop");
            clipMissile = Resources.Load<AudioClip>("Sfx/missile");
            clipIntercept = Resources.Load<AudioClip>("Sfx/intercept");
            clipPlane = Resources.Load<AudioClip>("Sfx/plane");
            clipPing = Resources.Load<AudioClip>("Sfx/ping");
            clipPeriscope = Resources.Load<AudioClip>("Sfx/periscope");
            clipSail = Resources.Load<AudioClip>("Sfx/sail");
            clipClick = Resources.Load<AudioClip>("Sfx/click");
            clipFlip = Resources.Load<AudioClip>("Sfx/flip");
            clipWin = Resources.Load<AudioClip>("Sfx/win");
            clipLose = Resources.Load<AudioClip>("Sfx/lose");
            mslNorth = Resources.Load<Texture2D>("Missile/north"); // ONE sprite, rotated to any heading
            var lf = new System.Collections.Generic.List<Texture2D>();
            for (int i = 0; i < 64; i++) { var t = Resources.Load<Texture2D>("Perk/CruiserLaunch/l" + i.ToString("00")); if (t == null) break; lf.Add(t); }
            lnchFrames = lf.ToArray();
            var bf = new System.Collections.Generic.List<Texture2D>();
            for (int i = 0; i < 64; i++) { var t = Resources.Load<Texture2D>("Perk/InterceptBoom/x" + i.ToString("00")); if (t == null) break; bf.Add(t); }
            boomFrames = bf.ToArray();
            clipBeep = Resources.Load<AudioClip>("Sfx/beep");
            markHit = Resources.Load<Texture2D>("Radar/hit");
            markDestroyed = Resources.Load<Texture2D>("Radar/destroyed");
            markMiss = Resources.Load<Texture2D>("Radar/miss");
            markRecon = Resources.Load<Texture2D>("Radar/recon");
            consoleTex = Resources.Load<Texture2D>("UI/final");
            frameTex = Resources.Load<Texture2D>("UI/finalframe");
            menuTex = Resources.Load<Texture2D>("UI/MainMenu");
            fleetSetupTex = Resources.Load<Texture2D>("UI/FleetPlacement");
            clipMenuTheme = Resources.Load<AudioClip>("Sfx/MenuTheme");
            if (ringTex == null)
            {
                const int R = 128;
                ringTex = new Texture2D(R, R, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var rp2 = new Color[R * R];
                for (int y = 0; y < R; y++)
                    for (int x = 0; x < R; x++)
                    {
                        float d = Mathf.Sqrt((x - R * 0.5f) * (x - R * 0.5f) + (y - R * 0.5f) * (y - R * 0.5f)) / (R * 0.5f);
                        float a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.96f) / 0.025f); // thin circle line
                        rp2[y * R + x] = new Color(1f, 1f, 1f, a * a);
                    }
                ringTex.SetPixels(rp2); ringTex.Apply();
            }
            if (wedgeFrames == null)
            {
                const int W = 512; const float half = W * 0.5f, span = 52f;
                int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
                int[] offX = { 0, 1, 2, 3 }, offY = { 0, 2, 1, 3 }; // per-frame pattern shift = phosphor "boil"
                wedgeFrames = new Texture2D[4];
                for (int f = 0; f < 4; f++)
                {
                    var tex2 = new Texture2D(W, W, TextureFormat.RGBA32, false)
                    { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                    var wp = new Color[W * W];
                    for (int y = 0; y < W; y++)
                        for (int x = 0; x < W; x++)
                        {
                            float dx = x - half, dy = y - half;
                            float r = Mathf.Sqrt(dx * dx + dy * dy) / (half * 0.98f);
                            float a = 0f;
                            if (r <= 1f && (dx != 0f || dy != 0f))
                            {
                                float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg; // 0 = beam edge (+x), trail on the + side
                                if (ang >= 0f && ang <= span)
                                {
                                    if (ang < 3f) a = 1f;                         // solid hot edge right at the beam
                                    else
                                    {
                                        // smooth slow fade with a fine boiling grain riding on it
                                        float g = Mathf.Pow(1f - ang / span, 1.8f);
                                        float grain = (bayer[(x + offX[f]) & 3, (y + offY[f]) & 3] + 0.5f) / 16f - 0.5f;
                                        a = Mathf.Clamp01(g * (1f + grain * 0.5f));
                                    }
                                    a *= Mathf.Clamp01((1f - r) / 0.04f);         // soft rim
                                }
                            }
                            wp[y * W + x] = new Color(1f, 1f, 1f, a);
                        }
                    tex2.SetPixels(wp); tex2.Apply();
                    wedgeFrames[f] = tex2;
                }
            }
            if (bandTex == null)
            {
                const int B = 64;
                bandTex = new Texture2D(1, B, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var bp = new Color[B];
                for (int y = 0; y < B; y++)
                {
                    float a = Mathf.Sin(Mathf.PI * y / (B - 1f)); // soft rise and fall
                    bp[y] = new Color(1f, 1f, 1f, a * a);
                }
                bandTex.SetPixels(bp); bandTex.Apply();
            }
            if (puffTex == null)
            {
                const int P = 48;
                puffTex = new Texture2D(P, P, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var pxs = new Color[P * P];
                for (int y = 0; y < P; y++)
                    for (int x = 0; x < P; x++)
                    {
                        float d = Mathf.Sqrt((x - P * 0.5f) * (x - P * 0.5f) + (y - P * 0.5f) * (y - P * 0.5f)) / (P * 0.46f);
                        float a = Mathf.Clamp01(1f - d); a *= a;
                        pxs[y * P + x] = new Color(1f, 1f, 1f, a);
                    }
                puffTex.SetPixels(pxs); puffTex.Apply();
            }
            if (cursorTex == null)
            {
                // procedural phosphor-green stencil arrow — the game's software cursor.
                // '#' = green body, 'X' = near-black outline, '.' = transparent; tip at (0,0).
                string[] map =
                {
                    "X..........",
                    "XX.........",
                    "X#X........",
                    "X##X.......",
                    "X###X......",
                    "X####X.....",
                    "X#####X....",
                    "X######X...",
                    "X#######X..",
                    "X########X.",
                    "X#####XXXXX",
                    "X##X##X....",
                    "X#X.X##X...",
                    "XX..X##X...",
                    ".....X##X..",
                    ".....X##X..",
                    "......XX...",
                };
                int cw = map[0].Length, chh = map.Length;
                cursorTex = new Texture2D(cw, chh, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                var cp = new Color[cw * chh];
                Color body = new Color(0.36f, 0.95f, 0.5f), edge = new Color(0.01f, 0.09f, 0.04f);
                for (int y = 0; y < chh; y++)
                    for (int x = 0; x < cw; x++)
                        cp[(chh - 1 - y) * cw + x] = map[y][x] == '#' ? body : map[y][x] == 'X' ? edge : Color.clear;
                cursorTex.SetPixels(cp); cursorTex.Apply();
                // belt & suspenders: macOS sometimes force-shows the OS cursor despite
                // Cursor.visible=false — so the OS cursor ITSELF becomes a transparent image.
                var clearTex = new Texture2D(32, 32, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                clearTex.SetPixels32(new Color32[32 * 32]); // all (0,0,0,0)
                clearTex.Apply();
                Cursor.SetCursor(clearTex, Vector2.zero, CursorMode.ForceSoftware);
            }
            dmgFrames = new Texture2D[Ships.Standard.Length][][];
            dmgLoopFrames = new Texture2D[Ships.Standard.Length][][];
            foreach (ShipType t in System.Enum.GetValues(typeof(ShipType)))
            {
                int len = Ships.Length(t);
                var cells = new Texture2D[len][];
                var loops = new Texture2D[len][];
                bool any = false, anyLoop = false;
                for (int k = 0; k < len; k++)
                {
                    var lst = new System.Collections.Generic.List<Texture2D>();
                    for (int i = 0; i < 64; i++) { var tx = Resources.Load<Texture2D>("Damage/" + t + "/a" + (k + 1) + "_" + i.ToString("00")); if (tx == null) break; lst.Add(tx); }
                    cells[k] = lst.ToArray();
                    if (lst.Count > 0) any = true;
                    lst.Clear(); // burning-wreck loop: takes over after the one-shot hit anim ends
                    for (int i = 0; i < 64; i++) { var tx = Resources.Load<Texture2D>("Damage/" + t + "/Loop/a" + (k + 1) + "_" + i.ToString("00")); if (tx == null) break; lst.Add(tx); }
                    loops[k] = lst.ToArray();
                    if (lst.Count > 0) anyLoop = true;
                }
                dmgFrames[(int)t] = any ? cells : null;
                dmgLoopFrames[(int)t] = anyLoop ? loops : null;
            }
        }

        private bool AllPlaced() { foreach (var f in fleet) if (!f.placed) return false; return true; }

        // Home-menu soundtrack: loops while on the menu, fades out smoothly once the match begins.
        private void UpdateMenuMusic()
        {
            if (musicSrc == null || clipMenuTheme == null) return;
            bool wantMusic = phase == Phase.Menu && musicVolume > 0f;
            float target = wantMusic ? musicVolume : 0f;
            if (wantMusic && !musicSrc.isPlaying)
            { musicSrc.clip = clipMenuTheme; musicSrc.volume = 0f; musicSrc.Play(); }
            // ~0.8s fade either way (unscaled so a SPACE pause doesn't stall it)
            musicSrc.volume = Mathf.MoveTowards(musicSrc.volume, target, Time.unscaledDeltaTime / 0.8f);
            if (!wantMusic && musicSrc.isPlaying && musicSrc.volume <= 0.001f) musicSrc.Stop();
        }

        private void Update()
        {
            Cursor.visible = false; // the game draws its own cursor (end of OnGUI)
            net?.Tick();            // pump the Relay transport every frame (no-op when not connected)
            if (pendingMsg != null && Time.time >= pendingMsgAt) { message = pendingMsg; pendingMsg = null; }
            for (int i = pendingSfx.Count - 1; i >= 0; i--)
                if (Time.time >= pendingSfx[i].t) { Sfx(pendingSfx[i].c, pendingSfx[i].vol); pendingSfx.RemoveAt(i); }
            UpdateMenuMusic();
            if (phase != Phase.Playing) return;
            // radar sweep beeps: soft tick each rotation, stronger blip when the beam crosses a contact
            if (targeting && clipBeep != null)
            {
                float sa = (Time.time * sweepSpeed) % 360f;
                bool wrapped = sa < prevSweepA;
                if (wrapped) Sfx(clipBeep, radarBeepVol * 0.5f);
                bool contactBeeped = false;
                for (int cy2 = 0; cy2 < Board.Size && !contactBeeped; cy2++)
                    for (int cx2 = 0; cx2 < Board.Size && !contactBeeped; cx2++)
                    {
                        bool contact = (match.EnemyBoard.IsShot(cx2, cy2) && match.EnemyBoard.HasShip(cx2, cy2)) || radarIntel.Contains((cx2, cy2));
                        if (!contact) continue;
                        float ca = (Mathf.Atan2((cy2 + 0.5f - 5f) * csY, (cx2 + 0.5f - 5f) * csX) * Mathf.Rad2Deg + 360f) % 360f;
                        bool crossed = wrapped ? (ca > prevSweepA || ca <= sa) : (ca > prevSweepA && ca <= sa);
                        if (crossed) { Sfx(clipBeep, radarBeepVol); contactBeeped = true; }
                    }
                prevSweepA = sa;
            }

            // torpedo run loop: seamless whir from launch until the fish AND its trail have left the scene
            bool torpRunning = torpLoopStart >= 0f && Time.time >= torpLoopStart && Time.time < torpLoopEnd;
            if (torpRunning && !audioLoop.isPlaying && clipTorpLoop != null)
            { audioLoop.clip = clipTorpLoop; audioLoop.volume = sfxVolume; audioLoop.Play(); }
            else if (!torpRunning && audioLoop.isPlaying) { audioLoop.Stop(); torpLoopStart = -1f; }
            if (pendReveal >= 0f && Time.time >= pendReveal)
            {
                radarIntel.Add((pendX, pendY));
                message = string.Format(T("📡 CONTACT AT {0}!"), Coord(pendX, pendY));
                float pingAt = Mathf.Max(Time.time, planeSfxEndT) + 0.15f; // CONTACT! — only once the engine is gone
                QueueSfx(pingAt, clipPing);
                QueueSfx(pingAt + 0.7f, clipPing, 0.8f);
                pendReveal = -1f;
            }
            // TURN CLOCK: your turn is on a timer — let it run out and the turn is forfeited
            if (phase == Phase.Playing && !match.Over && !online)
            {
                if (match.PlayerTurn && (!prevPlayerTurn || turnDeadline < 0f))
                    turnDeadline = Mathf.Max(Time.time, lockUntil) + turnTime; // clock starts once the enemy visuals clear
                if (match.PlayerTurn && turnDeadline > 0f && Time.time >= turnDeadline && !InputLocked)
                    SkipTurn(true);
                prevPlayerTurn = match.PlayerTurn;
            }
            if (online) return;   // online: no local AI — the opponent's moves arrive over the network (Step 6)
            if (match.PlayerTurn || match.Over) return;
            aiTimer -= Time.deltaTime;
            if (aiTimer <= 0f)
                PlayIncomingTurn(match.AiFire());
        }

        // Render one resolved enemy turn — the AI's own move in single-player, or the opponent's
        // decided move arriving over the network (ApplyOpponent*). Both hand in the same AiTurn, so the
        // whole VFX / audio / message path below is shared verbatim (Step 6).
        private void PlayIncomingTurn(BattleshipMatch.AiTurn t)
        {
                targeting = false; // enemy turn begins — the ONLY auto-switch back to your ships screen
                float lockT = Time.time + 0.5f;
                if (t.Relocated)
                {
                    if (t.Vacated != null)
                        foreach (var c in t.Vacated) { eImpact[c.x, c.y] = -1f; radarIntel.Remove((c.x, c.y)); }
                    message = "THEIR DAMAGED SHIP ESCAPED!";
                    Sfx(clipSail, 0.6f);
                    lockT = Time.time + 1.4f;
                }
                else if (t.Quiet)
                {
                    message = "🛦 ENEMY RECON PLANE OVERHEAD!";
                    Sfx(clipPlane, 0.55f);
                    ePlaneT0 = Time.time; // their plane crosses YOUR fleet, coming from their side (top)
                    lockT = Time.time + planeTime + 0.3f;
                }
                else
                {
                    bool torp = t.TorpedoCol >= 0;
                    string msg = "";
                    if (torp && t.Shots.Count == 0)
                    {
                        // empty run: the wake crosses your whole column (the loop audio rides along)
                        eTorpFrom = new Vector2(t.TorpedoCol, -1.4f);
                        eTorpTo = new Vector2(t.TorpedoCol, Board.Size + 0.8f);
                        eTorpT0 = Time.time; eTorpDur = (Board.Size + 2.2f) / Mathf.Max(1f, torpSpeed);
                        torpLoopStart = eTorpT0; torpLoopEnd = eTorpT0 + eTorpDur + trailLife;
                        QueueSfx(eTorpT0, clipTorpLaunch, 0.6f); // distant tube thunk from their side
                        lockT = Mathf.Max(lockT, eTorpT0 + eTorpDur + 0.3f);
                        msg = "ENEMY TORPEDO: NO HIT.";
                    }
                    for (int i = 0; i < t.Shots.Count; i++)
                    {
                        var ev = t.Shots[i];
                        if (i == 0) { aiShotX = ev.X; aiShotY = ev.Y; }
                        float start = Time.time + i * 0.3f; // stagger the scorch splash shell
                        if (ev.Result.Outcome == FireOutcome.Intercepted)
                        {
                            float meet = LaunchCounterMissile(ev.X, ev.Y); // hatch anim + counter flight
                            if (!torp) incoming.Add((new Vector2(ev.X, -2.5f), new Vector2(ev.X, ev.Y), Time.time, Mathf.Max(0.3f, meet - Time.time)));
                            QueueShake(meet, 0.25f);
                            lockT = Mathf.Max(lockT, meet + 0.8f);
                        }
                        else if (torp)
                        {
                            // visible fish dives from their side to the impact cell
                            if (ev.Result.Outcome == FireOutcome.Hit || ev.Result.Outcome == FireOutcome.Sunk)
                            {
                                eTorpFrom = new Vector2(ev.X, -1.4f); eTorpTo = new Vector2(ev.X, ev.Y);
                                eTorpT0 = start; eTorpDur = Mathf.Max(0.4f, (ev.Y + 1.4f) / Mathf.Max(1f, torpSpeed));
                                float arr = eTorpT0 + eTorpDur;
                                torpLoopStart = eTorpT0; torpLoopEnd = arr; // whir cuts at impact
                                QueueSfx(eTorpT0, clipTorpLaunch, 0.6f);
                                hitT[ev.X, ev.Y] = arr; QueueShake(arr, ev.Result.Outcome == FireOutcome.Sunk ? 0.35f : 0.22f);
                                QueueSfx(arr, RandomHit());
                                if (ev.Result.Outcome == FireOutcome.Sunk) QueueSfx(arr + 0.3f, clipSunk);
                                lockT = Mathf.Max(lockT, arr + 0.8f);
                            }
                        }
                        else
                        {
                            float dur = Mathf.Max(0.25f, (ev.Y + 2.5f) / Mathf.Max(1f, mslSpeed));
                            float launch = start + fireDelay; // their guns thunder first, missile shows up a beat later
                            incoming.Add((new Vector2(ev.X, -2.5f), new Vector2(ev.X, ev.Y), launch, dur));
                            QueueSfx(start, clipFire, 0.5f); // enemy guns, distant
                            if (ev.Result.Outcome == FireOutcome.Miss)
                            { pImpact[ev.X, ev.Y] = launch + dur; QueueShake(launch + dur, 0.08f); QueueSfx(launch + dur, clipSplash); }
                            if (ev.Result.Outcome == FireOutcome.Hit || ev.Result.Outcome == FireOutcome.Sunk)
                            {
                                hitT[ev.X, ev.Y] = launch + dur; QueueShake(launch + dur, ev.Result.Outcome == FireOutcome.Sunk ? 0.35f : 0.2f);
                                QueueSfx(launch + dur, RandomHit());
                                if (ev.Result.Outcome == FireOutcome.Sunk) QueueSfx(launch + dur + 0.3f, clipSunk);
                            }
                            lockT = Mathf.Max(lockT, launch + dur + 0.7f);
                        }
                        string part = ev.Result.Outcome == FireOutcome.Intercepted ? T("INTERCEPTED!") : Describe(ev.Result, false);
                        msg += (i == 0 ? "" : "   ") + Coord(ev.X, ev.Y) + ": " + part;
                    }
                    // action first, result only when the ordnance actually lands
                    message = torp ? "🐟 ENEMY TORPEDO INBOUND..." : "🚀 INCOMING FIRE...";
                    QueueMsg(msg, lockT - 0.4f);
                }
                lockUntil = lockT;
                if (match.PlayerBoard.AllSunk)
                {
                    phase = Phase.Over;
                    QueueMsg(online ? "YOUR FLEET IS DESTROYED - YOU LOSE." : "YOUR FLEET IS DESTROYED — AI WINS.", lockT);
                    QueueSfx(Time.time + 1.3f, clipLose);
                }
        }

        private void EnsureStyles()
        {
            if (big != null) return;
            uiFont = Resources.Load<Font>("Fonts/PressStart2P");
            big = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            hdr = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            mark = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            mark.normal.textColor = Color.white;
            small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            small.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            if (uiFont != null)
            {
                // the pixel font carries its own weight — no synthetic bold
                big.font = uiFont; big.fontStyle = FontStyle.Normal;
                hdr.font = uiFont; hdr.fontStyle = FontStyle.Normal;
                mark.font = uiFont; mark.fontStyle = FontStyle.Normal;
                small.font = uiFont;
                btn.font = uiFont; btn.fontStyle = FontStyle.Normal;
            }
        }

        private static string Coord(int x, int y) => ((char)('A' + x)).ToString() + (y + 1);
        private string Describe(FireResult r, bool atEnemy) => r.Outcome switch
        {
            FireOutcome.Miss => T("MISS"),
            FireOutcome.Hit => T("HIT!"),
            FireOutcome.Sunk => string.Format(T("SUNK {0} {1}!"), T(atEnemy ? "THEIR" : "YOUR"), T(r.SunkShip.ToString().ToUpperInvariant())),
            _ => ""
        };
        private static Orientation Flip(Orientation o) => o == Orientation.Horizontal ? Orientation.Vertical : Orientation.Horizontal;

        private bool[,] Occupied(int except)
        {
            var occ = new bool[Board.Size, Board.Size];
            for (int i = 0; i < fleet.Length; i++)
            {
                if (!fleet[i].placed || i == except) continue;
                int len = Ships.Length(fleet[i].type);
                int dx = fleet[i].o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx;
                for (int k = 0; k < len; k++) { int cx = fleet[i].x + dx * k, cy = fleet[i].y + dy * k; if (Board.InBounds(cx, cy)) occ[cx, cy] = true; }
            }
            return occ;
        }

        private bool CanPlace(int idx, int x, int y, Orientation o)
        {
            int len = Ships.Length(fleet[idx].type);
            int dx = o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx;
            var occ = Occupied(idx);
            for (int k = 0; k < len; k++) { int cx = x + dx * k, cy = y + dy * k; if (!Board.InBounds(cx, cy) || occ[cx, cy]) return false; }
            return true;
        }

        private void RandomizeFleet()
        {
            for (int i = 0; i < fleet.Length; i++) fleet[i].placed = false;
            var rng = new System.Random(unchecked(seed++ * 48611 + 13));
            for (int i = 0; i < fleet.Length; i++)
                for (int g = 0; g < 5000; g++)
                {
                    var o = rng.Next(2) == 0 ? Orientation.Horizontal : Orientation.Vertical;
                    int x = rng.Next(Board.Size), y = rng.Next(Board.Size);
                    if (CanPlace(i, x, y, o)) { fleet[i] = new FleetShip { type = fleet[i].type, placed = true, x = x, y = y, o = o }; break; }
                }
        }

        private void SyncBoard()
        {
            match.PlayerBoard.Clear();
            foreach (var f in fleet) if (f.placed) match.PlayerBoard.Place(f.type, f.x, f.y, f.o);
        }

        private void OnGUI()
        {
            if (match == null) NewGame();
            EnsureStyles();
            if (shipTex == null) LoadArt();

            if (phase == Phase.Menu) { DrawHomeMenu(Event.current); return; }

            bool showTarget = (targeting && phase == Phase.Playing) || phase == Phase.Over;
            bool console = consoleTex != null && phase == Phase.Playing; // the shell wraps BOTH play views
            glassOn = console && showTarget; // rectangular radar cells only on the scope
            if (console)
            {
                // the high-res shell is STRETCHED over the entire window (user's call — fullscreen console)
                consoleRect = new Rect(0f, 0f, Screen.width, Screen.height);
                float gx0 = HoleX * Screen.width, gw = HoleW * Screen.width;
                float gy0 = HoleY * Screen.height, gh = HoleH * Screen.height;
                glassRect = new Rect(Mathf.Floor(gx0), Mathf.Floor(gy0), Mathf.Ceil(gw), Mathf.Ceil(gh));
                if (showTarget)
                {
                    // like the ref: 11 grid columns (letters get a boxed column) + numbers floating above the grid
                    csX = glassRect.width / 11f;
                    csY = glassRect.height / 10.6f;
                    cs = Mathf.Max(20f, Mathf.Min(csX, csY)); // square reference for marker/font sizes
                }
                else
                {
                    // fleet on the glass: laid out square from the glass height, then the whole
                    // layer is h-stretched to FILL the glass (user's call — 4K sprites hold up)
                    cs = Mathf.Floor(Mathf.Max(20f, glassRect.height / 10f));
                    csX = csY = cs;
                }
            }
            else
            {
                float csH = (Screen.height - 16f) / 10f;
                float csW = (Screen.width - 320f - 40f) / 10f;
                cs = Mathf.Floor(Mathf.Max(20f, Mathf.Min(csH, csW)));
                csX = csY = cs;
            }
            float fb = Mathf.Clamp(cs, 34f, 78f);
            m = Mathf.Floor(fb * 0.4f);
            big.fontSize = Mathf.RoundToInt(fb * 0.7f);
            hdr.fontSize = Mathf.RoundToInt(fb * 0.44f);
            mark.fontSize = Mathf.Max(14, Mathf.RoundToInt(cs * 0.5f));
            small.fontSize = Mathf.RoundToInt(fb * 0.36f);
            btn.fontSize = Mathf.RoundToInt(fb * 0.4f);

            float boardW = 10f * cs;
            float panelX;
            if (console)
            {
                if (showTarget)
                {
                    boardX = Mathf.Floor(glassRect.x + csX);        // playable grid starts after the letter column
                    boardY = Mathf.Floor(glassRect.y + 0.6f * csY); // and below the floating number row
                }
                else
                {
                    boardX = Mathf.Floor(glassRect.x); // pre-stretch content space: board at glass origin
                    boardY = Mathf.Floor(glassRect.y + (glassRect.height - boardW) * 0.5f);
                }
                panelX = Screen.width; // console views are panel-free
            }
            else
            {
                boardX = 8f;
                boardY = Mathf.Floor((Screen.height - boardW) / 2f);
                panelX = Mathf.Floor(boardX + boardW + Mathf.Max(20f, cs * 0.4f));
            }
            float pw = Screen.width - panelX - 12f;

            // screen shake: fire any due impacts, then jolt the BOARD only (panel stays still)
            for (int i = shakes.Count - 1; i >= 0; i--)
                if (Time.time >= shakes[i].t)
                {
                    if (shakeT0 < 0f || shakes[i].mag >= shakeMag || Time.time - shakeT0 > shakeTime)
                    { shakeMag = shakes[i].mag; shakeT0 = Time.time; }
                    shakes.RemoveAt(i);
                }
            if (shakeT0 >= 0f)
            {
                float sk = (Time.time - shakeT0) / Mathf.Max(0.05f, shakeTime);
                if (sk >= 1f) shakeT0 = -1f;
                else
                {
                    float damp = (1f - sk) * (1f - sk);
                    float amp = shakeMag * cs * shakeAmount * damp;
                    boardX += (Mathf.PerlinNoise(Time.time * 28f, 0.31f) - 0.5f) * 2f * amp;
                    boardY += (Mathf.PerlinNoise(0.73f, Time.time * 28f) - 0.5f) * 2f * amp;
                }
            }

            Event e = Event.current;
            Vector2 mp = e.mousePosition;
            if ((dragIdx >= 0 || perkMode == PerkMode.Relocate) &&
                ((e.type == EventType.KeyDown && e.keyCode == KeyCode.R) || (e.type == EventType.MouseDown && e.button == 1)))
            { orient = Flip(orient); e.Use(); }
            // ESC toggles the DEBUG window (view switching lives on the console buttons)
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            { debugMenu = !debugMenu; Sfx(clipClick, 0.6f); e.Use(); }
            // SPACE hard-freezes the sim for screenshots — Time.time stops, so water, fire,
            // sweep, missiles and blinks all hold their exact frame; no overlay, shots stay clean
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Space)
            { paused = !paused; Time.timeScale = paused ? 0f : 1f; AudioListener.pause = paused; e.Use(); }

            if (phase == Phase.Setup)
            {
                DrawFleetSetup(e, mp);          // full framed setup screen (backdrop + grid + dock + controls)
                if (debugMenu) DrawDebugMenu();
                if (e.type == EventType.Repaint && cursorTex != null)
                {
                    float chS = Mathf.Max(18f, Screen.height * 0.03f);
                    GUI.DrawTexture(new Rect(e.mousePosition.x, e.mousePosition.y, chS * cursorTex.width / cursorTex.height, chS), cursorTex, ScaleMode.StretchToFill);
                }
                return;
            }
            if (showTarget)
            {
                if (console)
                {
                    // fullscreen shell → radar content on the glass → frame overlay eats any spill
                    if (e.type == EventType.Repaint) GUI.DrawTexture(consoleRect, consoleTex, ScaleMode.StretchToFill);
                    DrawEnemyGrid(boardX, boardY);
                    if (e.type == EventType.Repaint && frameTex != null) GUI.DrawTexture(consoleRect, frameTex, ScaleMode.StretchToFill);
                    // header title on the shell's top plate (plate rect measured from the art)
                    if (e.type == EventType.Repaint)
                    {
                        if (radarTitle == null)
                        {
                            radarTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                            radarTitle.normal.textColor = new Color(0.32f, 0.78f, 0.42f, 0.92f); // muted phosphor, like the prototype
                            if (uiFont != null) radarTitle.font = uiFont;
                        }
                        var hr = new Rect(Screen.width * 0.252f, Screen.height * 0.008f, Screen.width * 0.492f, Screen.height * 0.046f);
                        radarTitle.fontSize = Mathf.Max(10, Mathf.RoundToInt(hr.height * 0.5f));
                        GUI.Label(hr, T("TACTICAL RADAR"), radarTitle);
                        DrawLegendPanel(); // radar keeps its original right column: legend + status
                        DrawStatusPanel();
                        DrawFleetLeds();
                        DrawMessageStrip();
                    }
                    DrawFleetButton(); // interactive — must run on ALL events, not just Repaint
                    DrawTurnTimer();
                    DrawEndTurnButton();
                }
                else DrawEnemyGrid(boardX, boardY);
            }
            else
            {
                if (console && e.type == EventType.Repaint) GUI.DrawTexture(consoleRect, consoleTex, ScaleMode.StretchToFill);
                Matrix4x4 fleetM = GUI.matrix;
                Vector2 mpF = mp;
                if (console)
                {
                    // stretch the square board horizontally to FILL the glass, exactly like the radar.
                    // GUI.matrix scales drawing but NOT Event.mousePosition — convert it by hand.
                    // FleetHoleL: the hole's true left edge (art x342/4096) sits LEFT of HoleX — the
                    // grid's left border goes to the extreme left; the right edge stays flush.
                    float shiftL = glassRect.x - FleetHoleL * Screen.width;
                    float sxF = (glassRect.width + shiftL) / (10f * cs);
                    fleetSqueeze = sxF;
                    float offX = glassRect.x * (1f - sxF) - shiftL;
                    GUI.matrix = Matrix4x4.TRS(new Vector3(offX, 0f, 0f), Quaternion.identity,
                                               new Vector3(sxF, 1f, 1f)) * fleetM;
                    mpF = new Vector2((mp.x - offX) / sxF, mp.y);
                }
                DrawBoardBase(boardX, boardY); DrawPlacedShips(boardX, boardY); DrawShots(boardX, boardY);
                DrawFleetOverlays(boardX, boardY);
                if (perkMode == PerkMode.Relocate) HandleRelocate(boardX, boardY, e, mpF);
                else if (perkMode == PerkMode.Intercept) HandleIntercept(boardX, boardY, e, mpF);
                else if (debugHit) HandleDebugHit(boardX, boardY, e, mpF);
                GUI.matrix = fleetM;
                fleetSqueeze = 1f;
                if (console) // same console as the radar — only the glass content differs
                {
                    if (e.type == EventType.Repaint)
                    {
                        // inner glass shadow: soft dark falloff around the glass edge so the ocean
                        // sits BEHIND the bezel instead of running hard into it.
                        // anchored to the hole's TRUE left (FleetHoleL) — the grid now starts there;
                        // anchoring to glassRect.x paints a shadow seam 14px inside the water.
                        Color pcV = GUI.color;
                        float vx0 = FleetHoleL * Screen.width, vw = glassRect.xMax - vx0;
                        float vd = glassRect.height * 0.05f; const int VL = 8; float vs = vd / VL;
                        for (int vi = 0; vi < VL; vi++)
                        {
                            float va = 0.36f * (1f - (float)vi / VL) * (1f - (float)vi / VL);
                            GUI.color = new Color(0f, 0.02f, 0.03f, va);
                            GUI.DrawTexture(new Rect(vx0, glassRect.y + vi * vs, vw, vs), Texture2D.whiteTexture);
                            GUI.DrawTexture(new Rect(vx0, glassRect.yMax - (vi + 1) * vs, vw, vs), Texture2D.whiteTexture);
                            GUI.DrawTexture(new Rect(vx0 + vi * vs, glassRect.y, vs, glassRect.height), Texture2D.whiteTexture);
                            GUI.DrawTexture(new Rect(glassRect.xMax - (vi + 1) * vs, glassRect.y, vs, glassRect.height), Texture2D.whiteTexture);
                        }
                        GUI.color = pcV;
                    }
                    if (e.type == EventType.Repaint && frameTex != null) GUI.DrawTexture(consoleRect, frameTex, ScaleMode.StretchToFill);
                    if (e.type == EventType.Repaint)
                    {
                        if (radarTitle == null)
                        {
                            radarTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                            radarTitle.normal.textColor = new Color(0.32f, 0.78f, 0.42f, 0.92f);
                            if (uiFont != null) radarTitle.font = uiFont;
                        }
                        var hr2 = new Rect(Screen.width * 0.252f, Screen.height * 0.008f, Screen.width * 0.492f, Screen.height * 0.046f);
                        radarTitle.fontSize = Mathf.Max(10, Mathf.RoundToInt(hr2.height * 0.5f));
                        GUI.Label(hr2, T("YOUR FLEET"), radarTitle);
                        DrawStatusPanel(); // mid recess under the cards — same readout as the radar side
                        DrawFleetLeds();
                        DrawMessageStrip();
                    }
                    DrawPerkCards(); // perk cards live on the fleet — intercept/relocate pick cells right here
                    DrawFleetButton();
                    DrawTurnTimer();
                    DrawEndTurnButton();
                }
            }

            if (!console) DrawPanel(panelX, pw, showTarget, e, mp); // console views are panel-free
            if (debugMenu) DrawDebugMenu();

            // software cursor — last draw of the frame so it rides on top of everything.
            // Point-filtered chunky pixels, tip at the mouse point; the OS cursor is hidden
            // (Update), so screen recorders capture only this one.
            if (e.type == EventType.Repaint && cursorTex != null)
            {
                float chS = Mathf.Max(18f, Screen.height * 0.03f);
                float cwS = chS * cursorTex.width / cursorTex.height;
                GUI.DrawTexture(new Rect(e.mousePosition.x, e.mousePosition.y, cwS, chS), cursorTex, ScaleMode.StretchToFill);
            }
        }

        // Home menu — the first screen on launch. The MainMenu.png art (title, ships, radar sweep,
        // waves, version) is the backdrop; the four empty button plates baked into it get live labels
        // + click handling overlaid here. OnGUI returns right after this in Menu phase.
        private void DrawHomeMenu(Event e)
        {
            float W = Screen.width, H = Screen.height;

            // backdrop — the whole art stretched over the window (art is authored ~16:9)
            if (e.type == EventType.Repaint)
            {
                if (menuTex != null) GUI.DrawTexture(new Rect(0, 0, W, H), menuTex, ScaleMode.StretchToFill);
                else { Color pc = GUI.color; GUI.color = new Color(0.05f, 0.12f, 0.19f); GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture); GUI.color = pc; }
            }

            // settings overlay takes over input while open — the menu buttons underneath stay inert
            if (settingsOpen) { DrawSettings(e); return; }
            if (netOpen) { DrawNetOverlay(e); return; }

            // the four button plates, measured off the art as fractions of the frame.
            // xL/xR = plate span, top of the first plate, and the top-to-top step down the stack.
            const float xL = 0.3725f, xR = 0.6290f, plateTop = 0.410f, plateH = 0.092f, step = 0.1250f;
            var items = new (string label, System.Action act)[]
            {
                ("PLAY",        () => { Sfx(clipFlip, 0.9f); NewGame(); }),                 // fresh boards → Setup
                ("MULTIPLAYER", () => { Sfx(clipClick, 0.7f); netOpen = true; }),
                ("SETTINGS",    () => { Sfx(clipClick, 0.7f); settingsOpen = true; activeSlider = -1; }),
                ("QUIT",        () => { Sfx(clipClick, 0.7f); QuitGame(); }),
            };

            var lbl = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
            float rx = xL * W, rw = (xR - xL) * W, rh = plateH * H;
            for (int i = 0; i < items.Length; i++)
            {
                var r = new Rect(rx, (plateTop + step * i) * H, rw, rh);
                bool hot = r.Contains(e.mousePosition);

                if (e.type == EventType.Repaint)
                {
                    Color pc = GUI.color;
                    if (hot) // phosphor-green wash + brighter frame kiss so the plate reads as selected
                    {
                        GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.16f);
                        GUI.DrawTexture(r, Texture2D.whiteTexture);
                    }
                    GUI.color = pc;
                    lbl.fontSize = Mathf.RoundToInt(Mathf.Clamp(rh * 0.42f, 12f, 34f));
                    lbl.normal.textColor = hot ? new Color(0.62f, 1f, 0.72f) : new Color(0.82f, 0.90f, 0.97f);
                    GUI.Label(r, T(items[i].label), lbl);
                }

                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) items[i].act();
            }

            // transient status line (coming-soon toasts), just under the button stack
            if (e.type == EventType.Repaint && Time.time < menuNoteUntil && !string.IsNullOrEmpty(menuNote))
            {
                var ns = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ns.fontSize = Mathf.RoundToInt(Mathf.Clamp(H * 0.022f, 10f, 20f));
                ns.normal.textColor = new Color(0.45f, 0.72f, 0.88f, 0.9f);
                GUI.Label(new Rect(rx - rw * 0.2f, (plateTop + step * items.Length + 0.01f) * H, rw * 1.4f, H * 0.05f), T(menuNote), ns);
            }

            // software cursor — OnGUI's own cursor pass is skipped in Menu phase, so draw it here
            if (e.type == EventType.Repaint && cursorTex != null)
            {
                float chS = Mathf.Max(18f, H * 0.03f);
                float cwS = chS * cursorTex.width / cursorTex.height;
                GUI.DrawTexture(new Rect(e.mousePosition.x, e.mousePosition.y, cwS, chS), cursorTex, ScaleMode.StretchToFill);
            }
        }

        private void MenuNote(string m) { Sfx(clipClick, 0.7f); menuNote = m; menuNoteUntil = Time.time + 2.2f; }

        // MULTIPLAYER connect overlay (Step 2) — HOST a room (get a share code) or JOIN by code.
        // Lobby-only for now: "CONNECTED" means both players are in the same session. Actual move
        // sync over Relay is Step 3. Overlaid on the dimmed home menu, same look as DrawSettings.
        private void DrawNetOverlay(Event e)
        {
            float W = Screen.width, H = Screen.height;

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CloseNet(); e.Use(); return; }

            // dim the menu art behind the panel
            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = new Color(0.02f, 0.05f, 0.09f, 0.72f);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
                GUI.color = pc;
            }

            float pw = Mathf.Min(W * 0.62f, 760f);
            float ph = Mathf.Min(H * 0.66f, 560f);
            float px = (W - pw) * 0.5f, py = (H - ph) * 0.5f;
            var panel = new Rect(px, py, pw, ph);

            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = new Color(0.05f, 0.11f, 0.17f, 0.97f);
                GUI.DrawTexture(panel, Texture2D.whiteTexture);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.55f);
                DrawFrame(panel, Mathf.Max(2f, H * 0.003f));
                GUI.color = pc;

                var ts = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ts.fontSize = Mathf.RoundToInt(Mathf.Clamp(H * 0.036f, 14f, 28f));
                ts.normal.textColor = new Color(0.72f, 1f, 0.82f);
                GUI.Label(new Rect(px, py + ph * 0.05f, pw, ph * 0.14f), T("MULTIPLAYER"), ts);
            }

            float pad = pw * 0.09f;
            float innerX = px + pad, innerW = pw - pad * 2f;
            float y = py + ph * 0.24f;
            float bh = ph * 0.14f;

            bool connected = net.State == PikeNet.Status.Connected;
            bool busy = net.Busy || net.State == PikeNet.Status.Connecting;

            if (connected)
            {
                // success state — both players are in the room
                DrawNetLabel(new Rect(innerX, y, innerW, bh), T("CONNECTED"), new Color(0.55f, 1f, 0.68f), 0.5f);
                DrawNetLabel(new Rect(innerX, y + bh * 0.9f, innerW, bh),
                    string.Format(T("{0} PLAYERS IN ROOM"), net.PlayerCount), new Color(0.82f, 0.90f, 0.97f), 0.34f);
                DrawNetLabel(new Rect(innerX, y + bh * 1.7f, innerW, bh),
                    T("ROOM CODE") + "  " + net.JoinCode, new Color(0.62f, 1f, 0.72f), 0.34f);
                string relay = net.PingOk ? T("RELAY LINKED - PING OK")
                             : net.RelayLinked ? T("RELAY LINKED")
                             : T("RELAY LINKING...");
                DrawNetLabel(new Rect(innerX, y + bh * 2.6f, innerW, bh), relay,
                    net.PingOk ? new Color(0.55f, 1f, 0.68f) : new Color(0.45f, 0.72f, 0.88f), 0.28f);
                DrawNetLabel(new Rect(innerX, y + bh * 3.25f, innerW, bh * 0.7f), net.TransportInfo,
                    new Color(0.55f, 0.66f, 0.78f), 0.24f);   // debug: raw transport phase

                // proceed to fleet placement once the byte channel is up (both players do this)
                if (NetButton(new Rect(innerX, py + ph - bh * 2.35f - ph * 0.06f, innerW, bh),
                              T("PLACE FLEET"), e, net.RelayLinked))
                { StartOnlineSetup(); }

                if (NetButton(new Rect(px + (pw - innerW * 0.6f) * 0.5f, py + ph - bh - ph * 0.06f, innerW * 0.6f, bh),
                              T("LEAVE ROOM"), e, true))
                { Sfx(clipClick, 0.7f); net.Leave(); }
            }
            else
            {
                // HOST
                if (NetButton(new Rect(innerX, y, innerW, bh), net.IsHost && net.State == PikeNet.Status.Hosting
                        ? T("HOSTING...") : T("HOST A ROOM"), e, !busy))
                { Sfx(clipFlip, 0.8f); net.Host(); }

                // host's share code + waiting line
                if (net.IsHost && !string.IsNullOrEmpty(net.JoinCode))
                {
                    DrawNetLabel(new Rect(innerX, y + bh * 1.02f, innerW, bh * 0.8f),
                        T("SHARE THIS CODE") + "  " + net.JoinCode, new Color(0.62f, 1f, 0.72f), 0.34f);
                    DrawNetLabel(new Rect(innerX, y + bh * 1.72f, innerW, bh * 0.7f),
                        T("Waiting for opponent..."), new Color(0.45f, 0.72f, 0.88f), 0.28f);
                }

                // JOIN — code field + button
                float jy = y + bh * 2.7f;
                DrawNetLabel(new Rect(innerX, jy - bh * 0.55f, innerW, bh * 0.55f), T("- OR JOIN BY CODE -"),
                    new Color(0.55f, 0.66f, 0.78f), 0.26f);
                float fieldW = innerW * 0.58f, joinW = innerW * 0.38f, gap = innerW * 0.04f;
                var fieldR = new Rect(innerX, jy, fieldW, bh);
                if (e.type == EventType.Repaint)
                {
                    Color pc = GUI.color;
                    GUI.color = new Color(0.10f, 0.18f, 0.26f, 0.95f);
                    GUI.DrawTexture(fieldR, Texture2D.whiteTexture);
                    GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.40f);
                    DrawFrame(fieldR, 1.5f);
                    GUI.color = pc;
                }
                var tf = new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleCenter };
                if (hdr.font != null) tf.font = hdr.font;
                tf.fontSize = Mathf.RoundToInt(Mathf.Clamp(bh * 0.36f, 12f, 22f));
                tf.normal.textColor = tf.focused.textColor = new Color(0.82f, 0.95f, 0.88f);
                GUI.SetNextControlName("netCode");
                netCodeEntry = GUI.TextField(new Rect(fieldR.x + 6f, fieldR.y, fieldR.width - 12f, fieldR.height),
                                             netCodeEntry ?? "", 8, tf);
                netCodeEntry = PikeNet.Sanitize(netCodeEntry);   // only A-Z/0-9 make it into the field

                if (NetButton(new Rect(innerX + fieldW + gap, jy, joinW, bh), T("JOIN"), e,
                              !busy && !string.IsNullOrWhiteSpace(netCodeEntry)))
                { Sfx(clipFlip, 0.8f); net.Join(netCodeEntry); }

                // status / error line
                string status = net.State == PikeNet.Status.Connecting ? T("Connecting...")
                              : net.State == PikeNet.Status.Error ? (T("Error: ") + net.Error)
                              : "";
                if (e.type == EventType.Repaint && !string.IsNullOrEmpty(status))
                {
                    var ss = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                    ss.fontSize = Mathf.RoundToInt(Mathf.Clamp(bh * 0.26f, 9f, 16f));
                    ss.normal.textColor = net.State == PikeNet.Status.Error
                        ? new Color(1f, 0.55f, 0.5f) : new Color(0.45f, 0.72f, 0.88f);
                    GUI.Label(new Rect(innerX, jy + bh * 1.15f, innerW, bh * 1.1f), status, ss);
                }

                // BACK
                if (NetButton(new Rect(px + (pw - innerW * 0.5f) * 0.5f, py + ph - bh - ph * 0.05f, innerW * 0.5f, bh),
                              T("◀ BACK"), e, true))
                { CloseNet(); }
            }

            // software cursor (this method returns before DrawHomeMenu's own cursor pass)
            if (e.type == EventType.Repaint && cursorTex != null)
            {
                float chS = Mathf.Max(18f, H * 0.03f);
                float cwS = chS * cursorTex.width / cursorTex.height;
                GUI.DrawTexture(new Rect(e.mousePosition.x, e.mousePosition.y, cwS, chS), cursorTex, ScaleMode.StretchToFill);
            }
        }

        private void CloseNet() { Sfx(clipClick, 0.7f); if (net.InSession) net.Leave(); netOpen = false; }

        // From the connect overlay: leave the menu and go place a fleet for a networked match.
        private void StartOnlineSetup()
        {
            Sfx(clipFlip, 0.8f);
            netOpen = false;
            NewGame();               // fresh boards + empty fleet slots (also clears the online flag)...
            online = true;           // ...then mark THIS as a networked match
            phase = Phase.Setup;
            message = "Place your fleet, then READY.";
        }

        // Fired by PikeNet once both players are READY. myTurn = does this client shoot first.
        private void BeginOnlineMatch(bool myTurn)
        {
            match.SetOnlineStart(myTurn);
            onlineWaiting = false;
            phase = Phase.Playing;
            targeting = false;
            prevPlayerTurn = !myTurn;   // force the turn indicator to refresh
            message = myTurn ? "YOUR TURN" : "OPPONENT'S TURN";
        }

        // The perk seams (torpedo / relocate / intercept / radar) still can't cross the wire — that's
        // Step 7. Fire and END TURN are wired (Step 6) and route through their own online branches, so
        // this guard now only stops the perks from desyncing the two games.
        private bool OnlineActionBlocked()
        {
            if (!online) return false;
            message = T("Not available online yet.");
            return true;
        }

        // ---- Step 6: in-match network moves ----

        // I am the DEFENDER: the opponent's decided move arrived. Resolve it against my real board (my
        // Intercept can counter it), animate it exactly like an AI turn, then report the outcome back.
        private void OnNetMoveIntent(byte[] body)
        {
            if (!online || body == null || body.Length == 0) return;
            switch ((MoveKind)body[0])
            {
                case MoveKind.Pass:
                    match.ApplyOpponentPass();               // no shots — just flips the turn back to me
                    targeting = false; prevPlayerTurn = false; actionEndT = Time.time;
                    message = T("OPPONENT PASSED - YOUR TURN");
                    break;
                case MoveKind.Fire:
                    if (body.Length < 3) return;
                    int x = body[1], y = body[2];
                    var t = match.ApplyOpponentFire(x, y, false, null);
                    PlayIncomingTurn(t);                     // same VFX / audio path the AI uses
                    net.SendMoveResult(NetCodec.EncodeResult(BuildResults(t))); // hand the attacker their outcome
                    break;
                case MoveKind.Torpedo:
                    if (body.Length < 2) return;
                    var tt = match.ApplyOpponentTorpedo(body[1]);   // I own my layout — I resolve the struck cell
                    PlayIncomingTurn(tt);
                    net.SendMoveResult(NetCodec.EncodeResult(BuildResults(tt)));
                    break;
            }
        }

        // I am the ATTACKER: the defender resolved my shot. Fold each cell into my fog board, end my
        // turn, then animate the shells landing on the targeting scope from the reported outcome.
        private void OnNetMoveResult(byte[] body)
        {
            if (!online) return;
            var shots = NetCodec.DecodeResult(body);
            var evs = new System.Collections.Generic.List<BattleshipMatch.ShotEvent>(shots.Count);
            foreach (var s in shots)
            {
                var fr = new FireResult(s.Outcome, s.SunkShip);
                match.RecordMyShot(s.X, s.Y, fr, s.Footprint);   // fog learns the result (a sink reveals the hull)
                evs.Add(new BattleshipMatch.ShotEvent { X = s.X, Y = s.Y, Result = fr });
            }
            if (pendingKind == MoveKind.Torpedo) match.UseMyPerk(ShipType.Submarine); // advance my sub cooldown
            match.EndMyOnlineTurn();
            awaitingResult = false;
            targeting = true;                                    // stay on the scope to watch the ordnance land
            if (pendingKind == MoveKind.Torpedo)
                RenderMyTorpedo(pendingCol, evs);                // fish run from the reported struck cell
            else
                RenderMyShots(evs, pendingFireX, pendingFireY);  // shell salvo; handles the YOU WIN check
        }

        // Package the shots I resolved (against my own board) into wire results, attaching each sunk
        // ship's full footprint so the attacker's fog can reveal the whole hull.
        private System.Collections.Generic.List<ShotResult> BuildResults(BattleshipMatch.AiTurn t)
        {
            var list = new System.Collections.Generic.List<ShotResult>(t.Shots.Count);
            foreach (var ev in t.Shots)
            {
                var r = new ShotResult { X = ev.X, Y = ev.Y, Outcome = ev.Result.Outcome, SunkShip = ev.Result.SunkShip };
                if (ev.Result.Outcome == FireOutcome.Sunk)
                    r.Footprint = match.PlayerBoard.ShipCells(ev.Result.SunkShip);
                list.Add(r);
            }
            return list;
        }

        // a centred pixel-font label inside a rect; sizeFrac scales the font off the rect height
        private void DrawNetLabel(Rect r, string text, Color col, float sizeFrac)
        {
            if (Event.current.type != EventType.Repaint) return;
            var s = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            s.fontSize = Mathf.RoundToInt(Mathf.Clamp(r.height * sizeFrac, 9f, 30f));
            s.normal.textColor = col;
            GUI.Label(r, text, s);
        }

        // a framed plate button matching the settings BACK button; returns true on click
        private bool NetButton(Rect r, string label, Event e, bool enabled)
        {
            bool hot = enabled && r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = !enabled ? new Color(0.14f, 0.20f, 0.26f, 0.30f)
                          : hot ? new Color(0.30f, 0.85f, 0.45f, 0.22f) : new Color(0.20f, 0.30f, 0.40f, 0.30f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, enabled ? (hot ? 0.75f : 0.40f) : 0.20f);
                DrawFrame(r, 2f);
                GUI.color = pc;
                var bs = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                bs.fontSize = Mathf.RoundToInt(Mathf.Clamp(r.height * 0.32f, 11f, 22f));
                bs.normal.textColor = !enabled ? new Color(0.5f, 0.58f, 0.66f, 0.6f)
                    : hot ? new Color(0.62f, 1f, 0.72f) : new Color(0.82f, 0.90f, 0.97f);
                GUI.Label(r, label, bs);
            }
            if (!enabled) return false;
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // Fleet-placement screen — the FleetPlacement.png art (board frame, empty ship dock, empty
        // chip/button plates) is the backdrop; the live 10×10 grid, ship dock, difficulty chips and
        // control buttons are overlaid on the baked zones by normalized coords (same method as the
        // home menu). The art is LETTERBOXED to its native aspect so the plates line up at any window
        // size and the pixel art never stretches. All text is drawn live via T() so it stays EN/FR.
        // OnGUI returns right after this in Setup phase. Zone constants were measured off the art.
        private void DrawFleetSetup(Event e, Vector2 mp)
        {
            float W = Screen.width, H = Screen.height;

            // aspect-fit the art into the window, centred, dark bars filling any leftover margin
            float aspect = 4096f / 2288f;
            Rect art = (W / H > aspect)
                ? new Rect((W - H * aspect) * 0.5f, 0f, H * aspect, H)
                : new Rect(0f, (H - W / aspect) * 0.5f, W, W / aspect);

            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = new Color(0.03f, 0.06f, 0.09f);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);          // letterbox bars
                GUI.color = pc;
                if (fleetSetupTex != null) GUI.DrawTexture(art, fleetSetupTex, ScaleMode.StretchToFill);
                else { GUI.color = new Color(0.05f, 0.12f, 0.19f); GUI.DrawTexture(art, Texture2D.whiteTexture); GUI.color = pc; }
            }

            // --- board: reserve a status band at the top of the screen glass; the grid fills the rest ---
            var glass = NRect(0.075f, 0.130f, 0.400f, 0.720f, art);
            float stripH = glass.height * 0.095f;
            var bz = new Rect(glass.x, glass.y + stripH, glass.width, glass.height - stripH);
            cs = Mathf.Floor(Mathf.Max(6f, Mathf.Min(bz.width, bz.height) / Board.Size));
            float bw = cs * Board.Size;
            boardX = Mathf.Floor(bz.x + (bz.width - bw) * 0.5f);
            boardY = Mathf.Floor(bz.y + (bz.height - bw) * 0.5f);
            DrawBoardBase(boardX, boardY);
            DrawPlacedShips(boardX, boardY);
            HandleBoardDrag(boardX, boardY, e, mp);

            // status band ABOVE the grid (never over a cell) — drag hint + placement feedback
            if (e.type == EventType.Repaint && !string.IsNullOrEmpty(message))
            {
                var mr = new Rect(boardX, glass.y + stripH * 0.12f, bw, stripH * 0.76f);
                Color pc = GUI.color;
                GUI.color = new Color(0f, 0.02f, 0.03f, 0.55f);
                GUI.DrawTexture(mr, Texture2D.whiteTexture);
                GUI.color = pc;
                var ms = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ms.normal.textColor = new Color(0.45f, 0.95f, 0.55f);
                ms.fontSize = FitFont(ms, T(message), mr.width * 0.94f, Mathf.RoundToInt(mr.height * 0.6f), 8);
                GUI.Label(mr, T(message), ms);
            }

            // --- title plate (top-right) ---
            if (e.type == EventType.Repaint)
            {
                var tr = NRect(0.590f, 0.049f, 0.383f, 0.100f, art);
                var ts = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ts.normal.textColor = new Color(0.62f, 1f, 0.72f);
                ts.fontSize = FitFont(ts, T("PLACE YOUR FLEET"), tr.width * 0.9f, Mathf.RoundToInt(tr.height * 0.5f), 8);
                GUI.Label(tr, T("PLACE YOUR FLEET"), ts);
            }

            FleetDock(e, mp, art);

            // --- difficulty row (3 wide toggles) + control row (3 wide buttons); one shared font ---
            var levels = new[] { BattleshipAI.Level.Easy, BattleshipAI.Level.Medium, BattleshipAI.Level.Hard };
            float[] col = { 0.590f, 0.7125f, 0.840f };
            float[] colW = { 0.108f, 0.110f, 0.110f };
            const float diffY = 0.727f, ctrlY = 0.856f, rowH = 0.080f;
            var diffR = new Rect[3]; var ctrlR = new Rect[3];
            for (int i = 0; i < 3; i++) { diffR[i] = NRect(col[i], diffY, colW[i], rowH, art); ctrlR[i] = NRect(col[i], ctrlY, colW[i], rowH, art); }

            string[] diffLbl = { T("Easy").ToUpperInvariant(), T("Medium").ToUpperInvariant(), T("Hard").ToUpperInvariant() };
            string[] ctrlLbl = { T("RANDOM"), T("CLEAR"), online ? T("READY") : T("START") };
            int rowFont = SharedFont(new[] { diffR[0], diffR[1], diffR[2], ctrlR[0], ctrlR[1], ctrlR[2] },
                                     new[] { diffLbl[0], diffLbl[1], diffLbl[2], ctrlLbl[0], ctrlLbl[1], ctrlLbl[2] },
                                     Mathf.RoundToInt(diffR[0].height * 0.42f), 7);

            // per-button text nudges (fraction of plate): (x right+, y down+) — dialed in by eye
            Vector2[] diffOff = { new Vector2(-0.03f, 0.14f), new Vector2(0.05f, 0.09f), new Vector2(0.08f, 0.09f) };
            Vector2[] ctrlOff = { new Vector2(-0.03f, 0.14f), new Vector2(0f, 0.14f), new Vector2(0.08f, 0.14f) };
            for (int i = 0; i < 3; i++)
            {
                int li = i;
                SetupButton(diffR[i], diffLbl[i], rowFont, true, difficulty == levels[i], diffOff[i], e, mp,
                    () => { if (difficulty != levels[li]) { difficulty = levels[li]; NewGame(); Sfx(clipClick, 0.7f); } });
            }
            SetupButton(ctrlR[0], ctrlLbl[0], rowFont, true, false, ctrlOff[0], e, mp, () => { RandomizeFleet(); Sfx(clipClick, 0.7f); });
            SetupButton(ctrlR[1], ctrlLbl[1], rowFont, true, false, ctrlOff[1], e, mp, () => { for (int i = 0; i < fleet.Length; i++) fleet[i].placed = false; dragIdx = -1; Sfx(clipClick, 0.7f); });
            bool startEnabled = AllPlaced() && !(online && onlineWaiting);
            SetupButton(ctrlR[2], ctrlLbl[2], rowFont, startEnabled, false, ctrlOff[2], e, mp,
                () =>
                {
                    Sfx(clipFlip, 0.8f);
                    SyncBoard();
                    if (online) { net.SendFleetReady(); onlineWaiting = true; message = "Fleet locked - waiting for opponent..."; }
                    else { phase = Phase.Playing; targeting = false; message = "Your turn — tap TARGET to fire."; }
                });
        }

        // the 5-ship dock (right panel, middle): each unplaced ship sits in a berth, click to pick up
        private void FleetDock(Event e, Vector2 mp, Rect art)
        {
            var rack = NRect(0.6125f, 0.219f, 0.345f, 0.448f, art);
            float slotH = rack.height / fleet.Length;
            for (int i = 0; i < fleet.Length; i++)
            {
                var slot = new Rect(rack.x, rack.y + i * slotH, rack.width, slotH);
                if (fleet[i].placed || i == dragIdx) continue; // deployed / in-hand → empty berth
                bool hot = slot.Contains(mp);
                int len = Ships.Length(fleet[i].type);
                if (e.type == EventType.Repaint)
                {
                    if (hot)
                    {
                        Color pc = GUI.color;
                        GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.16f);
                        GUI.DrawTexture(slot, Texture2D.whiteTexture);
                        GUI.color = pc;
                    }
                    // inset inside the baked berth border, then split into sprite (left) + name (right)
                    float ix = slot.x + slot.width * 0.05f, iw = slot.width * 0.90f;
                    float pad = slot.height * 0.16f;
                    // shorter hulls render tall and kiss the top edge — nudge them down (destroyer most)
                    float yNudge = fleet[i].type == ShipType.Destroyer ? slot.height * 0.20f
                                 : fleet[i].type == ShipType.Submarine ? slot.height * 0.12f : 0f;
                    var tex = shipTex != null ? shipTex[(int)fleet[i].type] : null;
                    var sr = new Rect(ix, slot.y + pad + yNudge, iw * 0.50f, slot.height - 2f * pad);
                    if (tex != null) GUI.DrawTexture(sr, tex, ScaleMode.ScaleToFit);
                    else for (int k = 0; k < len; k++) Fill(new Rect(sr.x + k * (sr.height + 2f), sr.y, sr.height, sr.height), ShipCol, "");
                    float nameX = ix + iw * 0.54f, nameW = iw * 0.46f;
                    // match the name to the lowered sprite for the short hulls
                    float nameNudge = fleet[i].type == ShipType.Destroyer ? slot.height * 0.16f
                                    : fleet[i].type == ShipType.Submarine ? slot.height * 0.08f : 0f;
                    var ns = new GUIStyle(small) { alignment = TextAnchor.MiddleLeft };
                    ns.normal.textColor = hot ? new Color(0.62f, 1f, 0.72f) : new Color(0.80f, 0.90f, 0.85f);
                    ns.fontSize = FitFont(ns, T(fleet[i].type.ToString()), nameW, Mathf.RoundToInt(slot.height * 0.30f), 6);
                    GUI.Label(new Rect(nameX, slot.y + nameNudge, nameW, slot.height), T(fleet[i].type.ToString()), ns);
                }
                if (e.type == EventType.MouseDown && e.button == 0 && dragIdx < 0 && slot.Contains(mp))
                { dragIdx = i; orient = Orientation.Horizontal; message = "Right-click to rotate"; Sfx(clipClick, 0.5f); e.Use(); }
            }
        }

        // one baked button plate: live label + hover wash + click. `active` = persistent green
        // selected state (used by the difficulty toggles); dimmed & inert when !enabled.
        private void SetupButton(Rect r, string label, int fontSize, bool enabled, bool active, Vector2 labelOff, Event e, Vector2 mp, System.Action act)
        {
            bool hot = enabled && r.Contains(mp);
            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                // fill is inset well inside the baked plate so the wash never spills over its edge;
                // disabled draws NO box (would show as an ugly dark rectangle) — only the label dims
                if (active || hot)
                {
                    var f = new Rect(r.x + r.width * 0.06f, r.y + r.height * 0.16f, r.width * 0.88f, r.height * 0.68f);
                    GUI.color = active ? new Color(0.32f, 0.88f, 0.48f, 0.42f) : new Color(0.30f, 0.85f, 0.45f, 0.15f);
                    GUI.DrawTexture(f, Texture2D.whiteTexture);
                    GUI.color = pc;
                }
                var ls = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ls.normal.textColor = !enabled ? new Color(0.50f, 0.60f, 0.64f, 0.55f)
                                     : active   ? new Color(0.72f, 1f, 0.80f)
                                     : hot       ? new Color(0.62f, 1f, 0.72f)
                                                 : new Color(0.82f, 0.90f, 0.97f);
                ls.fontSize = fontSize;
                // labelOff nudges only the text (fraction of the plate), leaving the hit-box aligned
                GUI.Label(new Rect(r.x + labelOff.x * r.width, r.y + labelOff.y * r.height, r.width, r.height), label, ls);
            }
            if (enabled && GUI.Button(r, GUIContent.none, GUIStyle.none)) act();
        }

        // smallest fitting fontSize across several plates, so a row of buttons shares one size
        private int SharedFont(Rect[] rects, string[] labels, int hi, int lo)
        {
            var st = new GUIStyle(hdr);
            int best = hi;
            for (int i = 0; i < labels.Length; i++)
                best = Mathf.Min(best, FitFont(st, labels[i], rects[i].width * 0.88f, hi, lo));
            return best;
        }

        // pixel font can't be squeezed by GUIStyle, so shrink fontSize until the text fits maxW
        private int FitFont(GUIStyle st, string text, float maxW, int hi, int lo)
        {
            for (int s = hi; s > lo; s--)
            {
                st.fontSize = s;
                if (st.CalcSize(new GUIContent(text)).x <= maxW) return s;
            }
            return Mathf.Max(lo, 1);
        }

        // build a Rect from normalized x/y/w/h within an art Rect (letterboxed surface)
        private static Rect NRect(float nx, float ny, float nw, float nh, Rect a)
            => new Rect(a.x + nx * a.width, a.y + ny * a.height, nw * a.width, nh * a.height);

        private void QuitGame()
        {
            Application.Quit();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // --- Localization -------------------------------------------------------
        // T(en) returns the active language's string for an English key. French is
        // written accent-free (ASCII): the pixel font is all-caps and DrawMessageStrip
        // drops any char >= 127, so accents would vanish anyway. Unknown keys fall
        // back to the English text, so a missing entry degrades gracefully.
        private string T(string en)
        {
            if (lang == Lang.EN || en == null) return en;
            return FR.TryGetValue(en, out var v) ? v : en;
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> FR = new()
        {
            // home menu + settings
            { "PLAY", "JOUER" }, { "MULTIPLAYER", "MULTIJOUEUR" }, { "SETTINGS", "OPTIONS" }, { "QUIT", "QUITTER" },
            { "MULTIPLAYER — COMING SOON", "MULTIJOUEUR — BIENTOT DISPONIBLE" },
            { "MUSIC", "MUSIQUE" }, { "SFX", "EFFETS" }, { "LANGUAGE", "LANGUE" }, { "◀ BACK", "◀ RETOUR" },
            // panel headers ("BATTLESHIP" title is handled inline — it collides with the ship name key below)
            { "YOU WIN!", "VOUS GAGNEZ !" }, { "AI WINS", "L'IA GAGNE" },
            { "PLACE YOUR FLEET", "PLACEZ VOTRE FLOTTE" }, { "TARGETING", "CIBLAGE" }, { "YOUR FLEET", "VOTRE FLOTTE" },
            { "TACTICAL RADAR", "RADAR TACTIQUE" },
            // main action buttons
            { "🎯 TARGET ▶", "🎯 CIBLER ▶" }, { "◀ MY FLEET", "◀ MA FLOTTE" },
            { "⏱ TURN TIMER: {0}s", "⏱ TEMPS DU TOUR : {0}s" }, { "⏭ END TURN", "⏭ FIN DU TOUR" },
            { "⏳ END TURN ({0}s)", "⏳ FIN DU TOUR ({0}s)" }, { "⏳ END TURN", "⏳ FIN DU TOUR" },
            { "Enemy ships: {0} / 5", "Navires ennemis : {0} / 5" }, { "Your ships: {0} / 5", "Vos navires : {0} / 5" },
            { "PERKS", "ATOUTS" }, { "New Game", "Nouvelle partie" },
            // perk panel buttons (with pick-target variants)
            { "🚤 RELOCATE ◀ pick a spot", "🚤 DEPLACER ◀ choisir un lieu" }, { "🚤 RELOCATE", "🚤 DEPLACER" },
            { "📡 RADAR SCOUT", "📡 RADAR ECLAIREUR" },
            { "☄ SCORCH ARMED ✓", "☄ BRULURE ARMEE ✓" }, { "☄ SCORCH", "☄ BRULURE" },
            { "🛡 INTERCEPT ◀ pick a cell", "🛡 INTERCEPTION ◀ choisir une case" }, { "🛡 INTERCEPT", "🛡 INTERCEPTION" },
            { "🐟 TORPEDO ◀ pick a lane", "🐟 TORPILLE ◀ choisir une colonne" }, { "🐟 TORPEDO", "🐟 TORPILLE" },
            // fleet-placement screen (framed setup) — short labels sized to the baked plates
            { "RANDOM", "ALEATOIRE" }, { "CLEAR", "EFFACER" }, { "START", "DEMARRER" },
            { "Right-click to rotate", "Clic droit pour pivoter" },
            // multiplayer connect overlay (Step 2) — ASCII-only French (pixel font, no accents)
            { "CONNECTED", "CONNECTE" }, { "{0} PLAYERS IN ROOM", "{0} JOUEURS DANS LE SALON" },
            { "ROOM CODE", "CODE DU SALON" }, { "Ready - match sync arrives next step.", "Pret - la synchro arrive a la prochaine etape." },
            { "LEAVE ROOM", "QUITTER LE SALON" }, { "HOST A ROOM", "CREER UN SALON" }, { "HOSTING...", "HEBERGEMENT..." },
            { "SHARE THIS CODE", "PARTAGEZ CE CODE" }, { "Waiting for opponent...", "En attente d'un adversaire..." },
            { "- OR JOIN BY CODE -", "- OU REJOINDRE PAR CODE -" }, { "JOIN", "REJOINDRE" },
            { "Connecting...", "Connexion..." }, { "Error: ", "Erreur : " },
            { "RELAY LINKED - PING OK", "RELAIS CONNECTE - PING OK" }, { "RELAY LINKED", "RELAIS CONNECTE" },
            { "RELAY LINKING...", "CONNEXION RELAIS..." },
            { "PLACE FLEET", "PLACER LA FLOTTE" },   // "READY" is already in the table below
            { "YOUR TURN", "VOTRE TOUR" }, { "OPPONENT'S TURN", "TOUR DE L'ADVERSAIRE" },
            { "Place your fleet, then READY.", "Placez votre flotte, puis PRET." },
            { "Fleet locked - waiting for opponent...", "Flotte verrouillee - attente de l'adversaire..." },
            { "Online moves arrive in the next step.", "Les coups en ligne arrivent bientot." },
            // setup panel (legacy text panel — kept for reference)
            { "AI Difficulty", "Difficulte IA" }, { "Rotate (R)", "Pivoter (R)" }, { "Randomize", "Aleatoire" },
            { "Clear", "Effacer" }, { "▶ Start", "▶ Demarrer" }, { "Drag ships to the board:", "Glissez les navires sur la grille :" },
            { "Easy", "Facile" }, { "Medium", "Moyen" }, { "Hard", "Difficile" },
            // ship names (tray, mixed case)
            { "Carrier", "Porte-avions" }, { "Battleship", "Cuirasse" }, { "Cruiser", "Croiseur" },
            { "Submarine", "Sous-marin" }, { "Destroyer", "Destroyer" },
            // ship names (uppercase, used in "SUNK THEIR ..." lines)
            { "CARRIER", "PORTE-AVIONS" }, { "BATTLESHIP", "CUIRASSE" }, { "CRUISER", "CROISEUR" },
            { "SUBMARINE", "SOUS-MARIN" }, { "DESTROYER", "DESTROYER" },
            // radar legend
            { "* LEGEND *", "* LEGENDE *" },
            { "HIT", "TOUCHE" }, { "Ship hit", "Navire touche" },
            { "DESTROYED", "DETRUIT" }, { "Ship sunk", "Navire coule" },
            { "MISS", "MANQUE" }, { "Nothing here", "Rien ici" },
            { "RECON", "RECON" }, { "Enemy ship detected", "Navire ennemi detecte" },
            // status panel
            { "SHOTS FIRED", "TIRS EFFECTUES" }, { "HITS", "TOUCHES" }, { "SHIPS SUNK", "NAVIRES COULES" },
            { "STATUS", "STATUT" }, { "TURN-END", "FIN TOUR" }, { "END TURN", "FIN DU TOUR" },
            // perk cards
            { "SCOUT", "ECLAIREUR" }, { "LAUNCH A PLANE SCOUT.", "LANCER UN AVION ECLAIREUR." },
            { "INTERCEPT", "INTERCEPTION" }, { "INTERCEPT ENEMY MISSILE.", "INTERCEPTER UN MISSILE ENNEMI." },
            { "SCORCH", "BRULURE" }, { "MULTIPLE FIRING MISSILE.", "TIR MISSILE MULTIPLE." },
            { "TORPEDO", "TORPILLE" }, { "LAUNCH A TORPEDO.", "LANCER UNE TORPILLE." },
            { "RELOCATE", "DEPLACER" }, { "MOVE A SHIP TO A NEW POSITION.", "DEPLACER UN NAVIRE AILLEURS." },
            { "PICK A CELL TO SHIELD.", "CHOISIR UNE CASE A PROTEGER." }, { "PICK A LANE.", "CHOISIR UNE COLONNE." },
            { "PICK A SPOT.", "CHOISIR UN EMPLACEMENT." }, { "ARMED — NEXT SALVO x2.", "ARMEE — PROCHAINE SALVE x2." },
            { "OFFLINE", "HORS LIGNE" }, { "ARMED", "ARMEE" }, { "READY", "PRETE" },
            // combat result fragments (composed with coordinates)
            { "INTERCEPTED!", "INTERCEPTE !" }, { "HIT!", "TOUCHE !" },
            { "SUNK {0} {1}!", "COULE {0} {1} !" }, { "THEIR", "LEUR" }, { "YOUR", "VOTRE" },
            // transient status messages
            { "Click a ship, move, click the grid to drop it.", "Cliquez un navire, deplacez, cliquez la grille pour le poser." },
            { "🚀 INCOMING FIRE...", "🚀 TIR ENNEMI EN APPROCHE..." },
            { "🐟 ENEMY TORPEDO INBOUND...", "🐟 TORPILLE ENNEMIE EN APPROCHE..." },
            { "THEIR DAMAGED SHIP ESCAPED!", "LEUR NAVIRE ENDOMMAGE S'EST ECHAPPE !" },
            { "🛦 ENEMY RECON PLANE OVERHEAD!", "🛦 AVION DE RECON ENNEMI EN VUE !" },
            { "ENEMY TORPEDO: NO HIT.", "TORPILLE ENNEMIE : AUCUN IMPACT." },
            { "YOUR FLEET IS DESTROYED — AI WINS.", "VOTRE FLOTTE EST DETRUITE — L'IA GAGNE." },
            { "ENEMY FLEET DESTROYED — YOU WIN!", "FLOTTE ENNEMIE DETRUITE — VOUS GAGNEZ !" },
            { "🐟 TORPEDO AWAY...", "🐟 TORPILLE LANCEE..." },
            { "TORPEDO: NO CONTACT.", "TORPILLE : AUCUN CONTACT." },
            { "TORPEDO INTERCEPTED!", "TORPILLE INTERCEPTEE !" },
            { "SALVO AWAY...", "SALVE LANCEE..." }, { "FIRING {0}...", "TIR SUR {0}..." },
            // online (Step 6)
            { "Already fired there.", "Deja tire ici." },
            { "TURN PASSED", "TOUR PASSE" },
            { "OPPONENT PASSED - YOUR TURN", "ADVERSAIRE A PASSE - A VOUS" },
            { "Not available online yet.", "Pas encore disponible en ligne." },
            { "📡 CONTACT AT {0}!", "📡 CONTACT EN {0} !" },
            { "Returned to tray.", "Remis dans la reserve." }, { "Placed {0}.", "{0} place." },
            { "Fleet ready — press ▶ Start!", "Flotte prete — appuyez sur ▶ Demarrer !" },
            { "Your turn — tap TARGET to fire.", "A vous — appuyez sur CIBLER pour tirer." },
            { "No navigable route there — the fleet blocks the way.", "Aucune route navigable — la flotte bloque le passage." },
            { "Destroyer relocated — the enemy's markers just went stale!", "Destroyer deplace — les marqueurs ennemis sont perimes !" },
            { "Can't relocate there.", "Deplacement impossible ici." },
            { "Counter-missile battery locked on {0}.", "Batterie anti-missile verrouillee sur {0}." },
            { "Can't protect that cell (already shot, or it's the cruiser's fire-control center).", "Impossible de proteger cette case (deja tiree, ou centre de conduite de tir du croiseur)." },
            { "⏰ TIME UP — TURN FORFEITED.", "⏰ TEMPS ECOULE — TOUR PERDU." },
            { "NO NEW CONTACTS.", "AUCUN NOUVEAU CONTACT." },
            { "📡 RECON PLANE LAUNCHED...", "📡 AVION DE RECON LANCE..." },
        };

        private void CloseSettings()
        {
            // persist the chosen levels so they survive a restart
            PlayerPrefs.SetFloat("pk_sfxVol", sfxVolume);
            PlayerPrefs.SetFloat("pk_musicVol", musicVolume);
            PlayerPrefs.SetInt("pk_lang", (int)lang);
            PlayerPrefs.Save();
            settingsOpen = false; activeSlider = -1;
            Sfx(clipFlip, 0.8f);
        }

        // SETTINGS panel — overlaid on the dimmed home menu; music keeps playing behind it
        private void DrawSettings(Event e)
        {
            float W = Screen.width, H = Screen.height;

            // ESC backs out, same as the BACK button
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CloseSettings(); e.Use(); }

            // dim the menu art behind the panel
            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = new Color(0.02f, 0.05f, 0.09f, 0.72f);
                GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
                GUI.color = pc;
            }

            float pw = Mathf.Min(W * 0.62f, 760f);
            float ph = Mathf.Min(H * 0.58f, 520f);
            float px = (W - pw) * 0.5f, py = (H - ph) * 0.5f;
            var panel = new Rect(px, py, pw, ph);

            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = new Color(0.05f, 0.11f, 0.17f, 0.97f);          // console body
                GUI.DrawTexture(panel, Texture2D.whiteTexture);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.55f);          // phosphor-green frame
                DrawFrame(panel, Mathf.Max(2f, H * 0.003f));
                GUI.color = pc;
            }

            float pad = pw * 0.07f;
            float innerX = px + pad, innerW = pw - pad * 2f;

            // title
            if (e.type == EventType.Repaint)
            {
                var ts = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                ts.fontSize = Mathf.RoundToInt(Mathf.Clamp(H * 0.038f, 14f, 30f));
                ts.normal.textColor = new Color(0.72f, 1f, 0.82f);
                GUI.Label(new Rect(px, py + ph * 0.06f, pw, ph * 0.16f), T("SETTINGS"), ts);
            }

            float rowH = ph * 0.15f;
            float rowGap = ph * 0.035f;
            float firstRowY = py + ph * 0.24f;

            musicVolume = SettingsRow(e, T("MUSIC"), musicVolume, 0,
                new Rect(innerX, firstRowY, innerW, rowH));
            sfxVolume = SettingsRow(e, T("SFX"), sfxVolume, 1,
                new Rect(innerX, firstRowY + rowH + rowGap, innerW, rowH));
            LanguageRow(e, new Rect(innerX, firstRowY + 2f * (rowH + rowGap), innerW, rowH));

            // BACK button
            float bw = innerW * 0.42f, bh = ph * 0.15f;
            var backR = new Rect(px + (pw - bw) * 0.5f, py + ph - bh - ph * 0.07f, bw, bh);
            bool bhot = backR.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                GUI.color = bhot ? new Color(0.30f, 0.85f, 0.45f, 0.22f) : new Color(0.20f, 0.30f, 0.40f, 0.30f);
                GUI.DrawTexture(backR, Texture2D.whiteTexture);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, bhot ? 0.75f : 0.40f);
                DrawFrame(backR, 2f);
                GUI.color = pc;
                var bs = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                bs.fontSize = Mathf.RoundToInt(Mathf.Clamp(bh * 0.34f, 11f, 22f));
                bs.normal.textColor = bhot ? new Color(0.62f, 1f, 0.72f) : new Color(0.82f, 0.90f, 0.97f);
                GUI.Label(backR, T("◀ BACK"), bs);
            }
            if (GUI.Button(backR, GUIContent.none, GUIStyle.none)) CloseSettings();

            // release any active drag when the mouse comes up anywhere
            if (e.type == EventType.MouseUp && activeSlider != -1)
            {
                if (activeSlider == 1) Sfx(clipClick, 0.7f); // audition the new SFX level
                activeSlider = -1;
            }

            // software cursor (this method returns before DrawHomeMenu's own cursor pass)
            if (e.type == EventType.Repaint && cursorTex != null)
            {
                float chS = Mathf.Max(18f, H * 0.03f);
                float cwS = chS * cursorTex.width / cursorTex.height;
                GUI.DrawTexture(new Rect(e.mousePosition.x, e.mousePosition.y, cwS, chS), cursorTex, ScaleMode.StretchToFill);
            }
        }

        // one labelled slider row; returns the (possibly updated) value
        private float SettingsRow(Event e, string label, float value, int id, Rect row)
        {
            float labelW = row.width * 0.26f;
            float valW = row.width * 0.12f;
            var track = new Rect(row.x + labelW, row.y + row.height * 0.38f,
                                 row.width - labelW - valW, row.height * 0.24f);

            // drag handling — grab on the whole track height for an easy target
            var grab = new Rect(track.x, row.y, track.width, row.height);
            if (e.type == EventType.MouseDown && grab.Contains(e.mousePosition))
            { activeSlider = id; e.Use(); }
            if (activeSlider == id && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
            {
                value = Mathf.Clamp01((e.mousePosition.x - track.x) / track.width);
                e.Use();
            }

            if (e.type == EventType.Repaint)
            {
                Color pc = GUI.color;

                var ls = new GUIStyle(hdr) { alignment = TextAnchor.MiddleLeft };
                ls.fontSize = Mathf.RoundToInt(Mathf.Clamp(row.height * 0.34f, 11f, 22f));
                ls.normal.textColor = new Color(0.82f, 0.90f, 0.97f);
                GUI.Label(new Rect(row.x, row.y, labelW, row.height), label, ls);

                GUI.color = new Color(0.10f, 0.18f, 0.26f, 0.95f);          // track groove
                GUI.DrawTexture(track, Texture2D.whiteTexture);
                var fill = new Rect(track.x, track.y, track.width * value, track.height);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.85f);          // filled portion
                GUI.DrawTexture(fill, Texture2D.whiteTexture);
                GUI.color = new Color(0.30f, 0.85f, 0.45f, 0.45f);
                DrawFrame(track, 1.5f);

                float thumbW = Mathf.Max(8f, track.height * 0.9f);          // draggable knob
                var thumb = new Rect(track.x + track.width * value - thumbW * 0.5f,
                                     track.y - track.height * 0.55f, thumbW, track.height * 2.1f);
                GUI.color = (activeSlider == id) ? new Color(0.72f, 1f, 0.82f) : new Color(0.55f, 0.92f, 0.68f);
                GUI.DrawTexture(thumb, Texture2D.whiteTexture);

                var vs = new GUIStyle(hdr) { alignment = TextAnchor.MiddleRight };
                vs.fontSize = Mathf.RoundToInt(Mathf.Clamp(row.height * 0.30f, 10f, 20f));
                vs.normal.textColor = new Color(0.62f, 1f, 0.72f);
                GUI.Label(new Rect(row.x + row.width - valW, row.y, valW, row.height),
                          Mathf.RoundToInt(value * 100f) + "%", vs);

                GUI.color = pc;
            }
            return value;
        }

        // LANGUAGE row: label on the left, an EN | FR segmented toggle on the right
        private void LanguageRow(Event e, Rect row)
        {
            float labelW = row.width * 0.26f;
            var opts = new (string txt, Lang val)[] { ("EN", Lang.EN), ("FR", Lang.FR) };
            float segW = (row.width - labelW) * 0.5f;

            for (int i = 0; i < opts.Length; i++)
            {
                var seg = new Rect(row.x + labelW + i * segW + (i == 0 ? 0f : 4f),
                                   row.y + row.height * 0.18f, segW - 4f, row.height * 0.64f);
                bool active = lang == opts[i].val;
                bool hot = seg.Contains(e.mousePosition);

                if (e.type == EventType.Repaint)
                {
                    Color pc = GUI.color;
                    GUI.color = active ? new Color(0.30f, 0.85f, 0.45f, 0.55f)
                              : hot    ? new Color(0.30f, 0.85f, 0.45f, 0.18f)
                                       : new Color(0.10f, 0.18f, 0.26f, 0.85f);
                    GUI.DrawTexture(seg, Texture2D.whiteTexture);
                    GUI.color = new Color(0.30f, 0.85f, 0.45f, active ? 0.85f : 0.40f);
                    DrawFrame(seg, active ? 2f : 1.5f);
                    GUI.color = pc;

                    var ss = new GUIStyle(hdr) { alignment = TextAnchor.MiddleCenter };
                    ss.fontSize = Mathf.RoundToInt(Mathf.Clamp(seg.height * 0.42f, 11f, 22f));
                    ss.normal.textColor = active ? new Color(0.10f, 0.16f, 0.12f)
                                        : hot    ? new Color(0.62f, 1f, 0.72f)
                                                 : new Color(0.72f, 0.82f, 0.90f);
                    GUI.Label(seg, opts[i].txt, ss);
                }
                if (GUI.Button(seg, GUIContent.none, GUIStyle.none) && lang != opts[i].val)
                { lang = opts[i].val; Sfx(clipClick, 0.7f); }
            }

            if (e.type == EventType.Repaint)
            {
                var ls = new GUIStyle(hdr) { alignment = TextAnchor.MiddleLeft };
                ls.fontSize = Mathf.RoundToInt(Mathf.Clamp(row.height * 0.34f, 11f, 22f));
                ls.normal.textColor = new Color(0.82f, 0.90f, 0.97f);
                GUI.Label(new Rect(row.x, row.y, labelW, row.height), T("LANGUAGE"), ls);
            }
        }

        // 4-rect border stroke around r using the current GUI.color
        private void DrawFrame(Rect r, float t)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
        }

        // ESC window: every debug/dev lever in one place, drawn on top of whatever view is up
        private void DrawDebugMenu()
        {
            float w = Mathf.Min(Screen.width * 0.34f, 560f);
            float bh2 = Mathf.Max(30f, Screen.height * 0.043f);
            float pad2 = 14f;
            int rows = phase == Phase.Playing ? 11 : 8; // +2 anim-speed sliders, +1 marker painter, +1 scenario player
            float hgt = pad2 * 2f + (bh2 + 8f) * rows + bh2 * 0.9f;
            var r = new Rect((Screen.width - w) * 0.5f, (Screen.height - hgt) * 0.5f, w, hgt);

            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture,
                                ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, 0.55f), 0f, 0f);
                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                new Color(0.05f, 0.07f, 0.06f, 0.97f), 0f, 10f);
                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 3f,
                                new Color(0.32f, 0.78f, 0.42f, 0.8f), 3f, 10f);
            }
            float x = r.x + pad2, yy = r.y + pad2, bw2 = r.width - pad2 * 2f;
            GUI.Label(new Rect(x, yy, bw2, bh2 * 0.9f), "* DEBUG *", lgTitle ?? hdr);
            yy += bh2 * 0.9f + 6f;

            if (GUI.Button(new Rect(x, yy, bw2, bh2), showGrid ? "GRID: ON" : "GRID: OFF", btn)) showGrid = !showGrid;
            yy += bh2 + 8f;
            if (GUI.Button(new Rect(x, yy, bw2, bh2), debugHit ? "DEBUG HIT: ON" : "DEBUG HIT: OFF", btn)) debugHit = !debugHit;
            yy += bh2 + 8f;
            string[] mkNames = { "OFF", "HIT", "DESTROYED", "MISS", "RECON" };
            string mkCount = debugMarks.Count > 0 ? "  [" + debugMarks.Count + "]" : "";
            if (GUI.Button(new Rect(x, yy, bw2, bh2), "PLACE MARKER: " + mkNames[debugMark] + mkCount, btn))
                debugMark = (debugMark + 1) % 5; // radar clicks paint this marker; same cell+kind erases
            yy += bh2 + 8f;
            yy = FpsSlider(x, yy, bw2, bh2, "HIT ANIM", ref dmgFps);
            yy = FpsSlider(x, yy, bw2, bh2, "BURN LOOP", ref dmgLoopFps);
            if (phase == Phase.Playing)
            {
                if (GUI.Button(new Rect(x, yy, bw2, bh2), "PLAY SCENARIO " + (scenIdx + 1) + "/4", btn))
                { DebugPlayScenario(scenIdx); scenIdx = (scenIdx + 1) % 4; }
                yy += bh2 + 8f;
                if (GUI.Button(new Rect(x, yy, bw2, bh2), "REFRESH ALL PERKS", btn))
                { match.PlayerPerks.RefreshAll(); message = "DEBUG: PERKS REFRESHED."; }
                yy += bh2 + 8f;
                if (GUI.Button(new Rect(x, yy, bw2, bh2), "RANDOM FLEET", btn))
                { RandomizeFleet(); SyncBoard(); message = "DEBUG: FLEET RANDOMIZED."; }
                yy += bh2 + 8f;
            }
            if (GUI.Button(new Rect(x, yy, bw2, bh2), "SKIP MY TURN", btn) && phase == Phase.Playing && match.PlayerTurn)
            { SkipTurn(false); debugMenu = false; }
            yy += bh2 + 8f;
            if (GUI.Button(new Rect(x, yy, bw2, bh2), "NEW GAME", btn)) { Sfx(clipClick, 0.7f); NewGame(); debugMenu = false; }
            yy += bh2 + 8f;
            if (GUI.Button(new Rect(x, yy, bw2, bh2), "CLOSE  (ESC)", btn)) debugMenu = false;
        }

        // one debug-menu row: "LABEL  12 FPS" + slider, returns the next row's y
        private float FpsSlider(float x, float yy, float bw2, float bh2, string label, ref float fps)
        {
            var st = lgName ?? GUI.skin.label;
            GUI.Label(new Rect(x, yy, bw2 * 0.5f, bh2), label + "  " + Mathf.RoundToInt(fps) + " FPS", st);
            fps = Mathf.Round(GUI.HorizontalSlider(new Rect(x + bw2 * 0.52f, yy + bh2 * 0.3f, bw2 * 0.48f, bh2 * 0.5f), fps, 2f, 30f));
            return yy + bh2 + 8f;
        }



        private float DiffBtn(float x, float y, float h, BattleshipAI.Level d)
        {
            float w = cs * 2.6f;
            var prev = GUI.backgroundColor;
            if (difficulty == d) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
            if (GUI.Button(new Rect(x, y, w, h), T(d.ToString()), btn) && difficulty != d) { difficulty = d; NewGame(); }
            GUI.backgroundColor = prev;
            return x + w + cs * 0.15f;
        }

        private void DrawBoardBase(float ox, float oy)
        {
            float bw = Board.Size * cs;
            if (Event.current.type == EventType.Repaint)
            {
                Color pc = GUI.color; GUI.color = Color.white;
                Texture2D tex = waterTex; float tx = Board.Size / 4f, ty = Board.Size / 4f;
                bool frames = waterFrames != null && waterFrames.Length > 0;
                if (frames)
                {
                    tex = waterFrames[Mathf.FloorToInt(Time.time * waterFps) % waterFrames.Length];
                    tx = 1f; ty = 1f;
                }
                var wr = new Rect(ox, oy, bw, bw);
                if (fleetSqueeze > 1.001f)
                {
                    // the board is h-stretched onto the glass: extend the ocean SIDEWAYS by wrapping
                    // the frame (repeat-U) — full authored frame vertically, true wave shapes, per doc
                    tx *= fleetSqueeze;
                }
                if (tex != null) GUI.DrawTextureWithTexCoords(wr, tex, new Rect(0f, 0f, tx, ty));
                else { GUI.color = Water; GUI.DrawTexture(wr, Texture2D.whiteTexture); }
                GUI.color = pc;
            }
            DrawGridOverlay(ox, oy);
        }

        private void DrawPlacedShips(float ox, float oy)
        {
            for (int i = 0; i < fleet.Length; i++)
            {
                if (!fleet[i].placed || i == dragIdx) continue;
                if (relocT0 >= 0f && fleet[i].type == ShipType.Destroyer)
                {
                    float e = Time.time - relocT0;
                    if (e >= relocDur || segDur.Count == 0)
                    { relocT0 = -1f; DrawShipSprite(ox, oy, fleet[i].x, fleet[i].y, fleet[i].type, fleet[i].o); continue; }
                    int si = 0; float acc = 0f;
                    while (si < segDur.Count - 1 && e > acc + segDur[si]) { acc += segDur[si]; si++; }
                    if (segHide.Count > si && segHide[si]) continue; // over the horizon — nothing to draw
                    float u = Mathf.Clamp01((e - acc) / Mathf.Max(0.0001f, segDur[si]));
                    Vector2 c = Vector2.Lerp(segC0[si], segC1[si], u);
                    float a = Mathf.Lerp(segA0[si], segA1[si], u);
                    if (Event.current.type == EventType.Repaint && Time.time - puffTimer > 1f / Mathf.Max(1f, trailRate))
                    {
                        puffTimer = Time.time;
                        Vector2 dirv = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad));
                        puffs.Add((c - dirv * 0.5f, Time.time)); // foam wake off the stern
                    }
                    DrawShipAngle(ox, oy, c, a, fleet[i].type);
                    continue;
                }
                DrawShipSprite(ox, oy, fleet[i].x, fleet[i].y, fleet[i].type, fleet[i].o);
            }
            DrawShipDamage(ox, oy);
        }

        private void DrawShots(float ox, float oy)
        {
            var b = match.PlayerBoard;
            for (int gy = 0; gy < Board.Size; gy++)
                for (int gx = 0; gx < Board.Size; gx++)
                    if (b.IsShot(gx, gy) || pImpact[gx, gy] >= 0f) // pImpact alone = scripted scenario splash (board untouched)
                    {
                        if (pImpact[gx, gy] > Time.time || hitT[gx, gy] > Time.time) continue; // shell still inbound
                        var cell = new Rect(ox + gx * cs, oy + gy * cs, cs, cs);
                        // no marker squares on the fleet: hits burn via the damage anim, misses just splash
                        if (!b.HasShip(gx, gy)) DrawImpact(cell, pImpact[gx, gy]);
                    }
        }

        // per-ship display scale: some hulls read too small in their footprint — let them
        // spill past the cell edges a touch. Purely visual; hitbox/logic untouched.
        private static float ShipScale(ShipType t) =>
            t == ShipType.Submarine ? 1.18f :
            t == ShipType.Cruiser ? 1.18f : 1.10f; // everyone gets a little presence past the cell edge

        // draw a ship as its sprite spanning its tiles; rotate for vertical. Falls back to grey.
        // gx/gy are grid coords but FLOAT so the relocate slide can draw between cells.
        private void DrawShipSprite(float ox, float oy, float gx, float gy, ShipType type, Orientation o)
        {
            int len = Ships.Length(type);
            Texture2D tex = (shipTex != null && (int)type < shipTex.Length) ? shipTex[(int)type] : null;
            if (tex == null) { DrawShip(ox, oy, Mathf.RoundToInt(gx), Mathf.RoundToInt(gy), len, o, ShipCol); return; }
            if (Event.current.type != EventType.Repaint) return;
            float ph = gx * 12.9f + gy * 7.3f;
            float bob = Mathf.Sin(Time.time * bobSpeed + ph) * (cs * bobAmount);
            float sway = Mathf.Cos(Time.time * bobSpeed * 0.8f + ph) * (cs * bobAmount * 0.35f);
            float px = ox + gx * cs + sway, py = oy + gy * cs + bob;
            float sc = ShipScale(type);
            float w2 = len * cs, h2 = cs * sc;                      // grow HEIGHT only, centered — length stays in its cells
            if (o == Orientation.Horizontal)
                GUI.DrawTexture(new Rect(px + (len * cs - w2) * 0.5f, py + (cs - h2) * 0.5f, w2, h2), tex, ScaleMode.StretchToFill);
            else
            {
                // -90°: bow points UP, toward the enemy topside
                Vector2 center = new Vector2(px + cs * 0.5f, py + len * cs * 0.5f);
                Matrix4x4 mtx = GUI.matrix;
                RotateContent(-90f, center);
                GUI.DrawTexture(new Rect(center.x - w2 * 0.5f, center.y - h2 * 0.5f, w2, h2), tex, ScaleMode.StretchToFill);
                GUI.matrix = mtx;
            }
        }

        private void DrawShip(float ox, float oy, int gx, int gy, int len, Orientation o, Color col)
        {
            int dx = o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx;
            for (int k = 0; k < len; k++) { int cx = gx + dx * k, cy = gy + dy * k; if (Board.InBounds(cx, cy)) Fill(new Rect(ox + cx * cs, oy + cy * cs, cs - 2, cs - 2), col, ""); }
        }

        // soft glowing dot (radar symbology) — warped sideways while the CRT band rolls over it
        private void DrawGlow(float ox, float oy, float gx, float gy, float size, Color c)
        {
            if (puffTex == null) return;
            float s = cs * size; // markers stay round even when cells stretch
            float warp = CrtShift(gy * csY + csY * 0.5f);
            Color pc = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(ox + gx * csX + csX * 0.5f - s * 0.5f + warp, oy + gy * csY + csY * 0.5f - s * 0.5f, s, s), puffTex);
            GUI.color = pc;
        }

        // legend marker sprite on the scope, same CRT warp treatment
        private void DrawMark(float ox, float oy, float gx, float gy, float size, Texture2D tex, float alpha)
        {
            if (tex == null) return;
            float s = cs * size;
            float warp = CrtShift(gy * csY + csY * 0.5f);
            Color pc = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(ox + gx * csX + csX * 0.5f - s * 0.5f + warp, oy + gy * csY + csY * 0.5f - s * 0.5f, s, s), tex, ScaleMode.StretchToFill);
            GUI.color = pc;
        }

        /// <summary>Radar contact flash: expanding ring + bright core, replaces explosions on the scope.</summary>
        private bool DrawRadarBoom(Rect cell, float t0)
        {
            if (t0 < 0f) return false;
            float k = (Time.time - t0) / 0.8f;
            if (k < 0f || k >= 1f) return false;
            if (Event.current.type == EventType.Repaint)
            {
                float warp = CrtShift(cell.center.y - boardY);
                Color pc = GUI.color;
                float d = cs * (0.4f + 2.4f * k);
                GUI.color = new Color(0.45f, 1f, 0.55f, (1f - k) * 0.9f);
                GUI.DrawTexture(new Rect(cell.center.x - d * 0.5f + warp, cell.center.y - d * 0.5f, d, d), ringTex);
                float g = cs * 0.55f * (1f - k * 0.4f);
                GUI.color = new Color(0.75f, 1f, 0.8f, 1f - k);
                GUI.DrawTexture(new Rect(cell.center.x - g * 0.5f + warp, cell.center.y - g * 0.5f, g, g), puffTex);
                GUI.color = pc;
            }
            return true;
        }

        // the scope itself: phosphor-dark screen, green graticule, range rings, rotating sweep with afterglow
        // long phosphor persistence sector: no hot edge, just a slow smooth decay over
        // phosphorArc degrees with the same boiling grain as the sweep wedge. Rebaked
        // lazily whenever the arc slider moves.
        private void EnsurePhosphorFrames()
        {
            if (phosphorFrames != null && Mathf.Abs(phosphorArc - phosphorArcBaked) < 1f) return;
            if (phosphorFrames != null) foreach (var t in phosphorFrames) Destroy(t);
            const int W = 512; const float half = W * 0.5f;
            float span = phosphorArc;
            int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
            int[] offX = { 0, 1, 2, 3 }, offY = { 0, 2, 1, 3 };
            phosphorFrames = new Texture2D[4];
            for (int f = 0; f < 4; f++)
            {
                var tex2 = new Texture2D(W, W, TextureFormat.RGBA32, false)
                { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var wp = new Color[W * W];
                for (int y = 0; y < W; y++)
                    for (int x = 0; x < W; x++)
                    {
                        float dx = x - half, dy = y - half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy) / (half * 0.98f);
                        float a = 0f;
                        if (r <= 1f && (dx != 0f || dy != 0f))
                        {
                            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg; // 0 = beam edge (+x), trail on the + side
                            if (ang < 0f) ang += 360f;
                            if (ang <= span)
                            {
                                float g = Mathf.Pow(1f - ang / span, 1.6f);   // slow phosphor decay, no hot edge
                                float grain = (bayer[(x + offX[f]) & 3, (y + offY[f]) & 3] + 0.5f) / 16f - 0.5f;
                                a = Mathf.Clamp01(g * (1f + grain * 0.45f));
                                a *= Mathf.Clamp01((1f - r) / 0.04f);         // soft rim
                            }
                        }
                        wp[y * W + x] = new Color(1f, 1f, 1f, a);
                    }
                tex2.SetPixels(wp); tex2.Apply();
                phosphorFrames[f] = tex2;
            }
            phosphorArcBaked = phosphorArc;
        }

        private void DrawRadarBase(float ox, float oy)
        {
            float bwX = Board.Size * csX, bwY = Board.Size * csY; // the grid spans the whole glass
            if (Event.current.type == EventType.Repaint)
            {
                Color pc = GUI.color;
                if (!glassOn) // fullscreen console has its own baked glass; standalone scope paints its own
                {
                    GUI.color = new Color(0.02f, 0.1f, 0.045f, 1f); // deeper phosphor green, like the reference
                    GUI.DrawTexture(new Rect(ox, oy, bwX, bwY), Texture2D.whiteTexture);
                }
                // The grid is 11 columns wide: the LETTER column is boxed in like the reference.
                float gridL = glassOn ? ox - csX : ox;
                float gridW = glassOn ? bwX + csX : bwX;
                int cols = glassOn ? Board.Size + 1 : Board.Size;

                // the baked glass in the shell art is brighter than the phosphor tone we want —
                // pull it down with a flat dark-green tint before anything is drawn on it
                if (glassOn && glassDim > 0f)
                {
                    GUI.color = new Color(0f, 0.03f, 0.01f, glassDim);
                    GUI.DrawTexture(glassRect, Texture2D.whiteTexture);
                }

                // the roll bar warps EVERYTHING beneath it: redraw thin slices of the console's own
                // glass, shifted sideways — the baked glass texture itself bends inside the band
                if (glassOn && consoleTex != null && crtBarAlpha > 0f)
                {
                    float bh4 = bwY * crtBarHeight;
                    float top4 = CrtBandTop(bwY);
                    float lo4 = Mathf.Max(-0.6f * csY, top4), hi4 = Mathf.Min(bwY, top4 + bh4); // all the way up the glass
                    float stripH = Mathf.Max(2f, bh4 / 14f);
                    for (float sy3 = lo4; sy3 < hi4; sy3 += stripH)
                    {
                        float h4 = Mathf.Min(stripH, hi4 - sy3);
                        float shift = CrtShift(sy3 + h4 * 0.5f);
                        float wy = oy + sy3;
                        var uv = new Rect((gridL - consoleRect.x) / consoleRect.width,
                                          1f - (wy + h4 - consoleRect.y) / consoleRect.height,
                                          gridW / consoleRect.width,
                                          h4 / consoleRect.height);
                        GUI.color = Color.white;
                        GUI.DrawTextureWithTexCoords(new Rect(gridL + shift, wy, gridW, h4), consoleTex, uv);
                        if (crtBlur > 0f)
                        {
                            // ghost taps: the same slice re-drawn at small offsets = smeared/blurred look
                            float amp = 2f + 6f * crtBlur;
                            GUI.color = new Color(1f, 1f, 1f, 0.28f * crtBlur + 0.12f);
                            GUI.DrawTextureWithTexCoords(new Rect(gridL + shift - amp, wy, gridW, h4), consoleTex, uv);
                            GUI.DrawTextureWithTexCoords(new Rect(gridL + shift + amp, wy, gridW, h4), consoleTex, uv);
                            GUI.DrawTextureWithTexCoords(new Rect(gridL + shift - amp * 0.5f, wy - 1f, gridW, h4), consoleTex, uv);
                            GUI.DrawTextureWithTexCoords(new Rect(gridL + shift + amp * 0.5f, wy + 1f, gridW, h4), consoleTex, uv);
                        }
                    }
                    if (glassDim > 0f) // the re-drawn slices are raw art brightness — dim the band back down
                    {
                        GUI.color = new Color(0f, 0.03f, 0.01f, glassDim);
                        GUI.DrawTexture(new Rect(gridL - 10f, oy + lo4, gridW + 20f, hi4 - lo4), Texture2D.whiteTexture);
                    }
                }

                // graticule — bends sideways where the CRT band rolls over it
                GUI.color = new Color(0.14f, 0.85f, 0.32f, 0.42f);
                float t = Mathf.Max(1f, Mathf.Round(cs * 0.03f));
                float segH = Mathf.Max(3f, bwY / 48f); // vertical lines drawn in short segments so they can bend
                Color gridCol = GUI.color;
                for (int i = 0; i <= cols; i++)
                {
                    float lx = gridL + i * csX;
                    for (float sy2 = 0f; sy2 < bwY; sy2 += segH)
                    {
                        float h2 = Mathf.Min(segH, bwY - sy2);
                        float sh2 = CrtShift(sy2 + h2 * 0.5f);
                        GUI.DrawTexture(new Rect(lx - t * 0.5f + sh2, oy + sy2, t, h2), Texture2D.whiteTexture);
                        if (sh2 != 0f && crtBlur > 0f)
                        {
                            // in-band ghosting: the line smears sideways with the wave
                            float amp2 = 2f + 5f * crtBlur;
                            GUI.color = new Color(gridCol.r, gridCol.g, gridCol.b, gridCol.a * 0.45f * crtBlur);
                            GUI.DrawTexture(new Rect(lx - t * 0.5f + sh2 - amp2, oy + sy2, t, h2), Texture2D.whiteTexture);
                            GUI.DrawTexture(new Rect(lx - t * 0.5f + sh2 + amp2, oy + sy2, t, h2), Texture2D.whiteTexture);
                            GUI.color = gridCol;
                        }
                    }
                }
                for (int i = 0; i <= Board.Size; i++)
                    GUI.DrawTexture(new Rect(gridL + CrtShift(i * csY), oy + i * csY - t * 0.5f, gridW, t), Texture2D.whiteTexture);
                // the rolling band itself, faint, beneath the content
                if (crtBarAlpha > 0f && bandTex != null)
                {
                    float off3 = glassOn ? 0.6f * csY : 0f;
                    float bh3 = bwY * crtBarHeight;
                    float byy = CrtBandTop(bwY) + off3; // group-local (group starts at the glass TOP)
                    GUI.BeginGroup(new Rect(gridL, oy - off3, gridW, bwY + off3));
                    GUI.color = new Color(0.5f, 1f, 0.65f, crtBarAlpha);
                    GUI.DrawTexture(new Rect(0f, byy, gridW, bh3), bandTex, ScaleMode.StretchToFill);
                    // subtle afterglow trail behind the climbing bar (it moves up, so the trail hangs below)
                    for (int k2 = 1; k2 <= 4; k2++)
                    {
                        GUI.color = new Color(0.5f, 1f, 0.65f, crtBarAlpha * 0.55f * (1f - k2 / 5f));
                        GUI.DrawTexture(new Rect(0f, byy + bh3 * 0.55f * k2, gridW, bh3), bandTex, ScaleMode.StretchToFill);
                    }
                    GUI.EndGroup();
                }
                // range rings + center (circles stay circular — it's a scope, not the grid).
                // Rings are drawn in horizontal slices so the roll bar bends them too.
                Vector2 c = new Vector2(ox + bwX * 0.5f, oy + bwY * 0.5f);
                float minDim = Mathf.Min(bwX, bwY);
                GUI.color = new Color(0.2f, 0.78f, 0.35f, 0.5f);
                foreach (float f in new[] { 0.62f, 0.95f })
                {
                    float d = minDim * f;
                    float rx = c.x - d * 0.5f, ry = c.y - d * 0.5f;
                    const int slices = 16;
                    float sh = d / slices;
                    for (int s2 = 0; s2 < slices; s2++)
                    {
                        float wy2 = ry + s2 * sh;
                        float shift2 = CrtShift(wy2 + sh * 0.5f - oy);
                        GUI.DrawTextureWithTexCoords(new Rect(rx + shift2, wy2, d, sh), ringTex,
                            new Rect(0f, 1f - (s2 + 1f) / slices, 1f, 1f / slices));
                    }
                }
                GUI.color = new Color(0.3f, 1f, 0.5f, 0.8f);
                GUI.DrawTexture(new Rect(c.x - 3f + CrtShift(c.y - oy), c.y - 3f, 6f, 6f), puffTex);
                // rotating sweep: glowing afterglow SECTOR + crisp beam line on its leading edge.
                // Radius reaches the grid CORNERS; clipped to the grid so it never spills onto the frame.
                float a = (Time.time * sweepSpeed) % 360f;
                {
                    Matrix4x4 m = GUI.matrix;
                    GUIUtility.RotateAroundPivot(a, c);
                    int wf = Mathf.FloorToInt(Time.time * 12f) % wedgeFrames.Length;
                    var srect = new Rect(c.x - minDim * 0.5f, c.y - minDim * 0.5f, minDim, minDim);
                    if (phosphorGlow > 0f) // long fading phosphor wash under the bright wedge
                    {
                        EnsurePhosphorFrames();
                        GUI.color = new Color(0.22f, 1f, 0.4f, phosphorGlow);
                        GUI.DrawTexture(srect, phosphorFrames[wf], ScaleMode.StretchToFill);
                    }
                    GUI.color = new Color(0.3f, 1f, 0.45f, 0.55f);
                    var wtex = wedgeFrames[wf];
                    GUI.DrawTexture(srect, wtex, ScaleMode.StretchToFill);
                    GUI.color = new Color(0.65f, 1f, 0.7f, 0.9f);
                    GUI.DrawTexture(new Rect(c.x, c.y - 1f, minDim * 0.49f, 2f), Texture2D.whiteTexture);
                    GUI.matrix = m;
                }
                // coordinate labels in their own bands: numbers ABOVE the grid, letters LEFT of it (like the ref)
                if (radarLbl == null)
                {
                    radarLbl = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                    radarLbl.normal.textColor = new Color(0.45f, 1f, 0.6f, 0.85f);
                    if (uiFont != null) radarLbl.font = uiFont;
                }
                radarLbl.fontSize = Mathf.Max(8, Mathf.RoundToInt(cs * 0.26f));
                for (int i = 0; i < Board.Size; i++)
                {
                    // numbers FLOAT above the grid (no boxes); letters sit INSIDE their boxed column — and both bend
                    GUI.Label(new Rect(ox + i * csX + CrtShift(-0.3f * csY), oy - 0.6f * csY, csX, 0.6f * csY), (i + 1).ToString(), radarLbl);
                    GUI.Label(new Rect(gridL + CrtShift(i * csY + csY * 0.5f), oy + i * csY, csX, csY), ((char)('A' + i)).ToString(), radarLbl);
                }
                GUI.color = pc;
            }
        }

        private void DrawEnemyGrid(float ox, float oy)
        {
            DrawRadarBase(ox, oy);
            var b = match.EnemyBoard;
            bool canFire = phase == Phase.Playing && match.PlayerTurn && !match.Over && !InputLocked;
            bool torp = perkMode == PerkMode.Torpedo;

            // ESC-menu marker painter (screenshot staging): clicks place/remove fake markers
            // instead of firing. Consumed on MouseDown so the fire buttons never see them.
            if (debugMark > 0 && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                int mx = Mathf.FloorToInt((Event.current.mousePosition.x - ox) / csX);
                int my = Mathf.FloorToInt((Event.current.mousePosition.y - oy) / csY);
                if (mx >= 0 && mx < Board.Size && my >= 0 && my < Board.Size)
                {
                    int at = debugMarks.FindIndex(m => m.x == mx && m.y == my);
                    if (at >= 0 && debugMarks[at].kind == debugMark) debugMarks.RemoveAt(at); // same kind again = erase
                    else { if (at >= 0) debugMarks.RemoveAt(at); debugMarks.Add((mx, my, debugMark)); }
                    Event.current.Use();
                }
            }

            if (torp && canFire) HandleTorpedoLane(ox, oy);

            for (int gy = 0; gy < Board.Size; gy++)
                for (int gx = 0; gx < Board.Size; gx++)
                {
                    var r = new Rect(ox + gx * csX, oy + gy * csY, csX, csY);
                    if (!b.IsShot(gx, gy))
                    {
                        if (canFire && !torp && GUI.Button(r, GUIContent.none, GUIStyle.none)) FireAt(gx, gy);
                        if (radarIntel.Contains((gx, gy)) && Event.current.type == EventType.Repaint)
                        {
                            float a = 0.65f + 0.35f * Mathf.Sin(Time.time * 4.5f); // same clock as every contact
                            float fl = 0.75f + 0.25f * Mathf.PerlinNoise(Time.time * 9f, 0.5f);
                            DrawGlow(ox, oy, gx, gy, 1.05f, new Color(1f, 0.9f, 0.3f, 0.5f * a * fl)); // amber light bed
                            DrawMark(ox, oy, gx, gy, 0.6f, markRecon, a);
                        }
                    }
                    else
                    {
                        if (gx == torpHX && gy == torpHY && Time.time < torpRevealT) continue; // fish still inbound
                        if (revealAt != null && Time.time < revealAt[gx, gy]) continue;        // shell still inbound
                        bool hit = b.HasShip(gx, gy);
                        if (!DrawRadarBoom(r, eImpact[gx, gy]) && Event.current.type == EventType.Repaint)
                        {
                            bool hit2 = hit;
                            float blink = 0.55f + 0.45f * Mathf.Sin(Time.time * 4.5f); // ONE clock — all contacts blink together
                            float fl = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 9f, 0.5f); // shared lamp flicker
                            if (hit2)
                            {
                                bool sunk = b.IsSunkAt(gx, gy);
                                // each marker sits on a light bed of its own color, flickering with it
                                if (sunk)
                                {
                                    DrawGlow(ox, oy, gx, gy, 1.25f, new Color(0.7f, 0.88f, 1f, 0.55f * blink * fl)); // icy white light
                                    DrawMark(ox, oy, gx, gy, 0.8f, markDestroyed, blink);
                                }
                                else
                                {
                                    DrawGlow(ox, oy, gx, gy, 1.1f, new Color(1f, 0.25f, 0.12f, 0.55f * blink * fl)); // red light
                                    DrawMark(ox, oy, gx, gy, 0.68f, markHit, blink);
                                }
                            }
                            else
                            {
                                DrawGlow(ox, oy, gx, gy, 0.95f, new Color(0.35f, 0.95f, 0.45f, 0.3f * blink * fl)); // faint green light
                                DrawMark(ox, oy, gx, gy, 0.68f, markMiss, blink * 0.8f);
                            }
                        }
                    }
                }

            // debug-painted markers (screenshot staging) — drawn with the EXACT recipes of the
            // real ones (same light beds, same shared blink/flicker clocks) so shots look genuine
            if (debugMarks.Count > 0 && Event.current.type == EventType.Repaint)
            {
                float dBlink = 0.55f + 0.45f * Mathf.Sin(Time.time * 4.5f);
                float dPulse = 0.65f + 0.35f * Mathf.Sin(Time.time * 4.5f);
                float dFl = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 9f, 0.5f);
                foreach (var m in debugMarks)
                    switch (m.kind)
                    {
                        case 1:
                            DrawGlow(ox, oy, m.x, m.y, 1.1f, new Color(1f, 0.25f, 0.12f, 0.55f * dBlink * dFl));
                            DrawMark(ox, oy, m.x, m.y, 0.68f, markHit, dBlink); break;
                        case 2:
                            DrawGlow(ox, oy, m.x, m.y, 1.25f, new Color(0.7f, 0.88f, 1f, 0.55f * dBlink * dFl));
                            DrawMark(ox, oy, m.x, m.y, 0.8f, markDestroyed, dBlink); break;
                        case 3:
                            DrawGlow(ox, oy, m.x, m.y, 0.95f, new Color(0.35f, 0.95f, 0.45f, 0.3f * dBlink * dFl));
                            DrawMark(ox, oy, m.x, m.y, 0.68f, markMiss, dBlink * 0.8f); break;
                        case 4:
                            DrawGlow(ox, oy, m.x, m.y, 1.05f, new Color(1f, 0.9f, 0.3f, 0.5f * dPulse * dFl));
                            DrawMark(ox, oy, m.x, m.y, 0.6f, markRecon, dPulse); break;
                    }
            }

            if (ctrET >= 0f && !DrawRadarBoom(new Rect(ox + ctrEX * csX, oy + ctrEY * csY, csX, csY), ctrET) && Time.time - ctrET > 1f) ctrET = -1f;

            // scope overlays: recon plane + the torpedo's radar track
            if (Event.current.type == EventType.Repaint)
            {
                if (planeT0 >= 0f)
                {
                    float pk = (Time.time - planeT0) / Mathf.Max(0.1f, planeTime);
                    if (pk >= 1f) planeT0 = -1f;
                    else
                    {
                        // OUR scout on the scope keeps the PLANE SILHOUETTE (user call) but as an
                        // instrument return: amber-tinted white silhouette + light bed + fading
                        // afterimages, flickering like every other radar contact. The enemy's plane
                        // on the fleet view stays the real full-color sprite — window vs instrument.
                        var pTex = planeRadarTex != null ? planeRadarTex : planeTex;
                        float pfl = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 11f, 2.6f); // phosphor flicker
                        for (int i = 2; i >= 0; i--) // ghosts first, live contact on top
                        {
                            float tk = pk - i * 0.045f;
                            if (tk < 0f) continue;
                            float yy = Mathf.Lerp(Board.Size + 1.2f, -2.2f, tk);
                            float xx = 4.5f + Mathf.Sin(tk * Mathf.PI * 2.2f) * 3.2f;
                            float vx = Mathf.Cos(tk * Mathf.PI * 2.2f) * 3.2f * Mathf.PI * 2.2f;
                            float vy = -(Board.Size + 3.4f);
                            float ang = Mathf.Atan2(vx, -vy) * Mathf.Rad2Deg; // bank into the turns
                            Vector2 pc2 = new Vector2(ox + xx * csX + csX * 0.5f, oy + yy * csY + csY * 0.5f);
                            if (i == 0) DrawGlow(ox, oy, xx, yy, 1.5f, new Color(0.3f, 1f, 0.45f, 0.4f * pfl)); // green light bed under the contact
                            float s = cs * 1.5f;
                            Color pcP2 = GUI.color;
                            GUI.color = i == 0 ? new Color(0.55f, 1f, 0.6f, 0.95f * (0.75f + 0.25f * pfl))      // live silhouette, hot phosphor green
                                               : new Color(0.25f, 0.9f, 0.4f, 0.28f * (1f - i / 3f));           // decaying afterimage
                            Matrix4x4 pm = GUI.matrix;
                            GUIUtility.RotateAroundPivot(ang, pc2);
                            GUI.DrawTexture(new Rect(pc2.x - s * 0.5f, pc2.y - s * 0.5f, s, s), pTex, ScaleMode.StretchToFill);
                            GUI.matrix = pm;
                            GUI.color = pcP2;
                        }
                    }
                }
                if (torpVisT0 >= 0f)
                {
                    float k = (Time.time - torpVisT0) / torpVisDur;
                    if (k >= 1f) torpVisT0 = -1f;
                    else if (k >= 0f) // still behind the periscope moment
                    {
                        // radar track: a BLUE glass contact crawling up the lane, lit from below, with a fading tail
                        float tfl = 0.7f + 0.3f * Mathf.PerlinNoise(Time.time * 10f, 4.2f); // its light flickers
                        for (int i = 0; i < 8; i++)
                        {
                            float tk = k - i * 0.05f;
                            if (tk < 0f) break;
                            Vector2 tp = Vector2.Lerp(torpVisFrom, torpVisTo, tk);
                            if (tp.y > Board.Size - 0.3f || tp.y < -0.5f) continue;
                            if (i == 0)
                            {
                                DrawGlow(ox, oy, tp.x, tp.y, 1.1f, new Color(0.2f, 0.55f, 1f, 0.55f * tfl)); // blue light bed
                                DrawGlow(ox, oy, tp.x, tp.y, 0.46f, new Color(0.55f, 0.85f, 1f, 0.95f));     // glassy blue head
                                DrawGlow(ox, oy, tp.x, tp.y, 0.2f, new Color(0.9f, 0.98f, 1f, 0.95f * tfl)); // hot core
                            }
                            else
                                DrawGlow(ox, oy, tp.x, tp.y, 0.3f, new Color(0.4f, 0.7f, 1f, 0.45f * (1f - i / 8f)));
                        }
                    }
                }
                for (int si = shellVis.Count - 1; si >= 0; si--)
                {
                    var sv = shellVis[si];
                    float k = (Time.time - sv.t0) / sv.dur;
                    if (k >= 1f) { shellVis.RemoveAt(si); continue; }
                    if (k < 0f) continue; // still in the tube
                    // outgoing shell: a green glassy contact streaking across the scope, lit and
                    // flickering like every other radar return, with a fading tail behind it
                    float sfl = 0.55f + 0.45f * Mathf.PerlinNoise(Time.time * 13f, 7.7f + si * 3.1f);
                    Vector2 dirS = sv.to - sv.from;
                    float angS = Mathf.Atan2(dirS.y, dirS.x) * Mathf.Rad2Deg;
                    Color pcS0 = GUI.color;
                    for (int i = 0; i < 7; i++)
                    {
                        float tk = k - i * 0.05f;
                        if (tk < 0f) break;
                        Vector2 tp = Vector2.Lerp(sv.from, sv.to, tk);
                        if (tp.y > Board.Size - 0.2f) continue; // still entering from our waters
                        if (i == 0)
                        {
                            DrawGlow(ox, oy, tp.x, tp.y, 1.05f, new Color(0.25f, 1f, 0.45f, 0.5f * sfl)); // green light bed
                            // elongated silhouette pointed along the flight path
                            Vector2 pcS = new Vector2(ox + tp.x * csX + csX * 0.5f, oy + tp.y * csY + csY * 0.5f);
                            Matrix4x4 mS = GUI.matrix;
                            GUIUtility.RotateAroundPivot(angS, pcS);
                            float L = cs * 0.8f, H = cs * 0.32f;
                            GUI.color = new Color(0.55f, 1f, 0.65f, 0.9f * (0.8f + 0.2f * sfl));
                            GUI.DrawTexture(new Rect(pcS.x - L * 0.5f, pcS.y - H * 0.5f, L, H), puffTex);
                            GUI.matrix = mS;
                            DrawGlow(ox, oy, tp.x, tp.y, 0.18f, new Color(0.85f, 1f, 0.9f, 0.95f * sfl)); // hot core
                        }
                        else
                            DrawGlow(ox, oy, tp.x, tp.y, 0.26f, new Color(0.3f, 0.95f, 0.5f, 0.4f * (1f - i / 7f)));
                    }
                    GUI.color = pcS0;
                }

            }
        }

        // ---- CRT roll: local top of the refresh band, and the horizontal warp it applies to content ----
        private float CrtBandTop(float bw)
        {
            float off = glassOn ? 0.6f * csY : 0f;                 // include the number band at the top of the glass
            float travel = bw + off + 2f * bw * crtBarHeight;
            return bw - ((Time.time / Mathf.Max(0.5f, crtBarTime)) % 1f) * travel;
        }

        /// <summary>Horizontal displacement for content at a given board-local Y — wobbles while the band passes over it.</summary>
        private float CrtShift(float localY)
        {
            if (crtBarAlpha <= 0f) return 0f;
            float bw = Board.Size * csY, bh = bw * crtBarHeight;
            float t = (localY - CrtBandTop(bw)) / bh;
            if (t < 0f || t > 1f) return 0f;
            float w = Mathf.Sin(Mathf.PI * t); // strongest at the band's core
            return w * cs * crtWarp * Mathf.Sin(Time.time * 34f + localY * 0.12f);
        }

        // Torpedo mode: click ANY cell to pick a column — the fish always swims up from YOUR waters.
        private void HandleTorpedoLane(float ox, float oy)
        {
            Vector2 mp = Event.current.mousePosition;
            int hx = Mathf.FloorToInt((mp.x - ox) / csX), hy = Mathf.FloorToInt((mp.y - oy) / csY);
            bool over = hx >= 0 && hx < Board.Size && hy >= 0 && hy < Board.Size;
            if (over && Event.current.type == EventType.Repaint)
            {
                Color pc = GUI.color; GUI.color = new Color(0.3f, 1f, 0.9f, 0.22f);
                GUI.DrawTexture(new Rect(ox + hx * csX, oy, csX, Board.Size * csY), Texture2D.whiteTexture);
                GUI.color = pc;
            }
            for (int gy = 0; gy < Board.Size; gy++)
                for (int gx = 0; gx < Board.Size; gx++)
                    if (GUI.Button(new Rect(ox + gx * csX, oy + gy * csY, csX, csY), GUIContent.none, GUIStyle.none))
                        TryTorpedo(gx);
        }

        private void FireAt(int gx, int gy)
        {
            if (online) { FireOnline(gx, gy); return; }
            var res = match.PlayerFire(gx, gy);
            if (res.Outcome == FireOutcome.Invalid) return;
            RenderMyShots(match.LastPlayerShots, gx, gy);
        }

        // Online: send a fire intent to the defender and wait for the resolved result (OnNetMoveResult).
        // I don't hold the enemy layout, so nothing is resolved here — input stays locked until it lands.
        private void FireOnline(int gx, int gy)
        {
            if (!match.PlayerTurn || match.Over || awaitingResult || InputLocked) return;
            if (match.EnemyBoard.IsShot(gx, gy)) { message = T("Already fired there."); return; }
            awaitingResult = true;
            pendingKind = MoveKind.Fire;
            pendingFireX = gx; pendingFireY = gy;
            net.SendMoveIntent(NetCodec.EncodeFire(gx, gy));
            Sfx(clipClick, 0.7f);
            message = string.Format(T("FIRING {0}..."), Coord(gx, gy));
        }

        // Animate my own shots streaking onto the targeting scope — shared by single-player
        // (match.LastPlayerShots) and the networked result path (the defender's resolved ShotEvents).
        // gx/gy is the cell I aimed at. The board state is already mutated before this runs.
        private void RenderMyShots(System.Collections.Generic.List<BattleshipMatch.ShotEvent> shots, int gx, int gy)
        {
            Sfx(clipFire);
            // shells show up ON the scope: green flickering contacts streaking from our waters
            // (bottom edge) to their cells — nothing lands until each one gets there.
            // Scorch fires a visible SALVO: one blip per shot, staggered off separate tubes.
            int n = shots.Count;
            float settle = Time.time; string msg = "";
            for (int i = 0; i < n; i++)
            {
                var ev = shots[i];
                var sv = new ShellVis
                {
                    from = new Vector2(n > 1 ? 3.4f + i * 2.2f : 4.5f, Board.Size + 1.4f),
                    to = new Vector2(ev.X, ev.Y),
                    t0 = Time.time + fireDelay + i * 0.28f // muzzle sound first, shell appears a beat later
                };
                sv.dur = Mathf.Max(0.4f, Vector2.Distance(sv.from, sv.to) / Mathf.Max(1f, shellSpeed));
                shellVis.Add(sv);
                if (i > 0) QueueSfx(sv.t0 - fireDelay, clipFire, 0.9f); // every tube gets its own muzzle blast
                float at = sv.t0 + sv.dur; // this shot resolves when ITS shell arrives
                settle = Mathf.Max(settle, at);
                radarIntel.Remove((ev.X, ev.Y));
                revealAt[ev.X, ev.Y] = at;  // marker stays dark until the blip arrives
                if (ev.Result.Outcome == FireOutcome.Miss) { eImpact[ev.X, ev.Y] = at; QueueShake(at, 0.08f); QueueSfx(at, clipSplash); }
                if (ev.Result.Outcome == FireOutcome.Hit) { eImpact[ev.X, ev.Y] = at; QueueShake(at, 0.2f); QueueSfx(at, RandomHit()); }
                if (ev.Result.Outcome == FireOutcome.Sunk) { eImpact[ev.X, ev.Y] = at; QueueShake(at, 0.35f); QueueSfx(at, RandomHit()); QueueSfx(at + 0.3f, clipSunk); }
                if (ev.Result.Outcome == FireOutcome.Intercepted) { ctrEX = ev.X; ctrEY = ev.Y; ctrET = at; QueueShake(at, 0.25f); QueueSfx(at, clipIntercept); }
                string part = ev.Result.Outcome == FireOutcome.Intercepted ? T("INTERCEPTED!") : Describe(ev.Result, true);
                msg += (i == 0 ? "" : "   ") + (n > 1 ? Coord(ev.X, ev.Y) + ": " : "") + part;
            }
            message = n > 1 ? T("SALVO AWAY...") : string.Format(T("FIRING {0}..."), Coord(gx, gy));  // action now, result on impact
            QueueMsg(msg, settle);
            settle += 0.9f; // last shell's flight + boom settles
            actionEndT = settle;
            aiTimer = (settle - Time.time) + autoEndTurn;  // you stay on the targeting screen until YOU end the turn
            if (match.EnemyBoard.AllSunk) { phase = Phase.Over; QueueMsg("ENEMY FLEET DESTROYED — YOU WIN!", settle); QueueSfx(settle + 0.4f, clipWin); }
        }

        private void TryTorpedo(int gx)
        {
            if (online) { TorpedoOnline(gx); return; }
            if (!match.PlayerTorpedo(gx)) return;
            RenderMyTorpedo(gx, match.LastPlayerShots);
        }

        // Online: I don't hold the enemy layout, so I can't resolve the run — the defender does. Send the
        // lane, lock input, and wait for the struck cell in OnNetMoveResult (which calls RenderMyTorpedo).
        private void TorpedoOnline(int gx)
        {
            if (!match.PlayerTurn || match.Over || awaitingResult || InputLocked) return;
            if (!match.PlayerPerks.Ready(match.PlayerBoard, ShipType.Submarine)) return;
            awaitingResult = true;
            pendingKind = MoveKind.Torpedo;
            pendingCol = gx;
            perkMode = PerkMode.None;
            net.SendMoveIntent(NetCodec.EncodeTorpedo(gx));
            message = "🐟 TORPEDO AWAY...";
        }

        // Animate my torpedo run — shared by single-player (match.LastPlayerShots) and the networked
        // result path (the defender's resolved struck cell, 0 or 1 shots). col is the lane I fired.
        private void RenderMyTorpedo(int col, System.Collections.Generic.List<BattleshipMatch.ShotEvent> shots)
        {
            int gx = col;
            perkMode = PerkMode.None;
            Sfx(clipPeriscope); // then the torpedo LOOP takes over when the fish launches (see Update)
            if (shots.Count == 0)
            {
                // empty lane: the fish still runs the whole column and exits at the top, hitting nothing
                torpVisFrom = new Vector2(gx, 10.4f);
                torpVisTo = new Vector2(gx, -1.6f);
                torpVisDur = Mathf.Max(0.45f, (torpVisFrom.y - torpVisTo.y) / Mathf.Max(1f, torpSpeed));
                torpVisT0 = Time.time + 0.35f;
                torpLoopStart = torpVisT0; torpLoopEnd = torpVisT0 + torpVisDur + trailLife; // whir until the trail fades too
                QueueSfx(torpVisT0, clipTorpLaunch);
                message = "🐟 TORPEDO AWAY...";
                QueueMsg("TORPEDO: NO CONTACT.", torpVisT0 + torpVisDur);
            }
            else
            {
                var ev = shots[0];
                radarIntel.Remove((ev.X, ev.Y));
                torpVisFrom = new Vector2(gx, 10.4f); // out of your waters, below the board
                torpVisTo = new Vector2(ev.X, ev.Y);
                torpVisDur = Mathf.Max(0.45f, (torpVisFrom.y - ev.Y) / Mathf.Max(1f, torpSpeed));
                torpVisT0 = Time.time + 0.35f; // fish launches right after the periscope
                torpLoopStart = torpVisT0; torpLoopEnd = torpVisT0 + torpVisDur; // whir cuts at impact — the boom takes over
                QueueSfx(torpVisT0, clipTorpLaunch);
                float arr = torpVisT0 + torpVisDur;    // NOTHING explodes before the fish arrives
                if (ev.Result.Outcome == FireOutcome.Intercepted)
                {
                    ctrEX = ev.X; ctrEY = ev.Y; ctrET = arr; // counter-missile boom waits for the fish
                    QueueShake(arr, 0.25f); QueueSfx(arr, clipIntercept);
                    message = "🐟 TORPEDO AWAY...";
                    QueueMsg("TORPEDO INTERCEPTED!", arr);
                }
                else
                {
                    eImpact[ev.X, ev.Y] = arr;         // explosion anim plays right on the struck cell
                    torpHX = ev.X; torpHY = ev.Y; torpRevealT = arr; // hit marker hidden until impact
                    QueueShake(arr, ev.Result.Outcome == FireOutcome.Sunk ? 0.4f : 0.25f);
                    QueueSfx(arr, RandomHit());
                    if (ev.Result.Outcome == FireOutcome.Sunk) QueueSfx(arr + 0.3f, clipSunk);
                    message = "🐟 TORPEDO AWAY...";
                    QueueMsg(Coord(ev.X, ev.Y) + ": " + Describe(ev.Result, true), arr);
                }
            }
            float seq = 0.35f + torpVisDur; // periscope + full run (hit or empty)
            actionEndT = Time.time + seq + 0.4f;
            aiTimer = seq + autoEndTurn;
            if (match.EnemyBoard.AllSunk) { phase = Phase.Over; QueueMsg("ENEMY FLEET DESTROYED — YOU WIN!", Time.time + seq); QueueSfx(Time.time + 1.3f, clipWin); }
        }



        private void HandleBoardDrag(float ox, float oy, Event e, Vector2 mp)
        {
            if (e.type == EventType.MouseDown && e.button == 0 && dragIdx < 0)
            {
                int cx = Mathf.FloorToInt((mp.x - ox) / cs), cy = Mathf.FloorToInt((mp.y - oy) / cs);
                for (int i = 0; i < fleet.Length; i++)
                    if (fleet[i].placed && InShip(i, cx, cy)) { dragIdx = i; orient = fleet[i].o; fleet[i].placed = false; message = "Right-click to rotate"; e.Use(); break; }
            }

            if (dragIdx >= 0)
            {
                int cx = Mathf.FloorToInt((mp.x - ox) / cs), cy = Mathf.FloorToInt((mp.y - oy) / cs);
                bool over = cx >= 0 && cx < Board.Size && cy >= 0 && cy < Board.Size;
                int len = Ships.Length(fleet[dragIdx].type);

                if (e.type == EventType.Repaint)
                {
                    if (over)
                    {
                        var prev = GUI.color;
                        GUI.color = CanPlace(dragIdx, cx, cy, orient) ? OkTint : BadTint;
                        DrawShipSprite(ox, oy, cx, cy, fleet[dragIdx].type, orient);
                        GUI.color = prev;
                    }
                    else
                    {
                        var tex = shipTex != null ? shipTex[(int)fleet[dragIdx].type] : null;
                        var gr = new Rect(mp.x - len * cs * 0.5f, mp.y - cs * 0.5f, len * cs, cs);
                        if (tex != null) GUI.DrawTexture(gr, tex, ScaleMode.StretchToFill);
                        else DrawShip(0, 0, 0, 0, len, orient, ShipCol);
                    }
                }

                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (over && CanPlace(dragIdx, cx, cy, orient))
                    { fleet[dragIdx].placed = true; fleet[dragIdx].x = cx; fleet[dragIdx].y = cy; fleet[dragIdx].o = orient; message = AllPlaced() ? T("Fleet ready — press ▶ Start!") : string.Format(T("Placed {0}."), T(fleet[dragIdx].type.ToString())); dragIdx = -1; }
                    else if (!over) { dragIdx = -1; message = "Returned to tray."; }
                    e.Use();
                }
            }
        }

        private bool InShip(int idx, int cx, int cy)
        {
            int len = Ships.Length(fleet[idx].type);
            int dx = fleet[idx].o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx;
            for (int k = 0; k < len; k++) if (fleet[idx].x + dx * k == cx && fleet[idx].y + dy * k == cy) return true;
            return false;
        }

        private void Fill(Rect r, Color bg, string label)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color pc = GUI.color;
            GUI.color = Grid; GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), Texture2D.whiteTexture);
            GUI.color = bg; GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = pc;
            if (!string.IsNullOrEmpty(label)) GUI.Label(r, label, mark);
        }
    

        private void DrawPanel(float px, float pw, bool showTarget, Event e, Vector2 mp)
        {
            float bh = btn.fontSize + 16f;
            float y = boardY;
            GUI.Label(new Rect(px, y, pw, big.fontSize + 8), lang == Lang.FR ? "BATAILLE NAVALE" : "BATTLESHIP", big);
            y += big.fontSize + 14;

            string head = phase == Phase.Over ? (match.PlayerWon ? T("YOU WIN!") : T("AI WINS"))
                        : phase == Phase.Setup ? T("PLACE YOUR FLEET")
                        : showTarget ? T("TARGETING") : T("YOUR FLEET");
            GUI.Label(new Rect(px, y, pw, hdr.fontSize + 6), head, hdr);
            y += hdr.fontSize + 10;

            GUI.Label(new Rect(px, y, pw, (hdr.fontSize + 4) * 2), T(message), hdr);
            y += (hdr.fontSize + 4) * 2 + 8;

            if (phase == Phase.Setup) { DrawSetupPanel(px, pw, y, e, mp); return; }

            if (phase == Phase.Playing)
            {
                var r = new Rect(px, y, pw, bh * 1.3f);
                if (!targeting) { GUI.enabled = match.PlayerTurn && !InputLocked; if (GUI.Button(r, T("🎯 TARGET ▶"), btn)) { targeting = true; Sfx(clipFlip, 0.8f); } GUI.enabled = true; }
                else if (GUI.Button(r, T("◀ MY FLEET"), btn)) { targeting = false; Sfx(clipFlip, 0.8f); } // always available
                y += bh * 1.3f + 14;
                if (match.PlayerTurn && !match.Over && turnDeadline > 0f)
                {
                    // your turn clock, mirrored from the radar LCD — with the same skip option
                    int tleft = Mathf.CeilToInt(Mathf.Max(0f, turnDeadline - Time.time));
                    GUI.Label(new Rect(px, y, pw, hdr.fontSize + 6), string.Format(T("⏱ TURN TIMER: {0}s"), tleft), hdr);
                    y += hdr.fontSize + 10;
                    GUI.enabled = !InputLocked;
                    if (GUI.Button(new Rect(px, y, pw, bh * 1.3f), T("⏭ END TURN"), btn)) SkipTurn(false);
                    GUI.enabled = true;
                    y += bh * 1.3f + 14;
                }
                if (!match.PlayerTurn && !match.Over)
                {
                    // enemy turn is pending: confirm it, or let the countdown auto-end
                    bool canEnd = Time.time >= actionEndT;
                    GUI.enabled = canEnd;
                    string lbl = canEnd ? string.Format(T("⏳ END TURN ({0}s)"), Mathf.CeilToInt(Mathf.Max(0f, aiTimer))) : T("⏳ END TURN");
                    if (GUI.Button(new Rect(px, y, pw, bh * 1.3f), lbl, btn)) { aiTimer = 0.01f; Sfx(clipClick, 0.7f); }
                    GUI.enabled = true;
                    y += bh * 1.3f + 14;
                }
                GUI.Label(new Rect(px, y, pw, hdr.fontSize + 6), showTarget ? string.Format(T("Enemy ships: {0} / 5"), match.EnemyBoard.ShipsRemaining) : string.Format(T("Your ships: {0} / 5"), match.PlayerBoard.ShipsRemaining), hdr);
                y += hdr.fontSize + 14;

                GUI.Label(new Rect(px, y, pw, hdr.fontSize + 4), T("PERKS"), hdr); y += hdr.fontSize + 8;
                y = PerkButton(px, y, pw, ShipType.Destroyer,
                    T(perkMode == PerkMode.Relocate ? "🚤 RELOCATE ◀ pick a spot" : "🚤 RELOCATE"),
                    () => { perkMode = perkMode == PerkMode.Relocate ? PerkMode.None : PerkMode.Relocate; targeting = false; orient = fleet[FleetIndex(ShipType.Destroyer)].o; if (perkMode == PerkMode.Relocate) ComputeSailReach(); });
                y = PerkButton(px, y, pw, ShipType.Carrier, T("📡 RADAR SCOUT"), DoRadar);
                y = PerkButton(px, y, pw, ShipType.Battleship,
                    T(match.ScorchArmed ? "☄ SCORCH ARMED ✓" : "☄ SCORCH"),
                    () => match.ArmScorch(!match.ScorchArmed));
                y = PerkButton(px, y, pw, ShipType.Cruiser,
                    T(perkMode == PerkMode.Intercept ? "🛡 INTERCEPT ◀ pick a cell" : "🛡 INTERCEPT"),
                    () => { perkMode = perkMode == PerkMode.Intercept ? PerkMode.None : PerkMode.Intercept; targeting = false; });
                y = PerkButton(px, y, pw, ShipType.Submarine,
                    T(perkMode == PerkMode.Torpedo ? "🐟 TORPEDO ◀ pick a lane" : "🐟 TORPEDO"),
                    () => { perkMode = perkMode == PerkMode.Torpedo ? PerkMode.None : PerkMode.Torpedo; targeting = perkMode == PerkMode.Torpedo; });
            }
            else
            {
                if (GUI.Button(new Rect(px, y, pw, bh * 1.3f), T("New Game"), btn)) { Sfx(clipClick, 0.7f); NewGame(); }
            }
        }

        private void DrawSetupPanel(float px, float pw, float y, Event e, Vector2 mp)
        {
            float bh = btn.fontSize + 16f;
            GUI.Label(new Rect(px, y, pw, hdr.fontSize + 4), T("AI Difficulty"), hdr); y += hdr.fontSize + 8;
            float dw = (pw - 12f) / 3f;
            DiffRect(px, y, dw, bh, BattleshipAI.Level.Easy);
            DiffRect(px + dw + 6f, y, dw, bh, BattleshipAI.Level.Medium);
            DiffRect(px + 2f * (dw + 6f), y, dw, bh, BattleshipAI.Level.Hard);
            y += bh + 14;

            float hw = (pw - 8f) / 2f;
            if (GUI.Button(new Rect(px, y, hw, bh), T("Rotate (R)"), btn) && dragIdx >= 0) orient = Flip(orient);
            if (GUI.Button(new Rect(px + hw + 8f, y, hw, bh), T("Randomize"), btn)) RandomizeFleet();
            y += bh + 8;
            if (GUI.Button(new Rect(px, y, hw, bh), T("Clear"), btn)) { for (int i = 0; i < fleet.Length; i++) fleet[i].placed = false; }
            GUI.enabled = AllPlaced();
            if (GUI.Button(new Rect(px + hw + 8f, y, hw, bh), T("▶ Start"), btn)) { Sfx(clipFlip, 0.8f); SyncBoard(); phase = Phase.Playing; targeting = false; message = "Your turn — tap TARGET to fire."; }
            GUI.enabled = true;
            y += bh + 16;

            GUI.Label(new Rect(px, y, pw, hdr.fontSize + 4), T("Drag ships to the board:"), hdr); y += hdr.fontSize + 8;
            DrawTrayVertical(px, y, pw, e, mp);
        }

        private void DiffRect(float x, float y, float w, float h, BattleshipAI.Level d)
        {
            var prev = GUI.backgroundColor;
            if (difficulty == d) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
            if (GUI.Button(new Rect(x, y, w, h), T(d.ToString()), btn) && difficulty != d) { difficulty = d; NewGame(); }
            GUI.backgroundColor = prev;
        }

        private void DrawTrayVertical(float px, float y, float pw, Event e, Vector2 mp)
        {
            float sh = Mathf.Clamp(cs * 0.5f, 28f, 54f);
            for (int i = 0; i < fleet.Length; i++)
            {
                if (fleet[i].placed || i == dragIdx) continue;
                int len = Ships.Length(fleet[i].type);
                float sw = Mathf.Min(len * sh, pw * 0.6f);
                var slot = new Rect(px, y, pw, sh + 8);
                if (e.type == EventType.Repaint)
                {
                    var tex = shipTex != null ? shipTex[(int)fleet[i].type] : null;
                    if (tex != null) GUI.DrawTexture(new Rect(px, y, sw, sh), tex, ScaleMode.ScaleToFit);
                    else for (int k = 0; k < len; k++) Fill(new Rect(px + k * sh, y, sh - 2, sh - 2), ShipCol, "");
                    GUI.Label(new Rect(px + pw * 0.62f, y, pw * 0.38f, sh), T(fleet[i].type.ToString()) + " (" + len + ")", small);
                }
                if (e.type == EventType.MouseDown && e.button == 0 && dragIdx < 0 && slot.Contains(mp)) { dragIdx = i; orient = Orientation.Horizontal; e.Use(); }
                y += sh + 12;
            }
        }





        private void DrawGridOverlay(float ox, float oy)
        {
            if (!showGrid || Event.current.type != EventType.Repaint) return;
            Color pc = GUI.color; GUI.color = Grid;
            float bw = Board.Size * cs;
            float t = Mathf.Max(1f, Mathf.Round(cs * 0.04f));
            for (int i = 0; i <= Board.Size; i++)
            {
                GUI.DrawTexture(new Rect(ox + i * cs - t * 0.5f, oy, t, bw), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(ox, oy + i * cs - t * 0.5f, bw, t), Texture2D.whiteTexture);
            }
            GUI.color = pc;
        }


        private bool DrawImpact(Rect cell, float t0)
            => DrawFx(cell, t0, explFrames, 1.6f, new Color(0.75f, 0.9f, 1f)); // splash throws a pale water-glow

        /// <summary>Mid-air counter-missile detonation (falls back to the water explosion if no art).</summary>
        private bool DrawBoom(Rect cell, float t0)
            => DrawFx(cell, t0, (boomFrames != null && boomFrames.Length > 0) ? boomFrames : explFrames, 1.5f,
                      new Color(1f, 0.72f, 0.35f)); // fireball throws warm light

        private bool DrawFx(Rect cell, float t0, Texture2D[] frames, float scale, Color glow)
        {
            if (t0 < 0f || frames == null || frames.Length == 0) return false;
            int f = Mathf.FloorToInt((Time.time - t0) * explFps);
            if (f < 0 || f >= frames.Length) return false;
            if (Event.current.type == EventType.Repaint)
            {
                float ew = cell.width * scale, eh = cell.height * scale;
                var r = new Rect(cell.x + cell.width * 0.5f - ew * 0.5f, cell.y + cell.height * 0.5f - eh * 0.5f, ew, eh);
                Color pc = GUI.color;
                // subtle light on the water under the blast — brightest at ignition, dying with the anim
                float k = f / (float)frames.Length;
                float ga = 0.34f * (1f - k) * (1f - k);
                float gs = cell.width * (2.2f + 0.8f * k);
                GUI.color = new Color(glow.r, glow.g, glow.b, ga);
                GUI.DrawTexture(new Rect(cell.center.x - gs * 0.5f, cell.center.y - gs * 0.5f, gs, gs), puffTex);
                GUI.color = Color.white;
                GUI.DrawTexture(r, frames[f], ScaleMode.StretchToFill);
                GUI.color = pc;
            }
            return true;
        }








        private int FleetIndex(ShipType t)
        {
            for (int i = 0; i < fleet.Length; i++) if (fleet[i].type == t) return i;
            return -1;
        }

        private void HandleRelocate(float ox, float oy, Event e, Vector2 mp)
        {
            if (online) return;   // online relocate isn't wired yet (Step 7)
            int cx = Mathf.FloorToInt((mp.x - ox) / cs), cy = Mathf.FloorToInt((mp.y - oy) / cs);
            bool over = cx >= 0 && cx < Board.Size && cy >= 0 && cy < Board.Size;
            if (e.type == EventType.Repaint && over)
            {
                var prev = GUI.color;
                GUI.color = RelocateOk(cx, cy, orient) ? OkTint : BadTint; // red = illegal OR unreachable by sea
                DrawShipSprite(ox, oy, cx, cy, ShipType.Destroyer, orient);
                GUI.color = prev;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && over)
            {
                if (!RelocateOk(cx, cy, orient))
                { message = "No navigable route there — the fleet blocks the way."; e.Use(); return; }
                int di = FleetIndex(ShipType.Destroyer);
                var old = match.PlayerBoard.ShipCells(ShipType.Destroyer);
                if (match.PlayerRelocate(cx, cy, orient))
                {
                    if (old != null) foreach (var c in old) hitT[c.x, c.y] = -1f;
                    if (di >= 0)
                    {
                        int oldX = fleet[di].x, oldY = fleet[di].y; Orientation oldO = fleet[di].o;
                        fleet[di].x = cx; fleet[di].y = cy; fleet[di].o = orient;
                        BuildSailPath(di, oldX, oldY, oldO, cx, cy, orient);
                        relocT0 = Time.time;
                    }
                    foreach (var c in match.PlayerBoard.ShipCells(ShipType.Destroyer))
                        if (match.PlayerBoard.IsCellDamaged(c.x, c.y)) hitT[c.x, c.y] = Time.time - 999f;
                    perkMode = PerkMode.None;
                    message = "Destroyer relocated — the enemy's markers just went stale!";
                    actionEndT = Time.time + relocDur + 0.5f; // let the sail finish
                    aiTimer = relocDur + 0.5f + autoEndTurn;
                    Sfx(clipSail);
                }
                else message = "Can't relocate there.";
                e.Use();
            }
        }

        private void HandleIntercept(float ox, float oy, Event e, Vector2 mp)
        {
            if (online) return;   // online intercept isn't wired yet (Step 7)
            if (e.type != EventType.MouseDown || e.button != 0) return;
            int cx = Mathf.FloorToInt((mp.x - ox) / cs), cy = Mathf.FloorToInt((mp.y - oy) / cs);
            if (cx < 0 || cx >= Board.Size || cy < 0 || cy >= Board.Size) return;
            if (match.PlayerSetIntercept(cx, cy))
            {
                perkMode = PerkMode.None;
                message = string.Format(T("Counter-missile battery locked on {0}."), Coord(cx, cy));
                Sfx(clipPing, 0.7f); // battery lock-on
            }
            else message = "Can't protect that cell (already shot, or it's the cruiser's fire-control center).";
            e.Use();
        }

        /// <summary>
        /// Full intercept sequence: silo-launch anim on the cruiser's VLS cell, THEN the missile
        /// spawns there and flies to the intercepted cell, detonating on arrival.
        /// </summary>
        private float LaunchCounterMissile(int tx, int ty)
        {
            var cc = match.PlayerBoard.ShipCells(ShipType.Cruiser);
            int fx = (cc != null && cc.Count > 0) ? cc[0].x : tx; // silo cell — the VLS chunk the launch anim repaints (sprite-left since the bow-right flip)
            int fy = (cc != null && cc.Count > 0) ? cc[0].y : ty;
            mslFrom = new Vector2(fx, fy); mslTo = new Vector2(tx, ty);
            float dist = Vector2.Distance(mslFrom, mslTo);
            mslDur = Mathf.Max(0.25f, dist / Mathf.Max(1f, mslSpeed));
            float lnchDur = 0f;
            if (lnchFrames != null && lnchFrames.Length > 0) { lnchT0 = Time.time; lnchDur = Mathf.Max(0, lnchFrames.Length - 5) / Mathf.Max(1f, lnchFps); }
            mslT0 = Time.time + lnchDur;                     // missile departs 5 frames before the anim ends
            mslAngle = Mathf.Atan2(ty - fy, tx - fx) * Mathf.Rad2Deg + 90f; // exact heading (north sprite rotated)
            Sfx(clipMissile);
            ctrPX = tx; ctrPY = ty; ctrPT = mslT0 + mslDur;  // detonation when the missiles meet
            QueueSfx(ctrPT, clipIntercept);
            return ctrPT;
        }

        // ---- relocate sail: least-cost bow-first route through open water (state = bow cell + heading) ----
        // Headings: 0=E, 1=N, 2=W, 3=S. Forward advances the bow; 90° pivots (on bow or tail) cost 4×
        // a forward step so routes favor straight runs. The path becomes keyframe segments (center + angle).
        private static readonly int[] HdX = { 1, 0, -1, 0 };
        private static readonly int[] HdY = { 0, -1, 0, 1 };
        private static readonly float[] HdAng = { 0f, -90f, 180f, 90f };
        private bool[,] sailReach; // destinations the destroyer can actually sail to (computed on entering relocate mode)

        private static int SailEnc(int x, int y, int h) => (y * Board.Size + x) * 4 + h;

        private int[] SailSearch(bool[,] occ, int root)
        {
            bool Valid(int x, int y) => x >= 0 && x < Board.Size && y >= 0 && y < Board.Size && !occ[x, y];
            bool SweepOk(int x, int y) => x < 0 || x >= Board.Size || y < 0 || y >= Board.Size || !occ[x, y];
            int n = Board.Size * Board.Size * 4;
            var pv = new int[n]; var dist = new float[n]; var done = new bool[n];
            for (int i = 0; i < n; i++) { pv[i] = -2; dist[i] = float.MaxValue; }
            pv[root] = -1; dist[root] = 0f;
            while (true)
            {
                int cur = -1; float best = float.MaxValue;
                for (int i = 0; i < n; i++) if (!done[i] && dist[i] < best) { best = dist[i]; cur = i; }
                if (cur == -1) break;
                done[cur] = true;
                int h = cur & 3, cell = cur >> 2, x = cell % Board.Size, y = cell / Board.Size;
                void Relax(int px2, int py2, int ph2, float w)
                {
                    int e2 = SailEnc(px2, py2, ph2);
                    if (done[e2] || dist[cur] + w >= dist[e2]) return;
                    dist[e2] = dist[cur] + w;
                    pv[e2] = cur;
                }
                if (Valid(x + HdX[h], y + HdY[h])) Relax(x + HdX[h], y + HdY[h], h, 1f); // ahead — the bow always leads
                foreach (int nh in new[] { (h + 1) & 3, (h + 3) & 3 })
                {
                    // pivot on the BOW: tail swings; new tail cell + swept diagonal must be clear
                    if (Valid(x - HdX[nh], y - HdY[nh]) && SweepOk(x - HdX[h] - HdX[nh], y - HdY[h] - HdY[nh]))
                        Relax(x, y, nh, 4f);
                    // pivot on the TAIL: bow swings; new bow cell + swept diagonal must be clear
                    int tx = x - HdX[h], ty = y - HdY[h];
                    if (Valid(tx + HdX[nh], ty + HdY[nh]) && SweepOk(x + HdX[nh], y + HdY[nh]))
                        Relax(tx + HdX[nh], ty + HdY[nh], nh, 4f);
                }
            }
            return pv;
        }

        /// <summary>Reachable-destination map for the destroyer from its current berth (any arrival heading counts).</summary>
        private void ComputeSailReach()
        {
            int di = FleetIndex(ShipType.Destroyer);
            if (di < 0 || !fleet[di].placed) { sailReach = null; return; }
            var f = fleet[di];
            int h0 = f.o == Orientation.Horizontal ? 0 : 1;
            int sx = f.o == Orientation.Horizontal ? f.x + 1 : f.x, sy = f.y;
            int se = SailEnc(sx, sy, h0);
            var pv = SailSearch(Occupied(di), se);
            sailReach = new bool[Board.Size, Board.Size];
            for (int y = 0; y < Board.Size; y++)
                for (int x = 0; x < Board.Size; x++)
                    for (int h = 0; h < 4; h++)
                        if (pv[SailEnc(x, y, h)] != -2 || SailEnc(x, y, h) == se) { sailReach[x, y] = true; break; }
        }

        /// <summary>Placement is legal AND the destroyer can actually navigate its bow there.</summary>
        private bool RelocateOk(int cx, int cy, Orientation o)
        {
            if (!match.PlayerBoard.CanRelocate(ShipType.Destroyer, cx, cy, o)) return false;
            if (sailReach == null) return true;
            int bx = o == Orientation.Horizontal ? cx + 1 : cx;
            return Board.InBounds(bx, cy) && sailReach[bx, cy];
        }

        private void BuildSailPath(int di, int oxg, int oyg, Orientation oo, int nx, int ny, Orientation no)
        {
            segDur.Clear(); segC0.Clear(); segC1.Clear(); segA0.Clear(); segA1.Clear(); segHide.Clear();
            relocDur = 0f;
            var occ = Occupied(di);
            bool Valid(int x, int y) => x >= 0 && x < Board.Size && y >= 0 && y < Board.Size && !occ[x, y];
            // cells swept during a pivot may poke past the board edge, just not over a ship
            bool SweepOk(int x, int y) => x < 0 || x >= Board.Size || y < 0 || y >= Board.Size || !occ[x, y];
            int Enc(int x, int y, int h) => (y * Board.Size + x) * 4 + h;
            Vector2 CenterOf(int enc) { int h = enc & 3, cell = enc >> 2; return new Vector2(cell % Board.Size - HdX[h] * 0.5f, cell / Board.Size - HdY[h] * 0.5f); }
            void AddSeg(Vector2 c0, Vector2 c1, float a0, float a1, float dur, bool hide)
            { segC0.Add(c0); segC1.Add(c1); segA0.Add(a0); segA1.Add(a1); segDur.Add(dur); segHide.Add(hide); relocDur += dur; }
            float BuildSegs(System.Collections.Generic.List<int> sts, float startAng)
            {
                float ang = startAng;
                for (int i = 1; i < sts.Count; i++)
                {
                    int pa = sts[i - 1], cu = sts[i];
                    int ph = pa & 3, ch = cu & 3;
                    float a0 = ang, a1 = ang, dur;
                    if (ph == ch) dur = Vector2.Distance(CenterOf(pa), CenterOf(cu)) / Mathf.Max(1f, relocSpeed); // forward/reverse
                    else { a1 = ang + ((ch == ((ph + 1) & 3)) ? -90f : 90f); ang = a1; dur = relocTurnTime; }
                    AddSeg(CenterOf(pa), CenterOf(cu), a0, a1, Mathf.Max(0.05f, dur), false);
                }
                return ang;
            }

            int h0 = oo == Orientation.Horizontal ? 0 : 1;
            int sx = oo == Orientation.Horizontal ? oxg + 1 : oxg, sy = oyg; // bow cell (destroyer len 2)
            int h1 = no == Orientation.Horizontal ? 0 : 1;
            int gx = no == Orientation.Horizontal ? nx + 1 : nx, gy = ny;
            int se = Enc(sx, sy, h0), ge = Enc(gx, gy, h1);

            System.Collections.Generic.List<int> Chain(int[] pv, int end)
            { var l = new System.Collections.Generic.List<int>(); for (int c = end; c != -1; c = pv[c]) l.Add(c); l.Reverse(); return l; }
            int ChainLen(int[] pv, int end) { int n = 0; for (int c = end; c != -1; c = pv[c]) n++; return n; }
            bool Reached(int[] pv, int e, int root) => pv[e] != -2 || e == root;

            var prev1 = SailSearch(occ, se);

            // best directly-sailable goal: exact pose, else same cell any heading + rotate at the dock
            int chosen = Reached(prev1, ge, se) ? ge : -1;
            if (chosen == -1)
                for (int h = 0; h < 4; h++)
                    if (Reached(prev1, Enc(gx, gy, h), se)) { chosen = Enc(gx, gy, h); break; }

            if (chosen != -1)
            {
                var states = Chain(prev1, chosen);
                int ch = chosen & 3;
                while (ch != h1) // final in-place rotation to the docking orientation
                {
                    ch = (h1 - ch + 4) % 4 == 3 ? (ch + 3) & 3 : (ch + 1) & 3;
                    states.Add(Enc(gx, gy, ch));
                }
                BuildSegs(states, HdAng[h0]);
                return;
            }

            // fleet walls the route off entirely (rare): straight glide fallback
            float aStart = HdAng[h0];
            AddSeg(CenterOf(se), CenterOf(ge), aStart, aStart + Mathf.DeltaAngle(aStart, HdAng[h1]),
                   Mathf.Max(0.3f, Vector2.Distance(CenterOf(se), CenterOf(ge)) / Mathf.Max(1f, relocSpeed)), false);
        }

        // horizontal ship sprite rotated to an arbitrary heading around its center (cell units)
        private void DrawShipAngle(float ox, float oy, Vector2 c, float angle, ShipType type)
        {
            if (Event.current.type != EventType.Repaint) return;
            var tex = (shipTex != null && (int)type < shipTex.Length) ? shipTex[(int)type] : null;
            if (tex == null) return;
            int len = Ships.Length(type);
            Vector2 pc = new Vector2(ox + c.x * cs + cs * 0.5f, oy + c.y * cs + cs * 0.5f);
            float hs3 = cs * ShipScale(type);
            Matrix4x4 m = GUI.matrix;
            RotateContent(angle, pc);
            GUI.DrawTexture(new Rect(pc.x - len * cs * 0.5f, pc.y - hs3 * 0.5f, len * cs, hs3), tex, ScaleMode.StretchToFill);
            GUI.matrix = m;
        }

        private void DrawFleetOverlays(float ox, float oy)
        {
            if (match.PlayerPerks.HasProtection && Event.current.type == EventType.Repaint)
            {
                int sx = match.PlayerPerks.ProtectX, sy = match.PlayerPerks.ProtectY;
                float a = 0.16f + 0.08f * Mathf.Sin(Time.time * 4f);
                Color pc = GUI.color; GUI.color = new Color(0.35f, 0.75f, 1f, a);
                GUI.DrawTexture(new Rect(ox + sx * cs, oy + sy * cs, cs, cs), Texture2D.whiteTexture);
                GUI.color = pc;
                GUI.Label(new Rect(ox + sx * cs, oy + sy * cs, cs, cs), "🛡", mark);
            }
            bool rp = Event.current.type == EventType.Repaint;
            bool puffTick = rp && Time.time - puffTimer > 1f / Mathf.Max(1f, trailRate);
            if (puffTick) puffTimer = Time.time;
            if (rp) DrawPuffs(ox, oy); // trails render under the missiles
            if (eTorpT0 >= 0f && rp)
            {
                float ek = (Time.time - eTorpT0) / Mathf.Max(0.05f, eTorpDur);
                if (ek >= 1f) eTorpT0 = -1f;
                else if (ek >= 0f)
                {
                    Vector2 p = Vector2.Lerp(eTorpFrom, eTorpTo, ek);
                    if (p.y > -0.6f && p.y < Board.Size - 0.2f)
                    {
                        if (puffTick) puffs.Add((p - new Vector2(0f, 0.35f), Time.time)); // bubbles trail behind (above)
                        var etex = mslNorth;
                        if (etex != null)
                        {
                            float s = cs * 0.8f;
                            Vector2 c2 = new Vector2(ox + p.x * cs + cs * 0.5f, oy + p.y * cs + cs * 0.5f);
                            Color pc2 = GUI.color;
                            GUI.color = new Color(0.75f, 0.92f, 1f, torpAlpha); // same underwater ghosting
                            Matrix4x4 m2 = GUI.matrix;
                            RotateContent(180f, c2); // nose down, coming at you
                            GUI.DrawTexture(new Rect(c2.x - s * 0.5f, c2.y - s * 0.5f, s, s), etex, ScaleMode.StretchToFill);
                            GUI.matrix = m2;
                            GUI.color = pc2;
                        }
                    }
                }
            }
            if (incoming.Count > 0 && rp)
            {
                for (int i = incoming.Count - 1; i >= 0; i--)
                {
                    var m = incoming[i];
                    float k = (Time.time - m.t0) / m.dur;
                    if (k >= 1f) { incoming.RemoveAt(i); continue; }
                    if (k < 0f) continue;
                    Vector2 p = Vector2.Lerp(m.from, m.to, k);
                    if (p.y < -0.9f) continue; // still off-board above the top edge
                    if (puffTick && p.y > -0.5f) puffs.Add((p - new Vector2(0f, 0.3f), Time.time));
                    DrawExhaust(ox, oy, p - new Vector2(0f, 0.32f));
                    DrawMissile(ox, oy, p, 180f); // diving straight down at the player
                }
            }
            if (ePlaneT0 >= 0f && rp)
            {
                float pk = (Time.time - ePlaneT0) / Mathf.Max(0.1f, planeTime);
                if (pk >= 1f) ePlaneT0 = -1f;
                else if (planeTex != null)
                {
                    // enemy spotter: S-curve sweep from THEIR side (top) down over your fleet
                    float yy = Mathf.Lerp(-2.2f, Board.Size + 1.2f, pk);
                    float xx = 4.5f + Mathf.Sin(pk * Mathf.PI * 2.2f) * 3.2f;
                    float vx = Mathf.Cos(pk * Mathf.PI * 2.2f) * 3.2f * Mathf.PI * 2.2f;
                    float vy = Board.Size + 3.4f;
                    float pang = Mathf.Atan2(vx, -vy) * Mathf.Rad2Deg; // ≈180° = nose down, banking through turns
                    float ps = cs * 1.5f;
                    Vector2 pcc = new Vector2(ox + xx * cs + cs * 0.5f, oy + yy * cs + cs * 0.5f);
                    Matrix4x4 ppm = GUI.matrix;
                    RotateContent(pang, pcc);
                    GUI.DrawTexture(new Rect(pcc.x - ps * 0.5f, pcc.y - ps * 0.5f, ps, ps), planeTex, ScaleMode.StretchToFill);
                    GUI.matrix = ppm;
                }
            }
            if (lnchT0 >= 0f && rp) DrawCruiserLaunch(ox, oy);
            if (mslT0 >= 0f && rp)
            {
                float k = (Time.time - mslT0) / mslDur;
                if (k >= 1f) mslT0 = -1f;
                else if (k >= 0f) // negative = hatch anim still playing, missile not out yet
                {
                    Vector2 p = Vector2.Lerp(mslFrom, mslTo, k);
                    Vector2 dir = (mslTo - mslFrom).sqrMagnitude > 0.001f ? (mslTo - mslFrom).normalized : Vector2.up;
                    if (puffTick) puffs.Add((p - dir * 0.3f, Time.time));
                    DrawExhaust(ox, oy, p - dir * 0.32f);
                    DrawMissile(ox, oy, p, mslAngle);
                }
            }
            if (ctrPT >= 0f && !DrawBoom(new Rect(ox + ctrPX * cs, oy + ctrPY * cs, cs, cs), ctrPT) && Time.time - ctrPT > 1f) ctrPT = -1f;
        }

        // smoke trail puffs: grow and fade over ~0.55s, oldest pruned
        private void QueueShake(float when, float mag) => shakes.Add((when, mag));

        private void DrawPuffs(float ox, float oy)
        {
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                float k = (Time.time - puffs[i].t0) / Mathf.Max(0.05f, trailLife);
                if (k >= 1f) { puffs.RemoveAt(i); continue; }
                float s = cs * (trailSize + trailGrow * k);
                Color pc = GUI.color; GUI.color = new Color(1f, 1f, 1f, (1f - k) * trailAlpha);
                GUI.DrawTexture(new Rect(ox + puffs[i].g.x * cs + cs * 0.5f - s * 0.5f, oy + puffs[i].g.y * cs + cs * 0.5f - s * 0.5f, s, s), puffTex);
                GUI.color = pc;
            }
            if (puffs.Count > 240) puffs.RemoveRange(0, puffs.Count - 240);
        }

        // rotation composed in CONTENT space (right-multiplied) so it stays correct under the
        // fleet-glass stretch matrix — GUIUtility.RotateAroundPivot pivots in SCREEN space and
        // tears rotated sprites away from their (unrotated) trails when GUI.matrix is scaled
        private static void RotateContent(float angle, Vector2 pivot)
        {
            GUI.matrix = GUI.matrix
                * Matrix4x4.TRS(new Vector3(pivot.x, pivot.y, 0f), Quaternion.Euler(0f, 0f, angle), Vector3.one)
                * Matrix4x4.TRS(new Vector3(-pivot.x, -pivot.y, 0f), Quaternion.identity, Vector3.one);
        }

        // ONE north-facing sprite rotated to the exact heading — any angle, no 8-direction snapping
        private void DrawMissile(float ox, float oy, Vector2 g, float angle)
        {
            var tex = mslNorth;
            if (tex == null) return;
            float s = cs * 0.8f;
            Vector2 c = new Vector2(ox + g.x * cs + cs * 0.5f, oy + g.y * cs + cs * 0.5f);
            Matrix4x4 m = GUI.matrix;
            RotateContent(angle, c);
            GUI.DrawTexture(new Rect(c.x - s * 0.5f, c.y - s * 0.5f, s, s), tex, ScaleMode.StretchToFill);
            GUI.matrix = m;
        }

        // flickering orange exhaust glow at the missile's tail
        private void DrawExhaust(float ox, float oy, Vector2 g)
        {
            if (puffTex == null) return;
            float fl = 0.55f + 0.45f * Mathf.PerlinNoise(Time.time * 28f, 0.37f);
            float s = cs * 0.26f;
            Color pc = GUI.color; GUI.color = new Color(1f, 0.62f, 0.15f, 0.75f * fl);
            GUI.DrawTexture(new Rect(ox + g.x * cs + cs * 0.5f - s * 0.5f, oy + g.y * cs + cs * 0.5f - s * 0.5f, s, s), puffTex);
            GUI.color = pc;
        }

        // silo-launch anim on the cruiser's VLS cell, aligned with its bob/sway (rotated when vertical)
        private void DrawCruiserLaunch(float ox, float oy)
        {
            if (lnchFrames == null || lnchFrames.Length == 0) { lnchT0 = -1f; return; }
            int f = Mathf.FloorToInt((Time.time - lnchT0) * lnchFps);
            if (f < 0) return;
            if (f >= lnchFrames.Length) { lnchT0 = -1f; return; }
            int ci = FleetIndex(ShipType.Cruiser);
            if (ci < 0 || !fleet[ci].placed) { lnchT0 = -1f; return; }
            var fs = fleet[ci];
            int cx = fs.x;                                              // silo chunk (k=0, sprite-left since the bow-right flip)
            int cy = fs.o == Orientation.Horizontal ? fs.y : fs.y + 2;  // vertical ships face up — k=0 is the bottom cell
            float ph = fs.x * 12.9f + fs.y * 7.3f;
            float bob = Mathf.Sin(Time.time * bobSpeed + ph) * (cs * bobAmount);
            float sway = Mathf.Cos(Time.time * bobSpeed * 0.8f + ph) * (cs * bobAmount * 0.35f);
            float px = ox + cx * cs + sway, py = oy + cy * cs + bob;
            float hs2 = cs * ShipScale(ShipType.Cruiser); // hatch anim repaints the hull chunk — match its scale
            if (fs.o == Orientation.Horizontal)
                GUI.DrawTexture(new Rect(px, py + (cs - hs2) * 0.5f, cs, hs2), lnchFrames[f], ScaleMode.StretchToFill);
            else
            {
                Vector2 center = new Vector2(px + cs * 0.5f, py + cs * 0.5f);
                Matrix4x4 mtx = GUI.matrix;
                RotateContent(-90f, center);
                GUI.DrawTexture(new Rect(center.x - cs * 0.5f, center.y - hs2 * 0.5f, cs, hs2), lnchFrames[f], ScaleMode.StretchToFill);
                GUI.matrix = mtx;
            }
        }

        private Rect SlotRect(Rect f) => new Rect(f.x * Screen.width, f.y * Screen.height, f.width * Screen.width, f.height * Screen.height);

        // LEGEND panel in the console's top-right slot: our marker icons + labels, like the reference
        private void DrawLegendPanel()
        {
            if (lgTitle == null)
            {
                lgTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                lgTitle.normal.textColor = new Color(0.32f, 0.78f, 0.42f, 0.95f);
                lgName = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
                lgName.normal.textColor = new Color(0.82f, 0.93f, 0.85f, 0.95f);
                lgDesc = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                lgDesc.normal.textColor = new Color(0.38f, 0.72f, 0.45f, 0.9f);
                if (uiFont != null) { lgTitle.font = uiFont; lgName.font = uiFont; lgDesc.font = uiFont; }
            }
            var r = SlotRect(Slot1F);
            float pad = r.width * 0.07f;
            lgTitle.fontSize = Mathf.Max(9, Mathf.RoundToInt(r.width * 0.055f));
            lgName.fontSize = Mathf.Max(8, Mathf.RoundToInt(r.width * 0.045f));
            lgDesc.fontSize = Mathf.Max(7, Mathf.RoundToInt(r.width * 0.036f));
            GUI.Label(new Rect(r.x, r.y + r.height * 0.012f, r.width, r.height * 0.075f), T("* LEGEND *"), lgTitle);

            var entries = new (Texture2D tex, string name, string desc)[]
            {
                (markHit, T("HIT"), T("Ship hit")),
                (markDestroyed, T("DESTROYED"), T("Ship sunk")),
                (markMiss, T("MISS"), T("Nothing here")),
                (markRecon, T("RECON"), T("Enemy ship detected"))
            };
            float top = r.y + r.height * 0.105f;
            float entryH = (r.height * 0.87f) / entries.Length; // equal share for every entry
            Color pcL = GUI.color;
            for (int i = 0; i < entries.Length; i++)
            {
                float ey = Mathf.Round(top + i * entryH);
                if (i > 0) // grey separator between entries — thick enough to survive small game views
                {
                    float sepH = Mathf.Max(2f, Screen.height * 0.0022f);
                    // the shell art has a baked divider under separator #1 — draw ours lighter there so they match
                    GUI.color = new Color(0.62f, 0.68f, 0.72f, i == 1 ? 0.33f : 0.38f);
                    GUI.DrawTexture(new Rect(Mathf.Round(r.x + pad), ey, Mathf.Round(r.width - 2f * pad), sepH), Texture2D.whiteTexture);
                    GUI.color = pcL;
                }
                float icon = Mathf.Min(entryH * 0.38f, r.width * 0.16f);
                float l1y = ey + entryH * 0.16f;
                // left-aligned like the reference: icon at the left, texts beside/below it
                if (entries[i].tex != null)
                    GUI.DrawTexture(new Rect(r.x + pad, l1y, icon, icon), entries[i].tex, ScaleMode.StretchToFill);
                float tx = r.x + pad + icon + pad * 0.6f;
                float tw = r.xMax - pad - tx;
                GUI.Label(new Rect(tx, l1y, tw, icon), entries[i].name, lgName);
                DrawSpacedLabel(new Rect(tx, l1y + icon + entryH * 0.06f, tw, entryH * 0.45f), entries[i].desc, lgDesc, 0.2f);
            }
        }

        // STATUS panel in the middle-right slot: live battle stats (shots / hits / sunk), like the reference
        private void DrawStatusPanel()
        {
            if (lgTitle == null) // shared typography (was born in the legend panel, which no longer draws)
            {
                lgTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                lgTitle.normal.textColor = new Color(0.32f, 0.78f, 0.42f, 0.95f);
                lgName = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
                lgName.normal.textColor = new Color(0.82f, 0.93f, 0.85f, 0.95f);
                lgDesc = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                lgDesc.normal.textColor = new Color(0.38f, 0.72f, 0.45f, 0.9f);
                if (uiFont != null) { lgTitle.font = uiFont; lgName.font = uiFont; lgDesc.font = uiFont; }
            }
            if (stVal == null)
            {
                stVal = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
                stVal.normal.textColor = new Color(0.42f, 0.95f, 0.52f, 1f); // numbers glow green
                if (uiFont != null) stVal.font = uiFont;
            }
            var r = SlotRect(Slot2F);
            float pad = r.width * 0.07f;
            lgTitle.fontSize = Mathf.Max(9, Mathf.RoundToInt(r.width * 0.055f));
            GUI.Label(new Rect(r.x, r.y + r.height * 0.03f, r.width, r.height * 0.17f), "* STATS *", lgTitle);

            int shots = 0, hits = 0;
            for (int y = 0; y < Board.Size; y++)
                for (int x = 0; x < Board.Size; x++)
                    if (match.EnemyBoard.IsShot(x, y)) { shots++; if (match.EnemyBoard.HasShip(x, y)) hits++; }
            int sunk = 5 - match.EnemyBoard.ShipsRemaining;

            var rows = new (string label, string val)[]
            {
                (T("SHOTS FIRED"), shots.ToString()),
                (T("HITS"), hits.ToString()),
                (T("SHIPS SUNK"), sunk + "/5")
            };
            lgName.fontSize = Mathf.Max(8, Mathf.RoundToInt(r.width * 0.04f));
            stVal.fontSize = Mathf.Max(9, Mathf.RoundToInt(r.width * 0.048f));
            float top = r.y + r.height * 0.24f;
            float rowH = (r.height * 0.72f) / rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                float ry = top + i * rowH;
                GUI.Label(new Rect(r.x + pad, ry, r.width * 0.62f, rowH), rows[i].label, lgName); // labels pale
                GUI.Label(new Rect(r.x + pad, ry, r.width - 2f * pad, rowH), rows[i].val, stVal); // numbers green
            }
        }

        // perk cards in the tall right recess (both views): icon tile + title + blurb + LED,
        // whole card clickable. States: ready (lit LED), armed (amber, click cancels), CD n, OFFLINE.
        private void DrawPerkCards()
        {
            if (pkName == null)
            {
                pkName = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft };
                pkDesc = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                pkDesc.normal.textColor = new Color(0.55f, 0.64f, 0.58f, 0.88f);
                pkStat = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight };
                if (uiFont != null) { pkName.font = uiFont; pkDesc.font = uiFont; pkStat.font = uiFont; }
            }
            var r = SlotRect(Slot1F);
            float gap = r.height * 0.014f;
            float cardH = (r.height - 4f * gap) / 5f;
            bool playing = phase == Phase.Playing && match != null && !match.Over;
            float fl = LampFlicker();

            var cards = new (ShipType t, string name, string desc, Color col)[]
            {
                (ShipType.Carrier,    T("SCOUT"),     T("LAUNCH A PLANE SCOUT."),   new Color(0.55f, 0.85f, 0.40f)),
                (ShipType.Cruiser,    T("INTERCEPT"), T("INTERCEPT ENEMY MISSILE."), new Color(0.40f, 0.66f, 1.00f)),
                (ShipType.Battleship, T("SCORCH"),    T("MULTIPLE FIRING MISSILE."), new Color(1.00f, 0.62f, 0.18f)),
                (ShipType.Submarine,  T("TORPEDO"),   T("LAUNCH A TORPEDO."),        new Color(0.25f, 0.85f, 0.80f)),
                (ShipType.Destroyer,  T("RELOCATE"),  T("MOVE A SHIP TO A NEW POSITION."), new Color(0.74f, 0.52f, 1.00f)),
            };

            for (int i = 0; i < cards.Length; i++)
            {
                var (t, name, desc, col) = cards[i];
                var cr = new Rect(r.x, r.y + i * (cardH + gap), r.width, cardH);
                bool disabled = Perks.IsDisabled(match.PlayerBoard, t);
                int cd = match.PlayerPerks.CooldownLeft(t);
                bool armed = (t == ShipType.Cruiser && perkMode == PerkMode.Intercept) ||
                             (t == ShipType.Submarine && perkMode == PerkMode.Torpedo) ||
                             (t == ShipType.Destroyer && perkMode == PerkMode.Relocate) ||
                             (t == ShipType.Battleship && match.ScorchArmed);
                bool ready = !disabled && cd <= 0 && playing && match.PlayerTurn && !InputLocked;
                string blurb = !armed ? desc :
                    t == ShipType.Cruiser ? T("PICK A CELL TO SHIELD.") :
                    t == ShipType.Submarine ? T("PICK A LANE.") :
                    t == ShipType.Destroyer ? T("PICK A SPOT.") : T("ARMED — NEXT SALVO x2.");
                string stat = disabled ? T("OFFLINE") : cd > 0 ? "CD " + cd : armed ? T("ARMED") : T("READY");

                if (Event.current.type == EventType.Repaint)
                {
                    float rad = cardH * 0.1f;
                    // plate + perk-tinted edge (brighter while armed)
                    GUI.DrawTexture(cr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    new Color(0.035f, 0.05f, 0.045f, 0.88f), 0f, rad);
                    Color edge = col * (armed ? 1.1f : 0.75f); edge.a = armed ? 1f : 0.85f;
                    GUI.DrawTexture(cr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    edge, Mathf.Max(2f, cardH * 0.03f), rad);

                    // icon tile
                    float pad = cardH * 0.12f;
                    float tile = cardH - 2f * pad;
                    var tr = new Rect(cr.x + pad, cr.y + pad, tile, tile);
                    Color tbg = col * 0.20f; tbg.a = 0.9f;
                    GUI.DrawTexture(tr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, tbg, 0f, rad * 0.7f);
                    Color tedge = col * 0.6f; tedge.a = 0.8f;
                    GUI.DrawTexture(tr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, tedge, Mathf.Max(1f, cardH * 0.018f), rad * 0.7f);
                    if (ready && puffTex != null) // live perks throw a soft tinted glow around their tile
                    {
                        Color pcP = GUI.color;
                        GUI.color = new Color(col.r, col.g, col.b, 0.14f * fl);
                        float gs = tile * 1.7f;
                        GUI.DrawTexture(new Rect(tr.center.x - gs * 0.5f, tr.center.y - gs * 0.5f, gs, gs), puffTex);
                        GUI.color = pcP;
                    }
                    if (perkIconTex != null && perkIconTex[i] != null)
                    {
                        float ins = tile * 0.08f;
                        GUI.DrawTexture(new Rect(tr.x + ins, tr.y + ins, tile - 2f * ins, tile - 2f * ins),
                                        perkIconTex[i], ScaleMode.ScaleToFit);
                    }

                    // LED state: green = charged, amber = armed, RED = knocked offline, dark = cooldown
                    float led = cardH * 0.11f;
                    var lr = new Rect(cr.xMax - pad - led * 1.4f, cr.y + pad * 0.9f, led * 1.4f, led);
                    bool charged = !disabled && cd <= 0; // LED tracks perk health, not whose turn it is
                    bool litLed = armed || charged || disabled;
                    Color lb = armed ? new Color(1f, 0.72f, 0.2f) :
                               disabled ? new Color(0.95f, 0.20f, 0.14f) :
                               charged ? new Color(0.35f, 0.95f, 0.45f) : new Color(0.09f, 0.13f, 0.10f);
                    Color lc = litLed ? lb * (0.55f + 0.45f * fl) : lb; // flicker-dimmed tint for the state text

                    // texts
                    float txx = tr.xMax + pad;
                    float txw = lr.x - txx - pad * 0.5f;
                    pkName.fontSize = Mathf.Max(9, Mathf.RoundToInt(r.width * 0.055f));
                    pkDesc.fontSize = Mathf.Max(7, Mathf.RoundToInt(r.width * 0.036f));
                    pkStat.fontSize = Mathf.Max(7, Mathf.RoundToInt(r.width * 0.034f));
                    pkName.normal.textColor = disabled ? new Color(0.45f, 0.42f, 0.4f, 0.8f) : col;
                    GUI.Label(new Rect(txx, cr.y + pad * 0.9f, txw + pad * 0.5f, cardH * 0.3f), name, pkName);
                    DrawSpacedLabel(new Rect(txx, cr.y + pad * 0.9f + cardH * 0.28f, cr.xMax - pad - txx, cardH * 0.55f), blurb, pkDesc, 0.22f);
                    pkStat.normal.textColor = disabled ? new Color(0.8f, 0.3f, 0.25f, 0.85f) :
                                              cd > 0 ? new Color(0.55f, 0.6f, 0.55f, 0.8f) : lc;
                    GUI.Label(new Rect(cr.x, cr.y, cr.width - pad, cr.height - pad * 0.6f), stat, pkStat);

                    if (!ready && !armed) // dead cards recede into the panel
                        GUI.DrawTexture(cr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                        new Color(0.01f, 0.02f, 0.02f, 0.42f), 0f, rad);
                    else if (cr.Contains(Event.current.mousePosition))
                        GUI.DrawTexture(cr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                        new Color(col.r, col.g, col.b, 0.07f), 0f, rad);

                    // LED AFTER the overlay so a lit lamp (green, amber or red) punches through the dimmed card.
                    // Bottom-STATUS lamp recipe, but louder: double-layer bleed + per-card flicker phase.
                    Color pcL2 = GUI.color;
                    if (litLed)
                    {
                        float fl2 = 0.55f + 0.45f * Mathf.PerlinNoise(Time.time * 8f + i * 5.3f, 0.37f); // each lamp breathes on its own
                        if (puffTex != null)
                        {
                            GUI.color = new Color(lb.r, lb.g, lb.b, 0.30f * fl2); // wide halo
                            float gs = lr.width * 5.5f;
                            GUI.DrawTexture(new Rect(lr.center.x - gs * 0.5f, lr.center.y - gs * 0.5f, gs, gs), puffTex);
                            GUI.color = new Color(lb.r, lb.g, lb.b, 0.6f * fl2);  // hot core bleed
                            gs = lr.width * 2.6f;
                            GUI.DrawTexture(new Rect(lr.center.x - gs * 0.5f, lr.center.y - gs * 0.5f, gs, gs), puffTex);
                        }
                        GUI.color = new Color(lb.r, lb.g, lb.b, 0.7f + 0.3f * fl2);
                        GUI.DrawTexture(lr, Texture2D.whiteTexture);
                        GUI.color = new Color(Mathf.Lerp(lb.r, 1f, 0.45f), Mathf.Lerp(lb.g, 1f, 0.45f), Mathf.Lerp(lb.b, 1f, 0.45f), 0.85f * fl2);
                        GUI.DrawTexture(new Rect(lr.x + lr.width * 0.18f, lr.y + lr.height * 0.18f, lr.width * 0.64f, lr.height * 0.38f), Texture2D.whiteTexture); // glassy highlight
                    }
                    else
                    {
                        GUI.color = new Color(0.16f, 0.17f, 0.16f, 0.95f); // dead LED, muted gray
                        GUI.DrawTexture(lr, Texture2D.whiteTexture);
                        GUI.color = new Color(0f, 0f, 0f, 0.35f);
                        GUI.DrawTexture(new Rect(lr.x, lr.y, lr.width, 2f), Texture2D.whiteTexture); // sunken top edge
                    }
                    GUI.color = pcL2;
                }

                if ((ready || armed) && GUI.Button(cr, GUIContent.none, GUIStyle.none))
                {
                    Sfx(clipClick, 0.7f);
                    switch (t)
                    {
                        case ShipType.Carrier:
                            targeting = true; DoRadar(); break; // reveal happens on the scope — make sure we're looking at it
                        case ShipType.Cruiser:
                            perkMode = perkMode == PerkMode.Intercept ? PerkMode.None : PerkMode.Intercept; targeting = false; break;
                        case ShipType.Battleship:
                            match.ArmScorch(!match.ScorchArmed); break;
                        case ShipType.Submarine:
                            perkMode = perkMode == PerkMode.Torpedo ? PerkMode.None : PerkMode.Torpedo; targeting = perkMode == PerkMode.Torpedo; break;
                        case ShipType.Destroyer:
                            perkMode = perkMode == PerkMode.Relocate ? PerkMode.None : PerkMode.Relocate; targeting = false;
                            if (perkMode == PerkMode.Relocate) { orient = fleet[FleetIndex(ShipType.Destroyer)].o; ComputeSailReach(); }
                            break;
                    }
                }
            }
        }

        // view-switch button on the baked amber plate: on the radar it returns to the fleet,
        // on the fleet it opens the targeting scope. We only stamp arrow + label + hover.
        private void DrawFleetButton()
        {
            var r = SlotRect(Slot3F);
            bool toFleet = targeting;
            bool avail = toFleet || !InputLocked; // going TO the radar is blocked while enemy visuals play
            bool hover = r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Color ink = new Color(0.13f, 0.09f, 0.02f, 1f); // near-black brown stamp — must read on the baked amber plate
                if (fleetBtnLbl == null)
                {
                    fleetBtnLbl = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                    if (uiFont != null) fleetBtnLbl.font = uiFont;
                }
                fleetBtnLbl.normal.textColor = ink;
                fleetBtnLbl.fontSize = Mathf.Max(8, Mathf.RoundToInt(r.height * 0.17f));
                DrawSpacedLabel(r, toFleet ? "FLEET\nVIEW" : "RADAR\nVIEW", fleetBtnLbl, 0.25f);

                if (!avail)
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    new Color(0.02f, 0.02f, 0.03f, 0.55f), 0f, r.height * 0.12f);
                else if (hover) // subtle glow so the baked plate feels live
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    new Color(1f, 0.9f, 0.5f, 0.10f), 0f, r.height * 0.12f);
            }
            if (avail && GUI.Button(r, GUIContent.none, GUIStyle.none)) { targeting = !toFleet; Sfx(clipFlip, 0.8f); }
        }

        // classic 7-segment LCD digit built from rects; d < 0 draws all segments in the "off" ghost color
        private static readonly byte[] SegMap = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
        private void DrawSevenSeg(Rect r, int d, Color on, Color off)
        {
            byte m2 = d < 0 ? (byte)0 : SegMap[d];
            float t = Mathf.Max(2f, r.height * 0.13f);
            float x = r.x, y = r.y, w = r.width, h = r.height, hh = h * 0.5f;
            void Seg(int bit, Rect sr)
            {
                GUI.color = ((m2 >> bit) & 1) == 1 ? on : off;
                GUI.DrawTexture(sr, Texture2D.whiteTexture);
            }
            Seg(0, new Rect(x + t, y, w - 2f * t, t));                  // top
            Seg(1, new Rect(x + w - t, y + t * 0.6f, t, hh - t));       // top-right
            Seg(2, new Rect(x + w - t, y + hh + t * 0.4f, t, hh - t));  // bottom-right
            Seg(3, new Rect(x + t, y + h - t, w - 2f * t, t));          // bottom
            Seg(4, new Rect(x, y + hh + t * 0.4f, t, hh - t));          // bottom-left
            Seg(5, new Rect(x, y + t * 0.6f, t, hh - t));               // top-left
            Seg(6, new Rect(x + t, y + hh - t * 0.5f, w - 2f * t, t));  // middle
        }

        // auto-end countdown in the little LCD slot: lights up while the enemy turn is pending,
        // dead ghost segments the rest of the time. "AUTO-END" sits on the tab below.
        private void DrawTurnTimer()
        {
            if (Event.current.type != EventType.Repaint) return;
            var s = SlotRect(TimerF);
            bool playing = phase == Phase.Playing && match != null && !match.Over;
            bool myTurn = playing && match.PlayerTurn;
            bool live = playing;
            float remain = myTurn ? (turnDeadline > 0f ? turnDeadline - Time.time : turnTime)
                                  : playing ? aiTimer : 0f;
            int total = Mathf.CeilToInt(Mathf.Max(0f, remain));
            int mm = total / 60, ss = total % 60;
            Color pc = GUI.color;
            float fl = LampFlicker(); // same power rail as the fleet LEDs
            float onA = live ? (0.8f + 0.2f * fl) : 0.0f;
            if (myTurn && remain < 10f) onA *= (Mathf.FloorToInt(Time.time * 3f) & 1) == 0 ? 1f : 0.3f; // urgency blink
            Color on = new Color(0.28f, 0.78f, 0.4f, onA); // muted LCD green, not neon
            Color off = new Color(0.3f, 0.95f, 0.45f, 0.09f); // unlit LCD ghosts
            float dh = s.height * 0.60f, dw = dh * 0.58f, gap = dw * 0.24f, cw = dw * 0.34f;
            float totalW = 4f * dw + 4f * gap + cw;
            float x0 = s.x + (s.width - totalW) * 0.5f, y0 = s.y + (s.height - dh) * 0.5f + s.height * 0.07f;
            void DigitGlow(float gx, float gw) // compact halo per glyph — same look as the fleet LEDs
            {
                if (!live) return;
                float gs = dh * 2.1f;
                Color gc = GUI.color;
                GUI.color = new Color(on.r, on.g, on.b, 0.55f * fl * (onA > 0.3f ? 1f : 0.35f));
                GUI.DrawTexture(new Rect(gx + gw * 0.5f - gs * 0.5f, y0 + dh * 0.5f - gs * 0.5f, gs, gs), puffTex);
                GUI.color = gc;
            }
            DigitGlow(x0, dw); DrawSevenSeg(new Rect(x0, y0, dw, dh), live ? mm / 10 : -1, on, off); x0 += dw + gap;
            DigitGlow(x0, dw); DrawSevenSeg(new Rect(x0, y0, dw, dh), live ? mm % 10 : -1, on, off); x0 += dw + gap;
            bool colonOn = live && (Mathf.FloorToInt(Time.time * 2f) & 1) == 0; // blinks with the seconds
            if (colonOn) DigitGlow(x0, cw);
            GUI.color = colonOn ? on : off;
            float dot = Mathf.Max(2f, dh * 0.13f);
            GUI.DrawTexture(new Rect(x0 + (cw - dot) * 0.5f, y0 + dh * 0.27f, dot, dot), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x0 + (cw - dot) * 0.5f, y0 + dh * 0.66f, dot, dot), Texture2D.whiteTexture);
            x0 += cw + gap;
            DigitGlow(x0, dw); DrawSevenSeg(new Rect(x0, y0, dw, dh), live ? ss / 10 : -1, on, off); x0 += dw + gap;
            DigitGlow(x0, dw); DrawSevenSeg(new Rect(x0, y0, dw, dh), live ? ss % 10 : -1, on, off);
            GUI.color = pc;
            var tab = SlotRect(TimerTabF);
            if (radarTitle != null)
            {
                radarTitle.fontSize = Mathf.Max(7, Mathf.RoundToInt(tab.height * 0.62f));
                GUI.Label(tab, T("TURN-END"), radarTitle);
            }
        }

        // ends whatever is yours to end: skips your own turn (voluntarily or on timeout),
        // or confirms the pending enemy turn instead of waiting out the countdown
        private void SkipTurn(bool timeout)
        {
            if (online)
            {
                if (!match.PlayerTurn || match.Over || awaitingResult || InputLocked) return;
                net.SendMoveIntent(NetCodec.EncodePass());   // tell the opponent it's their turn now
                match.EndMyOnlineTurn();
                turnDeadline = -1f; perkMode = PerkMode.None; prevPlayerTurn = false;
                targeting = false; actionEndT = Time.time;
                Sfx(clipClick, 0.7f);
                message = T("TURN PASSED");
                return;
            }
            match.PlayerPass();
            turnDeadline = -1f; perkMode = PerkMode.None; prevPlayerTurn = false;
            targeting = false;          // enemy turn ALWAYS plays out on your fleet view
            actionEndT = Time.time;
            if (timeout)
            {
                message = "⏰ TIME UP — TURN FORFEITED.";
                Sfx(clipBeep, 0.9f);
                aiTimer = 1.2f;         // one breath to read the message, then they fire
            }
            else
            {
                Sfx(clipClick, 0.7f);
                aiTimer = 0.01f;        // you ended your turn — the enemy moves NOW
            }
        }

        // END TURN on the bottom-right amber plate: on YOUR turn it skips it,
        // on a pending enemy turn it confirms instead of waiting out the countdown
        private void DrawEndTurnButton()
        {
            var r = SlotRect(Slot4F);
            bool playing = phase == Phase.Playing && match != null && !match.Over;
            bool myTurn = playing && match.PlayerTurn && !InputLocked && !awaitingResult;
            // online has no AI countdown to confirm, so END TURN is only ever a pass on my own turn
            bool avail = myTurn || (playing && !online && !match.PlayerTurn && Time.time >= actionEndT);
            bool hover = r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Color ink = new Color(0.13f, 0.09f, 0.02f, 1f);
                if (fleetBtnLbl == null)
                {
                    fleetBtnLbl = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                    if (uiFont != null) fleetBtnLbl.font = uiFont;
                }
                fleetBtnLbl.normal.textColor = ink;
                fleetBtnLbl.fontSize = Mathf.Max(8, Mathf.RoundToInt(r.height * 0.20f));
                GUI.Label(r, T("END TURN"), fleetBtnLbl);

                if (!avail) // plate goes dark until there is a turn to end
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    new Color(0.02f, 0.02f, 0.03f, 0.55f), 0f, r.height * 0.14f);
                else if (hover)
                    GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                                    new Color(1f, 0.9f, 0.5f, 0.10f), 0f, r.height * 0.14f);
            }
            if (avail && GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                if (myTurn) SkipTurn(false);
                else { aiTimer = 0.01f; targeting = false; Sfx(clipClick, 0.7f); } // straight to your fleet — the enemy is firing
            }
        }

        // bottom-center strip: live combat commentary on the console (hits, sunk ships,
        // intercepts, recon contacts...). Mirrors the game `message`, scrubbed for the
        // pixel font (PS2P has no emoji) and set in console caps.
        private void DrawMessageStrip()
        {
            var s = SlotRect(MsgStripF);
            if (msgStyle == null)
            {
                msgStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                if (uiFont != null) msgStyle.font = uiFont;
            }
            string src = T(message);
            var sb = new System.Text.StringBuilder(src.Length);
            foreach (char ch in src)
            {
                if (ch == '—' || ch == '–') sb.Append('-');       // em/en dash → hyphen
                else if (ch == '…') sb.Append("...");                  // ellipsis
                else if (ch >= ' ' && ch < 127) sb.Append(char.ToUpperInvariant(ch));
            }
            string txt = sb.ToString().Trim();
            if (txt.Length == 0) return;
            float fl = LampFlicker();
            msgStyle.normal.textColor = new Color(0.34f, 0.82f, 0.45f, 0.75f + 0.25f * fl);
            msgStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(s.height * (txt.Length > 46 ? 0.20f : 0.26f)));
            DrawSpacedLabel(new Rect(s.x + s.width * 0.03f, s.y, s.width * 0.94f, s.height), txt, msgStyle, 0.22f);
        }

        // multi-line label with extra breathing room between lines — IMGUI has no leading
        // control (PS2P sets its lines tight), so wrap and lay the lines out by hand.
        // Handles explicit \n and word-wrap; vertical anchor follows the style's alignment.
        private void DrawSpacedLabel(Rect r, string text, GUIStyle st, float extraMult = 0.45f)
        {
            var lines = new System.Collections.Generic.List<string>();
            foreach (var para in text.Split('\n'))
            {
                if (!st.wordWrap) { lines.Add(para); continue; }
                string cur = "";
                foreach (var w in para.Split(' '))
                {
                    string probe = cur.Length == 0 ? w : cur + " " + w;
                    if (cur.Length == 0 || st.CalcSize(new GUIContent(probe)).x <= r.width) cur = probe;
                    else { lines.Add(cur); cur = w; }
                }
                lines.Add(cur);
            }
            float lh = st.CalcSize(new GUIContent("X")).y;
            float gap = lh * extraMult;
            float total = lines.Count * lh + (lines.Count - 1) * gap;
            var a = st.alignment;
            float y = a == TextAnchor.MiddleLeft || a == TextAnchor.MiddleCenter || a == TextAnchor.MiddleRight
                    ? r.y + (r.height - total) * 0.5f
                    : a == TextAnchor.LowerLeft || a == TextAnchor.LowerCenter || a == TextAnchor.LowerRight
                    ? r.yMax - total : r.y;
            bool prevWrap = st.wordWrap; st.wordWrap = false;
            foreach (var ln in lines)
            {
                GUI.Label(new Rect(r.x, y, r.width, lh), ln, st);
                y += lh + gap;
            }
            st.wordWrap = prevWrap;
        }

        // shared "power rail" for the console lamps: a fast subtle shimmer riding on a SLOW
        // wandering dip, like a real light struggling on old wiring. LEDs and LCD share it.
        private static float LampFlicker()
        {
            float fast = 0.82f + 0.18f * Mathf.PerlinNoise(Time.time * 9f, 0.5f);
            float slow = 0.68f + 0.32f * Mathf.PerlinNoise(Time.time * 0.9f, 7.3f);
            return fast * slow;
        }

        // bottom-left STATUS panel: five LEDs, one per ship of YOUR fleet. Alive = lit, sunk = dead gray.
        // The lit color tracks fleet health: green at strength, amber when worn down, red on the last ship.
        private void DrawFleetLeds()
        {
            var s = SlotRect(FleetLedsF);
            int rem = match != null ? match.PlayerBoard.ShipsRemaining : 5;
            Color lit = rem >= 4 ? new Color(0.32f, 1f, 0.45f) :
                        rem >= 2 ? new Color(1f, 0.72f, 0.2f) :
                                   new Color(1f, 0.22f, 0.12f);
            float fl = LampFlicker();
            if (rem <= 1) fl *= 0.62f + 0.38f * Mathf.Sin(Time.time * 4.5f);    // last ship: the LED pulses in alarm

            if (radarTitle != null)
            {
                radarTitle.fontSize = Mathf.Max(8, Mathf.RoundToInt(s.height * 0.17f));
                GUI.Label(new Rect(s.x, s.y + s.height * 0.12f, s.width, s.height * 0.32f), T("STATUS"), radarTitle);
            }

            float lw = s.width * 0.125f, lh = s.height * 0.26f, gap = lw * 0.28f;
            float x0 = s.x + (s.width - (5f * lw + 4f * gap)) * 0.5f;
            float ly = s.y + s.height * 0.47f;
            Color pc = GUI.color;
            for (int i = 0; i < 5; i++)
            {
                var lr = new Rect(x0 + i * (lw + gap), ly, lw, lh);
                bool alive = i < rem; // a fuel-gauge readout: losses always drain from the RIGHT
                if (alive)
                {
                    GUI.color = new Color(lit.r, lit.g, lit.b, 0.4f * fl); // LED light bleed
                    GUI.DrawTexture(new Rect(lr.center.x - lw * 1.3f, lr.center.y - lw * 1.3f, lw * 2.6f, lw * 2.6f), puffTex);
                    GUI.color = new Color(lit.r, lit.g, lit.b, 0.75f + 0.25f * fl);
                    GUI.DrawTexture(lr, Texture2D.whiteTexture);
                    GUI.color = new Color(Mathf.Lerp(lit.r, 1f, 0.45f), Mathf.Lerp(lit.g, 1f, 0.45f), Mathf.Lerp(lit.b, 1f, 0.45f), 0.85f * fl);
                    GUI.DrawTexture(new Rect(lr.x + lw * 0.18f, lr.y + lh * 0.18f, lw * 0.64f, lh * 0.38f), Texture2D.whiteTexture); // glassy highlight
                }
                else
                {
                    GUI.color = new Color(0.16f, 0.17f, 0.16f, 0.95f); // dead LED, muted gray
                    GUI.DrawTexture(lr, Texture2D.whiteTexture);
                    GUI.color = new Color(0f, 0f, 0f, 0.35f);
                    GUI.DrawTexture(new Rect(lr.x, lr.y, lr.width, 2f), Texture2D.whiteTexture); // sunken top edge
                }
            }
            GUI.color = pc;
        }

        private void DoRadar()
        {
            if (OnlineActionBlocked()) return;
            var c = match.PlayerRadar();
            if (c == null) { message = "NO NEW CONTACTS."; return; }
            pendX = c.Value.x; pendY = c.Value.y;        // blip revealed when the plane exits the top
            pendReveal = Time.time + planeTime;
            planeT0 = Time.time;
            planeSfxEndT = Time.time + (clipPlane != null ? clipPlane.length : planeTime);
            actionEndT = planeSfxEndT + 1.6f;            // engine dies, THEN the contact pings play out
            targeting = true; aiTimer = (actionEndT - Time.time) + autoEndTurn;
            message = "📡 RECON PLANE LAUNCHED...";
            Sfx(clipPlane, 0.7f); // engine quieter so the detection ping cuts through
        }

        private float PerkButton(float px, float y, float pw, ShipType t, string name, System.Action act)
        {
            float bh = btn.fontSize + 12f;
            bool disabled = Perks.IsDisabled(match.PlayerBoard, t);
            int cd = match.PlayerPerks.CooldownLeft(t);
            bool ready = !disabled && cd <= 0 && match.PlayerTurn && phase == Phase.Playing && !match.Over && !InputLocked && !awaitingResult;
            string label = disabled ? name + "  ✖ OFFLINE" : cd > 0 ? name + "  (" + cd + ")" : name;
            GUI.enabled = ready;
            if (GUI.Button(new Rect(px, y, pw, bh), label, btn) && act != null) { Sfx(clipClick, 0.7f); act(); }
            GUI.enabled = true;
            return y + bh + 6f;
        }

        private void HandleDebugHit(float ox, float oy, Event e, Vector2 mp)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
            int cx = Mathf.FloorToInt((mp.x - ox) / cs), cy = Mathf.FloorToInt((mp.y - oy) / cs);
            if (cx < 0 || cx >= Board.Size || cy < 0 || cy >= Board.Size) return;
            if (match.PlayerPerks.HasProtection && match.PlayerPerks.ProtectX == cx && match.PlayerPerks.ProtectY == cy)
            {
                match.DebugAiShoot(cx, cy); // REAL enemy shot at the shielded cell — full intercept pipeline
                aiTimer = 0.6f;
                message = "DEBUG: enemy shell inbound on your protected cell " + Coord(cx, cy) + "...";
                e.Use(); return;
            }
            if (CellHasDmgAnim(cx, cy)) { hitT[cx, cy] = Time.time; message = "DEBUG: hit " + Coord(cx, cy); e.Use(); }
        }


        private void DrawShipDamage(float ox, float oy)
        {
            if (Event.current.type != EventType.Repaint || dmgFrames == null || hitT == null) return;
            for (int i = 0; i < fleet.Length; i++)
            {
                if (!fleet[i].placed || i == dragIdx) continue;
                var f = fleet[i];
                if (relocT0 >= 0f && f.type == ShipType.Destroyer) continue; // damage rides along after the sail
                var cells = dmgFrames[(int)f.type];
                if (cells == null) continue;
                float ph = f.x * 12.9f + f.y * 7.3f;
                float bob = Mathf.Sin(Time.time * bobSpeed + ph) * (cs * bobAmount);
                float sway = Mathf.Cos(Time.time * bobSpeed * 0.8f + ph) * (cs * bobAmount * 0.35f);
                int len = Ships.Length(f.type);
                for (int k = 0; k < len; k++)
                {
                    // vertical ships face UP: chunk k (0 = stern) sits at the bottom-most-first cell
                    int cx = f.o == Orientation.Horizontal ? f.x + k : f.x;
                    int cy = f.o == Orientation.Horizontal ? f.y : f.y + (len - 1 - k);
                    if (hitT[cx, cy] < 0f) continue;
                    var fr = cells[k];
                    if (fr == null || fr.Length == 0) continue;
                    int df = Mathf.FloorToInt((Time.time - hitT[cx, cy]) * dmgFps);
                    if (df < 0) continue;
                    Texture2D frame;
                    if (df < fr.Length) frame = fr[df];
                    else
                    {
                        var lp = dmgLoopFrames[(int)f.type];
                        var lf = lp != null ? lp[k] : null;
                        if (lf != null && lf.Length > 0)
                        {
                            float burn = (Time.time - hitT[cx, cy]) - fr.Length / dmgFps; // seconds since the hit anim ended
                            frame = lf[(Mathf.FloorToInt(burn * dmgLoopFps) + cx * 3 + cy * 5) % lf.Length]; // per-cell phase so fires don't burn in sync
                        }
                        else frame = fr[fr.Length - 1]; // no loop art — hold the last wreck frame like before
                    }
                    float px = ox + cx * cs + sway, py = oy + cy * cs + bob;
                    // light thrown by the burning cell: a hot flash at impact decaying into a
                    // low flickering fire-glow that lives as long as the wreck burns
                    float age = Time.time - hitT[cx, cy];
                    Color pcG = GUI.color;
                    if (age < 1.1f)
                    {
                        float fa = 0.42f * (1f - age / 1.1f) * (1f - age / 1.1f);
                        float fsz = cs * 2.6f;
                        GUI.color = new Color(1f, 0.68f, 0.3f, fa);
                        GUI.DrawTexture(new Rect(px + cs * 0.5f - fsz * 0.5f, py + cs * 0.5f - fsz * 0.5f, fsz, fsz), puffTex);
                    }
                    float fl2 = 0.10f + 0.06f * Mathf.PerlinNoise(Time.time * 7f + cx * 3.1f + cy * 5.7f, 0.3f);
                    float gsz = cs * 1.8f;
                    GUI.color = new Color(1f, 0.55f, 0.2f, fl2);
                    GUI.DrawTexture(new Rect(px + cs * 0.5f - gsz * 0.5f, py + cs * 0.5f - gsz * 0.5f, gsz, gsz), puffTex);
                    GUI.color = pcG;
                    float hs = cs * ShipScale(f.type); // chunks repaint the hull — they grow with it
                    if (f.o == Orientation.Horizontal)
                        GUI.DrawTexture(new Rect(px, py + (cs - hs) * 0.5f, cs, hs), frame, ScaleMode.StretchToFill);
                    else
                    {
                        Vector2 center = new Vector2(px + cs * 0.5f, py + cs * 0.5f);
                        Matrix4x4 mtx = GUI.matrix;
                        RotateContent(-90f, center);
                        GUI.DrawTexture(new Rect(center.x - cs * 0.5f, center.y - hs * 0.5f, cs, hs), frame, ScaleMode.StretchToFill);
                        GUI.matrix = mtx;
                    }
                }
            }
        }

        private bool CellHasDmgAnim(int cx, int cy)
        {
            if (dmgFrames == null) return false;
            for (int i = 0; i < fleet.Length; i++)
                if (fleet[i].placed && dmgFrames[(int)fleet[i].type] != null)
                {
                    int dx = fleet[i].o == Orientation.Horizontal ? 1 : 0, dy = 1 - dx, len = Ships.Length(fleet[i].type);
                    for (int k = 0; k < len; k++) if (fleet[i].x + dx * k == cx && fleet[i].y + dy * k == cy) return true;
                }
            return false;
        }
}
}
