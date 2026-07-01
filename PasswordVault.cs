using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace KillSwitch;

public sealed class VaultEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Updated { get; set; } = "";
}

/// <summary>
/// A user-controlled, master-password-encrypted password vault (AES-256-GCM, PBKDF2-SHA256).
/// Entries are added by the user — there is no traffic interception or auto-capture.
/// Stored at %AppData%\KillSwitch\vault.dat; the master password is never stored.
/// </summary>
public sealed class PasswordVault
{
    private const int Iterations = 200_000;

    private byte[]? _key;
    private byte[] _salt = Array.Empty<byte>();

    public List<VaultEntry> Entries { get; private set; } = new();
    public bool Unlocked { get; private set; }

    private static string FilePath => Path.Combine(AppSettings.Dir, "vault.dat");
    public static bool Exists() => File.Exists(FilePath);

    public string? Create(string master)
    {
        if (string.IsNullOrEmpty(master)) return "Choose a master password.";
        _salt = RandomNumberGenerator.GetBytes(16);
        _key = Derive(master, _salt);
        Entries = new List<VaultEntry>();
        Unlocked = true;
        return SaveInternal();
    }

    public string? Unlock(string master)
    {
        try
        {
            var data = File.ReadAllBytes(FilePath);
            if (data.Length < 44) return "Vault file is corrupt.";
            _salt = data[..16];
            var nonce = data[16..28];
            var tag = data[28..44];
            var cipher = data[44..];
            var key = Derive(master, _salt);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
            Entries = JsonSerializer.Deserialize<List<VaultEntry>>(plain) ?? new List<VaultEntry>();
            _key = key;
            Unlocked = true;
            return null;
        }
        catch (CryptographicException) { return "Wrong master password."; }
        catch (Exception ex) { return "Couldn't open the vault: " + ex.Message; }
    }

    public void Lock()
    {
        Unlocked = false;
        Entries = new List<VaultEntry>();
        if (_key != null) { Array.Clear(_key); _key = null; }
    }

    public string? Save() => Unlocked ? SaveInternal() : "Vault is locked.";

    private string? SaveInternal()
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(Entries);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(_key!, 16)) aes.Encrypt(nonce, plain, cipher, tag);

            using var ms = new MemoryStream();
            ms.Write(_salt);
            ms.Write(nonce);
            ms.Write(tag);
            ms.Write(cipher);
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllBytes(FilePath, ms.ToArray());
            return null;
        }
        catch (Exception ex) { return "Save failed: " + ex.Message; }
    }

    private static byte[] Derive(string master, byte[] salt)
    {
        using var k = new Rfc2898DeriveBytes(master, salt, Iterations, HashAlgorithmName.SHA256);
        return k.GetBytes(32);
    }
}
