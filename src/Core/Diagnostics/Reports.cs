using System.Text;
using MelonLoader.Utils;

namespace EpochAssist.Core;

/// <summary>A saved crash report: its file name, when, the feature it is about and how often it happened.</summary>
internal sealed record ReportInfo(string File, string Time, string Feature, int Count);

/// <summary>
/// Crash reports in UserData/EpochAssist/reports: one text file per fault (a feature stopped after errors, or a start
/// that threw), with the exception and the diagnostics. The same fault again in one game session raises the count of
/// its report instead of writing another. Each is at most 64 KB, and the newest ten are kept. Nothing leaves the
/// player's machine; writing never throws into the host.
/// </summary>
internal static class Reports
{
    private const int MaxBytes = 64 * 1024;
    private const int Keep = 10;
    private const string Format = "Epoch Assist crash report 1";
    private const string CutNote = "[report cut at 64 KB]";

    private static List<ReportInfo>? s_list;

    private static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "reports");

    /// <summary>Why the last report couldn't be written, or null; Diagnostics shows it.</summary>
    public static string? LastWriteError { get; private set; }

    /// <summary>Writes (or counts again) the report for a fault; false when it couldn't be saved.</summary>
    public static bool Write(Feature feature, string phase, Exception e)
    {
        try
        {
            string session = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss");
            string signature = Signature($"{feature.Id}\n{e.GetType().FullName}\n{e.Message}\n{e.StackTrace}");
            Directory.CreateDirectory(Dir);
            if (!CountAgain(session, signature))
            {
                var now = DateTime.Now;
                string text = string.Join("\n", Format, $"time: {now:yyyy-MM-dd HH:mm:ss}", $"session: {session}", $"feature: {feature.Id} ({feature.Name})",
                    $"phase: {phase}", $"signature: {signature}", "count: 1", string.Empty, "exception:", e.ToString(), string.Empty, Diagnostics.Build().ToText());
                Save(Path.Combine(Dir, FreeName($"report-{now:yyyyMMdd-HHmmss}-{feature.Id}")), Cut(Diagnostics.Scrub(text.Replace("\r\n", "\n"))));
                Prune();
            }
            LastWriteError = null;
            s_list = null;
            return true;
        }
        catch (Exception error)
        {
            LastWriteError = error.Message;
            Log.WarnThrottled("reports:write", $"Crash report for {feature.Name} not saved: {error.Message}");
            return false;
        }
    }

    /// <summary>Saved reports, newest first.</summary>
    public static IReadOnlyList<ReportInfo> List()
    {
        if (s_list != null) return s_list;
        var list = new List<ReportInfo>();
        try
        {
            foreach (string path in Files().Reverse())
            {
                string Field(string[] header, string name) => header.FirstOrDefault(l => l.StartsWith(name + ": "))?.Substring(name.Length + 2) ?? "?";
                string[] header = File.ReadLines(path).Take(8).ToArray();
                string feature = Field(header, "feature");
                list.Add(new ReportInfo(Path.GetFileName(path), Field(header, "time"), feature.Split(' ')[0], int.TryParse(Field(header, "count"), out int n) ? n : 1));
            }
        }
        catch (Exception e)
        {
            // Not kept, so the list comes back once the folder can be read.
            Log.WarnThrottled("reports:list", $"Crash reports could not be listed: {e.Message}");
            return list;
        }
        return s_list = list;
    }

    /// <summary>Deletes every saved report; false with the reason when one couldn't be deleted.</summary>
    public static bool Clear(out string reason)
    {
        reason = string.Empty;
        try
        {
            foreach (string path in Files().ToList()) File.Delete(path);
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            Log.WarnThrottled("reports:clear", $"Crash reports not cleared: {e.Message}");
            return false;
        }
        finally
        {
            s_list = null;
        }
    }

    /// <summary>A report's text by its file name.</summary>
    public static string Read(string file) => File.ReadAllText(Path.Combine(Dir, Path.GetFileName(file)));

    // The same fault in the same session: raise the count of its report (among the newest) and keep that file.
    private static bool CountAgain(string session, string signature)
    {
        foreach (string path in Files().Reverse().Take(Keep))
        {
            string[] lines = File.ReadAllText(path).Split('\n');
            int header = Math.Min(lines.Length, 8);
            if (Array.IndexOf(lines, $"session: {session}", 0, header) < 0 || Array.IndexOf(lines, $"signature: {signature}", 0, header) < 0) continue;
            int at = Array.FindIndex(lines, 0, header, l => l.StartsWith("count: "));
            if (at < 0) continue;
            lines[at] = $"count: {(int.TryParse(lines[at].Substring(7), out int n) ? n + 1 : 2)}";
            Save(path, string.Join("\n", lines));
            return true;
        }
        return false;
    }

    // Oldest first: by time in the name, then by the -2, -3 added on a clash.
    private static IEnumerable<string> Files() =>
        Directory.Exists(Dir)
            ? Directory.GetFiles(Dir, "report-*.txt").OrderBy(p => SortKey(Path.GetFileNameWithoutExtension(p)), StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    // report-<yyyyMMdd-HHmmss>-<id>[-n]: feature ids have no dashes, so a fourth part is the clash number.
    private static string SortKey(string name)
    {
        string[] parts = name.Split('-');
        int clash = parts.Length > 4 && int.TryParse(parts[^1], out int n) ? n : 1;
        return parts.Length >= 3 ? $"{parts[1]}-{parts[2]}-{clash:D4}" : name;
    }

    private static string FreeName(string stem)
    {
        string name = stem + ".txt";
        for (int n = 2; File.Exists(Path.Combine(Dir, name)); n++) name = $"{stem}-{n}.txt";
        return name;
    }

    private static void Prune()
    {
        foreach (string old in Files().SkipLast(Keep).ToList()) File.Delete(old);
    }

    // Written beside the target first, so a report is never half written.
    private static void Save(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    // At most 64 KB of UTF-8, cut at a line end, with a note saying so.
    private static string Cut(string text)
    {
        var utf8 = new UTF8Encoding(false);
        if (utf8.GetByteCount(text) <= MaxBytes) return text;
        int budget = MaxBytes - utf8.GetByteCount("\n" + CutNote);
        var kept = new StringBuilder();
        int used = 0;
        foreach (string line in text.Split('\n'))
        {
            int size = utf8.GetByteCount(line) + (kept.Length > 0 ? 1 : 0);
            if (used + size > budget) break;
            if (kept.Length > 0) kept.Append('\n');
            kept.Append(line);
            used += size;
        }
        return kept.Append('\n').Append(CutNote).ToString();
    }

    // FNV-1a over the text: the same fault gives the same signature in every session and build.
    private static string Signature(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }
}
