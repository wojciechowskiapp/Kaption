using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>Builds the reader a manifest entry asks for, or null when this build has none for it.</summary>
    public delegate IAssistReader AssistReaderFactory(GameManifest game, string readerId, GameAssistReader spec);

    /// <summary>The readers this build has. A kind or a way of locating it does not know is skipped, never approximated.</summary>
    public static class AssistReaders
    {
        public static IAssistReader Create(GameManifest game, string readerId, GameAssistReader spec)
        {
            if (game == null || spec == null) return null;
            if (spec.Kind == GameAssistReader.KindFraction && spec.Locate == GameAssistReader.LocateSearch
                && spec.Spec == GameAssistReader.SpecStaminaScreen && spec.Cap == GameAssistReader.CapStaminaMax)
                return StaminaAssistReader.ForGame(game, readerId);
            return null;
        }
    }

    /// <summary>
    /// One game's share of the watch, put together off the capture tick: the readers the player switched on, the
    /// screens those readers need with their templates decoded, and when each is next due.
    /// </summary>
    internal sealed class AssistSession : IDisposable
    {
        internal sealed class Anchor
        {
            public GameAssistAnchor Spec;
            public AssistTemplate Template;
        }

        internal sealed class Screen
        {
            public string Id;
            public Anchor[] Anchors;
            public long Next;
            /// <summary>The frame this screen was last probed on (<see cref="IAssistHost.FrameSerial"/>), once it was.</summary>
            public bool Probed;
            public long ProbedSerial;
            /// <summary>What the last real probe saw (one probe, not the two that make a screen count).</summary>
            public bool LastSeen;
            /// <summary>Tick only: this look knows about the screen (it probes it, or the frame is the one last probed).</summary>
            public bool Told;
        }

        internal sealed class Slot
        {
            public IAssistReader Reader;
            /// <summary>Index into <see cref="Screens"/>, or -1 for a reader that finds its own anchor.</summary>
            public int Screen = -1;
            public long Next;
        }

        public GameManifest Game { get; private set; }
        public GameAssistCanvas Canvas { get; private set; }
        public GameAssistChecked Checked { get; private set; }
        public Screen[] Screens { get; private set; } = Array.Empty<Screen>();
        public Slot[] Readers { get; private set; } = Array.Empty<Slot>();
        public ScreenPresence Presence { get; private set; } = new ScreenPresence(0);
        public ScreenProbe Probe { get; } = new ScreenProbe();

        /// <param name="loadTemplate">Game id and template name to the picture; the embedded ones when null.</param>
        public static AssistSession Build(GameManifest game, AssistReaderFactory create, Func<string, bool> isOn,
            Func<string, string, AssistTemplate> loadTemplate = null)
        {
            loadTemplate ??= AssistTemplate.Load;
            var session = new AssistSession { Game = game };
            GameAssist assist = game?.Desktop?.Assist;
            if (assist?.Readers == null) return session;
            session.Canvas = assist.Canvas;
            session.Checked = assist.Checked;

            var screens = new List<Screen>();
            var slots = new List<Slot>();
            try
            {
                foreach (var entry in assist.Readers.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    if (!isOn(entry.Key)) continue;
                    IAssistReader reader = create(game, entry.Key, entry.Value);
                    if (reader == null)
                    {
                        Logger.Log.Info($"In-game helper: this build has no reader for {game.Id}/{entry.Key}; it stays off.");
                        continue;
                    }
                    var slot = new Slot { Reader = reader };
                    if (reader.Screen != null)
                    {
                        slot.Screen = screens.FindIndex(s => s.Id == reader.Screen);
                        if (slot.Screen < 0)
                        {
                            Screen screen = BuildScreen(game, assist, reader.Screen, loadTemplate);
                            if (screen == null)
                            {
                                Logger.Log.Warn($"In-game helper: {game.Id}/{entry.Key} needs the screen \"{reader.Screen}\", which this build cannot tell; it stays off.");
                                reader.Dispose();
                                continue;
                            }
                            screens.Add(screen);
                            slot.Screen = screens.Count - 1;
                        }
                    }
                    slots.Add(slot);
                }
            }
            catch
            {
                // A reader that failed to build takes nothing with it: the ones built before it are let go.
                foreach (Slot built in slots) built.Reader.Dispose();
                throw;
            }
            session.Screens = screens.ToArray();
            session.Readers = slots.ToArray();
            session.Presence = new ScreenPresence(session.Screens.Length);
            return session;
        }

        /// <summary>A screen with every template present, or null: a screen that cannot be told is not half-told.</summary>
        private static Screen BuildScreen(GameManifest game, GameAssist assist, string id, Func<string, string, AssistTemplate> loadTemplate)
        {
            if (assist.Canvas == null || assist.Screens == null || !assist.Screens.TryGetValue(id, out GameAssistScreen spec)) return null;
            if (spec?.Anchors == null || spec.Anchors.Length == 0) return null;
            var anchors = new Anchor[spec.Anchors.Length];
            for (int i = 0; i < anchors.Length; i++)
            {
                GameAssistAnchor a = spec.Anchors[i];
                AssistTemplate template = a == null ? null : loadTemplate(game.Id, a.Template);
                if (template == null || template.Width != a.W || template.Height != a.H || !AssistGeometry.FitsCanvas(a, assist.Canvas)) return null;
                anchors[i] = new Anchor { Spec = a, Template = template };
            }
            return new Screen { Id = id, Anchors = anchors };
        }

        public IEnumerable<string> ReaderIds => Readers.Select(r => r.Reader.Id);

        public void Dispose()
        {
            foreach (Slot slot in Readers) slot.Reader.Dispose();
        }
    }
}
