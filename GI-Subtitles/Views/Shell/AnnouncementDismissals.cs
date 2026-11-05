using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Announcements the player closed on Start (what migration 006 promised): their ids under
    /// <see cref="Key"/>, newest last, at most <see cref="Cap"/>, so a closed banner never comes back.
    /// </summary>
    public static class AnnouncementDismissals
    {
        public const string Key = "Ui.DismissedAnnouncements";
        public const int Cap = 50;

        public static IReadOnlyList<string> Load() => Config.Get<string[]>(Key, null) ?? Array.Empty<string>();

        public static void Dismiss(string id) => Config.Set(Key, Add(Load(), id).ToArray());

        /// <summary>The list with <paramref name="id"/> added at the end (moved there when present), the oldest dropped past the cap.</summary>
        public static IReadOnlyList<string> Add(IReadOnlyList<string> ids, string id)
        {
            var list = (ids ?? Array.Empty<string>()).Where(x => !string.IsNullOrEmpty(x) && !string.Equals(x, id, StringComparison.Ordinal)).ToList();
            if (!string.IsNullOrEmpty(id)) list.Add(id);
            return list.Count > Cap ? list.Skip(list.Count - Cap).ToList() : list;
        }

        /// <summary>The announcements still to show: an item without an id cannot be closed for good, so it stays.</summary>
        public static IReadOnlyList<T> Visible<T>(IEnumerable<T> items, Func<T, string> id, IReadOnlyCollection<string> dismissed)
        {
            var gone = new HashSet<string>(dismissed ?? Array.Empty<string>(), StringComparer.Ordinal);
            return (items ?? Enumerable.Empty<T>()).Where(i => i != null && !gone.Contains(id(i) ?? string.Empty)).ToList();
        }
    }
}
