using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ShangCloud.MMO.Api
{
    [Serializable]
    public class MmoNewRoomResponse
    {
        [JsonProperty("connect_key")] public string ConnectKey;
        [JsonProperty("edge_url")] public string EdgeUrl;
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("protocol")] public string Protocol;
    }

    [Serializable]
    public class MmoJoinRoomResponse
    {
        [JsonProperty("connect_key")] public string ConnectKey;
        [JsonProperty("edge_url")] public string EdgeUrl;
        [JsonProperty("room_id")] public string RoomId;
        [JsonProperty("protocol")] public string Protocol;
        [JsonProperty("assigned_uid")] public string AssignedUid;
    }

    [Serializable]
    internal class MmoStatusResponse
    {
        [JsonProperty("status")] public string Status;
        [JsonProperty("message")] public string Message;
    }

    [Serializable]
    internal class MmoRoomDataResponse
    {
        [JsonProperty("extra_data")] public Dictionary<string, object> ExtraData;
    }

    [Serializable]
    internal class MmoUserCountResponse
    {
        [JsonProperty("user_count")] public int UserCount;
    }

    [Serializable]
    internal class MmoRoomConfigRequest
    {
        [JsonProperty("allow_multi_login")] public bool AllowMultiLogin;
    }

    [Serializable]
    internal class MmoRoomDataSetRequest
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("value")] public object Value;
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string Type;
    }

    [Serializable]
    internal class MmoRoomDataDeleteRequest
    {
        [JsonProperty("key")] public string Key;
    }

    [Serializable]
    internal class MmoKickRequest
    {
        [JsonProperty("target_uid")] public string TargetUid;
    }
}
