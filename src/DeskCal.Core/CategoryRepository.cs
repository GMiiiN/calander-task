namespace DeskCal.Core;

public sealed record CategoryRecord(string ColorId, string Name, string ColorHex);

// 카테고리 = Google 이벤트 색(§4). 색 목록은 Google이 정하고, 이름만 사용자가 바꾼다.
public sealed class CategoryRepository(Database database)
{
    public IReadOnlyList<CategoryRecord> List()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT color_id, name, color_hex FROM category ORDER BY CAST(color_id AS INTEGER), color_id;";
        using var reader = command.ExecuteReader();
        var result = new List<CategoryRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    // 이름은 비울 수 없고, 서로 같거나 다른 카테고리의 color_id와 같을 수 없다. MCP가 이름이나 color_id로 카테고리를 찾기 때문이다.
    public void Rename(IReadOnlyDictionary<string, string> names)
    {
        var current = List();
        if (names.Keys.Any(id => current.All(c => c.ColorId != id))) throw new ArgumentException("Category not found.");
        var renamed = current.Select(c => c with { Name = names.TryGetValue(c.ColorId, out var name) ? name.Trim() : c.Name }).ToArray();
        if (renamed.Any(c => c.Name.Length == 0)) throw new ArgumentException("Category name is required.");
        if (renamed.DistinctBy(c => c.Name, StringComparer.Ordinal).Count() != renamed.Length)
            throw new ArgumentException("Category names must be unique.");
        if (renamed.Any(c => current.Any(other => other.ColorId != c.ColorId && other.ColorId == c.Name)))
            throw new ArgumentException("Category name must not be another category's color_id.");

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var category in renamed)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE category SET name = $name WHERE color_id = $id;";
            command.Parameters.AddWithValue("$name", category.Name);
            command.Parameters.AddWithValue("$id", category.ColorId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
