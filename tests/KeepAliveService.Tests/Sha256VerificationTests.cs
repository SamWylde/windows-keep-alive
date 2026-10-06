using System.Security.Cryptography;
using KeepAliveService.Update;
using Xunit;

namespace KeepAliveService.Tests;

public sealed class Sha256VerificationTests : IDisposable
{
    private readonly string _exe = Path.GetTempFileName();

    public Sha256VerificationTests() => File.WriteAllBytes(_exe, new byte[] { 1, 2, 3, 4, 5 });

    public void Dispose() => File.Delete(_exe);

    private string RealHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_exe)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matching_hash_is_accepted(bool lowerCaseWithFileName)
    {
        var text = lowerCaseWithFileName ? RealHash().ToLowerInvariant() + "  KeepAliveService.exe\n" : RealHash();
        Assert.Null(await GitHubUpdateChecker.CompareSha256Async(text, _exe));
    }

    [Fact]
    public async Task Wrong_hash_is_refused()
    {
        var wrong = new string('a', 64);
        var result = await GitHubUpdateChecker.CompareSha256Async(wrong + "  KeepAliveService.exe", _exe);
        Assert.NotNull(result);
        Assert.Contains("does not match", result);
    }

    [Fact]
    public async Task Missing_sha256_asset_is_refused()
    {
        Assert.NotNull(await GitHubUpdateChecker.CompareSha256Async(null, _exe));
        Assert.NotNull(await GitHubUpdateChecker.CompareSha256Async("  ", _exe));
    }

    [Theory]
    [InlineData("not a hash")]
    [InlineData("abc123")]
    [InlineData("zzzz")]
    public async Task Malformed_sha256_is_refused(string text)
    {
        var result = await GitHubUpdateChecker.CompareSha256Async(text, _exe);
        Assert.NotNull(result);
        Assert.Contains("format", result);
    }
}
