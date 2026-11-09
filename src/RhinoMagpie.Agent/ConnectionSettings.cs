using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoMagpie.Agent;

public static class ConnectionSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "miki-rhino", "settings.json");

    public static BridgeConfig Read(string? path = null)
    {
        path ??= ConfigPath;
        if (!File.Exists(path))
            throw new InvalidOperationException("请在 Miki-Rhino 设置中打开「模型连接」，填写自己的 API 地址、模型 ID 和密钥。");
        try
        {
            var stored = JsonSerializer.Deserialize<StoredConnection>(File.ReadAllText(path), Options);
            if (stored is null || stored.SchemaVersion != 1 || stored.KeyProtection != "dpapi-current-user" ||
                stored.ProtectedApiKey is null)
                throw new InvalidDataException("Invalid connection settings.");
            string key = "";
            if (stored.ProtectedApiKey.Length > 0)
            {
                byte[] plaintext = Transform(Convert.FromBase64String(stored.ProtectedApiKey), decrypt: true);
                try { key = Encoding.UTF8.GetString(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            return new BridgeConfig(stored.Model!, stored.BaseUrl!, key).Validate();
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException("无法读取 Miki-Rhino 连接或解密当前用户的密钥，请在「模型连接」中重新保存。", error);
        }
    }

    public static void Save(BridgeConfig config, string? path = null)
    {
        config = config.Validate();
        string protectedKey = "";
        if (config.ApiKey.Length > 0)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(config.ApiKey);
            try { protectedKey = Convert.ToBase64String(Transform(plaintext, decrypt: false)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        path = Path.GetFullPath(path ?? ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stored = new StoredConnection(1, config.Model, config.BaseUrl, "dpapi-current-user", protectedKey);
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Transform(byte[] value, bool decrypt)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Miki-Rhino 的密钥存储需要 Windows 当前用户加密服务。");
        byte[] entropyBytes = Encoding.UTF8.GetBytes("Miki-Rhino/connection/v1");
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var entropy = new Blob { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            bool success = decrypt
                ? CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, "Miki-Rhino", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new InvalidOperationException("Windows 当前用户密钥加密或解密失败。");
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ClearNative(input);
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                ClearNative(output);
                LocalFree(output.Data);
            }
        }
    }

    private static void ClearNative(Blob blob)
    {
        for (int index = 0; index < blob.Length; index++) Marshal.WriteByte(blob.Data, index, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record StoredConnection(int SchemaVersion, string? Model, string? BaseUrl,
        string? KeyProtection, string? ProtectedApiKey);
}
