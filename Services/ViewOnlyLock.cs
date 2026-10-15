using System.Security.Cryptography;
using System.Text;

namespace BgOptimizer.Services;

/// <summary>
/// The optional code that keeps view-only mode switched on. Only a salted PBKDF2 hash is stored, never the code
/// itself, and it is kept in <see cref="LockStore"/> (administrator-writable only), not in the user-editable
/// settings.json. It stops accidental changes and programs running as the user; it is not a barrier against an
/// administrator, who can delete the key.
/// </summary>
internal static class ViewOnlyLock
{
    // Long enough that the hash cannot be guessed offline in a practical time even if it is read from the registry.
    public const int MinLength = 8;

    private const string Scheme = "pbkdf2-sha256";
    private const int DefaultIterations = 200_000;
    // A hand-edited file must not be able to make the check hang (huge count) or be trivial (tiny count).
    private const int MinIterations = 10_000;
    private const int MaxIterations = 2_000_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>The text to store for <paramref name="code"/>: scheme, iteration count, salt and hash.</summary>
    public static string Create(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(code, salt, DefaultIterations);
        return $"{Scheme}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? code, string? stored)
    {
        if (code is null || !TryParse(stored, out var iterations, out var salt, out var expected)) return false;
        var actual = Derive(code, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True for a stored value this class can check. Anything else counts as "no code is set".</summary>
    public static bool IsValid(string? stored) => TryParse(stored, out _, out _, out _);

    /// <summary>Seconds to wait before another try: free for the first two wrong codes, then 5 s more each time (max 60).</summary>
    public static int WaitSeconds(int failures) => failures < 3 ? 0 : Math.Min(60, 5 * (failures - 2));

    private static byte[] Derive(string code, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0; salt = Array.Empty<byte>(); hash = Array.Empty<byte>();
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], out iterations) || iterations < MinIterations || iterations > MaxIterations) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        return salt.Length >= 8 && hash.Length == HashBytes;
    }
}
