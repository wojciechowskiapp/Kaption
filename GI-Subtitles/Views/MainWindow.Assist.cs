using System;
using System.Collections.Generic;
using System.Drawing;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Diagnostics;
using GI_Subtitles.Services.Games;
using OpenCvSharp;
using PaddleOCRSharp;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The screen watch's seat on the capture tick (Services/Assist/AssistWatch.cs): this window owns the capture
    /// backend, knows where the game and Kaption's own panels are, and has the text engine, so it answers the watch's
    /// questions. Everything the watch reads is opt-in and stays on this PC; nothing here sends input to the game or
    /// touches its process, memory or files.
    /// </summary>
    public partial class MainWindow : IAssistHost
    {
        private AssistWatch _assist;
        private PaddleOCREngine _assistLineEngine;
        private AssistLineReader _assistLineReader;

        private void InitAssist()
        {
            _assist = new AssistWatch(this, AssistReaders.Create, AssistSettings.IsOn, AssistStore.Default,
                game => GameRegistry.Current.Find(game), () => GameRegistry.Current.Listed);
            _assist.Read += OnAssistReading;
            AssistSettings.Changed += OnAssistSettingsChanged;
            DiagnosticBundle.RegisterStateProvider("In-game helper", () => _assist?.Describe() ?? "not started");
            Closed += (_, __) =>
            {
                AssistSettings.Changed -= OnAssistSettingsChanged;
                _assist?.Dispose();
                AssistStore.Default.Flush();
            };
        }

        private void OnAssistSettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnAssistSettingsChanged));
                return;
            }
            _assist?.SettingsChanged();
        }

        /// <summary>UI thread: a reader read a value. Each reader's own part of the app decides what it is worth.</summary>
        private void OnAssistReading(AssistReading reading)
        {
            if (reading.ReaderId == AssistSettings.StaminaReader) OnStaminaValue(reading.GameId, reading.Value, reading.At);
        }

        IScreenCapture IAssistHost.Capture => _captureBackend;

        // GDI holds no frame and cannot tell whether the screen changed: a new number every time.
        long IAssistHost.FrameSerial => _captureBackend is DxgiScreenCapture ? DxgiScreenCapture.FrameSerial : Environment.TickCount64;

        string IAssistHost.SelectedGame => Config.Get<string>("Game", Game ?? "Genshin");

        // The tick runs only while the game is the foreground window, so that window is the game's.
        bool IAssistHost.TryGetGameClient(out Rectangle client) => GameWindowDetector.TryGetClientArea(GetForegroundWindow(), out client);

        /// <summary>
        /// The subtitle panel and the Ctrl+Q popup where a frame can show them, now and at the previous read (a frame
        /// can be one composition older than the layout). Empty when Windows keeps them out of the capture.
        /// </summary>
        IReadOnlyList<Rectangle> IAssistHost.OwnWindowAreas()
        {
            if (!OverlayCanBeInFrame) return Array.Empty<Rectangle>();
            IReadOnlyList<Rectangle> now = OverlayScreenAreas();
            IReadOnlyList<Rectangle> before = _overlayAreaHistory.Observe(now, Environment.TickCount64);
            if (before == null || before.Count == 0) return now;
            var all = new List<Rectangle>(now.Count + before.Count);
            all.AddRange(now);
            all.AddRange(before);
            return all;
        }

        /// <summary>
        /// Recognition only, and only when the engine is free: a reader never waits behind a dialogue pass and never
        /// runs text detection (a new detector shape stays slow under DirectML).
        /// </summary>
        AssistLineReader IAssistHost.LineReader
        {
            get
            {
                PaddleOCREngine engine = data != null && data.Engine == SettingsWindow.EngineStatus.Ready ? data.engine : null;
                if (engine == null) return null;
                if (!ReferenceEquals(engine, _assistLineEngine))
                {
                    _assistLineEngine = engine;
                    _assistLineReader = (Mat line, out string text, out float score) => engine.TryRecognizeLine(line, out text, out score);
                }
                return _assistLineReader;
            }
        }

        bool IAssistHost.Post(Action onUiThread)
        {
            if (Dispatcher.HasShutdownStarted) return false;
            Dispatcher.BeginInvoke(onUiThread);
            return true;
        }
    }
}
