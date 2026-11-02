using System.Security.Cryptography;

namespace Monitoring.Protocol;

/// <summary>Mutual application-level authentication (protocol spec §9).
/// AuthHello payload = nonce(16) + clientProof(32) where clientProof =
/// HMAC-SHA256(psk, 0x01||nonce); the device verifies the client, then returns
/// AuthAck = HMAC-SHA256(psk, 0x02||nonce) which the client verifies.
/// Domain separation tags prevent reflecting one side's proof as the other's.</summary>
public static class AuthCode
{
    public const int NonceSize = 16;
    public const int HmacSize = 32;
    public const int HelloPayloadSize = NonceSize + HmacSize;

    private const byte ClientTag = 0x01;
    private const byte ServerTag = 0x02;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] ClientProof(byte[] psk, ReadOnlySpan<byte> nonce)
        => Compute(psk, ClientTag, nonce);

    public static byte[] ServerProof(byte[] psk, ReadOnlySpan<byte> nonce)
        => Compute(psk, ServerTag, nonce);

    public static bool VerifyClient(byte[] psk, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> proof)
        => FixedTimeEqual(ClientProof(psk, nonce), proof);

    public static bool VerifyServer(byte[] psk, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ack)
        => FixedTimeEqual(ServerProof(psk, nonce), ack);

    private static byte[] Compute(byte[] psk, byte tag, ReadOnlySpan<byte> nonce)
    {
        var input = new byte[1 + nonce.Length];
        input[0] = tag;
        nonce.CopyTo(input.AsSpan(1));
        return HMACSHA256.HashData(psk, input);
    }

    private static bool FixedTimeEqual(byte[] expected, ReadOnlySpan<byte> actual)
        => actual.Length == HmacSize && CryptographicOperations.FixedTimeEquals(expected, actual);
}
