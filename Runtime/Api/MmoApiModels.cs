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

    /// <summary>
    /// RFC 8628 device authorization response from POST /oauth/device_authorization.
    /// </summary>
    [Serializable]
    public class DeviceAuthorizationResponse
    {
        [JsonProperty("device_code")] public string DeviceCode;
        [JsonProperty("user_code")] public string UserCode;
        [JsonProperty("verification_uri")] public string VerificationUri;
        [JsonProperty("verification_uri_complete")] public string VerificationUriComplete;
        [JsonProperty("expires_in")] public int ExpiresIn;
        [JsonProperty("interval")] public int Interval;
    }

    /// <summary>
    /// OAuth token response (device_code / refresh_token grants).
    /// </summary>
    [Serializable]
    public class OAuthTokenResponse
    {
        [JsonProperty("access_token")] public string AccessToken;
        [JsonProperty("token_type")] public string TokenType;
        [JsonProperty("expires_in")] public int ExpiresIn;
        [JsonProperty("refresh_token")] public string RefreshToken;
        [JsonProperty("scope")] public string Scope;
        [JsonProperty("id_token")] public string IdToken;
    }

    [Serializable]
    internal class OAuthErrorResponse
    {
        [JsonProperty("error")] public string Error;
        [JsonProperty("error_description")] public string ErrorDescription;
    }
}
