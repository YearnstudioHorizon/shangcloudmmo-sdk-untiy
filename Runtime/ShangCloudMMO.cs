using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Newtonsoft.Json.Linq;
using ShangCloud.MMO.Threading;
using ShangCloud.MMO.Transport;

namespace ShangCloud.MMO
{
    /// <summary>房间成员信息（参考 extension getMemberList / __pong__ 成员 JSON）。</summary>
    public sealed class MmoRoomMember
    {
        public string Uid { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
    }

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

        /// <summary>
        /// 房间成员列表更新（收到 __pong__ 后触发）。参数：(userCount, members)。
        /// 参考 extension getMemberList / window._mmoMembers。
        /// </summary>
        public event Action<int, IReadOnlyList<MmoRoomMember>> OnMembersUpdated;

        private IMmoTransport _transport;
        private readonly MmoMessageQueue _messageQueue = new MmoMessageQueue();

        // 插帧引擎（移植自 core.js 的 _ensureInterpLoop / _mmoInterpState）
        private readonly MmoInterpEngine _interpEngine = new MmoInterpEngine();

        // 房间成员缓存（参考 extension 的 window._mmoMembers）
        private readonly List<MmoRoomMember> _members = new List<MmoRoomMember>();
        private int _roomUserCount;

        // 本端发送侧缺省变量缓存：未在本次 SendSyncVar 中出现的键自动沿用上次值
        private string _outgoingSyncUid = string.Empty;
        private readonly Dictionary<string, string> _outgoingSyncVars = new Dictionary<string, string>();
        private readonly HashSet<string> _outgoingSyncInterp = new HashSet<string>();

        // 传输层事件在后台线程触发，入队后在主线程 Update 派发（可安全改 UI）
        private readonly Queue<PendingTransportEvent> _pendingEvents = new Queue<PendingTransportEvent>();
        private readonly object _pendingEventsLock = new object();

        private enum PendingEventKind
        {
            Connected,
            Disconnected,
            Error,
            ServerClosed,
        }

        private struct PendingTransportEvent
        {
            public PendingEventKind Kind;
            public string Error;
        }

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
            ConnectInternal(protocol, edgeHost, edgePort, connectKey, edgeUrl);
        }

        /// <summary>
        /// Connects directly to an MMO edge node without using an API response.
        /// </summary>
        public void ConnectToEdge(string host, int port, string connectionKey)
        {
            ConnectToEdge(MmoProtocol.TCP, host, port, connectionKey);
        }

        /// <summary>
        /// Connects directly to an MMO edge node without using an API response.
        /// For WebSocket, this overload connects to ws://host:port/ws.
        /// </summary>
        public void ConnectToEdge(MmoProtocol connectionProtocol, string host, int port, string connectionKey)
        {
            protocol = connectionProtocol;
            connectKey = connectionKey;
            edgeHost = host;
            edgePort = port;
            edgeUrl = null;

            ConnectInternal(connectionProtocol, host, port, connectionKey, null);
        }

        /// <summary>
        /// Connects directly to a WebSocket MMO edge URL without using an API response.
        /// </summary>
        public void ConnectToWebSocketEdge(string websocketUrl, string connectionKey)
        {
            protocol = MmoProtocol.WebSocket;
            connectKey = connectionKey;
            edgeUrl = websocketUrl;
            ParseEdgeUrl(websocketUrl);

            ConnectInternal(MmoProtocol.WebSocket, edgeHost, edgePort, connectionKey, websocketUrl);
        }

        private void ConnectInternal(MmoProtocol connectionProtocol, string host, int port, string connectionKey, string websocketUrl)
        {
            if (string.IsNullOrEmpty(connectionKey))
            {
                Debug.LogError("ShangCloudMMO: connect_key must be set before connecting");
                return;
            }

            if (connectionProtocol == MmoProtocol.WebSocket)
            {
                if (string.IsNullOrEmpty(websocketUrl) && (string.IsNullOrEmpty(host) || port <= 0))
                {
                    Debug.LogError("ShangCloudMMO: edge_url or edge_host and edge_port must be set before connecting");
                    return;
                }
            }
            else if (string.IsNullOrEmpty(host) || port <= 0)
            {
                Debug.LogError("ShangCloudMMO: edge_host and edge_port must be set before connecting");
                return;
            }

            CleanupTransport();

            _transport = CreateTransport(connectionProtocol);
            if (_transport == null)
                return;

            AttachTransportEvents(_transport);

            if (connectionProtocol == MmoProtocol.WebSocket && !string.IsNullOrEmpty(websocketUrl))
            {
#if UNITY_WEBGL
                Debug.LogError("ShangCloudMMO: WebSocket transport is not supported on WebGL platform");
#else
                ((MmoWebSocketTransport)_transport).Connect(websocketUrl, connectionKey);
#endif
                return;
            }

            _transport.Connect(host, port, connectionKey);
        }

        private IMmoTransport CreateTransport(MmoProtocol connectionProtocol)
        {
            switch (connectionProtocol)
            {
                case MmoProtocol.TCP:
#if UNITY_WEBGL
                    Debug.LogError("ShangCloudMMO: TCP transport is not supported on WebGL platform");
                    return null;
#else
                    return new MmoTcpTransport();
#endif
                case MmoProtocol.UDP:
#if UNITY_WEBGL
                    Debug.LogError("ShangCloudMMO: UDP transport is not supported on WebGL platform");
                    return null;
#else
                    return new MmoUdpTransport();
#endif
                case MmoProtocol.WebSocket:
#if UNITY_WEBGL
                    Debug.LogError("ShangCloudMMO: WebSocket transport is not supported on WebGL platform");
                    return null;
#else
                    return new MmoWebSocketTransport();
#endif
                default:
                    Debug.LogError($"ShangCloudMMO: unsupported protocol {connectionProtocol}");
                    return null;
            }
        }

        private void AttachTransportEvents(IMmoTransport transport)
        {
            transport.SetMessageQueue(_messageQueue);
            // 后台线程只入队，主线程 Update 再触发 C# 事件
            transport.OnConnected += () => EnqueueTransportEvent(PendingEventKind.Connected, null);
            transport.OnDisconnected += () => EnqueueTransportEvent(PendingEventKind.Disconnected, null);
            transport.OnError += err => EnqueueTransportEvent(PendingEventKind.Error, err);
            transport.OnServerClosed += () => EnqueueTransportEvent(PendingEventKind.ServerClosed, null);
        }

        private void EnqueueTransportEvent(PendingEventKind kind, string error)
        {
            lock (_pendingEventsLock)
            {
                _pendingEvents.Enqueue(new PendingTransportEvent { Kind = kind, Error = error });
            }
        }

        private void DrainTransportEvents()
        {
            while (true)
            {
                PendingTransportEvent ev;
                lock (_pendingEventsLock)
                {
                    if (_pendingEvents.Count == 0) return;
                    ev = _pendingEvents.Dequeue();
                }

                switch (ev.Kind)
                {
                    case PendingEventKind.Connected:
                        OnConnected?.Invoke();
                        break;
                    case PendingEventKind.Disconnected:
                        ClearMembers();
                        ClearOutgoingSyncVarCache();
                        OnDisconnected?.Invoke();
                        break;
                    case PendingEventKind.Error:
                        OnConnectionError?.Invoke(ev.Error);
                        break;
                    case PendingEventKind.ServerClosed:
                        OnServerClosed?.Invoke();
                        break;
                }
            }
        }

        public void DisconnectFromEdge()
        {
            _transport?.Disconnect();
            _interpEngine.Clear();
            ClearOutgoingSyncVarCache();
            ClearMembers();
        }

        /// <summary>清空本端发送侧缺省变量缓存（断开连接时会自动调用）。</summary>
        public void ClearOutgoingSyncVarCache()
        {
            _outgoingSyncUid = string.Empty;
            _outgoingSyncVars.Clear();
            _outgoingSyncInterp.Clear();
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

        public new void SendMessage(string message)
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
        /// 缺省变量处理：SDK 缓存本端最近一次完整 vars/interp，本次未传入的键自动补发上次值。
        /// </summary>
        /// <param name="uid">发送方 UID。</param>
        /// <param name="vars">变量名→值（值会被转为字符串，与 core.js 一致）。</param>
        /// <param name="interp">需要接收端插帧平滑的变量名列表。</param>
        public void SendSyncVar(string uid, IDictionary<string, object> vars, IReadOnlyList<string> interp = null)
        {
            uid ??= string.Empty;
            if (!string.Equals(uid, _outgoingSyncUid, StringComparison.Ordinal))
            {
                _outgoingSyncUid = uid;
                _outgoingSyncVars.Clear();
                _outgoingSyncInterp.Clear();
            }

            if (vars != null)
            {
                foreach (var kv in vars)
                {
                    if (kv.Key == null) continue;
                    // core.js 将所有值以字符串形式序列化
                    string value = kv.Value switch
                    {
                        null => string.Empty,
                        bool b => b ? "true" : "false",
                        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                        _ => kv.Value.ToString(),
                    };
                    _outgoingSyncVars[kv.Key] = value ?? string.Empty;
                }
            }

            if (interp != null)
            {
                foreach (var name in interp)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        _outgoingSyncInterp.Add(name);
                    }
                }
            }

            var varsObj = new JObject();
            foreach (var kv in _outgoingSyncVars)
            {
                varsObj[kv.Key] = kv.Value;
            }

            var interpArr = new JArray();
            foreach (var name in _outgoingSyncInterp)
            {
                interpArr.Add(name);
            }

            var payload = new JObject
            {
                ["type"] = "__sync_var__",
                ["uid"] = uid,
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

            // 立即将自己加入本地成员列表（参考 extension wasm，无需等待 __pong__）
            UpsertMember(uid ?? string.Empty, nickname ?? string.Empty);
            // 主动查询完整成员列表
            QueryMembers();
        }

        /// <summary>
        /// 查询房间成员列表。发送 __ping__，服务端以 __pong__:N:membersJSON 响应并更新本地缓存。
        /// 结果通过 <see cref="OnMembersUpdated"/> 与 <see cref="GetMemberList"/> 获取。
        /// </summary>
        public void QueryMembers()
        {
            if (_transport == null || _transport.State != MmoConnectionState.Connected)
            {
                Debug.LogError("ShangCloudMMO: cannot query members, not connected");
                return;
            }
            SendMessage("__ping__");
        }

        /// <summary>
        /// 返回本地缓存的房间成员列表（参考 extension getMemberList）。
        /// 由 __join__/__leave__/__pong__ 维护。
        /// </summary>
        public IReadOnlyList<MmoRoomMember> GetMemberList()
        {
            return _members.ToArray();
        }

        /// <summary>返回最近一次 __pong__ 的房间人数（无缓存时为成员列表长度）。</summary>
        public int GetRoomUserCount()
        {
            return _roomUserCount > 0 ? _roomUserCount : _members.Count;
        }

        private void Update()
        {
            // 先派发传输层事件（Connected/Error 等），保证回调在主线程
            DrainTransportEvents();

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

        private void ClearMembers()
        {
            _members.Clear();
            _roomUserCount = 0;
        }

        private void UpsertMember(string uid, string nickname)
        {
            if (string.IsNullOrEmpty(uid)) return;
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Uid == uid)
                {
                    _members[i].Nickname = nickname ?? string.Empty;
                    return;
                }
            }
            _members.Add(new MmoRoomMember
            {
                Uid = uid,
                Nickname = nickname ?? string.Empty,
            });
        }

        private void RemoveMember(string uid)
        {
            _members.RemoveAll(m => m.Uid == uid);
        }

        private void ApplyMembersFromPong(string membersJson, int count)
        {
            if (!string.IsNullOrEmpty(membersJson) && membersJson != "null")
            {
                try
                {
                    var arr = JArray.Parse(membersJson);
                    var next = new List<MmoRoomMember>(arr.Count);
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var item = arr[i] as JObject;
                        if (item == null) continue;
                        string uid = item.Value<string>("uid") ?? string.Empty;
                        if (string.IsNullOrEmpty(uid)) continue;
                        next.Add(new MmoRoomMember
                        {
                            Uid = uid,
                            Nickname = item.Value<string>("nickname") ?? string.Empty,
                        });
                    }
                    _members.Clear();
                    _members.AddRange(next);
                }
                catch
                {
                    // 成员 JSON 解析失败时保留现有缓存
                }
            }
            _roomUserCount = count > 0 ? count : _members.Count;
            OnMembersUpdated?.Invoke(_roomUserCount, _members.ToArray());
        }

        private void ProcessBusinessMessage(string message)
        {
            // __pong__:<人数>:<成员JSON> —— 房间成员查询响应（参考 extension wasm）
            if (message.StartsWith("__pong__", StringComparison.Ordinal))
            {
                string rest = message.Length > 8 ? message.Substring(8) : string.Empty;
                if (rest.StartsWith(":", StringComparison.Ordinal))
                    rest = rest.Substring(1);
                string countStr = rest;
                string membersJson = string.Empty;
                int colonIdx = rest.IndexOf(':');
                if (colonIdx >= 0)
                {
                    countStr = rest.Substring(0, colonIdx);
                    membersJson = rest.Substring(colonIdx + 1);
                }
                int.TryParse(countStr, out int count);
                ApplyMembersFromPong(membersJson, count);
                return;
            }

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
                        UpsertMember(uid, nickname);
                        OnUserJoined?.Invoke(uid, nickname);
                        return;
                    }

                    if (type == "__leave__")
                    {
                        string uid = json.Value<string>("uid") ?? "";
                        RemoveMember(uid);
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
