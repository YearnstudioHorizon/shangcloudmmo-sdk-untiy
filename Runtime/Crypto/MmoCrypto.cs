using System;
using System.Buffers;
using System.Security.Cryptography;

namespace ShangCloud.MMO.Crypto
{
    public static class MmoCrypto
    {
        public const int SeedSize = 32;
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int TagSize = 16;
        public const int TimestampSize = 8;
        public const int MinPacketSize = NonceSize + TimestampSize + TagSize; // 36

        private static IMmoCryptoProvider _provider;

        public static void SetProvider(IMmoCryptoProvider provider)
        {
            _provider = provider;
        }

        public static byte[] GenerateSeed()
        {
            if (_provider != null) return _provider.GenerateSeed();
            return DefaultGenerateSeed();
        }

        public static byte[] DeriveKey(byte[] seed)
        {
            if (_provider != null) return _provider.DeriveKey(seed);
            return DefaultDeriveKey(seed);
        }

        /// <summary>
        /// Encrypts data. Output: [12B nonce][ciphertext(8B timestamp + data)][16B tag].
        /// Returns bytes written to outputBuffer.
        /// Required outputBuffer size: NonceSize + TimestampSize + data.Length + TagSize
        /// </summary>
        public static int Encrypt(byte[] key, ReadOnlySpan<byte> data, byte[] outputBuffer)
        {
            if (_provider != null) return _provider.Encrypt(key, data, outputBuffer);
            return DefaultEncrypt(key, data, outputBuffer);
        }

        /// <summary>
        /// Decrypts packet. Strips 8B timestamp. Returns bytes written to outputBuffer, or -1 on failure.
        /// Required outputBuffer size: packet.Length - NonceSize - TagSize - TimestampSize
        /// </summary>
        public static int Decrypt(byte[] key, ReadOnlySpan<byte> packet, byte[] outputBuffer)
        {
            if (_provider != null) return _provider.Decrypt(key, packet, outputBuffer);
            return DefaultDecrypt(key, packet, outputBuffer);
        }

        public static int GetEncryptedSize(int dataLength)
        {
            return NonceSize + TimestampSize + dataLength + TagSize;
        }

        public static int GetDecryptedDataSize(int packetLength)
        {
            return packetLength - NonceSize - TagSize - TimestampSize;
        }

        // --- Default implementations using System.Security.Cryptography.AesGcm ---

#if UNITY_WEBGL
        private static byte[] DefaultGenerateSeed()
        {
            throw new PlatformNotSupportedException(
                "AesGcm is not supported on WebGL. Call MmoCrypto.SetProvider() with a custom IMmoCryptoProvider.");
        }

        private static byte[] DefaultDeriveKey(byte[] seed)
        {
            throw new PlatformNotSupportedException(
                "AesGcm is not supported on WebGL. Call MmoCrypto.SetProvider() with a custom IMmoCryptoProvider.");
        }

        private static int DefaultEncrypt(byte[] key, ReadOnlySpan<byte> data, byte[] outputBuffer)
        {
            throw new PlatformNotSupportedException(
                "AesGcm is not supported on WebGL. Call MmoCrypto.SetProvider() with a custom IMmoCryptoProvider.");
        }

        private static int DefaultDecrypt(byte[] key, ReadOnlySpan<byte> packet, byte[] outputBuffer)
        {
            throw new PlatformNotSupportedException(
                "AesGcm is not supported on WebGL. Call MmoCrypto.SetProvider() with a custom IMmoCryptoProvider.");
        }
#else
        private static byte[] DefaultGenerateSeed()
        {
            var seed = new byte[SeedSize];
            RandomNumberGenerator.Fill(seed);
            return seed;
        }

        private static byte[] DefaultDeriveKey(byte[] seed)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(seed);
        }

        private static int DefaultEncrypt(byte[] key, ReadOnlySpan<byte> data, byte[] outputBuffer)
        {
            int ptLen = TimestampSize + data.Length;
            int totalLen = NonceSize + ptLen + TagSize;

            // Generate nonce -> outputBuffer[0..12)
            var nonce = outputBuffer.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            // Build plaintext: [8B timestamp BE][data]
            byte[] ptBuffer = ArrayPool<byte>.Shared.Rent(ptLen);
            try
            {
                var pt = ptBuffer.AsSpan(0, ptLen);
                long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                BigEndianHelper.WriteU64BE(pt, (ulong)ms);
                data.CopyTo(pt.Slice(TimestampSize));

                // AES-256-GCM encrypt
                var ciphertext = outputBuffer.AsSpan(NonceSize, ptLen);
                var tag = outputBuffer.AsSpan(NonceSize + ptLen, TagSize);

                using var aes = new AesGcm(key);
                aes.Encrypt(nonce, pt, ciphertext, tag);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ptBuffer);
            }

            return totalLen;
        }

        private static int DefaultDecrypt(byte[] key, ReadOnlySpan<byte> packet, byte[] outputBuffer)
        {
            if (packet.Length < MinPacketSize)
                return -1;

            var nonce = packet.Slice(0, NonceSize);
            int ctLen = packet.Length - NonceSize - TagSize;
            var ciphertext = packet.Slice(NonceSize, ctLen);
            var tag = packet.Slice(packet.Length - TagSize, TagSize);

            byte[] ptBuffer = ArrayPool<byte>.Shared.Rent(ctLen);
            try
            {
                var pt = ptBuffer.AsSpan(0, ctLen);

                using var aes = new AesGcm(key);
                try
                {
                    aes.Decrypt(nonce, ciphertext, tag, pt);
                }
                catch (CryptographicException)
                {
                    return -1;
                }

                if (ctLen <= TimestampSize)
                    return -1;

                int dataLen = ctLen - TimestampSize;
                pt.Slice(TimestampSize, dataLen).CopyTo(outputBuffer.AsSpan(0, dataLen));
                return dataLen;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ptBuffer);
            }
        }
#endif
    }
}
