using UnityEngine;
using ShangCloud.MMO;
using ShangCloud.MMO.Api;

/// <summary>
/// Example showing the full API -> Transport flow:
/// 1. Create or join a room via ShangCloudApiClient
/// 2. Configure ShangCloudMMO from the API response
/// 3. Connect to the edge node
/// 4. Send/receive messages
/// </summary>
public class MmoSample : MonoBehaviour
{
    [Header("API Configuration")]
    [SerializeField] private string accessToken = "your_access_token";
    [SerializeField] private string tokenType = "Bearer";
    [SerializeField] private string baseUrl = "https://api.yearnstudio.cn";

    [Header("Room")]
    [SerializeField] private string roomIdToJoin;
    [SerializeField] private string protocol = "tcp";

    [Header("MMO Component")]
    [SerializeField] private ShangCloudMMO mmo;

    private ShangCloudApiClient _api;

    async void Start()
    {
        if (mmo == null)
        {
            mmo = gameObject.AddComponent<ShangCloudMMO>();
        }

        _api = new ShangCloudApiClient(baseUrl);
        _api.AccessToken = accessToken;
        _api.TokenType = tokenType;

        // Subscribe to events
        mmo.OnConnected += OnConnected;
        mmo.OnDisconnected += OnDisconnected;
        mmo.OnConnectionError += OnError;
        mmo.OnMessageReceived += OnMessage;
        mmo.OnUserJoined += OnUserJoined;
        mmo.OnUserLeft += OnUserLeft;
        mmo.OnServerClosed += OnServerClosed;

        try
        {
            if (string.IsNullOrEmpty(roomIdToJoin))
            {
                // Create a new room
                var room = await _api.NewRoomAsync(protocol);
                Debug.Log($"[MMO] Room created: {room.RoomId}");
                mmo.ConfigureFromApiResponse(room.ConnectKey, room.EdgeUrl, room.Protocol);
            }
            else
            {
                // Join existing room
                var room = await _api.JoinRoomAsync(roomIdToJoin, protocol);
                Debug.Log($"[MMO] Joined room: {room.RoomId}");
                mmo.ConfigureFromApiResponse(room.ConnectKey, room.EdgeUrl, room.Protocol);
            }

            mmo.ConnectToEdge();
        }
        catch (ShangCloudApiException ex)
        {
            Debug.LogError($"[MMO] API error: {ex.Message}");
        }
    }

    void OnConnected()
    {
        Debug.Log("[MMO] Connected to edge node!");
        // Send join message
        mmo.SendMessage("{\"type\":\"__join__\",\"uid\":\"player1\",\"nickname\":\"Player One\"}");
    }

    void OnDisconnected()
    {
        Debug.Log("[MMO] Disconnected");
    }

    void OnError(string error)
    {
        Debug.LogError($"[MMO] Error: {error}");
    }

    void OnMessage(string message)
    {
        Debug.Log($"[MMO] Message: {message}");
    }

    void OnUserJoined(string uid, string nickname)
    {
        Debug.Log($"[MMO] User joined: {uid} ({nickname})");
    }

    void OnUserLeft(string uid)
    {
        Debug.Log($"[MMO] User left: {uid}");
    }

    void OnServerClosed()
    {
        Debug.Log("[MMO] Server closed the connection");
    }

    void OnDestroy()
    {
        _api?.Dispose();
    }
}
