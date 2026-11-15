using System.Security.Cryptography;
using System.Text;
using StudentRecords.Backup;
using Xunit;

public class ArchiveCryptoTests
{
    [Fact]
    public void CorrectPasswordRestoresExactBytesWithoutDeterministicCiphertext()
    {
        var input = Encoding.UTF8.GetBytes("Hồ sơ thử nghiệm riêng tư\0\n");
        var first = ArchiveCrypto.Encrypt(input, "Independent!Password2026");
        var second = ArchiveCrypto.Encrypt(input, "Independent!Password2026");
        Assert.Equal(input, ArchiveCrypto.Decrypt(first, "Independent!Password2026"));
        Assert.False(first.SequenceEqual(second));
        Assert.DoesNotContain("Hồ sơ", Encoding.UTF8.GetString(first));
    }
    [Fact]
    public void WrongPasswordIsRejectedBeforePlaintextIsReturned()
    {
        var backup = ArchiveCrypto.Encrypt([1, 2, 3, 4], "Correct!Password2026");
        Assert.ThrowsAny<CryptographicException>(() => ArchiveCrypto.Decrypt(backup, "Incorrect!Password2026"));
    }
    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        var backup = ArchiveCrypto.Encrypt([1, 2, 3, 4], "Correct!Password2026");
        backup[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ArchiveCrypto.Decrypt(backup, "Correct!Password2026"));
    }
    [Fact]
    public void TruncatedArchiveIsRejected()
    {
        Assert.ThrowsAny<CryptographicException>(() => ArchiveCrypto.Decrypt([1, 2, 3], "Correct!Password2026"));
    }
}
