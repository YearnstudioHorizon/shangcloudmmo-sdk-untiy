using System;
using ShangCloud.MMO.Threading;

namespace ShangCloud.MMO.Transport
{
    public interface IMmoTransport : IDisposable
    {
        MmoConnectionState State { get; }

        void Connect(string host, int port, string connectKey);
        void Disconnect();
        void Poll(float deltaTime);
        void Send(byte[] data, int length);
        void SetMessageQueue(MmoMessageQueue queue);

        event Action OnConnected;
        event Action OnDisconnected;
        event Action<string> OnError;
        event Action OnServerClosed;
    }
}
