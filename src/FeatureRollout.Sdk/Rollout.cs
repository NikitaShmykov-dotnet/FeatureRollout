using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FeatureRollout;

public static class Rollout
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static void ValidateUserIdEncoding(string id)
    {
        try
        {
            Utf8.GetByteCount(id);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("User ID must be well-formed UTF-16.", nameof(id), error);
        }
    }

    public static int Bucket(string flagKey, string userId)
    {
        ArgumentNullException.ThrowIfNull(flagKey);
        ArgumentNullException.ThrowIfNull(userId);
        int keyLength = Utf8.GetByteCount(flagKey);
        int idLength = Utf8.GetByteCount(userId);
        Span<byte> input = keyLength + idLength <= 512
            ? stackalloc byte[4 + keyLength + idLength]
            : new byte[checked(4 + keyLength + idLength)];

        BinaryPrimitives.WriteInt32BigEndian(input, keyLength);
        Utf8.GetBytes(flagKey, input[4..]);
        Utf8.GetBytes(userId, input[(4 + keyLength)..]);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(digest) % 10_000);
    }
}
