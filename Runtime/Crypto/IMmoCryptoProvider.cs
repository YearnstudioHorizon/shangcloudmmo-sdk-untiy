using System;

namespace ShangCloud.MMO.Crypto
{
    public interface IMmoCryptoProvider
    {
        byte[] GenerateSeed();
        byte[] DeriveKey(byte[] seed);

        /// <summary>
        /// Encrypts data with AES-256-GCM. Writes [12B nonce][ciphertext][16B tag] into outputBuffer.
        /// Plaintext is constructed internally as [8B timestamp_ms BE][data].
        /// </summary>
        /// <returns>Number of bytes written to outputBuffer.</returns>
        int Encrypt(byte[] key, ReadOnlySpan<byte> data, byte[] outputBuffer);

        /// <summary>
        /// Decrypts an AES-256-GCM packet. Input format: [12B nonce][ciphertext][16B tag].
        /// Strips the 8-byte timestamp prefix and writes actual data into outputBuffer.
        /// </summary>
        /// <returns>Number of bytes written to outputBuffer, or -1 on failure.</returns>
        int Decrypt(byte[] key, ReadOnlySpan<byte> packet, byte[] outputBuffer);
    }
}
