using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ShangCloud.MMO.Api
{
    public class ShangCloudApiClient : IDisposable
    {
        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;

        public string AccessToken { get; set; }
        public string TokenType { get; set; } = "Bearer";

        public ShangCloudApiClient(string baseUrl = "https://api.yearnstudio.cn")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>
        /// Creates a new MMO room. The caller becomes the room owner and joins automatically.
        /// </summary>
        public async Task<MmoNewRoomResponse> NewRoomAsync(string protocol = "tcp")
        {
            string body = await PostAsync("/api/mmo/room/new", "{}", protocol: protocol);
            return JsonConvert.DeserializeObject<MmoNewRoomResponse>(body);
        }

        /// <summary>
        /// Joins an existing MMO room. Each call generates an independent connect_key.
        /// </summary>
        public async Task<MmoJoinRoomResponse> JoinRoomAsync(string roomId, string protocol = "tcp")
        {
            string body = await PostAsync("/api/mmo/room/join", "{}", roomId: roomId, protocol: protocol);
            return JsonConvert.DeserializeObject<MmoJoinRoomResponse>(body);
        }

        /// <summary>
        /// Sets room configuration. Only the room owner can call this.
        /// </summary>
        public async Task SetRoomConfigAsync(string roomId, bool allowMultiLogin)
        {
            var req = new MmoRoomConfigRequest { AllowMultiLogin = allowMultiLogin };
            await PostAsync("/api/mmo/room/config", JsonConvert.SerializeObject(req), roomId: roomId);
        }

        /// <summary>
        /// Sets a key-value pair in the room's extra data. Only the room owner can call this.
        /// </summary>
        public async Task SetRoomDataAsync(string roomId, string key, object value, string type = null)
        {
            var req = new MmoRoomDataSetRequest { Key = key, Value = value, Type = type };
            await PostAsync("/api/mmo/room/data/set", JsonConvert.SerializeObject(req), roomId: roomId);
        }

        /// <summary>
        /// Retrieves all extra data stored in the room.
        /// </summary>
        public async Task<Dictionary<string, object>> GetRoomDataAsync(string roomId)
        {
            string body = await PostAsync("/api/mmo/room/data/get", "{}", roomId: roomId);
            var resp = JsonConvert.DeserializeObject<MmoRoomDataResponse>(body);
            return resp?.ExtraData ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Deletes a key from the room's extra data. Only the room owner can call this.
        /// </summary>
        public async Task DeleteRoomDataAsync(string roomId, string key)
        {
            var req = new MmoRoomDataDeleteRequest { Key = key };
            await PostAsync("/api/mmo/room/data/delete", JsonConvert.SerializeObject(req), roomId: roomId);
        }

        /// <summary>
        /// Kicks a user from the room. Only the room owner can call this. Cannot kick self.
        /// </summary>
        public async Task KickUserAsync(string roomId, string targetUid)
        {
            var req = new MmoKickRequest { TargetUid = targetUid };
            await PostAsync("/api/mmo/room/kick", JsonConvert.SerializeObject(req), roomId: roomId);
        }

        /// <summary>
        /// Returns the current number of users in the room.
        /// </summary>
        public async Task<int> GetRoomUserCountAsync(string roomId)
        {
            string body = await PostAsync("/api/mmo/room/usercount", "{}", roomId: roomId);
            var resp = JsonConvert.DeserializeObject<MmoUserCountResponse>(body);
            return resp?.UserCount ?? 0;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private async Task<string> PostAsync(string path, string jsonBody,
            string roomId = null, string protocol = null)
        {
            var url = _baseUrl + path;
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Authorization", $"{TokenType} {AccessToken}");

            if (!string.IsNullOrEmpty(roomId))
                request.Headers.TryAddWithoutValidation("X-MMO-Room", roomId);

            if (!string.IsNullOrEmpty(protocol))
                request.Headers.TryAddWithoutValidation("X-MMO-Protoctl", protocol);

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new ShangCloudApiException((int)response.StatusCode, responseBody);
            }

            return responseBody;
        }
    }
}
