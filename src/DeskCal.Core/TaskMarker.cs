namespace DeskCal.Core;

// 로컬 UID는 DB에 두고 Google에는 무작위 생성 시도 식별자만 보낸다.
// 표식은 메모 끝에 빈 줄을 하나 두고 붙인다. 연결이 확인되면 TaskSync가 Google 메모에서 지운다.
internal static class TaskMarker
{
    private const string Open = "[DeskCal:";
    private const string Separator = "\n\n";
    private const int TokenLength = 32;
    private const int NotesLimit = 8192; // Google Tasks 메모 한도

    // 끝에 이미 표식이 있으면 떼고 하나만 붙인다(같은 메모에 몇 번을 붙여도 표식은 하나다).
    // 메모가 길어 표식을 담을 수 없으면 원문 전체를 우선한다.
    public static string Add(string memo, string? token)
    {
        if (token is null) return memo;
        var notes = Read(memo).Memo + Separator + Open + token + "]";
        return notes.Length <= NotesLimit ? notes : memo;
    }

    // 다른 앱이 줄바꿈을 CRLF로 바꾸거나 앞뒤 공백을 정리해도 끝의 표식을 읽는다.
    // 붙일 때 넣은 빈 줄은 정확히 떼어 원래 메모 끝의 공백·줄바꿈을 보존한다.
    public static (string Memo, string? Token) Read(string? notes)
    {
        var text = notes ?? "";
        var end = text.TrimEnd();
        var start = end.Length - Open.Length - TokenLength - 1;
        if (start < 0 || !end.EndsWith(']') || !end.AsSpan(start).StartsWith(Open, StringComparison.Ordinal))
            return (text, null);
        var token = end.Substring(start + Open.Length, TokenLength);
        if (!token.All(char.IsAsciiHexDigitLower)) return (text, null);
        var before = end[..start];
        var first = LineBreak(before, before.Length);
        var second = first > 0 ? LineBreak(before, before.Length - first) : 0;
        if (second > 0) return (before[..(before.Length - first - second)], token);
        // 앞 공백이 정리되거나 바뀐 경우. 표식은 메모 처음이거나 공백 바로 뒤에 있어야 한다(본문 중간의 글자는 표식이 아니다).
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1])) return (text, null);
        return (before.TrimEnd(), token);
    }

    private static int LineBreak(string text, int end) =>
        end >= 2 && text[end - 2] == '\r' && text[end - 1] == '\n' ? 2 : end >= 1 && text[end - 1] == '\n' ? 1 : 0;
}
