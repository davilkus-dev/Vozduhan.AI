using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Vozduhan.AI;

/// <summary>
/// G3: a pool of ideas for spontaneous drops. Generated once a day by the model (kind "idea"), checked by code; a drop takes
/// an idea whose conditions hold at its due moment. An idea about people goes out only after the developer's ✅.
/// </summary>
public sealed partial class BotWorker
{
    public const int IdeaTextMax = 300, IdeaConditionsMax = 5, IdeaPeopleMax = 5;
    private static readonly TimeOnly IdeaTime = new(7, 0);
    public static readonly string[] IdeaFlags = ["any", "no_lessons_today", "morning", "evening", "friday_evening", "exam_period"];

    /// <summary>A condition from the dictionary in its canonical form; null — unknown or with a wrong parameter.</summary>
    internal string? IdeaCondition(string? raw)
    {
        var c = (raw ?? "").Trim();
        if (IdeaFlags.Contains(c.ToLowerInvariant())) return c.ToLowerInvariant();
        var parts = c.Split(':', 2);
        if (parts.Length != 2) return null;
        var (name, value) = (parts[0].Trim().ToLowerInvariant(), parts[1].Trim());
        var r = config.R;
        return name switch
        {
            "weekday" when int.TryParse(value, out var d) && d is >= 1 and <= 7 => $"weekday:{d}",
            "before_deadline" when int.TryParse(value, out var h) && h is >= 1 and <= 24 * 14 => $"before_deadline:{h}",
            "after_lesson" or "lesson_today" when r.Subject(value) is { Archived: false } s => $"{name}:{s.Id}",
            "person" when r.Members.FirstOrDefault(m => m.Active && m.Key == value) is { } member => $"person:{member.Key}",
            _ => null
        };
    }

    /// <summary>Whether a condition holds at this moment (code only).</summary>
    internal bool IdeaHolds(string condition, DateTimeOffset at)
    {
        var r = config.R;
        var local = config.LocalNow(at);
        var day = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        var lessons = Schedule.Day(r, day).Where(s => s.Status != "cancelled").ToList();
        bool Evening() => time >= new TimeOnly(17, 0);
        var parts = condition.Split(':', 2);
        var value = parts.ElementAtOrDefault(1) ?? "";
        switch (parts[0])
        {
            case "any": return true;
            case "morning": return time >= new TimeOnly(6, 0) && time < new TimeOnly(12, 0);
            case "evening": return Evening();
            case "friday_evening": return Schedule.Dow(day) == 5 && Evening();
            case "no_lessons_today": return lessons.Count == 0;
            case "exam_period":
                return DateOnly.TryParseExact(r.TermEnd, "yyyy-MM-dd", out var end) && day >= end.AddDays(-7) && day <= end.AddDays(30);
            case "weekday": return int.TryParse(value, out var d) && Schedule.Dow(day) == d;
            case "lesson_today": return lessons.Any(s => s.SubjectId == value);
            case "after_lesson": return lessons.Any(s => s.SubjectId == value && TimeOnly.Parse(s.End) <= time);
            case "before_deadline":
                return int.TryParse(value, out var h) && store.AllDeadlines().Any(x => x.Status == "active" &&
                    config.Utc(DateOnly.Parse(x.Date).ToDateTime(TimeOnly.Parse(x.Start))) is var start && start > at && start <= at.AddHours(h));
            case "person":
                var id = r.Members.FirstOrDefault(m => m.Active && m.Key == value)?.UserId;
                var since = Math.Min(at.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 3 * 3600;
                return id is not null && store.History(config.GroupId, config.ChatTopicId, int.MaxValue, 100)
                    .Any(raw => JsonSerializer.Deserialize<JsonElement>(raw) is var x && x.Field("from").Number("id") == id && x.Number("date") >= since);
            default: return false;
        }
    }

    private static string IdeaKey(string text) => Regex.Replace(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+", " ").Trim();
    internal static double IdeaScore(string[] conditions) => conditions.Count(c => c != "any");

    /// <summary>Every condition the web admin offers, with a Russian label.</summary>
    internal List<string[]> IdeaOptions()
    {
        var r = config.R;
        var list = new List<string[]>
        {
            new[] { "any", "в любой момент" }, new[] { "morning", "утром" }, new[] { "evening", "вечером" }, new[] { "friday_evening", "в пятницу вечером" },
            new[] { "no_lessons_today", "в день без пар" }, new[] { "exam_period", "в сессию" }
        };
        list.AddRange(Enumerable.Range(1, 7).Select(d => new[] { $"weekday:{d}", Schedule.DayNames[d] }));
        list.AddRange(new[] { 6, 24, 48, 72, 168 }.Select(h => new[] { $"before_deadline:{h}", $"дедлайн ближе {h} ч" }));
        foreach (var s in r.Subjects.Where(x => !x.Archived))
            list.AddRange([new[] { $"lesson_today:{s.Id}", $"сегодня есть {s.Name}" }, new[] { $"after_lesson:{s.Id}", $"после пары {s.Name}" }]);
        list.AddRange(r.Members.Where(m => m.Active).Select(m => new[] { $"person:{m.Key}", $"{m.ShortName} недавно писал(а) в чате" }));
        return list;
    }

    /// <summary>
    /// Checks one idea: text by length and TextGuard, conditions from the dictionary (unknown ones dropped, none left — any),
    /// people from the roster (unknown ones dropped; an idea about nobody known is rejected). strict: any bad item is an error.
    /// </summary>
    internal (string Text, string[] Conditions, string[] People)? CleanIdea(string? text, IEnumerable<string?> conditions, IEnumerable<string?> people, bool strict, out string? error)
    {
        error = null;
        var t = TextGuard.Normalize(text ?? "");
        if (t.Length is 0 or > IdeaTextMax || TextGuard.Check(t, "", IdeaTextMax) is not null)
        {
            error = $"Текст идеи — по-русски, от 1 до {IdeaTextMax} символов."; return null;
        }
        var rawConditions = conditions.ToList();
        if (strict && rawConditions.Any(c => !string.IsNullOrWhiteSpace(c) && IdeaCondition(c) is null)) { error = "Условие не из словаря."; return null; }
        var cs = rawConditions.Select(IdeaCondition).OfType<string>().Distinct().Take(IdeaConditionsMax).ToList();
        if (cs.Count > 1) cs.Remove("any");
        if (cs.Count == 0) cs.Add("any");
        var keys = config.R.Members.Where(m => m.Active).Select(m => m.Key).ToHashSet();
        var rawPeople = people.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        var ps = rawPeople.Where(keys.Contains).Distinct().Take(IdeaPeopleMax).ToArray();
        if (rawPeople.Count > 0 && (ps.Length == 0 || (strict && ps.Length != rawPeople.Distinct().Count()))) { error = "Неизвестный участник."; return null; }
        return (t, cs.ToArray(), ps);
    }

    /// <summary>Once a day in the morning, in the background.</summary>
    internal async Task IdeaTick(DateTimeOffset utc, CancellationToken ct)
    {
        if (!config.Features.Ideas) return;
        var local = config.LocalNow(utc);
        var key = $"ideas:generated:{DateOnly.FromDateTime(local):yyyy-MM-dd}";
        if (TimeOnly.FromDateTime(local) < IdeaTime || store.Get(key) is not null) return;
        store.Set(key, "1");
        await GenerateIdeas(utc, ct);
    }

    /// <summary>Asks the model for ideas and keeps the ones that pass the checks. Returns how many were added.</summary>
    internal async Task<int> GenerateIdeas(DateTimeOffset utc, CancellationToken ct)
    {
        var r = config.R;
        var f = config.Features;
        var today = DateOnly.FromDateTime(config.LocalNow(utc));
        var all = store.Ideas();
        var context = new
        {
            local_now = config.LocalNow(utc).ToString("yyyy-MM-dd HH:mm dddd", new System.Globalization.CultureInfo("ru-RU")),
            count = f.IdeasPerDay,
            schedule = Enumerable.Range(0, 7).Select(i => today.AddDays(i)).Select(d => new
            {
                date = d.ToString("yyyy-MM-dd"), weekday = Schedule.DayNames[Schedule.Dow(d)],
                lessons = Schedule.Day(r, d).Where(s => s.Status != "cancelled").Select(s => $"{s.Period} {s.Start}–{s.End} {s.Subject} [{s.SubjectId}] {Schedule.KindRu(s.Kind)}")
            }),
            subjects = r.Subjects.Where(s => !s.Archived).Select(s => new { s.Id, s.Name }),
            deadlines = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) > utc).Take(20).Select(conversation.DeadlineView),
            term_end = r.TermEnd,
            group = r.Members.Where(m => m.Active).Select(m => new { m.Key, short_name = m.ShortName, m.FullName, username = m.Username }),
            people_notes = r.Members.Where(m => m.Active && !string.IsNullOrWhiteSpace(m.AdminNote)).Select(m => new { m.Key, note = m.AdminNote }),
            recent_bot_posts = conversation.RecentPosts(),
            existing_ideas = all.Where(i => i.Status is "new" or "approved" or "used").Take(60).Select(i => i.Text)
        };
        var answer = await ai.Ideas(context, ct, "ideas:" + today.ToString("yyyy-MM-dd"));
        using var doc = JsonDocument.Parse(answer);
        var list = doc.RootElement.Field("ideas");
        if (list.ValueKind != JsonValueKind.Array) return 0;
        static IEnumerable<string?> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null) : [];
        var seen = all.Select(i => IdeaKey(i.Text)).ToHashSet();
        var now = utc.ToUnixTimeSeconds();
        var added = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (added >= f.IdeasPerDay) break;
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (CleanIdea(item.Text("text"), Strings(item.Field("conditions")), Strings(item.Field("people")), false, out _) is not { } idea || !seen.Add(IdeaKey(idea.Text))) continue;
            store.AddIdea(idea.Text, idea.Conditions, idea.People, "ai", "new", IdeaScore(idea.Conditions), now, now + f.IdeaTtlDays * 86400L);
            added++;
        }
        log.LogInformation("Ideas generated: {Added}", added);
        return added;
    }

    /// <summary>A free idea whose conditions hold at this moment and whose people were not mentioned within the cooldown.</summary>
    internal Idea? PickIdea(DateTimeOffset at)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ideas = store.Ideas();
        var cooldown = now - config.Features.PersonCooldownDays * 86400L;
        var busy = ideas.Where(i => (i.Status == "used" && i.UsedAt >= cooldown) || (i.Status is "new" or "approved" && i.PublicationKey is not null))
            .SelectMany(i => i.People).ToHashSet();
        return ideas.Where(i => i.Status is "new" or "approved" && i.PublicationKey is null && i.Expires > now)
            .Where(i => !i.People.Any(busy.Contains) && i.Conditions.All(c => IdeaHolds(c, at)))
            .OrderByDescending(i => i.Status == "approved").ThenByDescending(i => i.Score).ThenBy(i => i.Id).FirstOrDefault();
    }

    /// <summary>
    /// The extra of a spontaneous drop: with the flag, the idea it is written by (the same one on a regeneration). An idea about people
    /// makes the drop manual; the mark stays with the drop (kv) even if the idea returns to the pool.
    /// </summary>
    private object SpontExtra(Publication p, DateTimeOffset at, string forTime, string? previous)
    {
        var moment = new { for_date = EventDay(p).ToString("yyyy-MM-dd"), for_time = forTime, previous_variant = previous };
        if (!config.Features.Ideas) return moment;
        var idea = store.IdeaFor(p.Key);
        if (idea is null && PickIdea(at) is { } pick && store.LinkIdea(pick.Id, p.Key)) idea = pick;
        if (idea is null) return moment;
        store.Set("idea_for:" + p.Key, idea.Id.ToString());
        if (idea.People.Length > 0) store.SetPublicationMode(p.Key, ManualMode);
        var names = idea.People.Select(k => config.R.Members.FirstOrDefault(m => m.Key == k)?.ShortName).OfType<string>();
        return new { moment.for_date, moment.for_time, moment.previous_variant, idea = new { text = idea.Text, people = names } };
    }

    /// <summary>A drop written by an idea about people waits for the developer's ✅ whatever the settings say.</summary>
    private bool IdeaAboutPeople(string key) =>
        long.TryParse(store.Get("idea_for:" + key), out var id) && store.Idea(id) is { People.Length: > 0 };
}
