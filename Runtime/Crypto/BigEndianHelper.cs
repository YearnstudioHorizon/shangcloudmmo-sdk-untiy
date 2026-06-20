using System;
using System.Buffers.Binary;

namespace ShangCloud.MMO.Crypto
{
    public static class BigEndianHelper
    {
        public static void WriteU32BE(byte[] dst, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(offset, 4), value);
        }

        public static void WriteU64BE(byte[] dst, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst.AsSpan(offset, 8), value);
        }

        public static uint ReadU32BE(byte[] src, int offset)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(offset, 4));
        }

        public static ulong ReadU64BE(byte[] src, int offset)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(src.AsSpan(offset, 8));
        }

        public static void WriteU32BE(Span<byte> dst, uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dst, value);
        }

        public static void WriteU64BE(Span<byte> dst, ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(dst, value);
        }

        public static uint ReadU32BE(ReadOnlySpan<byte> src)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(src);
        }

        public static ulong ReadU64BE(ReadOnlySpan<byte> src)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(src);
        }
    }
}
