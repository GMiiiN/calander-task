namespace DeskCal.Core;

/// <summary>Google에 아직 올리지 않은 로컬 변경을 묻는다. 앱이 MCP 쓰기를 받으면 다음 정기 동기화(2분)를 기다리지 않고 바로 올리는 데 쓴다.</summary>
public static class PendingChanges
{
    /// <summary>
    /// <paramref name="since"/>(Validation.Timestamp 형식, UTC) 뒤에 바뀌고 아직 올리지 않은 일정·할 일이 있는지.
    /// 동기화 시작 시각을 넘기면, 그 동기화에서 실패해 남은 행에는 반응하지 않으므로 자기 신호로 동기화가 되풀이되지 않는다.
    /// </summary>
    public static bool ExistSince(Database database, string since)
    {
        using var connection = database.OpenConnection();
        using var command = Sql.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM event WHERE is_dirty=1 AND updated_at>$since) " +
            "OR EXISTS(SELECT 1 FROM todo WHERE is_dirty=1 AND push_state<>'unknown' AND updated_at>$since);",
            ("$since", since));
        return (long)command.ExecuteScalar()! != 0;
    }
}
