using LanLink.Core.Security;
using Xunit;

namespace LanLink.Core.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("C:\\Windows\\system.ini", "system.ini")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("a<b>c.txt", "abc.txt")]
    [InlineData("file.txt:hidden", "file.txthidden")]
    [InlineData("  name.txt. ", "name.txt")]
    public void FileName_Sanitizes(string raw, string expected) =>
        Assert.Equal(expected, SafePath.FileName(raw));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    public void FileName_Rejects(string raw) =>
        Assert.Throws<ArgumentException>(() => SafePath.FileName(raw));

    [Fact]
    public void RelativePath_RejectsTraversal()
    {
        Assert.Throws<ArgumentException>(() => SafePath.RelativePath("a/../b"));
        Assert.Throws<ArgumentException>(() => SafePath.RelativePath("../b"));
        Assert.Equal("a/b/c.txt", SafePath.RelativePath("a\\b/./c.txt"));
    }

    [Fact]
    public void Combine_StaysInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lanlink-root");
        Assert.StartsWith(Path.GetFullPath(root), SafePath.Combine(root, "sub/file.txt"));
    }

    [Fact]
    public void Unique_AddsSuffix()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "a.txt");
        File.WriteAllText(path, "x");
        Assert.Equal(Path.Combine(dir, "a (1).txt"), SafePath.Unique(path));
    }
}

public class PasswordTests
{
    [Fact]
    public void Derivation_IsDeterministicForSameSalt()
    {
        var v = PasswordVerifier.Create("correct horse battery", Argon2Params.Fast);
        Assert.Equal(v.Key, PasswordVerifier.DeriveKey("correct horse battery", v.Salt, v.Params));
        Assert.NotEqual(v.Key, PasswordVerifier.DeriveKey("correct horse batterY", v.Salt, v.Params));
    }

    [Theory]
    [InlineData("court", false)]
    [InlineData("aaaaaaaaaaaaaaaa", false)]
    [InlineData("une phrase de passe", true)]
    public void Policy(string password, bool ok) =>
        Assert.Equal(ok, PasswordPolicy.Validate(password) is null);

    [Fact]
    public void Proofs_DependOnBothCertificates()
    {
        var key = new byte[32];
        byte[] a = new byte[32], b = new byte[32], c = new byte[32];
        a[0] = 1; b[0] = 2; c[0] = 3;
        var p1 = HandshakeProof.Compute(key, "client", a, b, c, c);
        var p2 = HandshakeProof.Compute(key, "client", a, b, c, a);
        Assert.False(HandshakeProof.Verify(p1, p2));
        Assert.False(HandshakeProof.Verify(p1, HandshakeProof.Compute(key, "server", a, b, c, c)));
    }

    [Fact]
    public void RateLimiter_BlocksThenExpires()
    {
        var now = DateTime.UtcNow;
        var limiter = new AuthRateLimiter(3, TimeSpan.FromMinutes(1), () => now);
        for (var i = 0; i < 3; i++) limiter.RecordFailure("1.2.3.4");
        Assert.True(limiter.IsBlocked("1.2.3.4"));
        Assert.False(limiter.IsBlocked("5.6.7.8"));
        now = now.AddMinutes(2);
        Assert.False(limiter.IsBlocked("1.2.3.4"));
    }
}
