using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;

namespace GuildrunTargetingMod;

// Asks GitHub once per session whether a newer release exists, so a player finds out from the game
// rather than by happening to revisit the page. Everything a player is told about it happens in
// MenuUI ; this class knows nothing about Unity, holds no scene object and shows nothing.
//
// That split is the reason the threading here is boring. The request runs on a thread pool thread
// because a network call on Unity's thread would freeze the game for as long as the connection
// takes to fail, and a Unity API touched from any other thread is undefined behaviour. So the
// answer is parked in one field and the menu picks it up on its own thread, on the timer it already
// runs. No marshalling, no queue, no lock.
//
// The whole feature is allowed to fail silently and does. An update notice that cannot be produced
// is worth nothing, and an error dialog raised because someone is on a train is worth less than
// nothing.
internal sealed class UpdateCheck
{
    // The releases API rather than the download redirect, on purpose. It answers both questions in
    // one request : whether there is something newer, AND the exact file to fetch. Building the
    // asset URL from the tag instead would hardcode today's file-naming convention into every copy
    // of the mod already installed, so the day that convention changed, every older build would
    // send its players to a 404 that nothing could fix from our side.
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Larsonix/GuildrunTargetingMod/releases/latest";

    // GitHub answers 403 to a request with no User-Agent. It is not optional and the failure it
    // causes looks exactly like a network problem.
    private const string UserAgent = "GuildrunTargetingMod";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly MelonPreferences_Entry<bool> _enabled;
    private readonly string _currentVersion;
    private int _started;

    // Written on the request's thread and read on Unity's. The object is built complete and never
    // mutated, and a reference assignment is atomic, so a volatile field is the entire
    // synchronisation this needs. Nothing else here is shared.
    private volatile Result _result;

    /// <summary>A release newer than this build, and where to get it.</summary>
    internal sealed class Result
    {
        public readonly string Version;
        public readonly string DownloadUrl;

        public Result(string version, string downloadUrl)
        {
            Version = version;
            DownloadUrl = downloadUrl;
        }
    }

    public UpdateCheck(MelonPreferences_Entry<bool> enabled, string currentVersion)
    {
        _enabled = enabled;
        _currentVersion = currentVersion;
    }

    /// <summary>
    /// Starts the one request this session makes. Returns immediately ; the answer, if there is one,
    /// arrives in <see cref="TryTakeResult"/> some seconds later.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT gated on the mod being switched on. Whether a player wants their runs to
    /// count has nothing to do with whether they want the version they are running to be current,
    /// and a player who switched the mod off because something looked wrong is exactly the player a
    /// fix is waiting for. It IS gated on the player's own setting for this, which is the only
    /// consent that is actually about contacting the internet.
    /// </remarks>
    public void Start()
    {
        // Once per process, whatever calls it. Interlocked rather than a bool because the caller is
        // not promised to be a single thread and a second request would be pure waste.
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (_enabled == null || !_enabled.Value) return;
        if (!System.Version.TryParse(_currentVersion, out System.Version _)) return;
        // Fire and forget, and the body catches everything : an unobserved faulted task is a process
        // level event in .NET, and this must not be able to reach the game at all.
        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// The newer release, once, or null. Clearing it on the way out is what stops a dialog that was
    /// shown from being shown again by the next tick.
    /// </summary>
    public Result TryTakeResult()
    {
        Result found = _result;
        if (found != null) _result = null;
        return found;
    }

    private async Task RunAsync()
    {
        try
        {
            string body;
            using (var http = new HttpClient { Timeout = RequestTimeout })
            {
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                // The documented media type for the releases API. Asking for it by name is what
                // keeps a future default from changing the shape underneath us.
                http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                using HttpResponseMessage response = await http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Said once, quietly, and never shown to the player. Rate limiting and an
                    // offline machine both land here and neither is worth interrupting anyone for.
                    MelonLogger.Msg("[TargetingMod] update check: GitHub answered " + (int)response.StatusCode +
                                    ", so this session does not know whether a newer version exists");
                    return;
                }
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            ReleaseManifest.Release release = ReleaseManifest.ParseNewer(body, _currentVersion);
            if (release == null) return;
            _result = new Result(release.Version, release.DownloadUrl);
            MelonLogger.Msg("[TargetingMod] update check: " + release.Version + " is available, this build is " +
                            _currentVersion);
        }
        catch (Exception e)
        {
            // Every network failure there is, plus a malformed answer, plus a runtime without the
            // pieces this needs. None of them are the player's problem and none of them may touch
            // the game, so this is the end of the line for all of them.
            MelonLogger.Msg("[TargetingMod] update check did not complete: " + e.Message);
        }
    }

}
