using System.Text.Json;
using KeepAliveService.Services;
using KeepAliveService.Setup;
using Xunit;

namespace KeepAliveService.Tests;

public sealed class StatusWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ka-status-" + Guid.NewGuid().ToString("N"));
    private string StatusFile => Path.Combine(_dir, "status.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly string[] ContractKeys =
    {
        "schema", "version", "mode", "writtenUtc", "observedUtc", "state", "pass", "fail", "warn",
        "failing", "skipped", "serviceInstalled", "serviceRunning", "autoLoginConfigured",
        "remediationFailures", "cooldownUntilUtc",
    };

    [Theory]
    [InlineData(StatusStates.Ok, 8, 0, 3, 0, false)]
    [InlineData(StatusStates.Failing, 5, 2, 1, 1, false)]
    [InlineData(StatusStates.Exception, 0, 0, 0, 3, false)]
    [InlineData(StatusStates.Cooldown, 5, 2, 1, 3, true)]
    [InlineData(StatusStates.SetupIncomplete, 0, 0, 0, 0, false)]
    public void Writes_schema_1_json_with_exactly_the_contract_fields_for_every_state(
        string state, int pass, int fail, int warn, int failures, bool cooldown)
    {
        var observed = new DateTime(2026, 10, 5, 12, 30, 15, DateTimeKind.Utc);
        var report = new ComplianceReport(observed, pass, fail, warn,
            fail > 0 ? new[] { "Sleep timeout" } : Array.Empty<string>(), new[] { "Auto-login" });
        var until = cooldown ? observed.AddHours(6) : (DateTime?)null;

        Assert.True(StatusWriter.Write(state, KeepAliveMode.FullUnattended, observed, report, failures, until,
            statusPathOverride: StatusFile));

        using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
        var root = doc.RootElement;
        Assert.Equal(ContractKeys.OrderBy(k => k), root.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(state, root.GetProperty("state").GetString());
        Assert.Equal("full-unattended", root.GetProperty("mode").GetString());
        Assert.Equal(pass, root.GetProperty("pass").GetInt32());
        Assert.Equal(fail, root.GetProperty("fail").GetInt32());
        Assert.Equal(warn, root.GetProperty("warn").GetInt32());
        Assert.Equal(failures, root.GetProperty("remediationFailures").GetInt32());
        Assert.Equal(fail > 0 ? 1 : 0, root.GetProperty("failing").GetArrayLength());
        Assert.Equal(cooldown ? "2026-10-05T18:30:15Z" : null, root.GetProperty("cooldownUntilUtc").GetString());
    }

    [Fact]
    public void ObservedUtc_is_the_check_time_not_the_write_time()
    {
        var observed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        StatusWriter.Write(StatusStates.Ok, KeepAliveMode.KeepAwakeOnly, observed, null, 0, null,
            statusPathOverride: StatusFile);

        using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
        Assert.Equal("2020-01-02T03:04:05Z", doc.RootElement.GetProperty("observedUtc").GetString());
        Assert.NotEqual("2020-01-02T03:04:05Z", doc.RootElement.GetProperty("writtenUtc").GetString());
    }

    [Fact]
    public void Write_replaces_the_file_atomically_and_leaves_no_temp_file()
    {
        var t = DateTime.UtcNow;
        Assert.True(StatusWriter.Write(StatusStates.Failing, KeepAliveMode.KeepAwakeOnly, t, null, 1, null,
            statusPathOverride: StatusFile));
        Assert.True(StatusWriter.Write(StatusStates.Ok, KeepAliveMode.KeepAwakeOnly, t, null, 0, null,
            statusPathOverride: StatusFile));

        Assert.Equal(new[] { StatusFile }, Directory.GetFiles(_dir));
        using var doc = JsonDocument.Parse(File.ReadAllText(StatusFile));
        Assert.Equal("ok", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void Write_failure_returns_false_and_does_not_throw()
    {
        // The target path is an existing directory, so the move cannot succeed.
        Directory.CreateDirectory(StatusFile);
        Assert.False(StatusWriter.Write(StatusStates.Ok, KeepAliveMode.KeepAwakeOnly, DateTime.UtcNow, null, 0, null,
            statusPathOverride: StatusFile));
    }

    [Fact]
    public void Output_holds_no_secret_fields()
    {
        StatusWriter.Write(StatusStates.Ok, KeepAliveMode.FullUnattended, DateTime.UtcNow, null, 0, null,
            statusPathOverride: StatusFile);
        var text = File.ReadAllText(StatusFile);
        foreach (var word in new[] { "password", "secret", "token", "credential", "username" })
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
