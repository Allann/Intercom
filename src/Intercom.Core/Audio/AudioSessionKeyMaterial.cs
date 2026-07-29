using System.Security.Cryptography;
using System.Buffers.Binary;

namespace Intercom.Audio;

public sealed record AudioSessionKeyMaterial(byte[] Key, uint NoncePrefix)
{
    public static AudioSessionKeyMaterial Create()
    {
        var prefix = RandomNumberGenerator.GetBytes(4);
        return new(RandomNumberGenerator.GetBytes(32), BinaryPrimitives.ReadUInt32BigEndian(prefix));
    }
}
