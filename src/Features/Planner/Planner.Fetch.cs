using System.Net;
using System.Net.Http.Headers;
using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;

namespace EpochAssist.Features;

/// <summary>The two planner sites.</summary>
internal enum PlannerSite
{
    Maxroll,
    LastEpochTools,
}

/// <summary>
/// Which sites' link import a build offers. A release build hides a site's link import until that site has agreed;
/// dev builds show both. A site that agrees has its constant flipped; one that withdraws is dropped by its switch in
/// Settings at once, and by its constant in the next release.
/// </summary>
internal static class PlannerSites
{
    public const bool MaxrollAgreed = false;
    public const bool LetAgreed = false;

    public static bool Shown(PlannerSite site)
    {
#if DEV
        return true;
#else
        return site == PlannerSite.Maxroll ? MaxrollAgreed : LetAgreed;
#endif
    }

    public static string Name(PlannerSite site) => site == PlannerSite.Maxroll ? "Maxroll" : "Last Epoch Tools";
}

/// <summary>How a fetch ended.</summary>
internal enum FetchState
{
    Running,
    Imported,
    TimedOut,
    NotFound,
    Refused,
    NotAPlannerBuild,
    Failed,
}

/// <summary>One fetch: what it asked for and how it ended. Shown until the next paste; never retried.</summary>
internal sealed class FetchAttempt
{
    public PlannerSite Site;
    public string Url = string.Empty;
    public Paste Paste = null!;
    public FetchState State = FetchState.Running;
    /// <summary>The HTTP status, or 0 when there was no answer.</summary>
    public int Status;
    public long Bytes;
    public double Ms;
    public string Message = string.Empty;
    public Build? Build;
}

/// <summary>What a paste did: its outcome words, and the build when it imported with no request.</summary>
internal sealed record PasteResult(string Message)
{
    public bool Started { get; init; }
    public Build? Build { get; init; }
}

internal static partial class Planner
{
    /// <summary>The tab's outcome words for an import.</summary>
    internal static class Words
    {
        public const string NotABuild = "That isn't a planner link, a Maxroll export or a Last Epoch Tools build.";
        public const string Busy = "Already fetching a build.";

        public static string Fetching(PlannerSite site) => $"Fetching from {PlannerSites.Name(site)}…";

        public static string Imported(Build build) => $"Imported \"{build.Name}\" ({build.Stages.Count} {(build.Stages.Count == 1 ? "stage" : "stages")}).";

        public static string SiteOff(PlannerSite site) => $"{PlannerSites.Name(site)} links are switched off in Settings.";

        public static string Fallback(PlannerSite site) => site == PlannerSite.Maxroll
            ? "use Maxroll's Export and paste it here"
            : "open the build data link in the browser and paste the page here";

        /// <summary>The outcome line for a finished fetch.</summary>
        public static string Outcome(FetchAttempt attempt)
        {
            string site = PlannerSites.Name(attempt.Site);
            return attempt.State switch
            {
                FetchState.Running => Fetching(attempt.Site),
                FetchState.Imported when attempt.Build != null => Imported(attempt.Build),
                FetchState.TimedOut => $"{site} didn't answer within {Fetch.TimeoutSeconds} s. Paste again to retry, or {Fallback(attempt.Site)}.",
                FetchState.NotFound => $"{site} has no public build at that link.",
                FetchState.Refused => $"{site} refused the request ({attempt.Status}). Paste again later, or {Fallback(attempt.Site)}.",
                FetchState.NotAPlannerBuild => NotABuild,
                _ => $"{site} couldn't be reached. Paste again to retry, or {Fallback(attempt.Site)}.",
            };
        }
    }

    /// <summary>
    /// The planner's only network code. Every request starts from a paste: one GET of the build data address the paste
    /// named, at most one in flight, no retry, no redirect, no cookies, an identifying User-Agent and nothing about the
    /// player. The request and its parse run on a worker task; the main thread picks up the result with Poll.
    /// </summary>
    internal static class Fetch
    {
        public const int TimeoutSeconds = 10;

        private static readonly HttpClient s_client = MakeClient();
        private static Task<FetchAttempt>? s_task;
        // Bumped by Drop, so a request still running at shutdown hands its result to no one.
        private static int s_generation;
        private static int s_taskGeneration;
#if DEV
        private static string? s_testHost;
#endif

        public static bool Busy => s_task != null;

        /// <summary>The last fetch that ended, kept until the next paste.</summary>
        public static FetchAttempt? Last { get; private set; }

        private static HttpClient MakeClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"EpochAssist/{BuildInfo.Version} (+https://github.com/wistb/EpochAssist)");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

#if DEV
        /// <summary>Points the next request at a test address, so the timeout check reaches no site. Cleared after one
        /// request and on reload.</summary>
        public static void TestHost(string? url) => s_testHost = url;
        public static string? TestHostNow => s_testHost;
#endif

        /// <summary>
        /// Handles a paste, from the tab's button or the dev paste verb: a link of a shown site starts its one request;
        /// a Maxroll export or Last Epoch Tools build JSON is parsed here, with no request. The outcome words are the
        /// tab's.
        /// </summary>
        public static PasteResult Paste(string? text)
        {
            var paste = PasteKind.Recognise(text);
            PlannerSite? site = paste.Kind switch
            {
                PasteSource.MaxrollLink => PlannerSite.Maxroll,
                PasteSource.LetLink => PlannerSite.LastEpochTools,
                _ => null,
            };
            // A site whose link import isn't offered treats its links as any other text.
            if (site is { } s && !PlannerSites.Shown(s)) return new PasteResult(Words.NotABuild);
            if (site is { } linked)
            {
                if (Busy) return new PasteResult(Words.Busy);
                Start(linked, paste);
                return new PasteResult(Words.Fetching(linked)) { Started = true };
            }
            if (paste.Kind is not (PasteSource.MaxrollExport or PasteSource.LetJson)) return new PasteResult(Words.NotABuild);
            if (paste.Text.Length > MainThreadParseBytes) return new PasteResult(Words.NotABuild);
            var parsed = paste.Kind == PasteSource.MaxrollExport
                ? MaxrollFormat.ParseExport(paste.Text, DateTime.Now)
                : LetFormat.Parse(paste.Text, null, null, DateTime.Now);
            return parsed.Build is { } build ? new PasteResult(Words.Imported(build)) { Build = build } : new PasteResult(Words.NotABuild);
        }

        // Pasted text is parsed on the main thread, so only up to this size.
        private const int MainThreadParseBytes = 1024 * 1024;

        /// <summary>Starts the one request for a recognised link; false while one is running.</summary>
        public static bool Start(PlannerSite site, Paste paste)
        {
            if (s_task != null || paste.RequestUrl == null) return false;
            string url = paste.RequestUrl;
#if DEV
            if (s_testHost != null)
            {
                url = s_testHost;
                s_testHost = null;
            }
#endif
            Last = null;
            s_taskGeneration = s_generation;
            var attempt = new FetchAttempt { Site = site, Url = url, Paste = paste };
            s_task = Task.Run(() => Run(attempt));
            Log.Info($"Planner: fetching one build from {PlannerSites.Name(site)}.");
            return true;
        }

        /// <summary>The finished attempt, once, on the main thread; null while running or idle.</summary>
        public static FetchAttempt? Poll()
        {
            var task = s_task;
            if (task == null || !task.IsCompleted) return null;
            s_task = null;
            if (s_taskGeneration != s_generation) return null;
            var attempt = task.IsCompletedSuccessfully
                ? task.Result
                : new FetchAttempt { State = FetchState.Failed, Message = task.Exception?.GetBaseException().Message ?? "failed" };
            Last = attempt;
            Log.Info($"Planner: {PlannerSites.Name(attempt.Site)} fetch {attempt.State} (status {attempt.Status}, {attempt.Bytes} bytes, {attempt.Ms:F0} ms).");
            return attempt;
        }

        /// <summary>Drops a running request's result (it ends on its own within the timeout) and the last outcome.</summary>
        public static void Drop()
        {
            s_generation++;
            s_task = null;
            Last = null;
#if DEV
            s_testHost = null;
#endif
        }

        private static async Task<FetchAttempt> Run(FetchAttempt attempt)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using var response = await s_client.GetAsync(attempt.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                attempt.Status = (int)response.StatusCode;
                string? body = await ReadLimited(response).ConfigureAwait(false);
                attempt.Bytes = body?.Length ?? PasteKind.MaxBytes + 1;
                Classify(attempt, response, body);
            }
            catch (TaskCanceledException)
            {
                attempt.State = FetchState.TimedOut;
            }
            catch (HttpRequestException e)
            {
                attempt.State = FetchState.Failed;
                attempt.Message = e.Message;
            }
            finally
            {
                attempt.Ms = FrameTimes.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
            return attempt;
        }

        // The body as text, or null past the size limit.
        private static async Task<string?> ReadLimited(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentLength > PasteKind.MaxBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > PasteKind.MaxBytes) return null;
                memory.Write(buffer, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }

        private static void Classify(FetchAttempt attempt, HttpResponseMessage response, string? body)
        {
            var status = response.StatusCode;
            if (status == HttpStatusCode.NotFound)
            {
                attempt.State = FetchState.NotFound;
                return;
            }
            // A redirect isn't followed, and a challenge page is HTML: both are the site saying no.
            bool html = response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
            if (!response.IsSuccessStatusCode || html)
            {
                attempt.State = FetchState.Refused;
                return;
            }
            if (body == null)
            {
                attempt.State = FetchState.NotAPlannerBuild;
                return;
            }
            var paste = attempt.Paste;
            var parsed = attempt.Site == PlannerSite.Maxroll
                ? MaxrollFormat.ParseLink(body, paste.Profile, DateTime.Now)
                : LetFormat.Parse(body, paste.PageUrl, paste.Id, DateTime.Now);
            attempt.Build = parsed.Build;
            attempt.State = parsed.Build != null ? FetchState.Imported : parsed.Error == Parsed.NotFound ? FetchState.NotFound : FetchState.NotAPlannerBuild;
        }
    }
}
