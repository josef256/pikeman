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

        private ISession session;

        public bool InSession => session != null;

        // ---- entry points (fire-and-forget; the UI polls the fields above) ----

        /// <summary>Sign in, create a 2-player room, and expose its join Code. Waits for the guest.</summary>
        public async void Host()
        {
            if (Busy) return;
            Busy = true; Error = ""; IsHost = true; PlayerCount = 0; State = Status.Connecting;
            try
            {
                await EnsureSignedIn();
                var options = new SessionOptions { MaxPlayers = 2 };   // lobby-only for now (Relay comes in Step 3)
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
            Busy = true; Error = ""; IsHost = false; PlayerCount = 0; State = Status.Connecting;
            try
            {
                await EnsureSignedIn();
                ISession s = await MultiplayerService.Instance.JoinSessionByCodeAsync(clean);
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
            session = null;
            State = Status.Offline; JoinCode = ""; PlayerCount = 0; Error = ""; IsHost = false;
            if (s != null) { try { await s.LeaveAsync(); } catch { /* best-effort on the way out */ } }
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
            session = null;
            State = Status.Offline; PlayerCount = 0; JoinCode = ""; IsHost = false;
        }

        private void Fail(Exception ex)
        {
            Error = ex.Message;
            State = Status.Error;
            Debug.LogError("[PikeNet] " + ex);
        }
    }
}
