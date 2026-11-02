using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DeskCal.Mcp;

namespace DeskCal.Mcp.Tests;

public sealed class ClaudeDesktopConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "deskcal-config-" + Guid.NewGuid().ToString("N"));

    public ClaudeDesktopConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private async Task<(int ExitCode, string Error, string Command)> Register(string path)
    {
        var command = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(CalendarTools).Assembly.Location);
        start.ArgumentList.Add("--register-claude-desktop");
        start.ArgumentList.Add(path);
        start.Environment["DESKCAL_DB"] = Path.Combine(_directory, "must-not-create.db");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal("", await output);
        return (process.ExitCode, await error, command);
    }

    [Fact]
    public async Task RegistrationPreservesUserValuesAndBacksUpOriginalBytes()
    {
        var nested = "{\"leaf\":\"끝\"}";
        for (var i = 0; i < 25; i++) nested = "{\"child\":" + nested + "}";
        var original = "{\"Tool\":1,\"tool\":2,\"한글\":\"설정\",\"array\":[null],\"nil\":null,\"large\":123456789012345678901234567890,\"nested\":" + nested +
            ",\"mcpServers\":{\"other\":{\"command\":\"keep\",\"args\":[\"one\"]},\"deskcal\":{\"command\":\"old\"}}}";
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, original, new UTF8Encoding(true));
        var originalBytes = File.ReadAllBytes(path);

        var result = await Register(path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(originalBytes, File.ReadAllBytes(path + ".bak"));
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }));
        var before = JsonNode.Parse(original)!.AsObject();
        var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(before.Select(p => p.Key), after.Select(p => p.Key));
        foreach (var entry in before.Where(p => p.Key != "mcpServers"))
            Assert.True(JsonNode.DeepEquals(entry.Value, after[entry.Key]), entry.Key);
        Assert.Equal("123456789012345678901234567890", after["large"]!.ToJsonString());
        // 한글(실제 설정의 폴더 경로 등)은 \uXXXX로 바꾸지 않고 그대로 쓴다.
        Assert.Contains("\"한글\": \"설정\"", File.ReadAllText(path));
        Assert.True(JsonNode.DeepEquals(before["mcpServers"]!["other"], after["mcpServers"]!["other"]));
        Assert.Equal(new[] { "other", "deskcal" }, after["mcpServers"]!.AsObject().Select(p => p.Key));
        var deskcal = after["mcpServers"]!["deskcal"]!.AsObject();
        Assert.Equal(new[] { "command", "args" }, deskcal.Select(p => p.Key));
        Assert.Equal(result.Command, deskcal["command"]!.GetValue<string>());
        Assert.Empty(deskcal["args"]!.AsArray());
        Assert.False(File.Exists(Path.Combine(_directory, "must-not-create.db")));
    }

    [Fact]
    public async Task RegistrationCreatesMissingServersObject()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, "{}");
        var result = await Register(path);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["deskcal"]);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"mcpServers\":42}")]
    public async Task InvalidConfigIsUntouchedAndReportsFailure(string original)
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, original);
        File.WriteAllText(path + ".bak", "previous backup");
        var result = await Register(path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal("previous backup", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(Path.Combine(_directory, "must-not-create.db")));
    }
}
