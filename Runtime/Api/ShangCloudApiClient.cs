using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShangCloud.MMO.Api
{
    public class ShangCloudApiClient : IDisposable
    {
        private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
        private const string DefaultDeviceScope = "openid profile mmo";

        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;

        public string AccessToken { get; set; }
        public string TokenType { get; set; } = "Bearer";
        public string RefreshToken { get; set; }
        public string ClientId { get; set; }

        public ShangCloudApiClient(string baseUrl = "https://api.yearnstudio.cn")
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // 避免部分服务端对 Expect: 100-continue 处理异常
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;
        }

        /// <summary>
        /// 用当前 AccessToken 调用 GET /oauth/userinfo，用于确认 token 是否被 OAuth 端接受。
        /// 成功返回 JSON 字符串；失败抛 ShangCloudApiException。
        /// </summary>
        public async Task<string> GetUserInfoAsync()
        {
            return await GetAsync("/oauth/userinfo");
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

        /// <summary>
        /// Starts device authorization (RFC 8628) as a public client with PKCE S256 (no client_secret).
        /// Requires the app to enable "allow public PKCE" in the developer console.
        /// </summary>
        /// <param name="clientId">OAuth client_id</param>
        /// <param name="scope">Space-separated scopes; default includes mmo</param>
        /// <returns>Device codes and verification URIs; keep DeviceCode server-side only</returns>
        public async Task<(DeviceAuthorizationResponse Response, string CodeVerifier)> RequestDeviceAuthorizationAsync(
            string clientId = null, string scope = null)
        {
            clientId = clientId ?? ClientId;
            if (string.IsNullOrEmpty(clientId))
                throw new ArgumentException("client_id is required", nameof(clientId));

            string codeVerifier;
            string codeChallenge;
            MakePkce(out codeVerifier, out codeChallenge);

            var form = new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "scope", string.IsNullOrEmpty(scope) ? DefaultDeviceScope : scope },
                { "code_challenge", codeChallenge },
                { "code_challenge_method", "S256" },
            };

            string body = await FormPostAsync("/oauth/device_authorization", form, throwOnError: true);
            var response = JsonConvert.DeserializeObject<DeviceAuthorizationResponse>(body);
            if (response == null || string.IsNullOrEmpty(response.DeviceCode))
                throw new ShangCloudApiException("Invalid device_authorization response");

            if (response.Interval <= 0) response.Interval = 5;
            if (response.ExpiresIn <= 0) response.ExpiresIn = 900;

            ClientId = clientId;
            return (response, codeVerifier);
        }

        /// <summary>
        /// Polls the token endpoint once for a device_code grant (with PKCE code_verifier).
        /// Returns null while authorization is still pending.
        /// </summary>
        public async Task<OAuthTokenResponse> PollDeviceTokenOnceAsync(
            string deviceCode, string codeVerifier, string clientId = null)
        {
            clientId = clientId ?? ClientId;
            if (string.IsNullOrEmpty(clientId))
                throw new ArgumentException("client_id is required", nameof(clientId));
            if (string.IsNullOrEmpty(deviceCode))
                throw new ArgumentException("device_code is required", nameof(deviceCode));
            if (string.IsNullOrEmpty(codeVerifier))
                throw new ArgumentException("code_verifier is required", nameof(codeVerifier));

            var form = new Dictionary<string, string>
            {
                { "grant_type", DeviceCodeGrantType },
                { "device_code", deviceCode },
                { "client_id", clientId },
                { "code_verifier", codeVerifier },
            };

            var (status, body) = await FormPostRawAsync("/oauth/token", form);
            if (status >= 200 && status < 300)
            {
                var token = JsonConvert.DeserializeObject<OAuthTokenResponse>(body);
                if (token == null || string.IsNullOrEmpty(token.AccessToken))
                    throw new ShangCloudApiException(status, body);
                ApplyTokenResponse(token);
                return token;
            }

            var err = JsonConvert.DeserializeObject<OAuthErrorResponse>(body);
            string error = err?.Error ?? "";
            if (error == "authorization_pending" || error == "slow_down")
                return null;

            throw new ShangCloudApiException(status, body);
        }

        /// <summary>
        /// Full device login: request codes, notify UI via onUserCode, poll until token / timeout / cancel.
        /// Public client + PKCE, no client_secret.
        /// </summary>
        /// <param name="onUserCode">Called with (userCode, verificationUri, verificationUriComplete)</param>
        /// <param name="onPending">Optional; called on each authorization_pending poll</param>
        public async Task<OAuthTokenResponse> LoginWithDeviceAuthAsync(
            string clientId = null,
            string scope = null,
            Action<string, string, string> onUserCode = null,
            Action onPending = null,
            CancellationToken cancellationToken = default)
        {
            var (da, codeVerifier) = await RequestDeviceAuthorizationAsync(clientId, scope);
            onUserCode?.Invoke(da.UserCode, da.VerificationUri, da.VerificationUriComplete);

            int interval = da.Interval > 0 ? da.Interval : 5;
            DateTime deadline = DateTime.UtcNow.AddSeconds(da.ExpiresIn > 0 ? da.ExpiresIn : 900);

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);

                var form = new Dictionary<string, string>
                {
                    { "grant_type", DeviceCodeGrantType },
                    { "device_code", da.DeviceCode },
                    { "client_id", clientId ?? ClientId },
                    { "code_verifier", codeVerifier },
                };

                var (status, body) = await FormPostRawAsync("/oauth/token", form);
                if (status >= 200 && status < 300)
                {
                    var token = JsonConvert.DeserializeObject<OAuthTokenResponse>(body);
                    if (token == null || string.IsNullOrEmpty(token.AccessToken))
                        throw new ShangCloudApiException(status, body);
                    ApplyTokenResponse(token);
                    return token;
                }

                var err = JsonConvert.DeserializeObject<OAuthErrorResponse>(body);
                string error = err?.Error ?? "";
                if (error == "authorization_pending")
                {
                    onPending?.Invoke();
                    continue;
                }
                if (error == "slow_down")
                {
                    interval += 5;
                    continue;
                }

                throw new ShangCloudApiException(status, body);
            }

            throw new ShangCloudApiException("Device authorization timed out (device_code expired)");
        }

        /// <summary>
        /// Refreshes access_token using refresh_token as a public client (client_id only, no secret).
        /// </summary>
        public async Task<OAuthTokenResponse> RefreshAccessTokenAsync(
            string refreshToken = null, string clientId = null)
        {
            clientId = clientId ?? ClientId;
            refreshToken = refreshToken ?? RefreshToken;
            if (string.IsNullOrEmpty(clientId))
                throw new ArgumentException("client_id is required", nameof(clientId));
            if (string.IsNullOrEmpty(refreshToken))
                throw new ArgumentException("refresh_token is required", nameof(refreshToken));

            var form = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", clientId },
            };

            string body = await FormPostAsync("/oauth/token", form, throwOnError: true);
            var token = JsonConvert.DeserializeObject<OAuthTokenResponse>(body);
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                throw new ShangCloudApiException("Invalid refresh_token response");
            if (string.IsNullOrEmpty(token.RefreshToken))
                token.RefreshToken = refreshToken;
            ApplyTokenResponse(token);
            return token;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private void ApplyTokenResponse(OAuthTokenResponse token)
        {
            if (token == null) return;
            if (!string.IsNullOrEmpty(token.AccessToken))
                AccessToken = token.AccessToken.Trim();
            if (!string.IsNullOrEmpty(token.TokenType))
                TokenType = NormalizeTokenType(token.TokenType);
            if (!string.IsNullOrEmpty(token.RefreshToken))
                RefreshToken = token.RefreshToken.Trim();
        }

        private static string NormalizeTokenType(string tokenType)
        {
            if (string.IsNullOrWhiteSpace(tokenType)) return "Bearer";
            tokenType = tokenType.Trim();
            return tokenType.Equals("bearer", StringComparison.OrdinalIgnoreCase) ? "Bearer" : tokenType;
        }

        private void ApplyAuthHeader(HttpRequestMessage request)
        {
            if (string.IsNullOrWhiteSpace(AccessToken))
                throw new ShangCloudApiException(401, "AccessToken is empty; login first");

            // 仅发送 token 本身，不带 Bearer/TokenType（服务端按裸 token 校验）
            string token = AccessToken.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(7).Trim();

            if (!request.Headers.TryAddWithoutValidation("Authorization", token))
                throw new ShangCloudApiException(401, "Failed to set Authorization header");
        }

        internal static void MakePkce(out string codeVerifier, out string codeChallenge)
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            codeVerifier = Base64UrlEncode(bytes);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
                codeChallenge = Base64UrlEncode(hash);
            }
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private async Task<string> FormPostAsync(string path, Dictionary<string, string> form, bool throwOnError)
        {
            var (status, body) = await FormPostRawAsync(path, form);
            if (throwOnError && (status < 200 || status >= 300))
                throw new ShangCloudApiException(status, body);
            return body;
        }

        private async Task<(int Status, string Body)> FormPostRawAsync(string path, Dictionary<string, string> form)
        {
            var url = _baseUrl + path;
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form)
            };

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, responseBody);
        }

        private async Task<string> GetAsync(string path)
        {
            var url = _baseUrl + path;
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuthHeader(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new ShangCloudApiException((int)response.StatusCode, responseBody);
            return responseBody;
        }

        private async Task<string> PostAsync(string path, string jsonBody,
            string roomId = null, string protocol = null)
        {
            var url = _baseUrl + path;
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json")
            };

            ApplyAuthHeader(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrEmpty(roomId))
                request.Headers.TryAddWithoutValidation("X-MMO-Room", roomId);

            // 官方拼写即为 Protoctl（非 Protocol）
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

        /// <summary>解码 JWT payload（不验签），仅用于调试。</summary>
        public static JObject TryDecodeJwtPayload(string jwt)
        {
            if (string.IsNullOrWhiteSpace(jwt)) return null;
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return JObject.Parse(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
