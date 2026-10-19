using System.Globalization;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>One occurrence of a calendar event, in local time. All-day events span whole local days.</summary>
internal sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string Location);

/// <summary>
/// Reads iCalendar feeds (RFC 5545), such as the secret iCal address of a Google calendar or a
/// published Outlook / iCloud calendar, and lists the occurrences inside a time window. Handles time
/// zones, all-day events, cancelled events, moved single occurrences and the common repeat rules
/// (daily / weekly / monthly / yearly with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH,
/// BYSETPOS). Pure .NET, no Windows APIs.
/// </summary>
internal static class IcsCalendar
{
    // A daily event that started decades ago still expands quickly; this only stops broken rules.
    private const int MaxIterations = 20000;

    public static List<CalendarEvent> Expand(string ics, DateTime from, DateTime to)
    {
        var (events, zoneOffsets) = Parse(ics);
        var zones = new ZoneResolver(zoneOffsets);
        var result = new List<CalendarEvent>();

        // Single occurrences that were moved or cancelled ("this event only" edits) replace the
        // generated ones; keyed by UID + the original start.
        var overrides = new HashSet<(string, DateTime)>();
        foreach (var item in events)
        {
            if (item.RecurrenceId is { } id && zones.ToUtc(id) is { } original)
                overrides.Add((item.Uid, original));
        }

        foreach (var item in events)
        {
            if (item.Start is not { } start || item.Cancelled) continue;
            var duration = Duration(item, start);

            if (item.Rule is not { } rule || item.RecurrenceId is not null)
            {
                Add(result, item, start, duration, zones, from, to);
                continue;
            }

            var excluded = new HashSet<DateTime>();
            foreach (var date in item.Exceptions)
            {
                if (zones.ToUtc(date) is { } utc) excluded.Add(utc);
                if (date.DateOnly) excluded.Add(date.Wall.Date); // all-day exceptions match by date
            }

            // Expand in the event's own wall-clock time, so "every Monday 9:00" stays at 9:00 across DST.
            DateTime windowStart = zones.ToZoneWall(from.AddDays(-1) - duration, start);
            DateTime windowEnd = zones.ToZoneWall(to.AddDays(1), start);
            foreach (var wall in Occurrences(rule, start.Wall, windowStart, windowEnd, zones, start))
            {
                var occurrence = start with { Wall = wall };
                var utc = zones.ToUtc(occurrence);
                if (utc is { } instant && (excluded.Contains(instant) || overrides.Contains((item.Uid, instant)))) continue;
                if (start.DateOnly && excluded.Contains(wall.Date)) continue;
                Add(result, item, occurrence, duration, zones, from, to);
            }
        }

        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.AllDay.CompareTo(a.AllDay));
        return result;
    }

    private static TimeSpan Duration(IcsEvent item, IcsTime start)
    {
        if (item.End is { } end)
        {
            var span = end.Wall - start.Wall;
            if (span >= TimeSpan.Zero) return span;
        }
        if (item.Duration is { } duration) return duration;
        return start.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private static void Add(List<CalendarEvent> result, IcsEvent item, IcsTime start, TimeSpan duration, ZoneResolver zones, DateTime from, DateTime to)
    {
        DateTime localStart, localEnd;
        if (start.DateOnly)
        {
            localStart = start.Wall.Date;
            localEnd = localStart + (duration < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : duration);
        }
        else
        {
            localStart = zones.ToLocal(start);
            localEnd = zones.ToLocal(start with { Wall = start.Wall + duration });
        }

        // Overlaps the window (zero-length events count when they start inside it).
        if (localStart >= to || (localEnd <= from && !(localEnd == localStart && localStart >= from))) return;
        result.Add(new CalendarEvent(item.Summary, localStart, localEnd, start.DateOnly, item.Location));
    }

    // ───────────────────────────── recurrence ─────────────────────────────

    private static IEnumerable<DateTime> Occurrences(RecurrenceRule rule, DateTime start, DateTime windowStart, DateTime windowEnd, ZoneResolver zones, IcsTime startTime)
    {
        int count = 0;
        long period = 0;

        // Without COUNT the occurrences before the window don't matter: jump close to it.
        if (rule.Count is null && windowStart > start)
            period = Math.Max(0, PeriodsBetween(rule.Frequency, start, windowStart) / rule.Interval - 1);

        for (int iteration = 0; iteration < MaxIterations; iteration++, period++)
        {
            var candidates = Candidates(rule, start, period * rule.Interval);
            if (candidates is null) yield break; // past the end of the calendar

            foreach (var date in candidates)
            {
                var occurrence = date.Date + start.TimeOfDay;
                if (occurrence < start) continue;
                if (rule.Until is { } until && IsAfterUntil(occurrence, until, zones, startTime)) yield break;
                count++;
                if (rule.Count is { } limit && count > limit) yield break;
                if (occurrence > windowEnd) yield break;
                if (occurrence >= windowStart) yield return occurrence;
            }

            if (PeriodStart(rule.Frequency, start, period * rule.Interval) > windowEnd) yield break;
        }
    }

    private static bool IsAfterUntil(DateTime occurrence, IcsTime until, ZoneResolver zones, IcsTime start)
    {
        if (until.DateOnly) return occurrence.Date > until.Wall.Date;
        if (until.Utc && zones.ToUtc(start with { Wall = occurrence }) is { } utc) return utc > until.Wall;
        return occurrence > until.Wall;
    }

    private static long PeriodsBetween(Frequency frequency, DateTime start, DateTime target) => frequency switch
    {
        Frequency.Daily => (long)(target.Date - start.Date).TotalDays,
        Frequency.Weekly => (long)(target.Date - start.Date).TotalDays / 7,
        Frequency.Monthly => (target.Year - start.Year) * 12L + target.Month - start.Month,
        _ => target.Year - start.Year,
    };

    private static DateTime PeriodStart(Frequency frequency, DateTime start, long offset)
    {
        try
        {
            return frequency switch
            {
                Frequency.Daily => start.Date.AddDays(offset),
                Frequency.Weekly => start.Date.AddDays(offset * 7),
                Frequency.Monthly => new DateTime(start.Year, start.Month, 1).AddMonths((int)offset),
                _ => new DateTime(start.Year, 1, 1).AddYears((int)offset),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue;
        }
    }

    /// <summary>Dates of one period (a day, a week, a month or a year), in order; null past year 9998.</summary>
    private static List<DateTime>? Candidates(RecurrenceRule rule, DateTime start, long offset)
    {
        var periodStart = PeriodStart(rule.Frequency, start, offset);
        if (periodStart.Year >= 9998) return null;
        var dates = new List<DateTime>();

        switch (rule.Frequency)
        {
            case Frequency.Daily:
                dates.Add(periodStart);
                break;

            case Frequency.Weekly:
            {
                // The week containing this period's day, starting on WKST (Monday by default).
                int back = ((int)periodStart.DayOfWeek - (int)rule.WeekStart + 7) % 7;
                var weekStart = periodStart.AddDays(-back);
                if (rule.ByDay.Count == 0)
                {
                    dates.Add(periodStart);
                }
                else
                {
                    for (int i = 0; i < 7; i++)
                    {
                        var day = weekStart.AddDays(i);
                        if (rule.ByDay.Any(d => d.Day == day.DayOfWeek)) dates.Add(day);
                    }
                }
                break;
            }

            case Frequency.Monthly:
                dates.AddRange(DaysInMonth(rule, periodStart.Year, periodStart.Month, start.Day));
                break;

            default:
                foreach (int month in rule.ByMonth.Count > 0 ? rule.ByMonth : [start.Month])
                    dates.AddRange(DaysInMonth(rule, periodStart.Year, month, start.Day));
                break;
        }

        // Filters that narrow a broader frequency (e.g. FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR).
        if (rule.Frequency == Frequency.Daily && rule.ByDay.Count > 0)
            dates.RemoveAll(d => !rule.ByDay.Any(b => b.Day == d.DayOfWeek));
        if (rule.Frequency is Frequency.Daily or Frequency.Weekly or Frequency.Monthly && rule.ByMonth.Count > 0)
            dates.RemoveAll(d => !rule.ByMonth.Contains(d.Month));
        if (rule.Frequency == Frequency.Daily && rule.ByMonthDay.Count > 0)
            dates.RemoveAll(d => !rule.ByMonthDay.Any(m => m == d.Day || m == d.Day - DateTime.DaysInMonth(d.Year, d.Month) - 1));

        dates.Sort();
        if (rule.BySetPos.Count > 0 && rule.Frequency is not Frequency.Daily)
        {
            var picked = new List<DateTime>();
            foreach (int position in rule.BySetPos)
            {
                int index = position > 0 ? position - 1 : dates.Count + position;
                if (index >= 0 && index < dates.Count) picked.Add(dates[index]);
            }
            picked.Sort();
            return picked;
        }
        return dates;
    }

    private static IEnumerable<DateTime> DaysInMonth(RecurrenceRule rule, int year, int month, int defaultDay)
    {
        int length = DateTime.DaysInMonth(year, month);
        var days = new SortedSet<int>();

        if (rule.ByDay.Count > 0)
        {
            foreach (var (ordinal, weekday) in rule.ByDay)
            {
                var matches = Enumerable.Range(1, length).Where(d => new DateTime(year, month, d).DayOfWeek == weekday).ToList();
                if (ordinal == 0) days.UnionWith(matches);
                else if (ordinal > 0 && ordinal <= matches.Count) days.Add(matches[ordinal - 1]);
                else if (ordinal < 0 && -ordinal <= matches.Count) days.Add(matches[matches.Count + ordinal]);
            }
            if (rule.ByMonthDay.Count > 0)
                days.IntersectWith(rule.ByMonthDay.Select(d => d > 0 ? d : length + d + 1));
        }
        else if (rule.ByMonthDay.Count > 0)
        {
            foreach (int day in rule.ByMonthDay)
            {
                int actual = day > 0 ? day : length + day + 1;
                if (actual >= 1 && actual <= length) days.Add(actual);
            }
        }
        else if (defaultDay <= length)
        {
            days.Add(defaultDay); // "every month on the 31st" skips shorter months, as the RFC says
        }

        return days.Select(d => new DateTime(year, month, d));
    }

    // ───────────────────────────── parsing ─────────────────────────────

    private static (List<IcsEvent> Events, Dictionary<string, TimeSpan> ZoneOffsets) Parse(string ics)
    {
        var events = new List<IcsEvent>();
        var zoneOffsets = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        IcsEvent? current = null;
        string? zoneId = null;

        foreach (var line in Unfold(ics))
        {
            if (!TryParseLine(line, out string name, out var parameters, out string value)) continue;

            if (name == "BEGIN")
            {
                string component = value.Trim().ToUpperInvariant();
                stack.Push(component);
                if (component == "VEVENT") current = new IcsEvent();
                continue;
            }
            if (name == "END")
            {
                string component = value.Trim().ToUpperInvariant();
                if (stack.Count > 0) stack.Pop();
                if (component == "VEVENT" && current is not null)
                {
                    events.Add(current);
                    current = null;
                }
                if (component == "VTIMEZONE") zoneId = null;
                continue;
            }

            string scope = stack.Count > 0 ? stack.Peek() : "";
            if (scope == "VTIMEZONE" && name == "TZID")
            {
                zoneId = value.Trim();
            }
            else if (scope == "STANDARD" && name == "TZOFFSETTO" && zoneId is not null && TryParseOffset(value, out var offset))
            {
                zoneOffsets.TryAdd(zoneId, offset); // fallback when the zone name is unknown to Windows
            }
            else if (scope == "VEVENT" && current is not null)
            {
                ApplyProperty(current, name, parameters, value);
            }
        }
        return (events, zoneOffsets);
    }

    private static void ApplyProperty(IcsEvent item, string name, Dictionary<string, string> parameters, string value)
    {
        switch (name)
        {
            case "UID":
                item.Uid = value.Trim();
                break;
            case "SUMMARY":
                item.Summary = Unescape(value);
                break;
            case "LOCATION":
                item.Location = Unescape(value);
                break;
            case "STATUS":
                item.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                break;
            case "DTSTART":
                item.Start = ParseTime(value, parameters);
                break;
            case "DTEND":
                item.End = ParseTime(value, parameters);
                break;
            case "DURATION":
                item.Duration = ParseDuration(value);
                break;
            case "RECURRENCE-ID":
                item.RecurrenceId = ParseTime(value, parameters);
                break;
            case "RRULE":
                item.Rule = RecurrenceRule.Parse(value, parameters);
                break;
            case "EXDATE":
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (ParseTime(part, parameters) is { } date) item.Exceptions.Add(date);
                }
                break;
        }
    }

    /// <summary>Lines longer than 75 bytes continue on the next line, which then starts with a space or tab.</summary>
    private static IEnumerable<string> Unfold(string ics)
    {
        var builder = new StringBuilder();
        foreach (var raw in ics.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                builder.Append(line, 1, line.Length - 1);
                continue;
            }
            if (builder.Length > 0) yield return builder.ToString();
            builder.Clear().Append(line);
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    /// <summary>NAME;PARAM=VALUE;PARAM="QUOTED:VALUE":property value</summary>
    private static bool TryParseLine(string line, out string name, out Dictionary<string, string> parameters, out string value)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        name = value = "";

        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0) return false;

        var head = line[..colon].Split(';');
        name = head[0].Trim().ToUpperInvariant();
        foreach (var parameter in head.Skip(1))
        {
            int equals = parameter.IndexOf('=');
            if (equals > 0) parameters[parameter[..equals].Trim()] = parameter[(equals + 1)..].Trim().Trim('"');
        }
        value = line[(colon + 1)..];
        return true;
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                builder.Append(next is 'n' or 'N' ? ' ' : next);
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString().Trim();
    }

    internal static IcsTime? ParseTime(string value, Dictionary<string, string> parameters)
    {
        value = value.Trim();
        bool utc = value.EndsWith('Z');
        if (utc) value = value[..^1];

        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new IcsTime(date, null, Utc: false, DateOnly: true);

        if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;
        parameters.TryGetValue("TZID", out var zone);
        return new IcsTime(time, utc ? null : zone, utc, DateOnly: false);
    }

    /// <summary>P1D, PT1H30M, -PT15M, P2W...</summary>
    private static TimeSpan? ParseDuration(string value)
    {
        value = value.Trim().ToUpperInvariant();
        bool negative = value.StartsWith('-');
        value = value.TrimStart('+', '-');
        if (!value.StartsWith('P')) return null;

        TimeSpan total = TimeSpan.Zero;
        bool inTime = false;
        int number = 0;
        foreach (char c in value[1..])
        {
            if (char.IsDigit(c))
            {
                number = number * 10 + (c - '0');
                continue;
            }
            switch (c)
            {
                case 'T': inTime = true; break;
                case 'W': total += TimeSpan.FromDays(7 * number); break;
                case 'D': total += TimeSpan.FromDays(number); break;
                case 'H': total += TimeSpan.FromHours(number); break;
                case 'M' when inTime: total += TimeSpan.FromMinutes(number); break;
                case 'S': total += TimeSpan.FromSeconds(number); break;
            }
            number = 0;
        }
        return negative ? -total : total;
    }

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        value = value.Trim();
        if (value.Length < 5 || (value[0] != '+' && value[0] != '-')) return false;
        if (!int.TryParse(value.AsSpan(1, 2), out int hours) || !int.TryParse(value.AsSpan(3, 2), out int minutes)) return false;
        offset = new TimeSpan(hours, minutes, 0);
        if (value[0] == '-') offset = -offset;
        return true;
    }

    // ───────────────────────────── types ─────────────────────────────

    /// <summary>A date-time as written in the feed: wall clock + zone (null zone and not UTC = "floating", i.e. local).</summary>
    internal readonly record struct IcsTime(DateTime Wall, string? Zone, bool Utc, bool DateOnly);

    private sealed class IcsEvent
    {
        public string Uid { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Location { get; set; } = "";
        public bool Cancelled { get; set; }
        public IcsTime? Start { get; set; }
        public IcsTime? End { get; set; }
        public TimeSpan? Duration { get; set; }
        public IcsTime? RecurrenceId { get; set; }
        public RecurrenceRule? Rule { get; set; }
        public List<IcsTime> Exceptions { get; } = [];
    }

    private enum Frequency
    {
        Daily,
        Weekly,
        Monthly,
        Yearly,
    }

    private sealed class RecurrenceRule
    {
        public Frequency Frequency { get; private init; }
        public int Interval { get; private init; } = 1;
        public int? Count { get; private init; }
        public IcsTime? Until { get; private init; }
        public DayOfWeek WeekStart { get; private init; } = DayOfWeek.Monday;
        public List<(int Ordinal, DayOfWeek Day)> ByDay { get; } = [];
        public List<int> ByMonthDay { get; } = [];
        public List<int> ByMonth { get; } = [];
        public List<int> BySetPos { get; } = [];

        /// <summary>FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;UNTIL=20261231T235959Z — null for unsupported frequencies.</summary>
        public static RecurrenceRule? Parse(string value, Dictionary<string, string> parameters)
        {
            var parts = value.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim().ToUpperInvariant());

            Frequency? frequency = parts.GetValueOrDefault("FREQ") switch
            {
                "DAILY" => Frequency.Daily,
                "WEEKLY" => Frequency.Weekly,
                "MONTHLY" => Frequency.Monthly,
                "YEARLY" => Frequency.Yearly,
                _ => null, // HOURLY / MINUTELY / SECONDLY: not something a day planner shows
            };
            if (frequency is null) return null;

            var rule = new RecurrenceRule
            {
                Frequency = frequency.Value,
                Interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out int interval) && interval > 0 ? interval : 1,
                Count = int.TryParse(parts.GetValueOrDefault("COUNT"), out int count) && count > 0 ? count : null,
                Until = parts.TryGetValue("UNTIL", out var until) ? ParseTime(until, parameters) : null,
                WeekStart = Weekday(parts.GetValueOrDefault("WKST") ?? "MO") ?? DayOfWeek.Monday,
            };

            foreach (var day in List(parts, "BYDAY"))
            {
                int split = day.Length - 2;
                if (split < 0 || Weekday(day[split..]) is not { } weekday) continue;
                int ordinal = split > 0 && int.TryParse(day[..split], out int n) ? n : 0;
                rule.ByDay.Add((ordinal, weekday));
            }
            rule.ByMonthDay.AddRange(Numbers(parts, "BYMONTHDAY").Where(d => d != 0 && Math.Abs(d) <= 31));
            rule.ByMonth.AddRange(Numbers(parts, "BYMONTH").Where(m => m is >= 1 and <= 12));
            rule.BySetPos.AddRange(Numbers(parts, "BYSETPOS").Where(p => p != 0));
            return rule;
        }

        private static IEnumerable<string> List(Dictionary<string, string> parts, string key) =>
            parts.TryGetValue(key, out var value) ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

        private static IEnumerable<int> Numbers(Dictionary<string, string> parts, string key) =>
            List(parts, key).Select(v => int.TryParse(v, out int n) ? n : 0);

        private static DayOfWeek? Weekday(string code) => code switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday,
            _ => null,
        };
    }

    /// <summary>
    /// Maps TZID names to time zones: IANA ("Asia/Jakarta", Google) and Windows ("SE Asia Standard
    /// Time", Outlook) names both work; unknown names fall back to the feed's own VTIMEZONE offset.
    /// </summary>
    private sealed class ZoneResolver(Dictionary<string, TimeSpan> offsets)
    {
        private readonly Dictionary<string, TimeZoneInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public DateTime ToLocal(IcsTime time)
        {
            if (time.DateOnly) return time.Wall.Date;
            if (time.Utc) return TimeZoneInfo.ConvertTimeFromUtc(time.Wall, TimeZoneInfo.Local);
            if (Find(time.Zone) is not { } zone) return time.Wall; // floating time: already local
            return TimeZoneInfo.ConvertTimeFromUtc(ToUtc(time.Wall, zone), TimeZoneInfo.Local);
        }

        public DateTime? ToUtc(IcsTime time)
        {
            if (time.DateOnly) return null;
            if (time.Utc) return DateTime.SpecifyKind(time.Wall, DateTimeKind.Utc);
            return ToUtc(time.Wall, Find(time.Zone) ?? TimeZoneInfo.Local);
        }

        /// <summary>A local moment expressed as wall-clock time in the zone of <paramref name="reference"/>.</summary>
        public DateTime ToZoneWall(DateTime local, IcsTime reference)
        {
            if (reference.DateOnly) return local;
            var utc = ToUtc(local, TimeZoneInfo.Local);
            if (reference.Utc) return utc;
            return Find(reference.Zone) is { } zone ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone) : local;
        }

        private static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1); // skipped by a DST jump: the hour after
            return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
        }

        private TimeZoneInfo? Find(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache.TryGetValue(name, out var cached)) return cached;

            TimeZoneInfo? zone = null;
            foreach (var candidate in Candidates(name))
            {
                if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out zone)) break;
            }
            if (zone is null && offsets.TryGetValue(name, out var offset))
                zone = TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name);

            _cache[name] = zone;
            return zone;
        }

        // "/mozilla.org/20050126_1/Europe/Berlin" style prefixes: try the trailing Area/City part too.
        private static IEnumerable<string> Candidates(string name)
        {
            yield return name;
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int take = Math.Min(3, parts.Length - 1); take >= 1; take--)
                yield return string.Join('/', parts[^take..]);
        }
    }
}
