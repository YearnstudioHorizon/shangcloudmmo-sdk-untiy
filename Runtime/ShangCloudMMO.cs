using System;
using System.Collections.Generic;
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

        // 高级封装事件（参考 core.js 的广播与同步变量协议）
        /// <summary>收到广播消息 (uid, message, extra)。wire 格式：{"uid","message","extra"}，无 type 字段。</summary>
        public event Action<string, string, string> OnBroadcastReceived;
        /// <summary>收到同步变量 (uid, vars, interp)。wire 格式：{"type":"__sync_var__","uid","vars","interp"}。</summary>
        public event Action<string, IDictionary<string, string>, IReadOnlyList<string>> OnSyncVarReceived;

        /// <summary>
        /// 每次插帧变量被平滑推进时触发（uid, varName, current）。在 Update 中逐帧触发，
        /// 调用方据此回写场景对象（如移动克隆体）。移植自 core.js 的 _ensureInterpLoop。
        /// </summary>
        public event Action<string, string, double> OnSyncVarInterpolated;

        private IMmoTransport _transport;
        private readonly MmoMessageQueue _messageQueue = new MmoMessageQueue();

        // 插帧引擎（移植自 core.js 的 _ensureInterpLoop / _mmoInterpState）
        private readonly MmoInterpEngine _interpEngine = new MmoInterpEngine();

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
            _interpEngine.Clear();
        }

        /// <summary>
        /// 读取指定 uid 的同步变量当前值（插帧变量的 current，平滑后）。
        /// 若该变量不在插帧集合或尚未建立状态，回退到最近原始值。
        /// </summary>
        public double GetSyncVar(string uid, string varName)
        {
            return _interpEngine.GetSyncVar(uid, varName);
        }

        /// <summary>读取指定 uid 的同步变量原始字符串值（不做插帧）。</summary>
        public string GetSyncVarRaw(string uid, string varName)
        {
            return _interpEngine.GetSyncVarRaw(uid, varName);
        }

        /// <summary>清理指定 uid 的插帧状态（玩家离开时调用）。</summary>
        public void ClearSyncVarState(string uid)
        {
            _interpEngine.ClearUid(uid);
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

        /// <summary>
        /// 发送广播消息。wire 格式（参考 core.js 的 sendMmoMessage）：
        /// {"uid":"...","message":"...","extra":"..."} —— 不含 type 字段。
        /// </summary>
        public void SendBroadcast(string uid, string message, string extra = "")
        {
            var payload = new JObject
            {
                ["uid"] = uid ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["extra"] = extra ?? string.Empty,
            };
            SendMessage(payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>
        /// 发送同步变量。wire 格式（参考 core.js 的 __sync_var__）：
        /// {"type":"__sync_var__","uid":"...","vars":{...},"interp":["x",...]}
        /// </summary>
        /// <param name="uid">发送方 UID。</param>
        /// <param name="vars">变量名→值（值会被转为字符串，与 core.js 一致）。</param>
        /// <param name="interp">需要接收端插帧平滑的变量名列表。</param>
        public void SendSyncVar(string uid, IDictionary<string, object> vars, IReadOnlyList<string> interp = null)
        {
            var varsObj = new JObject();
            if (vars != null)
            {
                foreach (var kv in vars)
                {
                    // core.js 将所有值以字符串形式序列化
                    varsObj[kv.Key] = kv.Value switch
                    {
                        null => string.Empty,
                        bool b => b ? "true" : "false",
                        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                        _ => kv.Value.ToString(),
                    };
                }
            }

            var interpArr = new JArray();
            if (interp != null)
            {
                foreach (var name in interp)
                {
                    interpArr.Add(name ?? string.Empty);
                }
            }

            var payload = new JObject
            {
                ["type"] = "__sync_var__",
                ["uid"] = uid ?? string.Empty,
                ["vars"] = varsObj,
                ["interp"] = interpArr,
            };
            SendMessage(payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>发送加入房间通知。wire 格式：{"type":"__join__","uid":"...","nickname":"..."}</summary>
        public void SendJoinAnnouncement(string uid, string nickname)
        {
            var payload = new JObject
            {
                ["type"] = "__join__",
                ["uid"] = uid ?? string.Empty,
                ["nickname"] = nickname ?? string.Empty,
            };
            SendMessage(payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void Update()
        {
            // 先推进插帧引擎（收到 sync_var 后逐帧把 current → target）
            if (_transport != null)
            {
                var interpChanges = _interpEngine.Tick(Time.deltaTime);
                if (interpChanges != null && interpChanges.Count > 0)
                {
                    for (int i = 0; i < interpChanges.Count; i++)
                    {
                        var c = interpChanges[i];
                        OnSyncVarInterpolated?.Invoke(c.uid, c.varName, c.value);
                    }
                }
            }

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
                        _interpEngine.ClearUid(uid);
                        OnUserLeft?.Invoke(uid);
                        return;
                    }

                    if (type == "__sync_var__")
                    {
                        // 同步变量：{"type":"__sync_var__","uid","vars","interp"}
                        string uid = json.Value<string>("uid") ?? "";
                        var vars = new Dictionary<string, string>();
                        var varsToken = json["vars"] as JObject;
                        if (varsToken != null)
                        {
                            foreach (var prop in varsToken.Properties())
                            {
                                vars[prop.Name] = prop.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                            }
                        }
                        var interp = new List<string>();
                        var interpToken = json["interp"] as JArray;
                        if (interpToken != null)
                        {
                            foreach (var item in interpToken)
                            {
                                interp.Add(item?.ToString() ?? "");
                            }
                        }
                        // 先把 vars 喂给插帧引擎（数值且在 interp 中的变量会进入平滑状态）
                        _interpEngine.ApplySync(uid, vars, interp);
                        OnSyncVarReceived?.Invoke(uid, vars, interp);
                        return;
                    }
                    if (string.IsNullOrEmpty(type) && json["message"] != null &&
                        (json["uid"] != null || json["extra"] != null))
                    {
                        string uid = json.Value<string>("uid") ?? "";
                        string msg = json.Value<string>("message") ?? "";
                        string extra = json.Value<string>("extra") ?? "";
                        OnBroadcastReceived?.Invoke(uid, msg, extra);
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
