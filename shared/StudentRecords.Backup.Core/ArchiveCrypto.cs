using System.Security.Cryptography;
using System.Text;
namespace StudentRecords.Backup;
public static class ArchiveCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CRB1");
    private const int HeaderLength = 48;
    public const int MaximumBytes = 256 * 1024 * 1024;
    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        if (plaintext.Length > MaximumBytes) throw new InvalidDataException("Bản sao lưu vượt giới hạn 256 MiB.");
        var output = new byte[HeaderLength + plaintext.Length];
        Magic.CopyTo(output, 0);
        RandomNumberGenerator.Fill(output.AsSpan(4, 16));
        RandomNumberGenerator.Fill(output.AsSpan(20, 12));
        var key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(4, 16), 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(20, 12), plaintext, output.AsSpan(HeaderLength), output.AsSpan(32, 16), Magic);
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Decrypt(byte[] payload, string password)
    {
        if (payload.Length < HeaderLength || payload.Length > MaximumBytes + HeaderLength || !payload.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Định dạng bản sao lưu không hợp lệ.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, payload.AsSpan(4, 16), 600_000, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[payload.Length - HeaderLength];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(20, 12), payload.AsSpan(HeaderLength), payload.AsSpan(32, 16), plaintext, Magic);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
