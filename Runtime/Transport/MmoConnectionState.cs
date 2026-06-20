namespace ShangCloud.MMO.Transport
{
    public enum MmoConnectionState
    {
        Idle,
        Connecting,
        Handshake,
        Authenticating,
        Connected,
        Disconnected,
        Error
    }
}
