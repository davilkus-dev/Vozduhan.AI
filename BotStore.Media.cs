using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Vozduhan.AI;

/// <summary>
/// One library item: a sticker from Telegram or a file uploaded in the web admin. Description null — waits for Media Digest,
/// "" — could not be described. Raw — the Telegram object it came with (sticker, photo sizes, animation, video).
/// </summary>
public sealed record MediaItem(long Id, string Kind, string? FileId, string? UniqueId, string? LocalPath, string Title, string? Description, string Tags,
    string? Emoji, string? Set, bool Enabled, int Uses, long LastUsed, long AddedBy, long Created, bool Deleted, string? Raw);

public sealed partial class BotStore
{
    /// <summary>The SQLite build has FTS5; otherwise the library is searched by substring in code.</summary>
    public bool MediaFts { get; private set; }
    public const int PickerBest = 20, PickerRandom = 5;

    private void InitMedia()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS media_library(id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, file_id TEXT NULL,
                file_unique_id TEXT NULL UNIQUE, local_path TEXT NULL, title TEXT NOT NULL DEFAULT '', description TEXT NULL,
                tags TEXT NOT NULL DEFAULT '', emoji TEXT NULL, set_name TEXT NULL, enabled INTEGER NOT NULL DEFAULT 1,
                uses INTEGER NOT NULL DEFAULT 0, last_used INTEGER NOT NULL DEFAULT 0, added_by INTEGER NOT NULL, created INTEGER NOT NULL,
                deleted INTEGER NOT NULL DEFAULT 0, raw TEXT NULL);
            CREATE TABLE IF NOT EXISTS media_uses(id INTEGER PRIMARY KEY AUTOINCREMENT, media INTEGER NOT NULL, chat INTEGER NOT NULL, time INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS media_uses_chat ON media_uses(chat,time);
            """);
        try
        {
            Exec("""
                CREATE VIRTUAL TABLE IF NOT EXISTS media_fts USING fts5(title, description, tags, emoji, tokenize='unicode61 remove_diacritics 2');
                CREATE TRIGGER IF NOT EXISTS media_fts_insert AFTER INSERT ON media_library BEGIN
                    INSERT INTO media_fts(rowid,title,description,tags,emoji) VALUES(new.id,new.title,new.description,new.tags,new.emoji); END;
                CREATE TRIGGER IF NOT EXISTS media_fts_update AFTER UPDATE OF title,description,tags,emoji ON media_library BEGIN
                    DELETE FROM media_fts WHERE rowid=old.id;
                    INSERT INTO media_fts(rowid,title,description,tags,emoji) VALUES(new.id,new.title,new.description,new.tags,new.emoji); END;
                INSERT INTO media_fts(rowid,title,description,tags,emoji) SELECT id,title,description,tags,emoji FROM media_library
                    WHERE id NOT IN (SELECT rowid FROM media_fts);
                """);
            MediaFts = true;
        }
        catch (SqliteException) { MediaFts = false; }
    }

    private const string MediaColumns = "id,kind,file_id,file_unique_id,local_path,title,description,tags,emoji,set_name,enabled,uses,last_used,added_by,created,deleted,raw";
    private static MediaItem ReadMedia(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new(r.GetInt64(0), r.GetString(1), S(2), S(3), S(4), r.GetString(5), S(6), r.GetString(7), S(8), S(9), r.GetInt32(10) != 0,
            r.GetInt32(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14), r.GetInt32(15) != 0, S(16));
    }

    /// <summary>
    /// Adds an item; an already known file is not duplicated (restore — a deleted one comes back). A description already in
    /// the Media Digest cache is taken at once. Added — a new row.
    /// </summary>
    public (long Id, bool Added) MediaAdd(string kind, string fileId, string uniqueId, string? localPath, string title, string? emoji, string? set,
        string? raw, long actor, DateTimeOffset now, bool restore)
    {
        lock (gate)
        {
            if (Query($"SELECT {MediaColumns} FROM media_library WHERE file_unique_id=$u", ReadMedia, ("u", uniqueId)).FirstOrDefault() is { } old)
            {
                if (restore && old.Deleted) Exec("UPDATE media_library SET deleted=0, enabled=1 WHERE id=$id", ("id", old.Id));
                if (old.LocalPath is null && localPath is not null) Exec("UPDATE media_library SET local_path=$p WHERE id=$id", ("p", localPath), ("id", old.Id));
                return (old.Id, false);
            }
            Exec("""
                INSERT INTO media_library(kind,file_id,file_unique_id,local_path,title,description,emoji,set_name,added_by,created,raw)
                VALUES($k,$f,$u,$p,$t,(SELECT summary FROM media_digest WHERE file_unique_id=$u),$e,$s,$a,$now,$raw)
                """, ("k", kind), ("f", fileId), ("u", uniqueId), ("p", localPath), ("t", title), ("e", emoji), ("s", set), ("a", actor),
                ("now", now.ToUnixTimeSeconds()), ("raw", raw));
            return (Convert.ToInt64(Scalar("SELECT last_insert_rowid()")), true);
        }
    }
    public MediaItem? Media(long id)
    {
        lock (gate) return Query($"SELECT {MediaColumns} FROM media_library WHERE id=$id", ReadMedia, ("id", id)).FirstOrDefault();
    }
    public MediaItem? MediaByUnique(string? uniqueId)
    {
        lock (gate) return uniqueId is null ? null : Query($"SELECT {MediaColumns} FROM media_library WHERE file_unique_id=$u", ReadMedia, ("u", uniqueId)).FirstOrDefault();
    }
    /// <summary>The oldest item still waiting for a description.</summary>
    public MediaItem? MediaPending()
    {
        lock (gate) return Query($"SELECT {MediaColumns} FROM media_library WHERE description IS NULL AND deleted=0 AND file_id IS NOT NULL AND file_unique_id IS NOT NULL ORDER BY id LIMIT 1",
            ReadMedia).FirstOrDefault();
    }
    public void MediaDescribed(long id, string description)
    {
        lock (gate) Exec("UPDATE media_library SET description=$d WHERE id=$id AND description IS NULL", ("d", description), ("id", id));
    }
    /// <summary>Changes only the given fields of a library item that is not deleted.</summary>
    public bool MediaUpdate(long id, string? title, string? description, string? tags, bool? enabled)
    {
        lock (gate) return Exec("""
            UPDATE media_library SET title=COALESCE($t,title), description=CASE WHEN $hasD THEN $d ELSE description END, tags=COALESCE($g,tags),
                enabled=COALESCE($e,enabled) WHERE id=$id AND deleted=0
            """, ("t", title), ("hasD", description is not null), ("d", description), ("g", tags), ("e", enabled is null ? null : enabled.Value ? 1 : 0), ("id", id)) == 1;
    }
    /// <summary>Soft delete: the row stays.</summary>
    public bool MediaDelete(long id) { lock (gate) return Exec("UPDATE media_library SET deleted=1 WHERE id=$id AND deleted=0", ("id", id)) == 1; }
    public void MediaDisable(long id) { lock (gate) Exec("UPDATE media_library SET enabled=0 WHERE id=$id", ("id", id)); }
    public void MediaUsed(long id, long chat, DateTimeOffset now)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            Exec("UPDATE media_library SET uses=uses+1, last_used=$now WHERE id=$id", ("now", now.ToUnixTimeSeconds()), ("id", id));
            Exec("INSERT INTO media_uses(media,chat,time) VALUES($id,$c,$now)", ("id", id), ("c", chat), ("now", now.ToUnixTimeSeconds()));
            tx.Commit();
        }
    }

    private static readonly HashSet<string> StopWords = ["что", "как", "это", "так", "для", "все", "всё", "уже", "там", "тут", "его", "она", "они", "вот",
        "был", "мне", "тебе", "меня", "тебя", "или", "если", "когда", "чтобы", "только", "ещё", "еще", "нет", "да", "ну", "надо"];
    /// <summary>Search words: lower case, 3+ letters, no filler words; long words cut to 5 letters so endings do not matter.</summary>
    public static List<string> MediaTerms(string text) => Regex.Matches(text.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+").Select(x => x.Value)
        .Where(w => w.Length >= 3 && !StopWords.Contains(w)).Select(w => w.Length > 5 ? w[..5] : w).Distinct().Take(12).ToList();

    /// <summary>Ids of items matching any search word, best first (FTS5 rank, or the number of matched words without FTS5).</summary>
    private List<long> MediaMatch(string text, string where, params (string Key, object? Value)[] args)
    {
        var terms = MediaTerms(text);
        if (terms.Count == 0) return [];
        if (MediaFts)
            return Query($"SELECT m.id FROM media_fts JOIN media_library m ON m.id=media_fts.rowid WHERE media_fts MATCH $q AND {where} ORDER BY rank LIMIT 500",
                r => r.GetInt64(0), [.. args, ("q", string.Join(" OR ", terms.Select(t => $"\"{t}\"*")))]);
        // Like LIKE '%word%', but in code: SQLite's lower() does not know Cyrillic.
        return Query($"SELECT m.id, lower(m.title||' '||COALESCE(m.description,'')||' '||m.tags||' '||COALESCE(m.emoji,'')) FROM media_library m WHERE {where}",
                r => (Id: r.GetInt64(0), Text: r.GetString(1).ToLowerInvariant().Replace('ё', 'е')), args)
            .Select(x => (x.Id, Score: terms.Count(x.Text.Contains))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Id).Select(x => x.Id).ToList();
    }

    /// <summary>The web admin list: not deleted, by search words and kind; newest first without words.</summary>
    public List<MediaItem> MediaList(string? text, string? kind, int limit = 500)
    {
        lock (gate)
        {
            var all = Query($"SELECT {MediaColumns} FROM media_library m WHERE deleted=0 AND ($k IS NULL OR kind=$k) ORDER BY id DESC", ReadMedia, ("k", kind));
            if (string.IsNullOrWhiteSpace(text)) return all.Take(limit).ToList();
            var byId = all.ToDictionary(x => x.Id);
            return MediaMatch(text, "m.deleted=0").Where(byId.ContainsKey).Select(id => byId[id]).Take(limit).ToList();
        }
    }

    /// <summary>
    /// Picker candidates: up to 20 best by the words of the answer and the hint, plus 5 random of the longest unused; enabled,
    /// described, not sent to this chat in the last 24 hours.
    /// </summary>
    public List<MediaItem> MediaCandidates(long chat, string text, DateTimeOffset now)
    {
        lock (gate)
        {
            const string eligible = "m.enabled=1 AND m.deleted=0 AND m.file_id IS NOT NULL AND m.description IS NOT NULL AND m.id NOT IN (SELECT media FROM media_uses WHERE chat=$c AND time>$since)";
            (string, object?)[] args = [("c", chat), ("since", now.AddHours(-24).ToUnixTimeSeconds())];
            var best = MediaMatch(text, eligible, args).Take(PickerBest).ToList();
            var stale = Query($"SELECT m.id FROM media_library m WHERE {eligible} ORDER BY m.last_used, m.id LIMIT 50", r => r.GetInt64(0), args)
                .Where(id => !best.Contains(id)).OrderBy(_ => Random.Shared.Next()).Take(PickerRandom);
            var ids = best.Concat(stale).ToList();
            if (ids.Count == 0) return [];
            var rows = Query($"SELECT {MediaColumns} FROM media_library WHERE id IN ({string.Join(",", ids)})", ReadMedia).ToDictionary(x => x.Id);
            return ids.Select(id => rows[id]).ToList();
        }
    }

    public int DigestSpent(DateTimeOffset now) => int.TryParse(Get($"digest_budget:{Config.LocalNow(now):yyyy-MM-dd}"), out var n) ? n : 0;
}
