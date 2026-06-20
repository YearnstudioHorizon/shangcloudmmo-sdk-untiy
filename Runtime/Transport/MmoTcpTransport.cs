using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ShangCloud.MMO.Crypto;
using ShangCloud.MMO.Threading;

namespace ShangCloud.MMO.Transport
{
    public class MmoTcpTransport : MmoTransportBase
    {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _recvThread;
        private readonly object _sendLock = new object();

        public override void Connect(string host, int port, string connectKey)
        {
            _connectKey = connectKey;
            _heartbeatTimer = 0f;
            _state = MmoConnectionState.Connecting;

            _recvThread = new Thread(() => ConnectAndReceive(host, port))
            {
                IsBackground = true,
                Name = "MMO-TCP-Recv"
            };
            _recvThread.Start();
        }

        public override void Disconnect()
        {
            _state = MmoConnectionState.Disconnected;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
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
                SendFrame(encBuffer, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encBuffer);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _stream = null;
            _tcp = null;
        }

        private void ConnectAndReceive(string host, int port)
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();

                // Step 1: Send 32-byte seed (plaintext)
                byte[] seed = MmoCrypto.GenerateSeed();
                _aesKey = MmoCrypto.DeriveKey(seed);
                _stream.Write(seed, 0, seed.Length);
                _state = MmoConnectionState.Handshake;

                // Step 2: Send encrypted connect_key with length-prefix frame
                byte[] keyBytes = Encoding.UTF8.GetBytes(_connectKey);
                int encSize = MmoCrypto.GetEncryptedSize(keyBytes.Length);
                byte[] encBuffer = ArrayPool<byte>.Shared.Rent(encSize);
                try
                {
                    int written = EncryptData(keyBytes, encBuffer);
                    SendFrame(encBuffer, written);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(encBuffer);
                }
                _state = MmoConnectionState.Authenticating;

                // Step 3: Enter receive loop
                ReceiveLoop();
            }
            catch (Exception ex)
            {
                if (_disposed || _state == MmoConnectionState.Disconnected) return;
                _state = MmoConnectionState.Error;
                RaiseError($"TCP connection error: {ex.Message}");
                RaiseDisconnected();
            }
        }

        private void ReceiveLoop()
        {
            byte[] lenBuf = new byte[4];

            while (!_disposed && _state != MmoConnectionState.Disconnected)
            {
                // Read 4-byte length prefix
                if (!ReadExact(lenBuf, 0, 4)) break;

                uint payloadLen = BigEndianHelper.ReadU32BE(lenBuf, 0);
                if (payloadLen > MaxFrameSize)
                {
                    _state = MmoConnectionState.Error;
                    RaiseError("TCP frame length exceeds 1MB limit");
                    break;
                }

                // Read payload into rented buffer
                byte[] encPayload = ArrayPool<byte>.Shared.Rent((int)payloadLen);
                try
                {
                    if (!ReadExact(encPayload, 0, (int)payloadLen))
                    {
                        ArrayPool<byte>.Shared.Return(encPayload);
                        break;
                    }

                    // Decrypt
                    int maxDecrypted = MmoCrypto.GetDecryptedDataSize((int)payloadLen);
                    if (maxDecrypted <= 0)
                    {
                        continue;
                    }

                    byte[] decBuffer = ArrayPool<byte>.Shared.Rent(maxDecrypted);
                    int decLen = DecryptData(encPayload.AsSpan(0, (int)payloadLen), decBuffer);

                    if (decLen <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(decBuffer);
                        continue;
                    }

                    // Check if it's a system message (handled inline) or business message (enqueue)
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
                finally
                {
                    ArrayPool<byte>.Shared.Return(encPayload);
                }
            }

            if (_state != MmoConnectionState.Error && _state != MmoConnectionState.Disconnected)
            {
                _state = MmoConnectionState.Disconnected;
                RaiseDisconnected();
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read;
                try
                {
                    read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                }
                catch
                {
                    return false;
                }
                if (read <= 0) return false;
                totalRead += read;
            }
            return true;
        }

        private void SendFrame(byte[] payload, int length)
        {
            // [4B uint32 BE length][payload]
            byte[] header = new byte[4];
            BigEndianHelper.WriteU32BE(header, 0, (uint)length);

            lock (_sendLock)
            {
                try
                {
                    _stream?.Write(header, 0, 4);
                    _stream?.Write(payload, 0, length);
                }
                catch
                {
                    if (!_disposed && _state != MmoConnectionState.Disconnected)
                    {
                        _state = MmoConnectionState.Error;
                        RaiseError("TCP send failed");
                    }
                }
            }
        }
    }
}
