using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyCapture.Core.Settings;

/// <summary>
/// Serializes a secret as a DPAPI-protected blob (<see cref="DataProtectionScope.CurrentUser"/>)
/// so it never sits in plaintext inside the settings file. In-memory values stay plaintext;
/// only the on-disk representation changes.
/// </summary>
/// <remarks>
/// <para>
/// Encrypted values carry the <see cref="EncryptedPrefix"/> marker. A value without the
/// marker is passed through unchanged so settings written by 2.4.0 (plaintext) still load;
/// <see cref="SettingsStore"/> re-saves them encrypted at rest on the next load.
/// </para>
/// <para>
/// A blob that cannot be decrypted (profile restored from another machine, damaged user
/// key) degrades to an empty value: the app behaves as if no token was configured instead
/// of failing to start.
/// </para>
/// </remarks>
internal sealed class DpapiSecretConverter : JsonConverter<string>
{
    internal const string EncryptedPrefix = "dpapi:v1:";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? raw = reader.GetString();
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (!raw.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            // Legacy plaintext from 2.4.0 settings; migrated to encrypted on the next save.
            return raw;
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(raw[EncryptedPrefix.Length..]);
            byte[] bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        byte[] bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
        writer.WriteStringValue(EncryptedPrefix + Convert.ToBase64String(bytes));
    }
}
