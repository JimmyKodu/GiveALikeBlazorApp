using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GiveALikeBlazorApp.Services;

/// <summary>
/// Reads and decrypts bilibili cookies (SESSDATA, bili_jct) from the local
/// Chromium-based browser's cookie database (Edge / Chrome).
/// Works on Windows (DPAPI + AES-256-GCM) and Linux (AES-128-CBC with "peanuts" key).
/// </summary>
public sealed class CookieExtractor
{
    private readonly ILogger<CookieExtractor> _logger;

    public CookieExtractor(ILogger<CookieExtractor> logger) => _logger = logger;

    public sealed record BiliCookies(string Sessdata, string BiliJct);

    /// <summary>
    /// Attempts to extract bilibili cookies from Edge, then Chrome.
    /// Returns null if cookies are not found or decryption fails.
    /// </summary>
    public BiliCookies? ExtractBiliCookies()
    {
        foreach (var browser in new[] { "Edge", "Chrome" })
        {
            try
            {
                var result = TryExtractFromBrowser(browser);
                if (result is not null)
                {
                    _logger.LogInformation("Successfully extracted bilibili cookies from {Browser}", browser);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract cookies from {Browser}", browser);
            }
        }

        _logger.LogWarning("Could not extract bilibili cookies from any browser");
        return null;
    }

    private BiliCookies? TryExtractFromBrowser(string browser)
    {
        // Locate cookie database
        var cookieDbPath = FindCookieDb(browser);
        if (cookieDbPath is null)
        {
            _logger.LogDebug("{Browser} cookie database not found", browser);
            return null;
        }

        _logger.LogDebug("Found {Browser} cookie database at {Path}", browser, cookieDbPath);

        // Obtain the decryption key
        byte[]? key = GetDecryptionKey(browser);
        if (key is null)
        {
            _logger.LogWarning("Could not obtain decryption key for {Browser}", browser);
            return null;
        }

        // Copy files to a temp directory to avoid lock contention with the running browser
        var tempDir = Path.Combine(Path.GetTempPath(), $"bililike_cookies_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempDb = Path.Combine(tempDir, "Cookies");
            File.Copy(cookieDbPath, tempDb, overwrite: true);

            // Also copy WAL / SHM files if present (required for WAL-mode databases)
            CopyIfExists(cookieDbPath + "-wal", tempDb + "-wal");
            CopyIfExists(cookieDbPath + "-shm", tempDb + "-shm");

            return ReadCookies(tempDb, key);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ── Cookie DB location helpers ─────────────────────────────────────────

    private static string? FindCookieDb(string browser)
    {
        foreach (var path in GetCookiePaths(browser))
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> GetCookiePaths(string browser)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var sub = browser == "Edge"
                ? Path.Combine("Microsoft", "Edge")
                : Path.Combine("Google", "Chrome");

            yield return Path.Combine(local, sub, "User Data", "Default", "Network", "Cookies");
            yield return Path.Combine(local, sub, "User Data", "Default", "Cookies");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = browser == "Edge" ? "microsoft-edge" : "google-chrome";

            yield return Path.Combine(home, ".config", dir, "Default", "Network", "Cookies");
            yield return Path.Combine(home, ".config", dir, "Default", "Cookies");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = browser == "Edge" ? "Microsoft Edge" : "Google/Chrome";

            yield return Path.Combine(home, "Library", "Application Support", dir, "Default", "Cookies");
        }
    }

    private static string GetLocalStatePath(string browser)
    {
        string baseDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            baseDir = browser == "Edge"
                ? Path.Combine(local, "Microsoft", "Edge", "User Data")
                : Path.Combine(local, "Google", "Chrome", "User Data");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = browser == "Edge" ? "microsoft-edge" : "google-chrome";
            baseDir = Path.Combine(home, ".config", dir);
        }
        else // macOS
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = browser == "Edge" ? "Microsoft Edge" : "Google/Chrome";
            baseDir = Path.Combine(home, "Library", "Application Support", dir);
        }

        return Path.Combine(baseDir, "Local State");
    }

    // ── Decryption key retrieval ───────────────────────────────────────────

    private byte[]? GetDecryptionKey(string browser)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsKey(browser);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetLinuxKey();

        _logger.LogWarning("Cookie decryption is not supported on this platform");
        return null;
    }

    /// <summary>
    /// Windows: reads the encrypted AES key from Local State, then decrypts it with DPAPI.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private byte[]? GetWindowsKey(string browser)
    {
        var path = GetLocalStatePath(browser);
        if (!File.Exists(path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));

        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
        if (!osCrypt.TryGetProperty("encrypted_key", out var encKeyProp)) return null;

        var base64 = encKeyProp.GetString();
        if (string.IsNullOrEmpty(base64)) return null;

        var raw = Convert.FromBase64String(base64);

        // Expect "DPAPI" prefix (5 ASCII bytes)
        if (raw.Length < 6 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI")
            return null;

        // Decrypt with DPAPI (current-user scope)
        return ProtectedData.Unprotect(raw.AsSpan(5).ToArray(), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Linux: derives a 128-bit AES key from the password "peanuts" via PBKDF2-SHA1.
    /// </summary>
    private static byte[] GetLinuxKey()
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            "peanuts",
            "saltysalt"u8.ToArray(),
            iterations: 1,
            hashAlgorithm: HashAlgorithmName.SHA1,
            outputLength: 16);
    }

    // ── SQLite reading ─────────────────────────────────────────────────────

    private BiliCookies? ReadCookies(string dbPath, byte[] key)
    {
        string? sessdata = null;
        string? biliJct = null;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name, encrypted_value, value FROM cookies " +
            "WHERE host_key IN ('.bilibili.com', 'bilibili.com') " +
            "AND name IN ('SESSDATA', 'bili_jct')";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var encrypted = reader.IsDBNull(1) ? null : (byte[])reader[1];
            var plain = reader.IsDBNull(2) ? null : reader.GetString(2);

            // Prefer the plain-text value if the cookie was stored unencrypted
            string? value = null;
            if (!string.IsNullOrEmpty(plain))
            {
                value = plain;
            }
            else if (encrypted is { Length: > 0 })
            {
                value = DecryptCookieValue(encrypted, key);
            }

            if (value is null) continue;

            if (name == "SESSDATA") sessdata = value;
            else if (name == "bili_jct") biliJct = value;
        }

        if (sessdata is not null && biliJct is not null)
            return new BiliCookies(sessdata, biliJct);

        return null;
    }

    // ── Cookie decryption ──────────────────────────────────────────────────

    private string? DecryptCookieValue(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < 4) return null;

        var prefix = Encoding.ASCII.GetString(encrypted, 0, 3);

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (prefix is "v10" or "v20"))
                return DecryptAesGcm(encrypted, key);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && prefix == "v10")
                return DecryptAesCbc(encrypted, key);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && prefix == "v11")
                return DecryptAesGcm(encrypted, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cookie decryption failed (prefix={Prefix})", prefix);
            return null;
        }

        // Possibly unencrypted — return as-is
        return Encoding.UTF8.GetString(encrypted);
    }

    /// <summary>
    /// AES-256-GCM: used by Windows (Edge / Chrome) and newer Linux builds.
    /// Format: prefix(3) | nonce(12) | ciphertext(N) | tag(16)
    /// </summary>
    private static string DecryptAesGcm(byte[] data, byte[] key)
    {
        const int nonceLen = 12;
        const int tagLen = 16;
        const int prefixLen = 3;

        var nonce = data.AsSpan(prefixLen, nonceLen);
        var payload = data.AsSpan(prefixLen + nonceLen);
        var tag = payload[^tagLen..];
        var ciphertext = payload[..^tagLen];

        var plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(key, tagLen);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// AES-128-CBC: used by Linux with the "peanuts"-derived key.
    /// Format: v10(3) | ciphertext(N, PKCS7-padded)
    /// IV = 16 × 0x20 (space).
    /// </summary>
    private static string DecryptAesCbc(byte[] data, byte[] key)
    {
        var ciphertext = data.AsSpan(3).ToArray();
        var iv = new byte[16];
        Array.Fill(iv, (byte)' ');

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plain);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void CopyIfExists(string src, string dst)
    {
        if (File.Exists(src))
            File.Copy(src, dst, overwrite: true);
    }
}
