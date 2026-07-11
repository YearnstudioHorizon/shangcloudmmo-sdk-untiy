# ShangCloud MMO Unity SDK

适用于 Unity 的 ShangCloudMMO 实时通信 SDK，提供与主控服务器的 HTTP API 通信以及与边缘节点的加密实时通信（TCP/UDP/WebSocket）。

## 环境要求

- Unity 2021.2 或更高版本
- .NET Standard 2.1（项目设置 → Player → Api Compatibility Level）
- 依赖包：`com.unity.nuget.newtonsoft-json`（通过 UPM 自动安装）

## 安装

### 方式一：本地路径安装

1. 将 `shangcloud-sdk-mmo-unity` 文件夹放到项目外的任意目录
2. 在 Unity 中打开 **Window → Package Manager**
3. 点击左上角 **+** → **Add package from disk...**
4. 选择 `shangcloud-sdk-mmo-unity/package.json`

### 方式二：通过 git URL 安装

在 `Packages/manifest.json` 的 `dependencies` 中添加：

```json
"cn.yearnstudio.shangcloud.mmo": "https://your-git-url.git#path=shangcloud-sdk-mmo-unity"
```

## 架构概览

SDK 分为两个独立部分：

```
┌──────────────────────────┐      ConfigureFromApiResponse()      ┌──────────────────────────┐
│    Part 1: API 客户端     │  ──────────────────────────────────▶  │  Part 2: MMO 实时通信     │
│  ShangCloudApiClient      │    connect_key + edge_url + protocol │  ShangCloudMMO            │
│  (HTTP POST 请求)         │                                      │  (TCP/UDP/WebSocket)      │
└──────────────────────────┘                                      └──────────────────────────┘
```

- **API 客户端**：负责房间的创建、加入、配置、数据管理等 HTTP 接口调用
- **MMO 实时通信**：负责与边缘节点的 AES-256-GCM 加密通信，支持 TCP、UDP、WebSocket 三种协议

## 快速开始

### 1. 创建房间并连接

```csharp
using UnityEngine;
using ShangCloud.MMO;
using ShangCloud.MMO.Api;

public class MyGame : MonoBehaviour
{
    [SerializeField] private ShangCloudMMO mmo;

    private ShangCloudApiClient _api;

    async void Start()
    {
        // 初始化 API 客户端
        _api = new ShangCloudApiClient("https://api.yearnstudio.cn");
        _api.AccessToken = "your_access_token";
        _api.TokenType = "Bearer";

        // 注册事件
        mmo.OnConnected += () => Debug.Log("已连接");
        mmo.OnMessageReceived += msg => Debug.Log($"收到消息: {msg}");
        mmo.OnUserJoined += (uid, nick) => Debug.Log($"{nick}({uid}) 加入房间");
        mmo.OnUserLeft += uid => Debug.Log($"{uid} 离开房间");
        mmo.OnConnectionError += err => Debug.LogError($"连接错误: {err}");
        mmo.OnDisconnected += () => Debug.Log("已断开");

        // 创建房间（调用者自动成为房主）
        var room = await _api.NewRoomAsync("tcp");
        Debug.Log($"房间已创建: {room.RoomId}");

        // 将 API 返回结果传递给通信组件
        mmo.ConfigureFromApiResponse(room.ConnectKey, room.EdgeUrl, room.Protocol);

        // 连接边缘节点
        mmo.ConnectToEdge();
    }

    void OnDestroy()
    {
        _api?.Dispose();
    }
}
```

### 2. 加入已有房间

```csharp
var room = await _api.JoinRoomAsync("房间ID", "tcp");
mmo.ConfigureFromApiResponse(room.ConnectKey, room.EdgeUrl, room.Protocol);
mmo.ConnectToEdge();
```

### 3. 发送和接收消息

```csharp
// 连接成功后发送加入消息（封装版，等价于手写 __join__ JSON）
mmo.OnConnected += () =>
{
    mmo.SendJoinAnnouncement("player1", "玩家一");
};

// 发送广播消息（封装版，wire：{"uid","message","extra"}，无 type 字段）
mmo.SendBroadcast("player1", "Hello World", "");

// 发送同步变量（封装版，wire：{"type":"__sync_var__","uid","vars","interp"}）
// interp 列表中的变量名，接收端应做插帧平滑
mmo.SendSyncVar("player1", new Dictionary<string, object>
{
    { "x", transform.position.x },
    { "y", transform.position.y },
}, new[] { "x", "y" });

// 发送原始文本/二进制（底层接口，不经过封装）
mmo.SendMessage("Hello World");
byte[] data = new byte[] { 0x01, 0x02, 0x03 };
mmo.SendRaw(data, data.Length);
```

### 4. 接收广播与同步变量（封装事件）

```csharp
mmo.OnBroadcastReceived += (uid, message, extra) =>
    Debug.Log($"广播 {uid}: {message} (extra={extra})");

mmo.OnSyncVarReceived += (uid, vars, interp) =>
{
    Debug.Log($"同步变量 {uid}: {vars.Count} 项，插帧: [{string.Join(",", interp)}]");
    // vars: IDictionary<string,string>，interp: IReadOnlyList<string>
    if (vars.TryGetValue("x", out var xStr) && float.TryParse(xStr, out float x))
        // 应用 x ...
        Debug.Log($"x = {x}");
};

// 逐帧平滑推进（移植自 core.js 的 _ensureInterpLoop）
mmo.OnSyncVarInterpolated += (uid, varName, value) =>
{
    // 在此回写场景对象，例如移动对应 uid 的克隆体
    // if (varName == "x") clone.position = new Vector3((float)value, clone.position.y, 0);
};

// 也可在任意时刻直接读取平滑后的值
double currentX = mmo.GetSyncVar("player1", "x");
```

## API 客户端详细用法

`ShangCloudApiClient` 是一个纯 C# 类（不依赖 MonoBehaviour），所有方法均为 `async Task` 异步接口。

```csharp
var api = new ShangCloudApiClient("https://api.yearnstudio.cn");
api.AccessToken = "your_token";
api.TokenType = "Bearer";
```

### 房间管理

```csharp
// 创建房间，protocol 可选 "tcp" / "websocket"
MmoNewRoomResponse room = await api.NewRoomAsync("tcp");

// 加入房间
MmoJoinRoomResponse joined = await api.JoinRoomAsync(roomId, "tcp");

// 设置房间配置（仅房主）
await api.SetRoomConfigAsync(roomId, allowMultiLogin: false);

// 踢出用户（仅房主，不能踢自己）
await api.KickUserAsync(roomId, targetUid: "12345");

// 查询房间人数
int count = await api.GetRoomUserCountAsync(roomId);
```

### 房间数据（键值存储）

```csharp
// 设置数据（仅房主），type 可选 "number" / "string" / "boolean"
await api.SetRoomDataAsync(roomId, "score", 100, "number");
await api.SetRoomDataAsync(roomId, "is_started", true, "boolean");

// 获取所有数据
Dictionary<string, object> data = await api.GetRoomDataAsync(roomId);

// 删除数据（仅房主）
await api.DeleteRoomDataAsync(roomId, "score");
```

### 错误处理

```csharp
try
{
    var room = await api.NewRoomAsync();
}
catch (ShangCloudApiException ex)
{
    Debug.LogError($"HTTP {ex.StatusCode}: {ex.ResponseBody}");
    // 400 = 参数无效
    // 401 = token 无效
    // 403 = 权限不足
    // 404 = 房间不存在
}
```

## ShangCloudMMO 组件

`ShangCloudMMO` 是一个 `MonoBehaviour`，可以直接拖拽到 GameObject 上使用。

### Inspector 属性

| 属性 | 说明 |
|------|------|
| Protocol | 通信协议：TCP / UDP / WebSocket |
| Connect Key | 由 API 返回的连接密钥 |
| Edge Host | 边缘节点主机地址 |
| Edge Port | 边缘节点端口 |
| Edge Url | WebSocket 完整 URL（仅 WebSocket 协议使用） |

### 事件

| 事件 | 签名 | 说明 |
|------|------|------|
| `OnConnected` | `Action` | 连接成功，已通过认证 |
| `OnDisconnected` | `Action` | 连接断开 |
| `OnConnectionError` | `Action<string>` | 连接错误，参数为错误描述 |
| `OnMessageReceived` | `Action<string>` | 收到未识别为广播/同步变量的业务消息 |
| `OnRawMessageReceived` | `Action<byte[], int>` | 收到二进制消息，参数为缓冲区和有效长度 |
| `OnBroadcastReceived` | `Action<string,string,string>` | 收到广播消息 `(uid, message, extra)`（wire：`{"uid","message","extra"}`） |
| `OnSyncVarReceived` | `Action<string,IDictionary<string,string>,IReadOnlyList<string>>` | 收到同步变量 `(uid, vars, interp)`（wire：`__sync_var__`） |
| `OnSyncVarInterpolated` | `Action<string,string,double>` | 插帧引擎逐帧推进时触发 `(uid, varName, value)`，回写场景对象即可（移植自 core.js 的 `_ensureInterpLoop`） |
| `OnUserJoined` | `Action<string, string>` | 用户加入房间，参数为 uid 和 nickname |
| `OnUserLeft` | `Action<string>` | 用户离开房间，参数为 uid |
| `OnServerClosed` | `Action` | 服务端主动关闭连接 |

### 方法

| 方法 | 说明 |
|------|------|
| `ConfigureFromApiResponse(connectKey, edgeUrl, protocol)` | 从 API 响应配置连接参数 |
| `ConnectToEdge()` | 连接到边缘节点 |
| `DisconnectFromEdge()` | 断开连接 |
| `SendMessage(string)` | 发送原始文本消息（明文帧，不经过封装） |
| `SendRaw(byte[], int)` | 发送二进制数据 |
| `SendBroadcast(uid, message, extra)` | 封装广播，wire：`{"uid","message","extra"}` |
| `SendSyncVar(uid, vars, interp)` | 封装同步变量，wire：`{"type":"__sync_var__","uid","vars","interp"}` |
| `SendJoinAnnouncement(uid, nickname)` | 封装加入通知，wire：`{"type":"__join__","uid","nickname"}` |
| `GetSyncVar(uid, varName) -> double` | 读取插帧变量平滑后的当前值（移植自 core.js 的插帧引擎） |
| `GetSyncVarRaw(uid, varName) -> string` | 读取同步变量原始值（不做插帧） |
| `ClearSyncVarState(uid)` | 清理指定 uid 的插帧状态（玩家离开时调用） |

## 通信协议

### 安全机制

所有协议均使用相同的加密方案：

- **算法**：AES-256-GCM
- **密钥派生**：客户端生成 32 字节随机 Seed，通过 SHA-256 派生 AES 密钥
- **载荷结构**：`[12B Nonce][AES-GCM 密文(8B 时间戳ms + 实际数据 + 16B Tag)]`
- **防重放**：20 秒滑动窗口

### TCP

使用长度前缀帧解决粘包：`[4B 大端长度][加密载荷]`

连接流程：发送 32B Seed → 发送加密的 connect_key → 等待 `__auth_ok__` → 已连接

### UDP

使用 connectId 进行会话绑定：`[8B connectId 大端][加密载荷]`

连接流程：发送 `[8B connectId=0][32B Seed][加密 connect_key]` → 接收 `__auth_ok__` 并获取 connectId → 已连接

支持 NAT 恢复：15 秒无数据时自动重新绑定 Socket（保留 connectId 和密钥）。

### WebSocket

通过 `System.Net.WebSockets.ClientWebSocket` 实现。加密方式与 TCP/UDP 相同，WebSocket 自身处理帧边界，无需长度前缀。

连接流程：WebSocket 握手 → 发送 32B Seed → 发送加密 connect_key → 等待 `__auth_ok__` → 已连接

### 心跳

客户端每 3 秒自动发送 `__hb__` 心跳包，无需手动管理。

## 性能优化

### GC 优化（ArrayPool）

SDK 在所有高频网络通信路径中使用 `System.Buffers.ArrayPool<byte>.Shared` 池化缓冲区，避免在 MMO 场景下因频繁 `new byte[]` 导致的 GC 抖动和帧率掉落。

- 网络接收/发送缓冲区均从 ArrayPool 租用
- `MmoMessage` 持有租用的 buffer 引用，主线程消费后自动归还
- `MmoCrypto` 加解密操作写入调用方提供的 buffer，不产生内部分配

### 线程模型

```
Unity 主线程                          后台网络线程
┌─────────────────────┐              ┌─────────────────────┐
│ ShangCloudMMO.Update│              │ TCP: Stream.Read()  │
│  ├─ transport.Poll()│              │ UDP: UdpClient.Recv │
│  ├─ DrainAll() 消息  │◄── 消息队列 ──│ WS:  ReceiveAsync() │
│  ├─ 触发 C# 事件     │  (线程安全)   │                     │
│  └─ msg.Return()    │              │ 解密 → 入队           │
└─────────────────────┘              └─────────────────────┘
```

所有 C# 事件均在 Unity 主线程触发，可安全调用 Unity API。

## 平台兼容性

| 平台 | TCP | UDP | WebSocket | 说明 |
|------|-----|-----|-----------|------|
| Windows / macOS / Linux | ✅ | ✅ | ✅ | 完整支持 |
| Android | ✅ | ✅ | ✅ | 完整支持 |
| iOS | ✅ | ✅ | ✅ | 需 .NET Standard 2.1 |
| WebGL | ❌ | ❌ | ❌ | 不支持（见下方说明） |

### WebGL 限制

`System.Security.Cryptography.AesGcm` 和 `System.Net.Sockets` 在 WebGL 平台不可用。SDK 通过 `#if !UNITY_WEBGL` 编译隔离相关代码。

如需在 WebGL 平台使用，需要：

1. 实现 `IMmoCryptoProvider` 接口，通过 `.jslib` 桥接浏览器 `Web Crypto API`
2. 实现 WebSocket 的 `.jslib` 桥接（使用浏览器原生 WebSocket）
3. 在启动时调用 `MmoCrypto.SetProvider(yourProvider)` 注入自定义加解密实现

## 命名空间

| 命名空间 | 说明 |
|----------|------|
| `ShangCloud.MMO` | 主组件 `ShangCloudMMO`，协议和状态枚举 |
| `ShangCloud.MMO.Api` | HTTP API 客户端和数据模型 |
| `ShangCloud.MMO.Transport` | 传输层接口和 TCP/UDP/WebSocket 实现 |
| `ShangCloud.MMO.Crypto` | AES-256-GCM 加解密、密钥派生、大端序工具 |
| `ShangCloud.MMO.Threading` | 线程安全消息队列 |

## 典型流程

```
1. 房主创建房间
   api.NewRoomAsync("tcp")  →  拿到 connect_key + edge_url + room_id

2. 其他玩家加入房间
   api.JoinRoomAsync(roomId, "tcp")  →  拿到 connect_key + edge_url

3. 配置并连接边缘节点
   mmo.ConfigureFromApiResponse(connectKey, edgeUrl, protocol)
   mmo.ConnectToEdge()

4. 连接成功后发送加入消息
   mmo.SendMessage("{\"type\":\"__join__\",\"uid\":\"...\",\"nickname\":\"...\"}")

5. 房间内操作（通过 API 客户端）
   - 设置房间数据：api.SetRoomDataAsync(...)
   - 读取房间数据：api.GetRoomDataAsync(...)
   - 踢人：api.KickUserAsync(...)
   - 查人数：api.GetRoomUserCountAsync(...)

6. 断开连接
   mmo.DisconnectFromEdge()
```

## 许可证

MIT

## 备注

此项目由`Claude Opus 4.6`生成
