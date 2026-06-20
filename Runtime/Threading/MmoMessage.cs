using System.Buffers;

namespace ShangCloud.MMO.Threading
{
    public struct MmoMessage
    {
        public enum MessageType
        {
            Text,
            Binary
        }

        public MessageType Type;
        public string Text;
        public byte[] RentedBuffer;
        public int Length;

        public static MmoMessage CreateText(string text)
        {
            return new MmoMessage { Type = MessageType.Text, Text = text };
        }

        public static MmoMessage CreateBinary(byte[] rentedBuffer, int length)
        {
            return new MmoMessage
            {
                Type = MessageType.Binary,
                RentedBuffer = rentedBuffer,
                Length = length
            };
        }

        public void Return()
        {
            if (RentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(RentedBuffer);
                RentedBuffer = null;
            }
        }
    }
}
