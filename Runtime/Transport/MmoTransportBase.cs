using System;
using System.Buffers;
using System.Text;
using ShangCloud.MMO.Crypto;
using ShangCloud.MMO.Threading;

namespace ShangCloud.MMO.Transport
{
    public abstract class MmoTransportBase : IMmoTransport
    {
        protected MmoConnectionState _state = MmoConnectionState.Idle;
        protected byte[] _aesKey;
        protected string _connectKey;
        protected MmoMessageQueue _messageQueue;
        protected float _heartbeatTimer;
        protected volatile bool _disposed;

        protected const float HeartbeatInterval = 3.0f;
        protected const int MaxFrameSize = 1024 * 1024; // 1MB

        private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes("__hb__");

        public MmoConnectionState State => _state;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;
        public event Action OnServerClosed;

        public void SetMessageQueue(MmoMessageQueue queue)
        {
            _messageQueue = queue;
        }

        public abstract void Connect(string host, int port, string connectKey);
        public abstract void Disconnect();
        public abstract void Poll(float deltaTime);
        public abstract void Send(byte[] data, int length);

        public virtual void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
            }
        }

        protected int EncryptData(ReadOnlySpan<byte> data, byte[] outputBuffer)
        {
            return MmoCrypto.Encrypt(_aesKey, data, outputBuffer);
        }

        protected int DecryptData(ReadOnlySpan<byte> encrypted, byte[] outputBuffer)
        {
            return MmoCrypto.Decrypt(_aesKey, encrypted, outputBuffer);
        }

        protected void ProcessDecryptedMessage(byte[] buffer, int length)
        {
            if (length <= 0) return;

            string msg = Encoding.UTF8.GetString(buffer, 0, length);

            if (msg == "__auth_ok__")
            {
                _state = MmoConnectionState.Connected;
                RaiseConnected();
                return;
            }

            if (msg == "__hb__")
                return;

            if (msg == "__closed__")
            {
                _state = MmoConnectionState.Disconnected;
                RaiseServerClosed();
                RaiseDisconnected();
                return;
            }

            _messageQueue?.Enqueue(MmoMessage.CreateText(msg));
        }

        protected void SendHeartbeat()
        {
            Send(HeartbeatBytes, HeartbeatBytes.Length);
        }

        protected void RaiseConnected() => OnConnected?.Invoke();
        protected void RaiseDisconnected() => OnDisconnected?.Invoke();
        protected void RaiseError(string error) => OnError?.Invoke(error);
        protected void RaiseServerClosed() => OnServerClosed?.Invoke();
    }
}
