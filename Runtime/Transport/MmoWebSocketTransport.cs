#if !UNITY_WEBGL
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShangCloud.MMO.Crypto;
using ShangCloud.MMO.Threading;

namespace ShangCloud.MMO.Transport
{
    public class MmoWebSocketTransport : MmoTransportBase
    {
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private string _wsUrl;

        public void Connect(string wsUrl, string connectKey)
        {
            _connectKey = connectKey;
            _wsUrl = wsUrl;
            _heartbeatTimer = 0f;
            _state = MmoConnectionState.Connecting;

            _cts = new CancellationTokenSource();
            _ = ConnectAndReceiveAsync();
        }

        public override void Connect(string host, int port, string connectKey)
        {
            string url = $"ws://{host}:{port}/ws";
            Connect(url, connectKey);
        }

        public override void Disconnect()
        {
            _state = MmoConnectionState.Disconnected;
            _cts?.Cancel();
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                       .GetAwaiter().GetResult();
            }
            catch { }
            _ws?.Dispose();
            _ws = null;
        }

        public override void Poll(float deltaTime)
        {
            if (_state == MmoConnectionState.Connected)
            {
                _heartbeatTimer += deltaTime;
                if (_heartbeatTimer >= HeartbeatInterval)
                {
                    _heartbeatTimer = 0f;
                    SendHeartbeat();
                }
            }
        }

        public override void Send(byte[] data, int length)
        {
            if (_state != MmoConnectionState.Connected) return;

            int encSize = MmoCrypto.GetEncryptedSize(length);
            byte[] encBuffer = ArrayPool<byte>.Shared.Rent(encSize);
            try
            {
                int written = EncryptData(data.AsSpan(0, length), encBuffer);
                SendBinaryAsync(encBuffer, written).GetAwaiter().GetResult();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encBuffer);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _ws?.Dispose();
            _sendLock?.Dispose();
        }

        private async Task ConnectAndReceiveAsync()
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
                _state = MmoConnectionState.Handshake;

                // Step 1: Send 32-byte seed as binary message
                byte[] seed = MmoCrypto.GenerateSeed();
                _aesKey = MmoCrypto.DeriveKey(seed);
                await SendBinaryAsync(seed, seed.Length);

                // Step 2: Send encrypted connect_key
                byte[] keyBytes = Encoding.UTF8.GetBytes(_connectKey);
                int encSize = MmoCrypto.GetEncryptedSize(keyBytes.Length);
                byte[] encBuffer = ArrayPool<byte>.Shared.Rent(encSize);
                try
                {
                    int written = EncryptData(keyBytes, encBuffer);
                    await SendBinaryAsync(encBuffer, written);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(encBuffer);
                }
                _state = MmoConnectionState.Authenticating;

                // Step 3: Receive loop
                await ReceiveLoopAsync();
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                if (_disposed || _state == MmoConnectionState.Disconnected) return;
                _state = MmoConnectionState.Error;
                RaiseError($"WebSocket error: {ex.Message}");
                RaiseDisconnected();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] recvBuffer = ArrayPool<byte>.Shared.Rent(MaxFrameSize);
            try
            {
                while (!_cts.IsCancellationRequested && _state != MmoConnectionState.Disconnected)
                {
                    WebSocketReceiveResult result;
                    int totalReceived = 0;

                    do
                    {
                        result = await _ws.ReceiveAsync(
                            new ArraySegment<byte>(recvBuffer, totalReceived, recvBuffer.Length - totalReceived),
                            _cts.Token);
                        totalReceived += result.Count;
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _state = MmoConnectionState.Disconnected;
                        RaiseServerClosed();
                        RaiseDisconnected();
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary || totalReceived == 0)
                        continue;

                    // Decrypt
                    int maxDecrypted = MmoCrypto.GetDecryptedDataSize(totalReceived);
                    if (maxDecrypted <= 0) continue;

                    byte[] decBuffer = ArrayPool<byte>.Shared.Rent(maxDecrypted);
                    int decLen = DecryptData(recvBuffer.AsSpan(0, totalReceived), decBuffer);

                    if (decLen <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(decBuffer);
                        continue;
                    }

                    string msg = Encoding.UTF8.GetString(decBuffer, 0, decLen);
                    ArrayPool<byte>.Shared.Return(decBuffer);

                    if (msg == "__auth_ok__")
                    {
                        _state = MmoConnectionState.Connected;
                        RaiseConnected();
                    }
                    else if (msg == "__hb__")
                    {
                        // silently consumed
                    }
                    else if (msg == "__closed__")
                    {
                        _state = MmoConnectionState.Disconnected;
                        RaiseServerClosed();
                        RaiseDisconnected();
                        break;
                    }
                    else
                    {
                        _messageQueue?.Enqueue(MmoMessage.CreateText(msg));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recvBuffer);
            }

            if (_state != MmoConnectionState.Error && _state != MmoConnectionState.Disconnected)
            {
                _state = MmoConnectionState.Disconnected;
                RaiseDisconnected();
            }
        }

        private async Task SendBinaryAsync(byte[] data, int length)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_ws?.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(
                        new ArraySegment<byte>(data, 0, length),
                        WebSocketMessageType.Binary,
                        true,
                        _cts?.Token ?? CancellationToken.None);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
#endif
