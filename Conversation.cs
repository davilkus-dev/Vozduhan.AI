using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vozduhan.AI;

public sealed class Conversation(BotConfig config, BotStore store)
{
    private static readonly CultureInfo Ru = new("ru-RU");

    public object Identity(JsonElement user)
    {
        var id = user.Number("id");
        var known = config.FindMember(id);
        return new
        {
            telegram_id = id,
            telegram_username = (user.Text("username") ?? known?.Username) is { Length: > 0 } u ? "@" + u : null,
            telegram_display_name_not_username = $"{user.Text("first_name")} {user.Text("last_name")}".Trim(),
            short_name = known?.ShortName,
            full_name = known?.FullName,
            role = config.Role(id),
            is_bot = user.Flag("is_bot")
        };
    }
    public object Message(JsonElement m, bool includeReply = true)
    {
        var reply = m.Field("reply_to_message");
        var external = m.Field("external_reply");
        return new
        {
            message_id = m.Number("message_id"),
            time_utc = m.Number("date"),
            topic_id = m.TopicId(),
            author = Identity(m.Field("from")),
            text = Limit(MediaDigest.Text(m, store), 2400),
            reply_to_message_id = reply.Number("message_id"),
            selected_quote = m.Field("quote").Text("text"),
            quote_is_manual = m.Field("quote").Flag("is_manual"),
            replied_message = includeReply && reply.ValueKind == JsonValueKind.Object ? Message(reply, false) : null,
            external_reply_reference = external.ValueKind == JsonValueKind.Object ? new
            {
                chat_id = external.Field("chat").Number("id"),
                message_id = external.Number("message_id")
            } : null
        };
    }

    /// <summary>Trusted text written by the developer/head: university knowledge and per-subject tone notes. Everything, as before the diet.</summary>
    public string AdminContext(bool isPrivate = false) => Trusted(Expand(null, isPrivate), isPrivate);

    /// <summary>
    /// The trusted part of the Writer's system prompt for the chosen pieces: knowledge, subject tone notes, notes about people.
    /// No time, dates or message IDs, a fixed order (as in the registry): the same choice gives the same bytes.
    /// Notes about people never go into a private chat, whatever was chosen.
    /// </summary>
    public string Trusted(IReadOnlySet<string> chosen, bool isPrivate)
    {
        var r = config.R;
        var sb = new StringBuilder();
        var pieces = r.KnowledgeBlocks.Where(b => b.Text.Length > 0 && chosen.Contains(ContextBlocks.KnowledgePrefix + b.Key)).ToArray();
        if (pieces.Length > 0)
        {
            sb.Append("\n\nЗнания от разработчика и старосты (доверенный текст: вуз, сленг, традиции, шутки, правила баллов). Используй как свои знания, не цитируй дословно:");
            foreach (var b in pieces) sb.Append($"\n[{b.Key}]\n").Append(b.Text);
        }
        var notes = r.Subjects.Where(s => !s.Archived && !string.IsNullOrWhiteSpace(s.AdminComment) && chosen.Contains(ContextBlocks.SubjectNotePrefix + s.Id)).ToArray();
        if (notes.Length > 0)
        {
            sb.Append("\n\nСкрытые заметки администратора о предметах. Это только подсказка, каким тоном говорить о предмете; " +
                      "не пересказывай и не упоминай, что такие заметки существуют:");
            foreach (var s in notes) sb.Append($"\n- {s.Name}: {s.AdminComment!.Trim()}");
        }
        var people = r.Members.Where(m => m.Active && !string.IsNullOrWhiteSpace(m.AdminNote) && chosen.Contains(ContextBlocks.PersonPrefix + m.Key)).ToArray();
        if (!isPrivate && people.Length > 0)
        {
            sb.Append("\n\nЛокальные приколы о людях из группы (от разработчика). Используй к месту для язвительного стёба в общем чате, " +
                      "без неприкасаемых по ролям, но не в каждом сообщении и без травли. Не выдумывай новые проступки или прошлые разговоры; " +
                      "не цитируй дословно и не говори, что у тебя есть такие заметки:");
            foreach (var m in people) sb.Append($"\n- {m.ShortName} ({m.FullName}" + (string.IsNullOrWhiteSpace(m.Username) ? "" : ", @" + m.Username) + $"): {m.AdminNote!.Trim()}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// What the Router may ask for, with a short description each. Pieces of knowledge are described by their hint, never by their text.
    /// In a private chat notes about people are not offered at all.
    /// </summary>
    public List<(string Id, string About)> BlockCatalog(bool isPrivate)
    {
        var r = config.R;
        var list = ContextBlocks.Fixed.Select(f => (f.Id, f.About)).ToList();
        var pieces = r.KnowledgeBlocks.Where(b => b.Text.Length > 0).ToArray();
        if (pieces.Length > 0) list.Add((ContextBlocks.Knowledge, "все куски знаний сразу (дорого, бери конкретные)"));
        foreach (var b in pieces)
            list.Add((ContextBlocks.KnowledgePrefix + b.Key, Limit(b.Hint ?? "знания без подсказки", KnowledgeBlock.HintMax) + (b.Always ? " (уходит всегда)" : "")));
        var subjects = r.Subjects.Where(s => !s.Archived && !string.IsNullOrWhiteSpace(s.AdminComment)).ToArray();
        if (subjects.Length > 0) list.Add((ContextBlocks.SubjectNotes, "тон обо всех предметах сразу"));
        foreach (var s in subjects) list.Add((ContextBlocks.SubjectNotePrefix + s.Id, $"как говорить о предмете «{s.Name}»"));
        if (isPrivate) return list;
        var people = r.Members.Where(m => m.Active && !string.IsNullOrWhiteSpace(m.AdminNote)).ToArray();
        if (people.Length > 0) list.Add((ContextBlocks.PeopleNotes, "мемы и описания всех одногруппников сразу (дорого)"));
        foreach (var m in people)
            list.Add((ContextBlocks.PersonPrefix + m.Key, $"кто такой и мемы про {m.ShortName}" + (string.IsNullOrWhiteSpace(m.Username) ? "" : " (@" + m.Username + ")")));
        return list;
    }

    /// <summary>
    /// The Router's choice turned into concrete blocks. null — everything (no diet). Unknown ids are dropped, group ids expand
    /// to their pieces, knowledge marked «always» is added, notes about people are removed in a private chat.
    /// </summary>
    public IReadOnlySet<string> Expand(IEnumerable<string>? requested, bool isPrivate)
    {
        var r = config.R;
        var knowledge = r.KnowledgeBlocks.Where(b => b.Text.Length > 0).Select(b => ContextBlocks.KnowledgePrefix + b.Key).ToList();
        var subjects = r.Subjects.Where(s => !s.Archived && !string.IsNullOrWhiteSpace(s.AdminComment)).Select(s => ContextBlocks.SubjectNotePrefix + s.Id).ToList();
        var people = isPrivate ? [] : r.Members.Where(m => m.Active && !string.IsNullOrWhiteSpace(m.AdminNote)).Select(m => ContextBlocks.PersonPrefix + m.Key).ToList();
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (requested is null)
        {
            result.UnionWith(ContextBlocks.Fixed.Select(f => f.Id));
            result.UnionWith(knowledge); result.UnionWith(subjects); result.UnionWith(people);
            return result;
        }
        foreach (var id in requested)
        {
            if (ContextBlocks.Fixed.Any(f => f.Id == id)) result.Add(id);
            else if (id == ContextBlocks.Knowledge) result.UnionWith(knowledge);
            else if (id == ContextBlocks.SubjectNotes) result.UnionWith(subjects);
            else if (id == ContextBlocks.PeopleNotes) result.UnionWith(people);
            else if (knowledge.Contains(id) || subjects.Contains(id) || people.Contains(id)) result.Add(id);
        }
        result.UnionWith(r.KnowledgeBlocks.Where(b => b.Always && b.Text.Length > 0).Select(b => ContextBlocks.KnowledgePrefix + b.Key));
        return result;
    }

    /// <summary>
    /// The Writer's request with the diet: the system part (fixed rules are added by the client) and the user part with only
    /// the chosen blocks. The message, the last ShortHistory messages and «now» (time, day, parity) always go.
    /// Fields go from the most stable to the most volatile, always in the same order.
    /// </summary>
    public (string Trusted, string User) Writer(JsonElement focus, bool isPrivate, IReadOnlyList<string>? blocks, string? webNote = null)
    {
        var chosen = Expand(blocks, isPrivate);
        bool Has(string id) => chosen.Contains(id);
        var chat = focus.Field("chat").Number("id");
        var topic = focus.TopicId();
        var id = (int)focus.Number("message_id");
        var utc = DateTimeOffset.UtcNow;
        var r = config.R;
        var local = config.LocalNow(utc);
        var today = DateOnly.FromDateTime(local);
        var active = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= utc).ToList();
        var user = new Dictionary<string, object?>
        {
            ["conversation"] = isPrivate ? "личка с ботом" : "Воздуханство (общий чат группы)",
            ["time_zone"] = config.TimeZone,
            ["instruction"] = "Ответь на focus_message. История — фон, а не пачка вопросов. Учитывай selected_quote и replied_message."
        };
        if (Has(ContextBlocks.Members)) user["known_group_members"] = MembersView();
        if (Has(ContextBlocks.Deadlines)) user["confirmed_upcoming_deadlines"] = UpcomingDeadlines(active);
        var schedule = new Dictionary<string, object?>();
        if (Has(ContextBlocks.Subjects)) schedule["subjects"] = SubjectsView(today);
        if (Has(ContextBlocks.Bells)) schedule["bells"] = r.Periods.Select(p => $"{p.Number}: {p.Start}–{p.End}");
        if (Has(ContextBlocks.Changes)) schedule["upcoming_one_off_changes"] = ChangesView(today);
        var days = Enumerable.Range(0, 8).Where(i => Has(ContextBlocks.ScheduleWeek) || (i == 0 && Has(ContextBlocks.ScheduleToday)) || (i == 1 && Has(ContextBlocks.ScheduleTomorrow))).ToArray();
        if (days.Length > 0) schedule["days"] = DaysView(today, days);
        schedule["now"] = NowView(utc, Has(ContextBlocks.ScheduleToday) || Has(ContextBlocks.ScheduleWeek), active);
        user["schedule"] = schedule;
        var missing = ContextBlocks.Fixed.Where(f => f.Factual && !Has(f.Id)).Select(f => f.Short).ToArray();
        if (missing.Length > 0)
            user["context_note"] = "Тебе передана только часть данных, нет: " + string.Join(", ", missing) +
                ". Если ответ зависит от них — не выдумывай и не утверждай, что этого нет; скажи, что сейчас не помнишь, или переспроси.";
        if (webNote is not null) user["web_search"] = webNote;
        user["recent_messages"] = History(chat, topic, id, Has(ContextBlocks.HistoryLong) ? config.ContextMessages : Math.Min(ShortHistory, config.ContextMessages));
        if (Has(ContextBlocks.ReplyChain)) user["reply_ancestors"] = Ancestors(focus, chat, topic);
        user["focus_message"] = Message(focus);
        return (Trusted(chosen, isPrivate), Json.Write(user));
    }
    /// <summary>History that always goes to the Writer with the diet.</summary>
    public const int ShortHistory = 8;

    private List<object> History(long chat, int topic, int id, int count)
    {
        var history = store.History(chat, topic, id, count);
        var messages = new List<object>();
        var budget = 22000;
        foreach (var raw in history.AsEnumerable().Reverse())
        {
            using var doc = JsonDocument.Parse(raw);
            var shaped = Message(doc.RootElement);
            var size = Json.Write(shaped).Length;
            if (messages.Count > 0 && size > budget) break;
            messages.Add(shaped); budget -= size;
        }
        messages.Reverse();
        return messages;
    }
    private List<object> Ancestors(JsonElement focus, long chat, int topic)
    {
        var ancestors = new List<object>();
        var next = focus.Field("reply_to_message").Number("message_id");
        var visited = new HashSet<long>();
        while (next > 0 && ancestors.Count < 8 && visited.Add(next))
        {
            var raw = store.Message(chat, (int)next);
            if (raw is null) break;
            using var doc = JsonDocument.Parse(raw);
            // Only this conversation may contribute stored reply ancestors.
            if (doc.RootElement.TopicId() != topic) break;
            ancestors.Add(Message(doc.RootElement));
            next = doc.RootElement.Field("reply_to_message").Number("message_id");
        }
        return ancestors;
    }
    private IEnumerable<object> UpcomingDeadlines(IEnumerable<Deadline> active)
    {
        var r = config.R;
        return active.Take(25).Select(d => new { d.Id, subject = r.SubjectName(d.SubjectId), d.Work, d.Date, d.Period, d.Start, d.Room,
            start_unix = config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start))).ToUnixTimeSeconds() }).ToList();
    }

    private long? Unix(Slot s) => TimeOnly.TryParse(s.Start, out var t)
        ? config.Utc(DateOnly.ParseExact(s.Date, "yyyy-MM-dd").ToDateTime(t)).ToUnixTimeSeconds() : null;

    private object? SlotView(Slot? s) => s is null ? null : new
    {
        date = s.Date, weekday = Schedule.DayNames[Schedule.Dow(DateOnly.ParseExact(s.Date, "yyyy-MM-dd"))],
        s.Period, time = $"{s.Start}–{s.End}", start_unix = Unix(s), subject = s.Subject, subject_id = s.SubjectId, kind = Schedule.KindLong(s.Kind),
        room = s.RoomText, teacher = s.Teacher, campus = s.Campus, s.Note, s.Status
    };
    /// <summary>Current moment: time, weekday, week and parity; with lessons — the current and the next lesson.</summary>
    private object NowView(DateTimeOffset utc, bool lessons, IReadOnlyList<Deadline> deadlines)
    {
        var r = config.R;
        var local = config.LocalNow(utc);
        var today = DateOnly.FromDateTime(local);
        var week = Schedule.Week(r, today);
        var basic = new
        {
            local_time = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            now_unix = utc.ToUnixTimeSeconds(),
            weekday = Schedule.DayNames[Schedule.Dow(today)],
            week_number = Schedule.InTerm(r, today) ? week : (int?)null,
            week_parity = Schedule.InTerm(r, today) ? Schedule.ParityRu(Schedule.Parity(week)) : "вне семестра"
        };
        if (!lessons) return basic;
        var now = Schedule.Now(r, local);
        return new
        {
            basic.local_time, basic.now_unix, basic.weekday, basic.week_number, basic.week_parity,
            current_lesson = SlotView(now.Current),
            next_lesson = SlotView(now.Next),
            minutes_to_next = now.Next is null ? (int?)null : now.MinutesToNext,
            deadlines_at_next_lesson = now.Next is null ? [] : deadlines
                .Where(d => d.Date == now.Next.Date && d.SubjectId == now.Next.SubjectId).Select(d => $"#{d.Id} {d.Work}").ToArray()
        };
    }
    private IEnumerable<object> DaysView(DateOnly today, IEnumerable<int> offsets)
    {
        var r = config.R;
        return offsets.Select(i => today.AddDays(i)).Select(d => new
        {
            date = d.ToString("yyyy-MM-dd"),
            weekday = Schedule.DayNames[Schedule.Dow(d)],
            label = d == today ? "сегодня" : d == today.AddDays(1) ? "завтра" : d == today.AddDays(2) ? "послезавтра" : null,
            parity = Schedule.InTerm(r, d) ? Schedule.ParityRu(Schedule.Parity(Schedule.Week(r, d))) : "вне семестра",
            lessons = Schedule.Day(r, d).Select(s => new
            {
                s.Period, time = $"{s.Start}–{s.End}", start_unix = Unix(s), subject = s.Subject, kind = Schedule.KindLong(s.Kind),
                room = s.RoomText, teacher = s.Teacher, campus = s.Campus, s.Note, s.Status
            }).ToList()
        }).ToList();
    }
    private IEnumerable<object> SubjectsView(DateOnly today)
    {
        var r = config.R;
        return r.Subjects.Where(s => !s.Archived).Select(s => new
        {
            s.Id, s.Name, short_name = s.ShortName, aliases = s.Aliases,
            lessons = r.Lessons.Where(l => l.SubjectId == s.Id && (l.Until is null || DateOnly.ParseExact(l.Until, "yyyy-MM-dd") >= today))
                .OrderBy(l => l.DayOfWeek).ThenBy(l => l.Period).Select(l => new
                {
                    kind = Schedule.KindLong(l.Kind), weekday = Schedule.DayNames[l.DayOfWeek], weeks = Schedule.WeeksRu(l.Weeks),
                    l.Period, room = l.Online ? "дистанционно" : l.Room, teacher = l.Teacher,
                    campus = string.IsNullOrWhiteSpace(l.Campus) ? (r.DefaultCampus.Length > 0 ? r.DefaultCampus : null) : l.Campus,
                    l.Note, from = l.From, until = l.Until
                }).ToList()
        }).ToList();
    }
    private IEnumerable<string> ChangesView(DateOnly today)
    {
        var r = config.R;
        return r.Changes.Where(c => DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", out var d) && d >= today && d <= today.AddDays(45))
            .OrderBy(c => c.Date).Select(c => Schedule.Describe(r, c)).ToList();
    }

    public object ScheduleFacts(DateTimeOffset utc, int days = 8)
    {
        var r = config.R;
        var today = DateOnly.FromDateTime(config.LocalNow(utc));
        var deadlines = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= utc).ToList();
        return new
        {
            now = NowView(utc, true, deadlines),
            days = DaysView(today, Enumerable.Range(0, days)),
            subjects = SubjectsView(today),
            upcoming_one_off_changes = ChangesView(today),
            bells = r.Periods.Select(p => $"{p.Number}: {p.Start}–{p.End}")
        };
    }

    /// <summary>The full context, as before the diet (WriterDiet off).</summary>
    public string Context(JsonElement focus, bool isPrivate, string? webNote = null)
    {
        var chat = focus.Field("chat").Number("id");
        var topic = focus.TopicId();
        var id = (int)focus.Number("message_id");
        var messages = History(chat, topic, id, config.ContextMessages);
        var ancestors = Ancestors(focus, chat, topic);
        var now = DateTimeOffset.UtcNow;
        var relevant = UpcomingDeadlines(store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= now));
        return Json.Write(new
        {
            conversation = isPrivate ? "личка с ботом" : "Воздуханство (общий чат группы)",
            time_zone = config.TimeZone,
            schedule = ScheduleFacts(now),
            confirmed_upcoming_deadlines = relevant,
            known_group_members = MembersView(),
            recent_messages = messages,
            reply_ancestors = ancestors,
            focus_message = Message(focus),
            instruction = "Ответь на focus_message. История — фон, а не пачка вопросов. Учитывай selected_quote и replied_message.",
            web_search = webNote
        });
    }

    /// <summary>Compact registry for the editor planner: IDs it may reference and the next days as they currently stand.</summary>
    public object PlannerState(long actor, Draft? deadlineDraft, RegistryDraft? registryDraft)
    {
        var r = config.R;
        var utc = DateTimeOffset.UtcNow;
        var local = config.LocalNow(utc);
        var today = DateOnly.FromDateTime(local);
        var bound = r.Members.Where(m => m.UserId is not null).Select(m => m.UserId!.Value).ToHashSet();
        var owner = actor == config.OwnerId; // the head never gets owner-only notes, even indirectly through the planner
        return new
        {
            local_now = local.ToString("yyyy-MM-dd HH:mm dddd", Ru),
            editor_role = config.Role(actor),
            week = Schedule.InTerm(r, today) ? $"{Schedule.Week(r, today)}-я, {Schedule.ParityRu(Schedule.Parity(Schedule.Week(r, today)))}" : "вне семестра",
            registry = new
            {
                members = r.Members.Select(m => new { m.Key, m.ListNumber, m.FullName, m.ShortName, m.Username, m.UserId, m.Active, m.Role, adminNote = owner ? m.AdminNote : null }),
                subjects = r.Subjects.Select(s => new { s.Id, s.Name, s.ShortName, s.Aliases, adminComment = owner ? s.AdminComment : null, s.Archived }),
                lessons = r.Lessons.Select(l => Schedule.Describe(r, l)),
                changes = r.Changes.Where(c => string.CompareOrdinal(c.Date, today.AddDays(-7).ToString("yyyy-MM-dd")) >= 0).Select(c => Schedule.Describe(r, c)),
                bells = r.Periods,
                r.WeekOneMonday, r.TermEnd, r.DefaultCampus,
                knowledge = owner ? r.KnowledgeBlocks.Select(b => new { b.Key, b.Hint, b.Always, preview = Limit(b.Text, 300), length = b.Text.Length }) : null,
                owner_only = owner ? null : "Редактор — не разработчик: сленг, комментарии к предметам, заметки о людях и знания ему недоступны; такие просьбы не превращай в operations, а вежливо скажи, что это настраивает разработчик." 
            },
            days_ahead = Enumerable.Range(0, 14).Select(i => today.AddDays(i)).Select(d => new
            {
                date = d.ToString("yyyy-MM-dd"), weekday = Schedule.DayShort[Schedule.Dow(d)],
                lessons = Schedule.Day(r, d).Select(s => $"{s.Period} пара {Schedule.KindRu(s.Kind)} {s.Subject} [{s.LessonId ?? s.ChangeId}] {s.Status}")
            }),
            unbound_seen_users = store.SeenUsers(200).Where(u => !bound.Contains(u.Id)).Take(40)
                .Select(u => new { telegram_id = u.Id, u.Username, u.Name }),
            existing_deadlines = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= utc).TakeLast(40)
                .Select(d => new { d.Id, d.SubjectId, d.Work, d.Date, d.Period }),
            pending_deadline_draft = deadlineDraft,
            pending_registry_draft = registryDraft is null ? null : new { registryDraft.Lines, operations = registryDraft.Operations.Select(o => JsonDocument.Parse(o).RootElement.Clone()) }
        };
    }

    public object MembersView()
    {
        var r = config.R;
        return r.Members.Where(m => m.Active).Select(m => new
            {
                list_number = m.ListNumber, full_name = m.FullName, short_name = m.ShortName,
                role = m.UserId == config.OwnerId ? "developer" : m.Role == "head" ? "head (староста)" : "member",
                // The only things that identify a Telegram account; display names are NOT usernames.
                telegram_username = string.IsNullOrWhiteSpace(m.Username) ? null : "@" + m.Username,
                telegram_link = string.IsNullOrWhiteSpace(m.Username) ? null : "https://t.me/" + m.Username,
                telegram_id = m.UserId,
                mention_html = m.UserId is > 0 ? $"<a href=\"tg://user?id={m.UserId}\">{System.Net.WebUtility.HtmlEncode(m.ShortName)}</a>" : null,
                telegram_display_name_not_username = m.TelegramName
            }).ToList();
    }

    public object DeadlineView(Deadline d) => new
    {
        d.Id, subject = config.R.SubjectName(d.SubjectId), d.Work, d.Date,
        weekday = Schedule.DayNames[Schedule.Dow(DateOnly.Parse(d.Date))], d.Period, d.Start, d.End, d.Room, d.Note,
        start_unix = config.Utc(DateOnly.Parse(d.Date).ToDateTime(TimeOnly.Parse(d.Start))).ToUnixTimeSeconds()
    };

    /// <summary>Context for messages the bot writes on its own initiative into the group chat.</summary>
    public string AmbientContext(string kind, object? extra = null, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(config.LocalNow(now));
        var history = store.History(config.GroupId, config.ChatTopicId, int.MaxValue, 20).Select(raw =>
        {
            using var doc = JsonDocument.Parse(raw);
            return Message(doc.RootElement, false);
        }).ToList();
        var active = store.AllDeadlines().Where(d => d.Status == "active" && Deadlines.Ends(config, d) >= now).ToList();
        return Json.Write(new
        {
            conversation = "Воздуханство (общий чат группы)", kind, time_zone = config.TimeZone,
            schedule = ScheduleFacts(now),
            confirmed_upcoming_deadlines = active.Take(25).Select(DeadlineView),
            today_deadlines = active.Where(d => d.Date == today.ToString("yyyy-MM-dd")).Select(DeadlineView),
            tomorrow_deadlines = active.Where(d => d.Date == today.AddDays(1).ToString("yyyy-MM-dd")).Select(DeadlineView),
            known_group_members = MembersView(),
            recent_messages = history,
            recent_bot_posts = RecentPosts(),
            recent_deadline_mentions = store.Mentions(now.AddHours(-24).ToUnixTimeSeconds()).Select(x => new
            {
                deadline = active.FirstOrDefault(d => d.Id == x.Deadline) is { } d ? $"#{d.Id} {config.R.SubjectName(d.SubjectId)} — {d.Work}" : $"#{x.Deadline}",
                at = config.LocalNow(DateTimeOffset.FromUnixTimeSeconds(x.Time)).ToString("dd.MM HH:mm"), x.Kind
            }),
            extra
        });
    }
    public List<string> RecentPosts() => store.Get("ambient_recent") is { } s ? Json.Read<List<string>>(s) : [];
    public void AddRecentPost(string text)
    {
        var list = RecentPosts(); list.Add(Limit(text, 500));
        store.Set("ambient_recent", Json.Write(list.TakeLast(8).ToList()));
    }

    public static bool CallsBot(JsonElement m, long botId, string username)
    {
        if (m.Field("reply_to_message").Field("from").Number("id") == botId) return true;
        var text = m.Text("text") ?? m.Text("caption") ?? "";
        if (Regex.IsMatch(text, @"(?<!\w)(воздухан(?:а|у|ом|е|чик)?|бот(?:а|у|ом|е|ик|яра)?)(?!\w)", RegexOptions.IgnoreCase)) return true;
        var entities = m.Field(m.Text("text") is null ? "caption_entities" : "entities");
        if (entities.ValueKind != JsonValueKind.Array) return false;
        foreach (var e in entities.EnumerateArray())
        {
            if (e.Text("type") == "text_mention" && e.Field("user").Number("id") == botId) return true;
            var start = (int)e.Number("offset"); var count = (int)e.Number("length");
            if (start < 0 || count < 0 || start + count > text.Length) continue;
            var value = text.Substring(start, count);
            if (e.Text("type") == "mention" && value.Equals("@" + username, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Text("type") == "bot_command" && value.EndsWith("@" + username, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static string Limit(string s, int count) => s.Length <= count ? s : s[..(count - 1)] + "…";
}

/// <summary>
/// Context blocks the Router may choose for the Writer (WriterDiet). Fixed ones are data built by code; knowledge pieces,
/// subject tone notes and notes about people are addressed one by one: «knowledge:local_memes», «subject_note:java», «person:m3».
/// </summary>
public static class ContextBlocks
{
    public const string ScheduleToday = "schedule_today", ScheduleTomorrow = "schedule_tomorrow", ScheduleWeek = "schedule_week",
        Subjects = "subjects", Changes = "schedule_changes", Bells = "bells", Deadlines = "deadlines", Members = "members",
        HistoryLong = "history_long", ReplyChain = "reply_chain",
        Knowledge = "knowledge", SubjectNotes = "subject_notes", PeopleNotes = "people_notes",
        KnowledgePrefix = "knowledge:", SubjectNotePrefix = "subject_note:", PersonPrefix = "person:";
    public sealed record Info(string Id, string About, string Short, bool Factual);
    public static readonly Info[] Fixed =
    [
        new(ScheduleToday, "пары сегодня, текущая и следующая пара", "расписания на сегодня", true),
        new(ScheduleTomorrow, "пары завтра", "расписания на завтра", true),
        new(ScheduleWeek, "пары на 8 дней вперёд (включает сегодня и завтра)", "расписания на неделю", true),
        new(Subjects, "предметы: названия, сленг, кто ведёт, кабинеты и корпуса по дням недели — нужно, если упомянут предмет (даже сленгом)", "списка предметов, преподавателей и кабинетов", true),
        new(Changes, "разовые изменения расписания на 45 дней (отмены, переносы, замены)", "разовых изменений расписания", true),
        new(Bells, "расписание звонков", "звонков", true),
        new(Deadlines, "подтверждённые ближайшие дедлайны (сдачи)", "дедлайнов", true),
        new(Members, "список группы: имена, ФИО, юзернеймы, контакты и теги — нужно, чтобы назвать или тегнуть человека", "списка группы и контактов", true),
        new(HistoryLong, "длинная история чата (до 30 сообщений) вместо последних 8", "давней истории чата", false),
        new(ReplyChain, "цепочка сообщений, на которые отвечают (ветка ответов)", "цепочки ответов", false)
    ];
}
