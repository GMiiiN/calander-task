namespace DeskCal.Core;

// 로컬 UID는 DB에 두고 Google에는 무작위 생성 시도 식별자만 보낸다.
internal static class TaskMarker
{
    private const string Prefix = "\n\n[DeskCal:";
    public static string Add(string memo, string? token)
    {
        var suffix = Prefix + token + "]";
        return token is not null && memo.Length + suffix.Length <= 8192 ? memo + suffix : memo;
    }

    public static (string Memo, string? Token) Read(string? notes)
    {
        var text = notes ?? "";
        var offset = text.Length - Prefix.Length - 33;
        if (offset < 0 || !text.EndsWith(']') || !text.AsSpan(offset).StartsWith(Prefix, StringComparison.Ordinal))
            return (text, null);
        var token = text.Substring(offset + Prefix.Length, 32);
        return token.All(char.IsAsciiHexDigitLower) ? (text[..offset], token) : (text, null);
    }
}
