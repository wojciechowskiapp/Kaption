// ─────────────────────────────────────────────────────────────────────────────
//  TranslationPackInfo.cs
//  ---------------------------------------------------------------------------
//  Describes a single translation pack (one game + one target language) as it
//  appears in the Translations tab. A pack's on-disk state falls into one of
//  five categories, each carrying a different support-ticket story:
//
//    * LocalCache      — a matcher cache (`.gisub`) sits in the local-built
//                         folder `%APPDATA%\Kaption\<Game>\`. VoiceContentHelper
//                         produced it from some source JSON. That source is
//                         EITHER still present on disk (see HasLocalSource)
//                         or has been cleaned up / was never there — which
//                         means the cache is effectively orphaned and we can't
//                         prove where the bits came from.
//    * PaidCached      — downloaded by DictionarySyncService, decrypted with
//                         the distribution key, then re-encrypted machine-
//                         bound. Lives in `%APPDATA%\Kaption\paid-dicts\<Game>\`
//                         with a matching row in `paid-dicts\manifest.json`.
//    * RemoteAvailable — listed by `/api/license/files` and unlocked for the
//                         caller's effective_tier; not yet on disk.
//    * RemoteLocked    — listed by `/api/license/files` but tier-gated above
//                         the caller. Shown so users know the pack exists and
//                         how to unlock it.
//    * Missing         — the caller's configured target language has NONE of
//                         the above for the configured game. Used by the
//                         startup-warning path to tell fresh installs why no
//                         subtitles appear.
//
//  Why LocalCache replaces the old "LocalBuilt": the phrase "LocalBuilt"
//  implied "built from something that shipped with Kaption." In practice the
//  source JSON usually came from a third-party mirror (DimbreathBot) or was
//  produced by `tools/translate_textmap.py` on a developer machine. The cache
//  itself tells us nothing about that origin — we have to sniff sibling files
//  to know. HasLocalSource carries that extra bit.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.ComponentModel;

namespace GI_Subtitles.Models
{
    /// <summary>Provenance of a translation pack — where the bytes on disk came from.</summary>
    public enum TranslationPackSource
    {
        /// <summary>
        /// Matcher cache is in the local-built folder. The *source* of that
        /// cache may or may not still exist on disk — see <see cref="TranslationPackInfo.HasLocalSource"/>.
        /// </summary>
        LocalCache,
        /// <summary>Downloaded via DictionarySync and re-encrypted machine-bound.</summary>
        PaidCached,
        /// <summary>Available on the backend for the current tier but not yet on disk.</summary>
        RemoteAvailable,
        /// <summary>Exists on the backend but tier-gated above the current user.</summary>
        RemoteLocked,
        /// <summary>
        /// Nothing on disk, nothing in the remote catalog for the current tier.
        /// Only set by <c>DictionaryInventoryService.CheckTargetLanguage</c> —
        /// the regular scan never emits this value because scans enumerate
        /// what exists, not what's asked for.
        /// </summary>
        Missing,
    }

    /// <summary>
    /// One row in the Translations tab. Represents the union of "what's on
    /// disk for this game/lang" and "what the server offers for this game/lang".
    /// Instances are constructed by <c>DictionaryInventoryService</c>, then
    /// bound to the WPF ListView — all property changes go through
    /// <see cref="INotifyPropertyChanged"/> so the UI refreshes without
    /// rebinding the whole collection.
    /// </summary>
    public sealed class TranslationPackInfo : INotifyPropertyChanged
    {
        // ───────────────────────────────────────────────────────────────────
        //  Identity
        // ───────────────────────────────────────────────────────────────────

        /// <summary>"Genshin" or "StarRail" — matches the Config["Game"] tag.</summary>
        public string Game { get; set; }

        /// <summary>Two-letter target language tag like "PL", "EN", "DE" — matches Config["Output"].</summary>
        public string Language { get; set; }

        /// <summary>Human display name e.g. "Genshin Impact".</summary>
        public string GameDisplayName { get; set; }

        /// <summary>Human display name e.g. "Polski (PL)".</summary>
        public string LanguageDisplayName { get; set; }

        private string _directionLabel;
        /// <summary>
        /// Translation direction for this pack expressed as
        /// "<i>source</i> → <i>target</i>" (e.g. "English → Polski").
        /// Set by <c>RefreshTranslationsAsync</c> after each scan using the
        /// user's current Config["Input"] value — the pack itself doesn't
        /// know the source language because OCR runs in whatever language
        /// the user configured at the app level, not per-pack. Null by
        /// default so the row hides the indicator when direction is unknown.
        /// </summary>
        public string DirectionLabel
        {
            get => _directionLabel;
            set { if (_directionLabel != value) { _directionLabel = value; Raise(nameof(DirectionLabel)); Raise(nameof(HasDirection)); } }
        }

        /// <summary>Visibility helper — true when a non-empty DirectionLabel is set.</summary>
        public bool HasDirection => !string.IsNullOrEmpty(_directionLabel);

        // ───────────────────────────────────────────────────────────────────
        //  Local state
        // ───────────────────────────────────────────────────────────────────

        private TranslationPackSource _source;
        /// <summary>Where these bytes came from (see enum docs).</summary>
        public TranslationPackSource Source
        {
            get => _source;
            set { if (_source != value) { _source = value; Raise(nameof(Source)); Raise(nameof(SourceLabel)); } }
        }

        private string _localPath;
        /// <summary>Full path to the primary on-disk file, or null if not installed.</summary>
        public string LocalPath
        {
            get => _localPath;
            set { if (_localPath != value) { _localPath = value; Raise(nameof(LocalPath)); Raise(nameof(IsInstalled)); } }
        }

        private long _localSize;
        /// <summary>On-disk size in bytes. 0 if not installed.</summary>
        public long LocalSize
        {
            get => _localSize;
            set { if (_localSize != value) { _localSize = value; Raise(nameof(LocalSize)); Raise(nameof(SizeLabel)); } }
        }

        private DateTime? _localModifiedAt;
        /// <summary>Last-modified timestamp of the primary local file.</summary>
        public DateTime? LocalModifiedAt
        {
            get => _localModifiedAt;
            set { if (_localModifiedAt != value) { _localModifiedAt = value; Raise(nameof(LocalModifiedAt)); Raise(nameof(ModifiedLabel)); } }
        }

        private string _sourceFilePath;
        /// <summary>
        /// Path to the source file that was likely used to build
        /// <see cref="LocalPath"/>: a sibling <c>TextMap&lt;Lang&gt;.json</c>
        /// or <c>TextMap&lt;Lang&gt;.gisub</c> in the same folder. Null when
        /// no such sibling exists (orphan cache — migrated from an older
        /// install or hand-placed and we can't verify origin).
        /// </summary>
        public string SourceFilePath
        {
            get => _sourceFilePath;
            set { if (_sourceFilePath != value) { _sourceFilePath = value; Raise(nameof(SourceFilePath)); Raise(nameof(HasLocalSource)); Raise(nameof(SourceLabel)); } }
        }

        /// <summary>Matching source-file size (0 if unknown). Used in diagnostic logs.</summary>
        public long SourceFileSize { get; set; }

        /// <summary>Matching source-file mtime (null if unknown). Used in diagnostic logs.</summary>
        public DateTime? SourceModifiedAt { get; set; }

        /// <summary>True when <see cref="SourceFilePath"/> is set — we can prove the cache came from a local source.</summary>
        public bool HasLocalSource => !string.IsNullOrEmpty(SourceFilePath);

        // ───────────────────────────────────────────────────────────────────
        //  Remote state
        // ───────────────────────────────────────────────────────────────────

        /// <summary>Backend file_version_id when this pack is listed on /api/license/files.</summary>
        public string RemoteFileVersionId { get; set; }

        private string _remoteVersion;
        /// <summary>Server-side version string (e.g. "6.4", "2026-04-13") when known.</summary>
        public string RemoteVersion
        {
            get => _remoteVersion;
            set { if (_remoteVersion != value) { _remoteVersion = value; Raise(nameof(RemoteVersion)); Raise(nameof(CanUpdate)); } }
        }

        private string _localVersion;
        /// <summary>
        /// Version string of the on-disk pack, read from
        /// <c>paid-dicts\manifest.json</c> for <see cref="TranslationPackSource.PaidCached"/>
        /// rows. Null for LocalCache — we don't know what a locally-built
        /// matcher came from, so staleness can't be proven there.
        /// </summary>
        public string LocalVersion
        {
            get => _localVersion;
            set { if (_localVersion != value) { _localVersion = value; Raise(nameof(LocalVersion)); Raise(nameof(CanUpdate)); } }
        }

        /// <summary>Wire size of the .gisub-dist in bytes when known.</summary>
        public long RemoteSize { get; set; }

        /// <summary>Lowest tier allowed to download (free_beta, pro, admin). Null when unlisted.</summary>
        public string RemoteMinTier { get; set; }

        /// <summary>Whether the caller's effective_tier is sufficient to download.</summary>
        public bool RemoteUnlocked { get; set; }

        // ───────────────────────────────────────────────────────────────────
        //  Derived / display helpers (bound directly in XAML)
        // ───────────────────────────────────────────────────────────────────

        /// <summary>True when a usable file exists on disk for this game/lang pair.</summary>
        public bool IsInstalled => !string.IsNullOrEmpty(LocalPath);

        /// <summary>Short status pill text — "Installed", "Available", "Locked", etc.</summary>
        public string StatusLabel
        {
            get
            {
                switch (Source)
                {
                    case TranslationPackSource.LocalCache:
                        return HasLocalSource ? "Installed (local)" : "Installed (unknown origin)";
                    case TranslationPackSource.PaidCached: return "Installed (Kaption pack)";
                    case TranslationPackSource.RemoteAvailable: return "Available";
                    case TranslationPackSource.RemoteLocked: return "Locked";
                    case TranslationPackSource.Missing: return "Not installed";
                    default: return "?";
                }
            }
        }

        /// <summary>Longer source description shown as a subtitle in the list row.</summary>
        public string SourceLabel
        {
            get
            {
                switch (Source)
                {
                    case TranslationPackSource.LocalCache:
                        return HasLocalSource
                            ? "Built from a local source file on this device."
                            : "Cache found with no local source — origin unknown (likely migrated from an older install).";
                    case TranslationPackSource.PaidCached:
                        return "Downloaded from Kaption and encrypted to this device.";
                    case TranslationPackSource.RemoteAvailable:
                        return "Kaption pack available to download.";
                    case TranslationPackSource.RemoteLocked:
                        return RemoteMinTier != null
                            ? $"Requires {RemoteMinTier}. Upgrade to unlock."
                            : "Tier-locked. Upgrade to unlock.";
                    case TranslationPackSource.Missing:
                        return "No local cache and no available download for your tier.";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>Pretty byte size — "88.6 MB", "312 B", etc.</summary>
        public string SizeLabel
        {
            get
            {
                long bytes = LocalSize > 0 ? LocalSize : RemoteSize;
                if (bytes <= 0) return "—";
                return FormatSize(bytes);
            }
        }

        /// <summary>Human "5 minutes ago" / "2026-04-13" depending on recency.</summary>
        public string ModifiedLabel
        {
            get
            {
                if (!LocalModifiedAt.HasValue) return "—";
                var dt = LocalModifiedAt.Value;
                var age = DateTime.Now - dt;
                if (age.TotalSeconds < 60) return "just now";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
                if (age.TotalHours < 24) return $"{(int)age.TotalHours} h ago";
                if (age.TotalDays < 7) return $"{(int)age.TotalDays} d ago";
                return dt.ToString("yyyy-MM-dd");
            }
        }

        /// <summary>True when the "Open Folder" link should render (i.e. LocalPath exists).</summary>
        public bool CanOpenFolder => IsInstalled;

        /// <summary>
        /// True when the "Download" action should appear on the row:
        /// the pack is listed on the backend, our tier unlocks it, and
        /// nothing is yet on disk. We DON'T include LocalCache packs here —
        /// re-pulling over a valid local cache is what the top-level
        /// "Sync Now" button is for (it always fetches regardless of
        /// whether something's already cached). Keeps the per-row action
        /// unambiguous: one row, one verb.
        /// </summary>
        public bool CanDownload =>
            Source == TranslationPackSource.RemoteAvailable && !IsInstalled;

        /// <summary>
        /// True when an installed Kaption pack has a different
        /// <see cref="RemoteVersion"/> than its <see cref="LocalVersion"/>.
        /// Surfaces the per-row "Update" button — a targeted re-sync without
        /// the tab-wide Sync button. LocalCache rows don't participate
        /// (no manifest → no version to compare).
        /// </summary>
        public bool CanUpdate =>
            Source == TranslationPackSource.PaidCached
            && !string.IsNullOrEmpty(RemoteVersion)
            && !string.IsNullOrEmpty(LocalVersion)
            && !string.Equals(RemoteVersion, LocalVersion, StringComparison.Ordinal)
            && RemoteUnlocked;

        private bool _isActiveTarget;
        /// <summary>
        /// True when this pack's (Game, Language) matches the user's
        /// current <c>Config["Game"]</c> + <c>Config["Output"]</c>
        /// selection — i.e. this is the language subtitles appear in
        /// right now. The Translations tab renders an accent stripe +
        /// "Active" label when this is true; clicking a non-active row
        /// writes Config and flips the property for the new row, with
        /// the old row updating via INotifyPropertyChanged.
        /// </summary>
        public bool IsActiveTarget
        {
            get => _isActiveTarget;
            set
            {
                if (_isActiveTarget != value)
                {
                    _isActiveTarget = value;
                    Raise(nameof(IsActiveTarget));
                    Raise(nameof(ActiveLabel));
                    Raise(nameof(RowHighlightOpacity));
                }
            }
        }

        /// <summary>"Active" when this row is the selected target, empty otherwise — XAML binds directly.</summary>
        public string ActiveLabel => _isActiveTarget ? "Active" : string.Empty;

        /// <summary>
        /// Subtle row background hint — the active row gets a faint tint so
        /// it's distinguishable at a glance without being loud. Value is
        /// consumed by an <c>Opacity</c> binding on a colored overlay.
        /// </summary>
        public double RowHighlightOpacity => _isActiveTarget ? 0.08 : 0.0;

        // ───────────────────────────────────────────────────────────────────
        //  INotifyPropertyChanged plumbing
        // ───────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ───────────────────────────────────────────────────────────────────
        //  Byte formatter — standalone so the model has no other deps.
        // ───────────────────────────────────────────────────────────────────
        private static string FormatSize(long bytes)
        {
            const long KB = 1024;
            const long MB = KB * 1024;
            const long GB = MB * 1024;
            if (bytes >= GB) return $"{bytes / (double)GB:0.##} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:0.#} MB";
            if (bytes >= KB) return $"{bytes / (double)KB:0.#} KB";
            return $"{bytes} B";
        }
    }
}
