using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;

namespace Battleship
{
    /// <summary>
    /// Pikeman's connection layer — Step 2 of the multiplayer build.
    ///
    /// Wraps UGS anonymous sign-in plus a 2-player Session with join-by-code (host creates a room and
    /// gets a short Code; a friend joins with that Code). THIS STEP IS LOBBY-ONLY: it proves that
    /// Authentication + Lobby + join-by-code work end to end. There is no Relay allocation and no
    /// byte channel yet — moving actual moves over the wire needs a custom INetworkHandler driving
    /// Unity Transport, which lands in Step 3.
    ///
    /// Plain C# object owned by BattleshipController (not a MonoBehaviour). UGS drives its own polling
    /// via the services scheduler, so nothing here needs a per-frame tick. UGS async continuations
    /// resume on Unity's main thread, so the public fields below are safe to read straight from OnGUI.
    /// All entry points are fire-and-forget (async void); the UI just polls <see cref="State"/> etc.
    /// </summary>
    public class PikeNet
    {
        public enum Status { Offline, Connecting, Hosting, Connected, Error }

        public Status State { get; private set; } = Status.Offline;
        public string JoinCode { get; private set; } = "";   // room code to share (host) / that was joined (guest)
        public string Error { get; private set; } = "";       // last failure message, shown in the overlay
        public int PlayerCount { get; private set; }          // players currently in the session (1 or 2)
        public bool IsHost { get; private set; }
        public bool Busy { get; private set; }                // an async op is in flight (host/join)

        // Step 3 — Relay transport status (the byte channel that sits on top of the lobby session)
        public bool RelayLinked => transport != null && transport.Connected;   // UTP peer link is up
        public bool PingOk { get; private set; }               // a Ping/Pong round-trip has completed
        public string TransportInfo => transport != null ? transport.Diag() : "no transport";  // on-screen debug

        private ISession session;
        private PikeTransport transport;

        // Step 4 — match-start handshake
        private bool localReady, remoteReady, matchStarted;
        /// <summary>Fires once when both fleets are ready and the match begins.
        /// The bool is "am I first" from this client's perspective.</summary>
        public event Action<bool> OnMatchStart;

        // Step 6 — in-match moves. PikeNet stays a byte pipe here: the bodies are built and parsed by
        // the game layer (NetCodec), so no game rules leak into the connection code.
        /// <summary>Attacker's decided move arrived — the defender resolves it. Arg = the tag-free body.</summary>
        public event Action<byte[]> OnMoveIntent;
        /// <summary>Defender's resolved outcome arrived — the attacker applies it. Arg = the tag-free body.</summary>
        public event Action<byte[]> OnMoveResult;

        // Step 7e — rematch handshake: both players must ask for a rematch before a new match begins.
        private bool localRematch, remoteRematch;
        /// <summary>Fires once, on both clients, when both have requested a rematch — re-arm and replay.</summary>
        public event Action OnRematch;

        public bool InSession => session != null;

        // ---- entry points (fire-and-forget; the UI polls the fields above) ----

        /// <summary>Sign in, create a 2-player room, and expose its join Code. Waits for the guest.</summary>
        public async void Host()
        {
            if (Busy) return;
            Busy = true; Error = ""; IsHost = true; PlayerCount = 0; PingOk = false; State = Status.Connecting;
            try
            {
                await EnsureSignedIn();
                transport = NewTransport();
                var options = new SessionOptions { MaxPlayers = 2 }
                    .WithRelayNetwork()                 // allocate a Relay endpoint for this room
                    .WithNetworkHandler(transport);     // drive it with our raw UTP handler
                IHostSession host = await MultiplayerService.Instance.CreateSessionAsync(options);
                Bind(host);
                JoinCode = host.Code;
                State = Status.Hosting;
                RefreshCount();
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy = false; }
        }

        /// <summary>Sign in and join an existing room by its Code.</summary>
        public async void Join(string code)
        {
            if (Busy) return;
            string clean = Sanitize(code);   // strip spaces/punctuation, force uppercase — UGS rejects anything else
            if (clean.Length == 0) { Error = "Enter a room code first."; State = Status.Error; return; }
            Busy = true; Error = ""; IsHost = false; PlayerCount = 0; PingOk = false; State = Status.Connecting;
            try
            {
                await EnsureSignedIn();
                transport = NewTransport();
                // the guest inherits the Relay network from the session; it only attaches the handler
                var jopt = new JoinSessionOptions().WithNetworkHandler(transport);
                ISession s = await MultiplayerService.Instance.JoinSessionByCodeAsync(clean, jopt);
                Bind(s);
                JoinCode = s.Code;
                RefreshCount();
                if (State != Status.Connected) State = Status.Connected;
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy = false; }
        }

        /// <summary>Leave the current room and reset to Offline (best-effort).</summary>
        public async void Leave()
        {
            var s = session;
            session = null; transport = null;   // the SDK calls transport.StopAsync as the session network tears down
            State = Status.Offline; JoinCode = ""; PlayerCount = 0; Error = ""; IsHost = false; PingOk = false;
            localReady = remoteReady = matchStarted = false; localRematch = remoteRematch = false;
            if (s != null) { try { await s.LeaveAsync(); } catch { /* best-effort on the way out */ } }
        }

        /// <summary>Pump the Relay transport. Called every frame from BattleshipController.Update.</summary>
        public void Tick() => transport?.Tick();

        // ---- Relay transport wiring (Step 3) ----

        private PikeTransport NewTransport()
        {
            var t = new PikeTransport();
            t.OnConnected    += OnRelayConnected;
            t.OnData         += OnRelayData;
            t.OnDisconnected += OnRelayDisconnected;
            return t;
        }

        private void OnRelayConnected()
        {
            // the host opens the round-trip probe as soon as the byte channel is live
            if (IsHost) transport?.Send(new byte[] { (byte)NetMsg.Ping });
        }

        private void OnRelayData(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            switch ((NetMsg)data[0])
            {
                case NetMsg.Ping:
                    transport?.Send(new byte[] { (byte)NetMsg.Pong });   // guest bounces it back
                    PingOk = true;
                    break;
                case NetMsg.Pong:
                    PingOk = true;                                        // host saw the reply -> link proven
                    Debug.Log("[PikeNet] Relay round-trip OK");
                    break;
                case NetMsg.FleetReady:
                    remoteReady = true;
                    TryStartMatch();                                      // host may now have both readies
                    break;
                case NetMsg.MatchStart:
                    if (!matchStarted)                                    // guest: host has decided the first turn
                    {
                        matchStarted = true;
                        bool guestFirst = data.Length > 1 && data[1] == 1;
                        OnMatchStart?.Invoke(guestFirst);                 // "am I first" for the guest
                    }
                    break;
                case NetMsg.MoveIntent:
                    OnMoveIntent?.Invoke(Body(data));                     // defender: resolve the attacker's move
                    break;
                case NetMsg.MoveResult:
                    OnMoveResult?.Invoke(Body(data));                     // attacker: apply the resolved outcome
                    break;
                case NetMsg.Rematch:
                    remoteRematch = true;                                 // opponent wants to play again
                    TryRematch();
                    break;
            }
        }

        /// <summary>Strip the leading NetMsg tag, handing the game layer just the message body.</summary>
        private static byte[] Body(byte[] data)
        {
            var b = new byte[data.Length - 1];
            Array.Copy(data, 1, b, 0, b.Length);
            return b;
        }

        // ---- Step 6: send an in-match move (attacker) / its resolved result (defender) ----

        /// <summary>Send my decided move to the opponent (I am the attacker this turn).</summary>
        public bool SendMoveIntent(byte[] body) => SendTagged(NetMsg.MoveIntent, body);

        /// <summary>Send the resolved outcome back to the attacker (I am the defender this turn).</summary>
        public bool SendMoveResult(byte[] body) => SendTagged(NetMsg.MoveResult, body);

        private bool SendTagged(NetMsg tag, byte[] body)
        {
            int n = body?.Length ?? 0;
            var buf = new byte[1 + n];
            buf[0] = (byte)tag;
            if (n > 0) Array.Copy(body, 0, buf, 1, n);
            return transport != null && transport.Send(buf);
        }

        private void OnRelayDisconnected() { PingOk = false; }

        /// <summary>Called when THIS client asks for a rematch at the game-over screen.</summary>
        public void SendRematch()
        {
            localRematch = true;
            transport?.Send(new byte[] { (byte)NetMsg.Rematch });
            TryRematch();
        }

        // Both players agreed: re-arm the FleetReady/MatchStart handshake and tell the game to replay.
        private void TryRematch()
        {
            if (!localRematch || !remoteRematch) return;
            localRematch = remoteRematch = false;
            localReady = remoteReady = matchStarted = false;   // so the next FleetReady/MatchStart runs fresh
            OnRematch?.Invoke();
        }

        /// <summary>Called when THIS client has placed its fleet and pressed READY.</summary>
        public void SendFleetReady()
        {
            localReady = true;
            transport?.Send(new byte[] { (byte)NetMsg.FleetReady });
            TryStartMatch();
        }

        // Host-only: once both fleets are ready, pick who shoots first and broadcast MatchStart.
        private void TryStartMatch()
        {
            if (matchStarted || !IsHost || !localReady || !remoteReady) return;
            matchStarted = true;
            const byte hostFirst = 0;   // 0 = host shoots first (balance/randomize later)
            transport?.Send(new byte[] { (byte)NetMsg.MatchStart, hostFirst });
            OnMatchStart?.Invoke(hostFirst == 0);   // host is first when hostFirst==0
        }

        /// <summary>Keep only A–Z / 0–9 (uppercased) — the exact character set UGS join codes use.
        /// Used both to filter the JOIN text field live and to clean the code before joining, so a
        /// stray space, dash or lowercase letter can never trigger an "invalid characters" error.</summary>
        public static string Sanitize(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            var sb = new System.Text.StringBuilder(code.Length);
            foreach (char c in code)
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c >= 'a' && c <= 'z') sb.Append((char)(c - 32));
            return sb.ToString();
        }

        // ---- internals ----

        private static async Task EnsureSignedIn()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        private void Bind(ISession s)
        {
            session = s;
            s.PlayerJoined       += _ => RefreshCount();
            s.PlayerHasLeft      += _ => RefreshCount();
            s.Changed            += RefreshCount;
            s.RemovedFromSession += OnRemoved;
        }

        private void RefreshCount()
        {
            if (session == null) return;
            PlayerCount = session.PlayerCount;
            if (PlayerCount >= 2) State = Status.Connected;   // both players present — room is full
            else if (IsHost) State = Status.Hosting;          // host still waiting on the guest
        }

        private void OnRemoved()
        {
            session = null; transport = null;
            State = Status.Offline; PlayerCount = 0; JoinCode = ""; IsHost = false; PingOk = false;
            localReady = remoteReady = matchStarted = false; localRematch = remoteRematch = false;
        }

        private void Fail(Exception ex)
        {
            Error = ex.Message;
            State = Status.Error;
            Debug.LogError("[PikeNet] " + ex);
        }
    }
}
