using System.Security.Cryptography;

namespace DriftwoodServer;

internal static class FileHashVerifier
{
    public static void Verify(string path, string expectedSha256, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{description} is missing", path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} does not match the pinned build");
        }
    }
}
