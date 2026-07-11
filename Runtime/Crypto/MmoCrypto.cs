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
            FillRandom(seed);
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
            FillRandom(nonce);

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

                try
                {
                    using var aes = new AesGcm(key);
                    aes.Encrypt(nonce, pt, ciphertext, tag);
                }
                catch (Exception ex) when (IsAesGcmUnavailable(ex))
                {
                    EncryptAesGcmFallback(key, nonce, pt, ciphertext, tag);
                }
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

                try
                {
                    try
                    {
                        using var aes = new AesGcm(key);
                        aes.Decrypt(nonce, ciphertext, tag, pt);
                    }
                    catch (Exception ex) when (IsAesGcmUnavailable(ex))
                    {
                        if (!DecryptAesGcmFallback(key, nonce, ciphertext, tag, pt))
                            return -1;
                    }
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

        private static bool IsAesGcmUnavailable(Exception ex)
        {
            return ex is PlatformNotSupportedException || ex is NotSupportedException;
        }

        private static void FillRandom(Span<byte> buffer)
        {
            try
            {
                RandomNumberGenerator.Fill(buffer);
                return;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is NotSupportedException)
            {
            }

            byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                using var rng = RandomNumberGenerator.Create();
                rng.GetBytes(rented);
                rented.AsSpan(0, buffer.Length).CopyTo(buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static void EncryptAesGcmFallback(
            byte[] key,
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> plaintext,
            Span<byte> ciphertext,
            Span<byte> tag)
        {
            using var aes = CreateEcbAes(key);
            using var encryptor = aes.CreateEncryptor();

            byte[] h = new byte[16];
            byte[] zero = new byte[16];
            EncryptBlock(encryptor, zero, h);

            byte[] j0 = BuildJ0(nonce);
            ApplyCtr(encryptor, j0, plaintext, ciphertext);
            WriteGcmTag(encryptor, h, j0, ciphertext, tag);
        }

        private static bool DecryptAesGcmFallback(
            byte[] key,
            ReadOnlySpan<byte> nonce,
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> tag,
            Span<byte> plaintext)
        {
            using var aes = CreateEcbAes(key);
            using var encryptor = aes.CreateEncryptor();

            byte[] h = new byte[16];
            byte[] zero = new byte[16];
            EncryptBlock(encryptor, zero, h);

            byte[] j0 = BuildJ0(nonce);
            byte[] expectedTag = new byte[TagSize];
            WriteGcmTag(encryptor, h, j0, ciphertext, expectedTag);
            if (!FixedTimeEquals(tag, expectedTag))
                return false;

            ApplyCtr(encryptor, j0, ciphertext, plaintext);
            return true;
        }

        private static Aes CreateEcbAes(byte[] key)
        {
            var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            return aes;
        }

        private static byte[] BuildJ0(ReadOnlySpan<byte> nonce)
        {
            byte[] j0 = new byte[16];
            nonce.CopyTo(j0);
            j0[15] = 1;
            return j0;
        }

        private static void ApplyCtr(
            ICryptoTransform encryptor,
            byte[] j0,
            ReadOnlySpan<byte> input,
            Span<byte> output)
        {
            byte[] counter = new byte[16];
            Buffer.BlockCopy(j0, 0, counter, 0, 16);
            byte[] streamBlock = new byte[16];

            int offset = 0;
            while (offset < input.Length)
            {
                IncrementCounter32(counter);
                EncryptBlock(encryptor, counter, streamBlock);

                int blockLen = Math.Min(16, input.Length - offset);
                for (int i = 0; i < blockLen; i++)
                {
                    output[offset + i] = (byte)(input[offset + i] ^ streamBlock[i]);
                }

                offset += blockLen;
            }
        }

        private static void WriteGcmTag(
            ICryptoTransform encryptor,
            byte[] h,
            byte[] j0,
            ReadOnlySpan<byte> ciphertext,
            Span<byte> tag)
        {
            byte[] s = ComputeGHash(h, ciphertext);
            byte[] encryptedJ0 = new byte[16];
            EncryptBlock(encryptor, j0, encryptedJ0);

            for (int i = 0; i < TagSize; i++)
            {
                tag[i] = (byte)(encryptedJ0[i] ^ s[i]);
            }
        }

        private static byte[] ComputeGHash(byte[] h, ReadOnlySpan<byte> ciphertext)
        {
            byte[] y = new byte[16];
            byte[] block = new byte[16];

            int offset = 0;
            while (offset < ciphertext.Length)
            {
                Array.Clear(block, 0, block.Length);
                int blockLen = Math.Min(16, ciphertext.Length - offset);
                ciphertext.Slice(offset, blockLen).CopyTo(block);
                XorInPlace(y, block);
                MultiplyGf128(y, h);
                offset += blockLen;
            }

            Array.Clear(block, 0, block.Length);
            ulong cipherBits = (ulong)ciphertext.Length * 8UL;
            WriteU64BE(block, 8, cipherBits);
            XorInPlace(y, block);
            MultiplyGf128(y, h);

            return y;
        }

        private static void MultiplyGf128(byte[] x, byte[] h)
        {
            byte[] z = new byte[16];
            byte[] v = new byte[16];
            Buffer.BlockCopy(h, 0, v, 0, 16);

            for (int i = 0; i < 128; i++)
            {
                if (GetBit(x, i) != 0)
                    XorInPlace(z, v);

                bool lsb = (v[15] & 1) != 0;
                ShiftRightOne(v);
                if (lsb)
                    v[0] ^= 0xe1;
            }

            Buffer.BlockCopy(z, 0, x, 0, 16);
        }

        private static int GetBit(byte[] data, int bitIndex)
        {
            int byteIndex = bitIndex / 8;
            int bitInByte = 7 - (bitIndex % 8);
            return (data[byteIndex] >> bitInByte) & 1;
        }

        private static void ShiftRightOne(byte[] data)
        {
            int carry = 0;
            for (int i = 0; i < data.Length; i++)
            {
                int nextCarry = data[i] & 1;
                data[i] = (byte)((data[i] >> 1) | (carry << 7));
                carry = nextCarry;
            }
        }

        private static void XorInPlace(byte[] target, byte[] value)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] ^= value[i];
            }
        }

        private static void EncryptBlock(ICryptoTransform encryptor, byte[] input, byte[] output)
        {
            encryptor.TransformBlock(input, 0, 16, output, 0);
        }

        private static void IncrementCounter32(byte[] counter)
        {
            for (int i = 15; i >= 12; i--)
            {
                counter[i]++;
                if (counter[i] != 0)
                    break;
            }
        }

        private static void WriteU64BE(byte[] buffer, int offset, ulong value)
        {
            for (int i = 7; i >= 0; i--)
            {
                buffer[offset + i] = (byte)(value & 0xff);
                value >>= 8;
            }
        }

        private static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }
#endif
    }
}
