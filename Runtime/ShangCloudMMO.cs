using System;
using System.Text;
using UnityEngine;
using Newtonsoft.Json.Linq;
using ShangCloud.MMO.Threading;
using ShangCloud.MMO.Transport;

namespace ShangCloud.MMO
{
    public class ShangCloudMMO : MonoBehaviour
    {
        [SerializeField] private MmoProtocol protocol = MmoProtocol.TCP;
        [SerializeField] private string connectKey;
        [SerializeField] private string edgeHost;
        [SerializeField] private int edgePort;
        [SerializeField] private string edgeUrl;

        public MmoConnectionState State
        {
            get
            {
                if (_transport == null) return MmoConnectionState.Disconnected;
                return _transport.State;
            }
        }

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnConnectionError;
        public event Action<string> OnMessageReceived;
        public event Action<byte[], int> OnRawMessageReceived;
        public event Action<string, string> OnUserJoined;
        public event Action<string> OnUserLeft;
        public event Action OnServerClosed;

        private IMmoTransport _transport;
        private readonly MmoMessageQueue _messageQueue = new MmoMessageQueue();

        /// <summary>
        /// Configures this component from an API response.
        /// Parses edgeUrl into host+port for TCP/UDP, or stores full URI for WebSocket.
        /// </summary>
        public void ConfigureFromApiResponse(string responseConnectKey, string responseEdgeUrl, string responseProtocol)
        {
            connectKey = responseConnectKey;
            edgeUrl = responseEdgeUrl;

            switch (responseProtocol?.ToLowerInvariant())
            {
                case "websocket":
                case "ws":
                    protocol = MmoProtocol.WebSocket;
                    break;
                case "udp":
                    protocol = MmoProtocol.UDP;
                    break;
                default:
                    protocol = MmoProtocol.TCP;
                    break;
            }

            ParseEdgeUrl(responseEdgeUrl);
        }

        public void ConnectToEdge()
        {
            if (string.IsNullOrEmpty(connectKey))
            {
                Debug.LogError("ShangCloudMMO: connect_key must be set before connecting");
                return;
            }

            CleanupTransport();

            switch (protocol)
            {
                case MmoProtocol.TCP:
                    _transport = new MmoTcpTransport();
                    break;
                case MmoProtocol.UDP:
                    _transport = new MmoUdpTransport();
                    break;
                case MmoProtocol.WebSocket:
#if UNITY_WEBGL
                    Debug.LogError("ShangCloudMMO: WebSocket transport is not supported on WebGL platform");
                    return;
#else
                    var wsTransport = new MmoWebSocketTransport();
                    _transport = wsTransport;
                    break;
#endif
            }

            _transport.SetMessageQueue(_messageQueue);
            _transport.OnConnected += () => OnConnected?.Invoke();
            _transport.OnDisconnected += () => OnDisconnected?.Invoke();
            _transport.OnError += err => OnConnectionError?.Invoke(err);
            _transport.OnServerClosed += () => OnServerClosed?.Invoke();

            if (protocol == MmoProtocol.WebSocket && !string.IsNullOrEmpty(edgeUrl))
            {
#if !UNITY_WEBGL
                ((MmoWebSocketTransport)_transport).Connect(edgeUrl, connectKey);
#endif
            }
            else
            {
                if (string.IsNullOrEmpty(edgeHost) || edgePort <= 0)
                {
                    Debug.LogError("ShangCloudMMO: edge_host and edge_port must be set before connecting");
                    return;
                }
                _transport.Connect(edgeHost, edgePort, connectKey);
            }
        }

        public void DisconnectFromEdge()
        {
            _transport?.Disconnect();
        }

        public void SendMessage(string message)
        {
            if (_transport == null || _transport.State != MmoConnectionState.Connected)
            {
                Debug.LogError("ShangCloudMMO: cannot send message, not connected");
                return;
            }

            byte[] data = Encoding.UTF8.GetBytes(message);
            _transport.Send(data, data.Length);
        }

        public void SendRaw(byte[] data, int length)
        {
            if (_transport == null || _transport.State != MmoConnectionState.Connected)
            {
                Debug.LogError("ShangCloudMMO: cannot send data, not connected");
                return;
            }

            _transport.Send(data, length);
        }

        private void Update()
        {
            if (_transport == null) return;

            _transport.Poll(Time.deltaTime);

            var messages = _messageQueue.DrainAll();
            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                try
                {
                    switch (msg.Type)
                    {
                        case MmoMessage.MessageType.Text:
                            ProcessBusinessMessage(msg.Text);
                            break;
                        case MmoMessage.MessageType.Binary:
                            OnRawMessageReceived?.Invoke(msg.RentedBuffer, msg.Length);
                            break;
                    }
                }
                finally
                {
                    msg.Return();
                }
            }
        }

        private void OnDestroy()
        {
            CleanupTransport();
        }

        private void CleanupTransport()
        {
            if (_transport != null)
            {
                _transport.Disconnect();
                _transport.Dispose();
                _transport = null;
            }
        }

        private void ProcessBusinessMessage(string message)
        {
            if (message.Length > 0 && message[0] == '{')
            {
                try
                {
                    var json = JObject.Parse(message);
                    string type = json.Value<string>("type");

                    if (type == "__join__")
                    {
                        string uid = json.Value<string>("uid") ?? "";
                        string nickname = json.Value<string>("nickname") ?? "";
                        OnUserJoined?.Invoke(uid, nickname);
                        return;
                    }

                    if (type == "__leave__")
                    {
                        string uid = json.Value<string>("uid") ?? "";
                        OnUserLeft?.Invoke(uid);
                        return;
                    }
                }
                catch
                {
                    // not valid JSON or missing fields, treat as regular message
                }
            }

            OnMessageReceived?.Invoke(message);
        }

        private void ParseEdgeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            // WebSocket: keep full URL
            if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                edgeUrl = url;
                // Also extract host:port for fallback
                try
                {
                    var uri = new Uri(url);
                    edgeHost = uri.Host;
                    edgePort = uri.Port;
                }
                catch { }
                return;
            }

            // Strip protocol prefix if present (tcp://, udp://)
            string hostPort = url;
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
                hostPort = url.Substring(schemeEnd + 3);

            // Split host:port
            int colonIdx = hostPort.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(hostPort.Substring(colonIdx + 1), out int port))
            {
                edgeHost = hostPort.Substring(0, colonIdx);
                edgePort = port;
            }
            else
            {
                edgeHost = hostPort;
            }
        }
    }
}
