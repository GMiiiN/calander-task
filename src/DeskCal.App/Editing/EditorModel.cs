using System.Globalization;
using DeskCal.Core;

namespace DeskCal.App;

public enum RepeatKind { None, Daily, Weekly, Monthly, Yearly, Custom }

public sealed class EditorModel
{
    private EventRecord? _event;
    private TodoRecord? _todo;
    private string? _occurrenceId;
    private string _kind = "event";
    private int _durationDays;
    private bool _isOvernight;
    private RepeatKind _originalRepeat;

    private EditorModel() { }

    public string Kind
    {
        get => _kind;
        set { if (CanChangeKind) _kind = value; }
    }
    public bool IsNew => _event is null && _todo is null;
    public string Title { get; set; } = "";
    public DateOnly? Date { get; set; }
    public bool IsAllDay { get; set; } = true;
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public RepeatKind Repeat { get; set; }
    public string? ColorId { get; set; }
    public bool IsDday { get; set; }
    public string Memo { get; set; } = "";
    public string CreatedBy => _event?.CreatedBy ?? _todo?.CreatedBy ?? "user";
    public bool CanChangeKind => IsNew;
    public bool CanEditRepeat => Kind == "event" && RecurrenceInput.IsEditable(_event?.RecurrenceRule);
    public bool IsRecurring => Kind == "event" && (Repeat != RepeatKind.None || _occurrenceId is not null);

    public static EditorModel NewEvent(DateOnly date, TimeOnly? start)
    {
        var end = start?.AddHours(1, out _);
        return new()
        {
            Date = date, IsAllDay = start is null, StartTime = start, EndTime = end,
            _durationDays = start is not null && end < start ? 1 : 0, _isOvernight = start is not null && end < start
        };
    }

    public static EditorModel NewTodo(DateOnly? dueDate) => new() { _kind = "todo", Date = dueDate };

    public static EditorModel FromEvent(EventRecord record, string? occurrenceId)
    {
        var startDate = ParseDate(record.StartDate);
        var repeat = ReadRepeat(record.RecurrenceRule, startDate.DayOfWeek);
        return new()
        {
            _event = record, _occurrenceId = occurrenceId,
            _durationDays = ParseDate(record.EndDate).DayNumber - startDate.DayNumber,
            _isOvernight = record.StartTime is not null && ParseDate(record.EndDate).DayNumber - startDate.DayNumber == 1 &&
                string.CompareOrdinal(record.EndTime, record.StartTime) <= 0,
            Title = record.Title, Date = startDate, IsAllDay = record.StartTime is null,
            StartTime = ParseTime(record.StartTime), EndTime = ParseTime(record.EndTime),
            Repeat = repeat, _originalRepeat = repeat, ColorId = record.ColorId,
            IsDday = record.IsDday, Memo = record.Memo
        };
    }

    public static EditorModel FromTodo(TodoRecord record) => new()
    {
        _todo = record, _kind = "todo", Title = record.Title,
        Date = record.DueDate is null ? null : ParseDate(record.DueDate), Memo = record.Memo
    };

    public string? Save(EventRepository events, TodoRepository todos)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Title)) return "제목을 입력하세요.";
            if (Kind == "todo")
            {
                var dueDate = FormatDate(Date);
                _todo = _todo is null
                    ? todos.Create([new(Title, dueDate, Memo, "user")])[0]
                    : todos.Update(_todo.TodoUid, new()
                    {
                        Title = Changed(Title, _todo.Title), DueDate = Changed(dueDate, _todo.DueDate),
                        Memo = Changed(Memo, _todo.Memo)
                    });
                return null;
            }
            if (Kind != "event") return "일정 또는 할 일을 선택하세요.";
            if (Date is null) return "날짜를 입력하세요.";
            // 자정을 넘는 시간 일정(23:30–00:30 등)으로 열린 경우에만, 지금 시간이 여전히 자정을 넘는지로 기간을 다시 정한다.
            if (_isOvernight) _durationDays = !IsAllDay && EndTime <= StartTime ? 1 : 0;
            if (!IsAllDay)
            {
                if (StartTime is null || EndTime is null) return "시작 시간과 끝 시간을 입력하세요.";
                if (_durationDays == 0 && EndTime <= StartTime) return "끝 시간이 시작 시간보다 늦어야 합니다.";
            }
            var startDate = FormatDate(Date)!;
            var endDate = FormatDate(Date.Value.AddDays(_durationDays))!;
            var startTime = IsAllDay ? null : FormatTime(StartTime);
            var endTime = IsAllDay ? null : FormatTime(EndTime);
            if (_event is null)
            {
                _event = events.Create([new(Title, startDate)
                {
                    EndDate = endDate, StartTime = startTime, EndTime = endTime,
                    ColorId = ColorId, IsDday = IsDday, Memo = Memo, CreatedBy = "user",
                    Repeat = BuildRepeat()
                }])[0];
            }
            else
            {
                var repeatChanged = CanEditRepeat && Repeat != RepeatKind.Custom &&
                    (Repeat != _originalRepeat || Repeat == RepeatKind.Weekly && startDate != _event.StartDate);
                _event = events.Update(SeriesUid(), new()
                {
                    Title = Changed(Title, _event.Title), StartDate = Changed(startDate, _event.StartDate),
                    EndDate = Changed(endDate, _event.EndDate), StartTime = Changed(startTime, _event.StartTime),
                    EndTime = Changed(endTime, _event.EndTime), Memo = Changed(Memo, _event.Memo),
                    ColorId = Changed(ColorId, _event.ColorId),
                    IsDday = IsDday == _event.IsDday ? default : new(true, IsDday),
                    Repeat = repeatChanged ? new(true, BuildRepeat()) : default
                });
            }
            _originalRepeat = ReadRepeat(_event.RecurrenceRule, ParseDate(_event.StartDate).DayOfWeek);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Explain(exception, "저장하지 못했습니다");
        }
        catch (Exception)
        {
            return "저장하지 못했습니다. 입력 내용과 항목 상태를 확인한 뒤 다시 시도하세요.";
        }
    }

    public string? Delete(EventRepository events, TodoRepository todos, bool onlyThisOccurrence)
    {
        try
        {
            if (IsNew) return "아직 저장하지 않은 항목입니다.";
            if (_todo is not null) todos.Delete(_todo.TodoUid);
            else if (onlyThisOccurrence && _occurrenceId is not null) events.DeleteOccurrence(_occurrenceId);
            else events.Delete(SeriesUid());
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Explain(exception, "삭제하지 못했습니다");
        }
        catch (Exception)
        {
            return "삭제하지 못했습니다. 항목 상태를 확인한 뒤 다시 시도하세요.";
        }
    }

    // Core가 거부한 이유를 사용자가 해결할 수 있는 문장으로 바꾼다. 모르는 이유는 원문을 덧붙인다.
    private static string Explain(Exception exception, string action) => exception.Message switch
    {
        "Edit this recurrence in Google Calendar." => "Google 캘린더에서 만든 반복 규칙입니다. 반복은 Google 캘린더에서 바꾸세요.",
        "Edit in Google Calendar or resolve occurrence exceptions first." =>
            "따로 바뀐 회차(완료·삭제 등)가 있는 반복 일정은 시작일과 반복을 바꿀 수 없습니다. 그 회차들을 먼저 되돌리거나 Google 캘린더에서 바꾸세요.",
        "Restore the deleted event before editing." => "삭제된 일정입니다.",
        "Restore the series first." => "반복 일정 전체가 삭제된 상태입니다.",
        _ => $"{action}: {exception.Message}"
    };

    private string SeriesUid() => _occurrenceId is null ? _event!.EventUid : OccurrenceId.Parse(_occurrenceId).EventUid;

    private RecurrenceInput? BuildRepeat() => Repeat switch
    {
        RepeatKind.None => null,
        RepeatKind.Daily => new("daily"),
        RepeatKind.Weekly => new("weekly", Weekdays: [Date!.Value.DayOfWeek]),
        RepeatKind.Monthly => new("monthly"),
        RepeatKind.Yearly => new("yearly"),
        _ => throw new ArgumentException("반복 규칙을 선택하세요.")
    };

    private static RepeatKind ReadRepeat(string? rule, DayOfWeek weekday)
    {
        if (rule is null) return RepeatKind.None;
        if (!RecurrenceInput.IsEditable(rule)) return RepeatKind.Custom;
        var fields = rule[6..].Split(';').Select(part => part.Split('=')).ToDictionary(part => part[0], part => part[1]);
        if (fields.ContainsKey("UNTIL") || fields.ContainsKey("COUNT") ||
            fields.TryGetValue("INTERVAL", out var interval) && int.Parse(interval, CultureInfo.InvariantCulture) != 1)
            return RepeatKind.Custom;
        if (fields["FREQ"] == "WEEKLY")
        {
            string[] weekdays = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
            if (!fields.TryGetValue("BYDAY", out var days) || days != weekdays[(int)weekday]) return RepeatKind.Custom;
        }
        return fields["FREQ"] switch
        {
            "DAILY" => RepeatKind.Daily, "WEEKLY" => RepeatKind.Weekly,
            "MONTHLY" => RepeatKind.Monthly, "YEARLY" => RepeatKind.Yearly,
            _ => RepeatKind.Custom
        };
    }

    private static Patch<string?> Changed(string? value, string? previous) => value == previous ? default : new(true, value);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static TimeOnly? ParseTime(string? value) => value is null ? null : TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
    private static string? FormatDate(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? FormatTime(TimeOnly? value) => value?.ToString("HH:mm", CultureInfo.InvariantCulture);
}
