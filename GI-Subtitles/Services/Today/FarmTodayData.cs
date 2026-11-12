using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Data;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Today
{
    /// <summary>
    /// The farm card's two reads, each at most once per server day and kept on disk: the domain schedule
    /// (kaption.one/data/today/genshin.json → today.json) and the player's showcase (api.kaption.one/api/cards/genshin/
    /// &lt;uid&gt; → showcase.json), both in %APPDATA%\Kaption\Genshin\. Never throws: a failed read falls back to the
    /// last good copy, and without one the card simply does not show. Info/Warn logs only, never the UID.
    /// </summary>
    public static class FarmTodayData
    {
        internal static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static string Dir => GameDataPaths.GameDir(FarmCardSettings.GameId);
        public static string ScheduleFile => Path.Combine(Dir, "today.json");
        public static string ShowcaseFile => Path.Combine(Dir, "showcase.json");

        private static string ScheduleUrl => (Config.Get("AppUrl", CharacterPageLink.Site) ?? CharacterPageLink.Site).TrimEnd('/') + "/data/today/genshin.json";
        private static string ApiBase => (Config.Get("ApiUrl", "https://api.kaption.one") ?? "https://api.kaption.one").TrimEnd('/');

        /// <summary>The schedule: the copy on disk when it was fetched during this server day, else a fresh read.</summary>
        public static async Task<TodaySchedule> ScheduleAsync(ServerDay day)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = ReadSchedule(out DateTime writtenUtc);
                if (cached != null && writtenUtc >= day.Start.UtcDateTime) return cached;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    string text = await Http.GetStringAsync(ScheduleUrl, cts.Token).ConfigureAwait(false);
                    var fresh = TodaySchedule.Parse(text);
                    if (fresh != null)
                    {
                        WriteAtomic(ScheduleFile, text);
                        return fresh;
                    }
                    Logger.Log.Warn("Farm card: the domain schedule from kaption.one is not readable; keeping the last copy.");
                }
                catch (Exception ex)
                {
                    Logger.Log.Info($"Farm card: domain schedule not fetched ({ex.GetType().Name}: {ex.Message}).");
                }
                return cached;
            }
            finally
            {
                Gate.Release();
            }
        }

        // A UID the API said has no player, and the server day it said so: not asked again until the next reset.
        private static (string Uid, string Day)? _unknownUid;

        /// <summary>
        /// The player's showcase for this server day: the cached read when it is from today (and for this UID), else one
        /// read of the API. A failed read keeps the older copy (Roster null when there is none); a UID with no player
        /// comes back as <see cref="ShowcaseRead.UnknownUid"/>.
        /// </summary>
        public static async Task<ShowcaseRead> ShowcaseAsync(string uid, ServerDay day, FarmServer server)
        {
            if (uid == null) return ShowcaseRead.None;
            string key = day.Key(server);
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_unknownUid == (uid, key)) return ShowcaseRead.Unknown;
                var cached = ShowcaseRoster.FromCache(ReadText(ShowcaseFile), uid);
                if (cached != null && cached.Day == key) return new ShowcaseRead(cached, false);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var resp = await Http.GetAsync($"{ApiBase}/api/cards/genshin/{uid}", cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        string text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        var fresh = ShowcaseRoster.FromApi(text, uid, key, DateTimeOffset.UtcNow);
                        if (fresh != null)
                        {
                            WriteAtomic(ShowcaseFile, fresh.ToCacheJson());
                            Logger.Log.Info($"Farm card: showcase read ({fresh.Characters.Count} characters).");
                            return new ShowcaseRead(fresh, false);
                        }
                        Logger.Log.Warn("Farm card: the showcase answer is not readable.");
                    }
                    else
                    {
                        string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        if (ShowcaseRoster.IsUnknownUidAnswer((int)resp.StatusCode, body))
                        {
                            _unknownUid = (uid, key);
                            Logger.Log.Info("Farm card: the saved UID has no player (HTTP 404); not asked again this server day.");
                            return ShowcaseRead.Unknown;
                        }
                        Logger.Log.Info($"Farm card: showcase not read (HTTP {(int)resp.StatusCode}).");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log.Info($"Farm card: showcase not read ({ex.GetType().Name}: {ex.Message}).");
                }
                return new ShowcaseRead(cached, false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>"Usuń UID i dane": the key and the cached showcase go.</summary>
        public static void ForgetPlayer()
        {
            FarmCardSettings.ForgetUid();
            _unknownUid = null;
            try { if (File.Exists(ShowcaseFile)) File.Delete(ShowcaseFile); }
            catch (Exception ex) { Logger.Log.Warn($"Farm card: cached showcase not deleted: {ex.Message}"); }
            Logger.Log.Info("Farm card: UID and cached showcase forgotten.");
        }

        private static TodaySchedule ReadSchedule(out DateTime writtenUtc)
        {
            writtenUtc = DateTime.MinValue;
            try
            {
                if (!File.Exists(ScheduleFile)) return null;
                writtenUtc = File.GetLastWriteTimeUtc(ScheduleFile);
                return TodaySchedule.Parse(ReadText(ScheduleFile));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card: cached schedule not read: {ex.Message}");
                return null;
            }
        }

        internal static string ReadText(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length <= ShowcaseRoster.MaxBytes ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".part";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Farm card: {Path.GetFileName(path)} not saved: {ex.Message}");
            }
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
            string v = typeof(FarmTodayData).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"Kaption/{v}");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }
    }

    /// <summary>One showcase lookup: the roster (null when nothing could be read), or the API saying the UID has no player.</summary>
    public sealed record ShowcaseRead(ShowcaseRoster Roster, bool UnknownUid)
    {
        public static readonly ShowcaseRead None = new(null, false);
        public static readonly ShowcaseRead Unknown = new(null, true);
    }
}
