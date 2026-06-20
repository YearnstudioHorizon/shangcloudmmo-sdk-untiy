using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ShangCloud.MMO.Crypto;
using ShangCloud.MMO.Threading;

namespace ShangCloud.MMO.Transport
{
    public class MmoUdpTransport : MmoTransportBase
    {
        private UdpClient _udp;
        private Thread _recvThread;
        private IPEndPoint _remoteEp;
        private IPAddress _resolvedIp;
        private int _edgePort;
        private ulong _connectId;
        private float _timeoutTimer;
        private volatile bool _packetReceived;

        private const float AuthTimeout = 10.0f;
        private const float HbTimeout = 15.0f;
        private const int MaxConnectKeyBytes = 256;

        public override void Connect(string host, int port, string connectKey)
        {
            _connectKey = connectKey;
            _connectId = 0;
            _heartbeatTimer = 0f;
            _timeoutTimer = 0f;
            _edgePort = port;
            _packetReceived = false;

            try
            {
                var addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                {
                    _state = MmoConnectionState.Error;
                    RaiseError($"Failed to resolve hostname: {host}");
                    return;
                }
                _resolvedIp = addresses[0];
            }
            catch (Exception ex)
            {
                _state = MmoConnectionState.Error;
                RaiseError($"DNS resolution failed: {ex.Message}");
                return;
            }

            _state = MmoConnectionState.Connecting;

            _recvThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MMO-UDP-Recv"
            };

            InitSocket();
            SendAuthPacket();
            _state = MmoConnectionState.Authenticating;

            _recvThread.Start();
        }

        public override void Disconnect()
        {
            _state = MmoConnectionState.Disconnected;
            _connectId = 0;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        public override void Poll(float deltaTime)
        {
            if (_state == MmoConnectionState.Authenticating)
            {
                _timeoutTimer += deltaTime;
                if (_timeoutTimer >= AuthTimeout)
                {
                    _state = MmoConnectionState.Error;
                    RaiseError("UDP authentication timed out");
                }
                return;
            }

            if (_state == MmoConnectionState.Connected)
            {
                _heartbeatTimer += deltaTime;
                if (_heartbeatTimer >= HeartbeatInterval)
                {
                    _heartbeatTimer = 0f;
                    SendHeartbeat();
                }

                if (_packetReceived)
                {
                    _timeoutTimer = 0f;
                    _packetReceived = false;
                }
                else
                {
                    _timeoutTimer += deltaTime;
                }

                if (_timeoutTimer >= HbTimeout)
                {
                    ReconnectSocket();
                    _timeoutTimer = 0f;
                }
            }
        }

        public override void Send(byte[] data, int length)
        {
            if (_state != MmoConnectionState.Connected) return;

            int encSize = MmoCrypto.GetEncryptedSize(length);
            byte[] encBuf = ArrayPool<byte>.Shared.Rent(encSize);
            try
            {
                int written = EncryptData(data.AsSpan(0, length), encBuf);

                // [8B connectId BE][encrypted payload]
                int packetSize = 8 + written;
                byte[] packet = ArrayPool<byte>.Shared.Rent(packetSize);
                try
                {
                    BigEndianHelper.WriteU64BE(packet, 0, _connectId);
                    Buffer.BlockCopy(encBuf, 0, packet, 8, written);
                    _udp?.Send(packet, packetSize);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encBuf);
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _udp = null;
        }

        private void InitSocket()
        {
            _udp = new UdpClient(0); // bind to any available local port
            _remoteEp = new IPEndPoint(_resolvedIp, _edgePort);
            _udp.Connect(_remoteEp);
        }

        private void SendAuthPacket()
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(_connectKey);
            if (keyBytes.Length > MaxConnectKeyBytes)
            {
                _state = MmoConnectionState.Error;
                RaiseError("connect_key byte length exceeds 256 bytes");
                return;
            }

            byte[] seed = MmoCrypto.GenerateSeed();
            _aesKey = MmoCrypto.DeriveKey(seed);

            int encSize = MmoCrypto.GetEncryptedSize(keyBytes.Length);
            byte[] encKey = ArrayPool<byte>.Shared.Rent(encSize);
            int encWritten;
            try
            {
                encWritten = MmoCrypto.Encrypt(_aesKey, keyBytes, encKey);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(encKey);
                _state = MmoConnectionState.Error;
                RaiseError("Failed to encrypt connect_key");
                return;
            }

            // [8B connectId=0 BE][32B seed][encrypted connect_key]
            int totalSize = 8 + MmoCrypto.SeedSize + encWritten;
            byte[] authPacket = ArrayPool<byte>.Shared.Rent(totalSize);
            try
            {
                BigEndianHelper.WriteU64BE(authPacket, 0, 0UL);
                Buffer.BlockCopy(seed, 0, authPacket, 8, MmoCrypto.SeedSize);
                Buffer.BlockCopy(encKey, 0, authPacket, 8 + MmoCrypto.SeedSize, encWritten);
                _udp.Send(authPacket, totalSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(authPacket);
                ArrayPool<byte>.Shared.Return(encKey);
            }
        }

        private void ReceiveLoop()
        {
            while (!_disposed && _state != MmoConnectionState.Disconnected)
            {
                try
                {
                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet;
                    try
                    {
                        packet = _udp.Receive(ref remoteEp);
                    }
                    catch (SocketException)
                    {
                        if (_disposed || _state == MmoConnectionState.Disconnected) break;
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (packet.Length < 8) continue;

                    _packetReceived = true;

                    ulong pktConnectId = BigEndianHelper.ReadU64BE(packet, 0);
                    int payloadSize = packet.Length - 8;
                    if (payloadSize <= 0) continue;

                    int maxDecrypted = MmoCrypto.GetDecryptedDataSize(payloadSize);
                    if (maxDecrypted <= 0) continue;

                    byte[] decBuffer = ArrayPool<byte>.Shared.Rent(maxDecrypted);
                    int decLen = DecryptData(packet.AsSpan(8, payloadSize), decBuffer);

                    if (decLen <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(decBuffer);
                        continue;
                    }

                    string msg = Encoding.UTF8.GetString(decBuffer, 0, decLen);
                    ArrayPool<byte>.Shared.Return(decBuffer);

                    if (_state == MmoConnectionState.Authenticating)
                    {
                        _connectId = pktConnectId;
                    }

                    if (msg == "__auth_ok__")
                    {
                        _state = MmoConnectionState.Connected;
                        _timeoutTimer = 0f;
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
                catch (Exception ex)
                {
                    if (_disposed || _state == MmoConnectionState.Disconnected) break;
                    _state = MmoConnectionState.Error;
                    RaiseError($"UDP receive error: {ex.Message}");
                    break;
                }
            }
        }

        private void ReconnectSocket()
        {
            try { _udp?.Close(); } catch { }

            try
            {
                _udp = new UdpClient(0);
                _remoteEp = new IPEndPoint(_resolvedIp, _edgePort);
                _udp.Connect(_remoteEp);
                // Keep _connectId and _aesKey intact
            }
            catch (Exception ex)
            {
                _state = MmoConnectionState.Error;
                RaiseError($"UDP rebind failed during NAT recovery: {ex.Message}");
            }
        }
    }
}
