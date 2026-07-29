using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Services.Multiplayer;

namespace Battleship
{
    /// <summary>
    /// Step 3 — raw Unity Transport driven over a UGS Relay connection, WITHOUT Netcode for GameObjects.
    ///
    /// Plugged into a Session via <c>SessionOptions.WithNetworkHandler(this)</c>. When the session's
    /// network starts, the SDK calls <see cref="StartAsync"/> with a <see cref="NetworkConfiguration"/>
    /// that carries our role (Host/Server/Client) and the Relay data. We run a single
    /// <see cref="NetworkDriver"/> with one reliable-ordered pipeline and exactly one peer (1 host + 1 guest).
    ///
    /// IMPORTANT: the SDK does NOT tick us. <see cref="Tick"/> must be pumped every frame on the main
    /// thread (BattleshipController.Update -> PikeNet.Tick -> here) or the Relay allocation times out.
    /// Relay binding is asynchronous: after Bind we wait for <c>driver.Bound</c> before Listen/Connect.
    /// </summary>
    public class PikeTransport : INetworkHandler
    {
        private NetworkDriver driver;
        private NetworkPipeline reliable;
        private NetworkConnection peer;             // the single other player
        private NetworkRole role;
        private enum Phase { Idle, Binding, Live }
        private Phase phase = Phase.Idle;
        private bool startCalled;

        public bool Connected { get; private set; }
        public string LastError { get; private set; } = "";

        /// <summary>Short human-readable transport state for on-screen debugging.</summary>
        public string Diag()
        {
            if (!startCalled) return "handler idle (StartAsync not called)";
            if (!driver.IsCreated) return $"{role} no-driver err={LastError}";
            return $"{role} ph={phase} bound={driver.Bound} conn={Connected}";
        }

        // fired on the main thread from Tick()
        public event Action<byte[]> OnData;
        public event Action OnConnected;
        public event Action OnDisconnected;

        public Task StartAsync(NetworkConfiguration configuration)
        {
            role = configuration.Role;
            startCalled = true;
            try
            {
                var settings = new NetworkSettings(Allocator.Temp);
                // In this SDK's non-Entities path, the SDK puts BOTH the host's allocation data AND the
                // joining client's own relay data into RelayServerData; RelayClientData stays default
                // unless the Entities netcode package is installed. So always use RelayServerData.
                var data = configuration.RelayServerData;
                settings.WithRelayParameters(ref data);

                driver = NetworkDriver.Create(settings);
                reliable = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));  // must precede any connection

                if (driver.Bind(NetworkEndpoint.AnyIpv4) != 0)
                {
                    LastError = "Relay bind failed";
                    Debug.LogError("[PikeTransport] " + LastError);
                }
                else
                {
                    phase = Phase.Binding;   // Listen (host) / Connect (client) happens in Tick once driver.Bound
                    Debug.Log($"[PikeTransport] StartAsync role={role}, bind ok, waiting for relay bind");
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError("[PikeTransport] StartAsync: " + ex);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Connected = false;
            phase = Phase.Idle;
            if (driver.IsCreated)
            {
                if (peer.IsCreated) peer.Disconnect(driver);
                driver.ScheduleUpdate().Complete();
                driver.Dispose();
            }
            peer = default;
            return Task.CompletedTask;
        }

        /// <summary>Pump the driver and drain its events. Call every frame on the main thread.</summary>
        public void Tick()
        {
            if (!driver.IsCreated) return;
            driver.ScheduleUpdate().Complete();

            // relay bind handshake finished -> start listening (host) or dialing (client), once
            if (phase == Phase.Binding && driver.Bound)
            {
                if (role == NetworkRole.Client) { peer = driver.Connect(); Debug.Log("[PikeTransport] relay bound, client connecting"); }
                else { driver.Listen(); Debug.Log("[PikeTransport] relay bound, host listening"); }
                phase = Phase.Live;
            }

            // host: accept the incoming guest (Accept IS the connect signal on the server side)
            if (phase == Phase.Live && role != NetworkRole.Client)
            {
                NetworkConnection c;
                while ((c = driver.Accept()).IsCreated)
                {
                    peer = c;
                    Connected = true;
                    Debug.Log("[PikeTransport] host accepted guest");
                    OnConnected?.Invoke();
                }
            }

            // drain data / client-side connect / disconnect
            NetworkEvent.Type ev;
            while ((ev = driver.PopEvent(out NetworkConnection conn, out DataStreamReader reader)) != NetworkEvent.Type.Empty)
            {
                switch (ev)
                {
                    case NetworkEvent.Type.Connect:      // client's outbound connect completed
                        Connected = true;
                        Debug.Log("[PikeTransport] client connect event");
                        OnConnected?.Invoke();
                        break;
                    case NetworkEvent.Type.Data:
                        var buf = new byte[reader.Length];
                        reader.ReadBytes(new Span<byte>(buf));
                        OnData?.Invoke(buf);
                        break;
                    case NetworkEvent.Type.Disconnect:
                        Connected = false;
                        peer = default;
                        OnDisconnected?.Invoke();
                        break;
                }
            }
        }

        /// <summary>Send a reliable, ordered message to the peer. False if not connected / send failed.</summary>
        public bool Send(byte[] payload)
        {
            if (!driver.IsCreated || !peer.IsCreated || payload == null) return false;
            if (driver.BeginSend(reliable, peer, out var writer) != 0) return false;
            writer.WriteBytes(new Span<byte>(payload));
            return driver.EndSend(writer) >= 0;
        }
    }
}
