using System.Security.Cryptography;
using System.Text;

namespace SqlJobRunner;

// Shared login for EDWUTIL.OPR.EdwJobGeneratorRequests. Stored encrypted so the source does not show it in plain text.
// The key ships with the application: this hides the credential from readers, it does not protect it from decompilation.
// Keep the login limited to SELECT/INSERT/UPDATE on the requests table.
internal static class RequestCredentials
{
    private const string UserBlob = "8lfNR0/pQAqX83naLAaHBPRUqITQGuN+SES82eV2wX1RtfY0mA==";
    private const string PasswordBlob = "pnaektRSon8Vz/qnBThrdEMGQeOR1ficjnWWGAd5yy5IA7aAiaHg5gDtVA==";
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("SqlJobRunner|EdwJobGeneratorRequests|v1"));
    public static string UserName => Decrypt(UserBlob);
    public static string Password => Decrypt(PasswordBlob);
    private static string Decrypt(string blob)
    {
        var data = Convert.FromBase64String(blob);
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
