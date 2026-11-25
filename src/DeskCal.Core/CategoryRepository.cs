using System.Text.Json.Nodes;

namespace DeskCal.Core;

public sealed record CategoryRecord(string ColorId, string Name, string ColorHex);

// 카테고리 = Google 이벤트 색(§4). 색 목록은 Google이 정하고, 이름(별칭)만 사용자가 바꾼다.
// 별칭은 Google에 연결하면 Drive 앱 전용 공간으로 다른 기기와 맞춘다(S6, SharedSettings). Google의 색과 일정의 color_id는 그대로다.
public sealed class CategoryRepository(Database database)
{
    // Google 기본 11색과 처음 이름. Google 연결 전에도 이 표로 시작한다(§4.1).
    public static readonly IReadOnlyList<CategoryRecord> Defaults =
    [
        new("1", "라벤더", "#7986CB"), new("2", "세이지", "#33B679"), new("3", "포도", "#8E24AA"), new("4", "플라밍고", "#E67C73"),
        new("5", "바나나", "#F6BF26"), new("6", "귤", "#F4511E"), new("7", "공작", "#039BE5"), new("8", "흑연", "#616161"),
        new("9", "블루베리", "#3F51B5"), new("10", "바질", "#0B8043"), new("11", "토마토", "#D50000"),
    ];

    public static string? DefaultName(string colorId) => Defaults.FirstOrDefault(d => d.ColorId == colorId)?.Name;

    public IReadOnlyList<CategoryRecord> List()
    {
        using var connection = database.OpenConnection();
        return List(connection, null);
    }

    internal static IReadOnlyList<CategoryRecord> List(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT color_id, name, color_hex FROM category ORDER BY CAST(color_id AS INTEGER), color_id;";
        using var reader = command.ExecuteReader();
        var result = new List<CategoryRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    // 이름은 비울 수 없고, 서로 같거나 다른 카테고리의 color_id와 같을 수 없다. MCP가 이름이나 color_id로 카테고리를 찾기 때문이다.
    public CategoryRecord Rename(string colorId, string name) =>
        Rename(new Dictionary<string, string> { [colorId] = name }).Single(c => c.ColorId == colorId);

    public IReadOnlyList<CategoryRecord> Rename(IReadOnlyDictionary<string, string> names)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = List(connection, transaction);
        if (names.Keys.Any(id => current.All(c => c.ColorId != id))) throw new ArgumentException("Category not found.");
        var renamed = current.Select(c => c with { Name = names.TryGetValue(c.ColorId, out var name) ? name.Trim() : c.Name }).ToArray();
        if (renamed.Any(c => c.Name.Length == 0)) throw new ArgumentException("Category name is required.");
        if (renamed.DistinctBy(c => c.Name, StringComparer.Ordinal).Count() != renamed.Length)
            throw new ArgumentException("Category names must be unique.");
        if (renamed.Any(c => current.Any(other => other.ColorId != c.ColorId && other.ColorId == c.Name)))
            throw new ArgumentException("Category name must not be another category's color_id.");

        var now = Validation.Timestamp(TimeProvider.System);
        foreach (var category in renamed.Where(c => names.ContainsKey(c.ColorId)))
        {
            // 바뀌지 않은 이름은 다른 기기에 새 버전으로 보내지 않는다.
            if (current.Single(c => c.ColorId == category.ColorId).Name == category.Name) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE category SET name = $name WHERE color_id = $id;";
            command.Parameters.AddWithValue("$name", category.Name);
            command.Parameters.AddWithValue("$id", category.ColorId);
            command.ExecuteNonQuery();
            SharedSettings.Edit(connection, transaction, SharedSettings.CategoryPrefix + category.ColorId, JsonValue.Create(category.Name), now);
        }
        transaction.Commit();
        return renamed;
    }
}
