using System;
using System.Drawing;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using OpenCvSharp;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// The stamina number as a reader of the screen watch (Services/Assist): the watch decides when to look and hands
    /// over the top band of the game picture; this finds the icon and reads the number beside it exactly as before
    /// (<see cref="StaminaScreenReader"/>). It finds its own anchor, so it is not bound to a measured window shape:
    /// where the icon is not found, nothing is read.
    /// </summary>
    public sealed class StaminaAssistReader : IAssistReader
    {
        private readonly GameManifest _game;
        private readonly GameStaminaScreen _spec;
        private readonly StaminaScreenReader _reader;
        private readonly StaminaSampleSaver _saver;
        private readonly Func<bool> _saveSamples;
        private readonly ThrottledLogger _saveLog = new ThrottledLogger(TimeSpan.FromMinutes(10));

        public StaminaAssistReader(GameManifest game, string id, StaminaScreenReader reader, StaminaSampleSaver saver, Func<bool> saveSamples)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _spec = game.Desktop?.StaminaScreen ?? throw new ArgumentException("The game has no measured stamina screen.", nameof(game));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            Id = id;
            _saver = saver;
            _saveSamples = saveSamples ?? (() => false);
        }

        /// <summary>The reader for a game, or null when its manifest has no stamina, no measured screen or no icon.</summary>
        public static StaminaAssistReader ForGame(GameManifest game, string id)
        {
            StaminaScreenReader reader = StaminaScreenReader.ForGame(game);
            return reader == null
                ? null
                : new StaminaAssistReader(game, id, reader, new StaminaSampleSaver(game.GameId, StaminaSettings.SampleLimit), () => StaminaSettings.SaveSamples);
        }

        public string Id { get; }
        public string LogName => "Stamina reader";
        public string Screen => null;
        public int PeriodSeconds => _spec.SampleSeconds;
        public bool BlacksOutOwnWindows => true;

        public Rectangle Area(Rectangle client, bool fixedPlacesAllowed) => StaminaLocator.SearchArea(client, _spec);

        public AssistReading Look(AssistPixels pixels, System.Drawing.Size client, DateTimeOffset capturedAt, AssistLineReader read)
        {
            using Mat strip = pixels.AsMat();
            StaminaSample sample = _reader.Read(strip, client, (Mat line, out string text, out float score) =>
            {
                text = null;
                score = 0;
                return read != null && read(line, out text, out score);
            });
            if (Logger.IsDebugEnabled && sample.Outcome != StaminaSampleOutcome.NoIcon)
                Logger.Log.Debug($"Stamina look: {sample.Outcome}, icon {sample.IconScore:0.00}, text {sample.TextScore:0.00}"
                                 + (sample.KnownPlace ? " (known place)" : "") + (sample.Reused ? " (unchanged picture)" : ""));
            if (_saver != null && _saveSamples())
            {
                try { _saver.Save(strip, sample, capturedAt); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
                {
                    if (_saveLog.ShouldLog("save", out _)) Logger.Log.Warn($"Stamina reader: a sample picture was not saved: {ex.Message}");
                }
            }
            return sample.Outcome switch
            {
                StaminaSampleOutcome.Read => new AssistReading(_game.Id, Id, AssistOutcome.Read, AssistRefusal.None, sample.Value, sample.Text, capturedAt),
                StaminaSampleOutcome.ReaderBusy => AssistReading.Nothing(_game.Id, Id, AssistOutcome.Busy, capturedAt),
                StaminaSampleOutcome.NoNumber => AssistReading.Nothing(_game.Id, Id, AssistOutcome.Refused, capturedAt, AssistRefusal.NothingBeside),
                StaminaSampleOutcome.Refused => AssistReading.Nothing(_game.Id, Id, AssistOutcome.Refused, capturedAt, AssistRefusal.NotTheShape),
                _ => AssistReading.Nothing(_game.Id, Id, AssistOutcome.NotShown, capturedAt),
            };
        }

        public void Dispose() => _reader.Dispose();
    }
}
