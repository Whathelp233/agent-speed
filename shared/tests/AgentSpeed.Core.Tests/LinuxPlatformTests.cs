using AgentSpeed.Core.IO;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Traffic.Linux;

namespace AgentSpeed.Core.Tests;

public sealed class LinuxPlatformTests
{
    [Fact]
    public async Task HelperRejectsAnUnprivilegedImpersonator()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-peer-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "helper.sock");
            using var server = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            server.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path)); server.Listen(1);
            var accept = server.AcceptAsync();
            using var sampler = new HelperSampler(path);
            Assert.Null(sampler.Poll(true, true, 0));
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AgentSpeed.Core.Traffic.Monitor.ProcessCounterStatus.HelperDenied, sampler.Status);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void ProcStatUsesLastParenthesisAndClockTicks()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Fields 3..22, with a command name that contains spaces and a closing parenthesis.
        var fields = Enumerable.Repeat("0", 20).ToArray(); fields[0] = "S"; fields[1] = "17"; fields[19] = "12345";
        var entry = ProcessReader.ParseStat("42 (worker ) process) " + string.Join(' ', fields), 1_700_000_000_000_000, 100);
        Assert.NotNull(entry); Assert.Equal(42, entry.Value.Pid); Assert.Equal(17, entry.Value.ParentPid);
        Assert.Equal(1_700_000_123_450_000, entry.Value.StartUs);
        Assert.Null(ProcessReader.ParseStat("broken", 0, 100));
        Assert.Null(ProcessReader.ParseStat("42 (x) " + string.Join(' ', fields), 0, 0));
    }
    [Fact]
    public void PhysicalFileIdentitySurvivesRenameButDetectsReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "agent-speed-inode-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.jsonl"); File.WriteAllText(path, "old");
            using var old = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var identity = FileIdentity.From(old); Assert.NotNull(identity);
            File.Move(path, path + ".1");
            using var renamed = File.OpenHandle(path + ".1"); Assert.Equal(identity, FileIdentity.From(renamed));
            File.WriteAllText(path, "new"); using var replacement = File.OpenHandle(path);
            Assert.NotEqual(identity, FileIdentity.From(replacement));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void LinuxClientReportsLinuxWithoutChangingWindowsBounds()
    {
        var client = new ReportClientInfo("1.4.1", "Linux", 6);
        Assert.Equal(6, client.OsMajor); Assert.Equal("AgentSpeed/1.4.1 (Linux)", client.UserAgent);
        Assert.Equal(10, new ReportClientInfo("1.4.1", "Windows", 6).OsMajor);
    }
    [Fact]
    public void LinuxPeerAppearsInProfile()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""
        {"sharedUserId":null,"displayName":"test","avatar":null,"devices":[
          {"deviceId":"123e4567-e89b-42d3-a456-426614174000","platform":"Linux","isCurrent":true,"online":true}]}
        """);
        var profile = ReportProfileJson.Profile(bytes, ReportUsernameRules.Standard);
        Assert.NotNull(profile); Assert.Single(profile.Devices); Assert.Equal("Linux", profile.Devices[0].Platform);
    }
}
