using System.Security.Cryptography;
using System.Text;

namespace Rotina.Core;

/// <summary>
/// The recovery key ends any activity at once, so a bug can never lock the user out. Only its hash is stored.
/// The phone computes the same hash (app.rotina.Recovery), so it can check the key offline.
/// </summary>
public static class Recovery
{
    // No 0/O or 1/I, so the printed key is easy to read back. 32 symbols x 20 = 100 bits.
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var chars = bytes.Select(b => Alphabet[b % 32]).ToArray(); // 256 is a multiple of 32: no bias
        return string.Join("-", Enumerable.Range(0, 4).Select(g => new string(chars, g * 5, 5)));
    }

    /// <summary>Upper case, letters and digits only: "abcde-fghjk" and "ABCDE FGHJK" are the same key.</summary>
    public static string Normalize(string key) => new([.. key.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(key))));

    public static bool Matches(string key, string? hash) =>
        hash is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(hash));
}
