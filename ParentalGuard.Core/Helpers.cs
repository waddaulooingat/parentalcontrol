using System.Security.Cryptography;
using System.Text;

namespace ParentalGuard.Core;

/// <summary>Shared on-disk locations for all ParentalGuard data files.</summary>
public static class AppPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ParentalGuard");

    public static string ReportsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ParentalGuardReports");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);
    }
}

/// <summary>Stores and verifies the parent PIN as a salted hash in pin.dat.</summary>
public class PinStore
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    private readonly string _path;

    public PinStore(string? pinFilePath = null)
    {
        _path = pinFilePath ?? Path.Combine(AppPaths.DataDirectory, "pin.dat");
    }

    public bool HasPin => File.Exists(_path);

    public void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Hash(pin, salt);

        Directory.CreateDirectory(AppPaths.DataDirectory);

        var buffer = new byte[SaltSize + HashSize];
        Buffer.BlockCopy(salt, 0, buffer, 0, SaltSize);
        Buffer.BlockCopy(hash, 0, buffer, SaltSize, HashSize);
        File.WriteAllBytes(_path, buffer);
    }

    public bool Verify(string pin)
    {
        if (!File.Exists(_path)) return false;

        var buffer = File.ReadAllBytes(_path);
        if (buffer.Length != SaltSize + HashSize) return false;

        var salt = new byte[SaltSize];
        var expectedHash = new byte[HashSize];
        Buffer.BlockCopy(buffer, 0, salt, 0, SaltSize);
        Buffer.BlockCopy(buffer, SaltSize, expectedHash, 0, HashSize);

        var actualHash = Hash(pin, salt);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] Hash(string pin, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
}

/// <summary>
/// Downloads the StevenBlack unified hosts list (adult content edition) and merges the
/// blocked domains into a <see cref="BlocklistManager"/> as external/auto-updated entries.
/// </summary>
public class BlocklistFetcher
{
    private const string StevenBlackUrl =
        "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts";

    private readonly HttpClient _httpClient;

    public BlocklistFetcher(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<int> RefreshAsync(BlocklistManager manager, CancellationToken cancellationToken = default)
    {
        var hostsText = await _httpClient.GetStringAsync(StevenBlackUrl, cancellationToken).ConfigureAwait(false);
        var domains = ParseHostsFile(hostsText);
        manager.SetExternalDomains(domains);
        return domains.Count;
    }

    internal static HashSet<string> ParseHostsFile(string hostsText)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in hostsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (parts[0] != "0.0.0.0" && parts[0] != "127.0.0.1") continue;

            var domain = parts[1].Trim().ToLowerInvariant();
            if (domain is "localhost" or "0.0.0.0" or "" ) continue;

            domains.Add(domain);
        }

        return domains;
    }
}
