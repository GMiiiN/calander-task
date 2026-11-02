using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskCal.Mcp;

public static class ClaudeDesktopConfig
{
    private const int MAX_DEPTH = 1024;

    public static void Register(string path, string command)
    {
        var config = Read(path);
        if (!config.TryGetPropertyValue("mcpServers", out var servers))
            config["mcpServers"] = servers = new JsonObject();
        if (servers is not JsonObject serverObject)
            throw new JsonException("mcpServers must be a JSON object.");
        serverObject["deskcal"] = new JsonObject { ["command"] = command, ["args"] = new JsonArray() };
        Write(path, config);
    }

    public static void Unregister(string path)
    {
        var config = Read(path);
        if (!config.TryGetPropertyValue("mcpServers", out var servers)) return;
        if (servers is not JsonObject serverObject)
            throw new JsonException("mcpServers must be a JSON object.");
        if (!serverObject.Remove("deskcal")) return;
        Write(path, config);
    }

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path),
        new JsonNodeOptions { PropertyNameCaseInsensitive = false },
        new JsonDocumentOptions { MaxDepth = MAX_DEPTH }) as JsonObject
        ?? throw new JsonException("Claude Desktop configuration must be a JSON object.");

    private static void Write(string path, JsonObject config)
    {
        // 기본 인코더는 한글을 \uXXXX로 바꾼다. 사용자 설정(한글 폴더 경로 등)을 읽을 수 있게 그대로 쓴다.
        var json = config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, MaxDepth = MAX_DEPTH, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        // 완성된 내용으로만 원본을 교체하여 쓰기 도중 설정이 손상되지 않게 한다.
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
