﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Blish_HUD.Debug;
using Humanizer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Extended;
using MonoGame.Extended.BitmapFonts;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Blish_HUD {

    public class DebugService : GameService {

        #region Logging

        private static Logger Logger;

        private static LoggingConfiguration _logConfiguration;

        private const string STRUCLOG_TIME      = "${time:invariant=true}";
        private const string STRUCLOG_LEVEL     = "${level:uppercase=true:padding=-5}";
        private const string STRUCLOG_LOGGER    = "${logger}";
        private const string STRUCLOG_MESSAGE   = "${message}";
        private const string STRUCLOG_EXCEPTION = "${onexception:${newline}${exception:format=toString}${newline}}";

        private const long MAX_LOG_SIZE     = 1048576; // 1 MB
        private const int  MAX_LOG_SESSIONS = 6;

        internal static void InitDebug() {
            // Make sure crash dir is available for logs as early as possible
            string logPath = DirectoryUtil.RegisterDirectory("logs");

            // Init the Logger
            _logConfiguration = new LoggingConfiguration();

            string headerLayout = $"Blish HUD v{Program.OverlayVersion}";

            var logFile = new FileTarget("logfile") {
                Layout            = $"{STRUCLOG_TIME} | {STRUCLOG_LEVEL} | {STRUCLOG_LOGGER} | {STRUCLOG_MESSAGE}{STRUCLOG_EXCEPTION}",
                Header            = headerLayout,
                FileNameKind      = FilePathKind.Absolute,
                ArchiveFileKind   = FilePathKind.Absolute,
                FileName          = Path.Combine(logPath, "blishhud.${cached:${date:format=yyyyMMdd-HHmmss}}.log"),
                ArchiveFileName   = Path.Combine(logPath, "blishhud.${cached:${date:format=yyyyMMdd-HHmmss}}.{#}.log"),
                ArchiveDateFormat = "yyyyMMdd-HHmmss",
                ArchiveAboveSize  = MAX_LOG_SIZE,
                ArchiveNumbering  = ArchiveNumberingMode.Sequence,
                MaxArchiveFiles   = MAX_LOG_SESSIONS,
                EnableFileDelete  = true,
                CreateDirs        = true,
                Encoding          = Encoding.UTF8,
                KeepFileOpen      = true
            };

            var asyncLogFile = new AsyncTargetWrapper("asynclogfile", logFile) {
                QueueLimit        = 200,
                OverflowAction    = AsyncTargetWrapperOverflowAction.Discard,
                ForceLockingQueue = false
            };

            _logConfiguration.AddTarget(asyncLogFile);

            _logConfiguration.AddRule(
                                      ApplicationSettings.Instance.DebugEnabled
                                          ? LogLevel.Debug
                                          : LogLevel.Info,
                                      LogLevel.Fatal, asyncLogFile
                                     );

            if (ApplicationSettings.Instance.DebugEnabled) {
                AddDebugTarget(_logConfiguration);
            }

            LogManager.Configuration = _logConfiguration;

            Logger = Logger.GetLogger<DebugService>();
        }

        public static void TargetDebug(string time, string level, string logger, string message) {
            System.Diagnostics.Debug.WriteLine($"{time} | {level} | {logger} | {message}");
        }

        private static void AddDebugTarget(LoggingConfiguration logConfig) {
            LogManager.ThrowExceptions = true;

            var logDebug = new MethodCallTarget("logdebug") {
                ClassName  = typeof(DebugService).AssemblyQualifiedName,
                MethodName = nameof(TargetDebug),
                Parameters = {
                    new MethodCallParameter(STRUCLOG_TIME),
                    new MethodCallParameter(STRUCLOG_LEVEL),
                    new MethodCallParameter(STRUCLOG_LOGGER),
                    new MethodCallParameter(STRUCLOG_MESSAGE)
                }
            };

            _logConfiguration.AddTarget(logDebug);
            _logConfiguration.AddRule(LogLevel.Debug, LogLevel.Fatal, logDebug);
        }

        private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs args) {
            Input.DisableHooks();

            var e = (Exception) args.ExceptionObject;

            Logger.Fatal(e, "Blish HUD encountered a fatal crash!");
        }

        #endregion

        #region FPS

        private const int FRAME_DURATION_SAMPLES = 100;

        public FrameCounter FrameCounter { get; private set; }

        #endregion

        #region Measuring

        private const int DEFAULT_DEBUGCOUNTER_SAMPLES = 60;

        private ConcurrentDictionary<string, DebugCounter> _funcTimes;

        /// <summary>
        /// </summary>
        /// <param name="func"></param>
        [Conditional("DEBUG")]
        public void StartTimeFunc(string func) {
            StartTimeFunc(func, DEFAULT_DEBUGCOUNTER_SAMPLES);
        }

        [Conditional("DEBUG")]
        public void StartTimeFunc(string func, int length) {
            if (!_funcTimes.ContainsKey(func)) {
                _funcTimes.TryAdd(func, new DebugCounter(length));
            } else {
                _funcTimes[func].StartInterval();
            }
        }

        [Conditional("DEBUG")]
        public void StopTimeFunc(string func) {
            _funcTimes[func].EndInterval();
        }

        [Conditional("DEBUG")]
        public void StopTimeFuncAndOutput(string func) {
            _funcTimes[func].EndInterval();
            Logger.Debug("{funcName} ran for {$funcTime}.", func, _funcTimes[func]?.GetTotal().Seconds().Humanize());
        }

        #endregion

        #region Debug Overlay

        public OverlayStrings OverlayTexts { get; private set; }

        public void DrawDebugOverlay(SpriteBatch spriteBatch, GameTime gameTime) {
            int debugLeft = Graphics.WindowWidth - 600;

            spriteBatch.DrawString(Content.DefaultFont14, $"FPS: {Math.Round(Debug.FrameCounter.CurrentAverage, 0)}", new Vector2(debugLeft, 25), Color.Red);

            int i = 0;

            foreach (KeyValuePair<string, DebugCounter> timedFuncPair in _funcTimes.Where(ft => ft.Value.GetAverage() > 1).OrderByDescending(ft => ft.Value.GetAverage())) {
                spriteBatch.DrawString(Content.DefaultFont14, $"{timedFuncPair.Key} {Math.Round(timedFuncPair.Value.GetAverage())} ms", new Vector2(debugLeft, 50 + i++ * 25), Color.Orange);
            }

            foreach (Func<GameTime, string> func in this.OverlayTexts.Values) {
                spriteBatch.DrawString(Content.DefaultFont14, func(gameTime), new Vector2(debugLeft, 50 + i++ * 25), Color.Yellow);
            }
        }

        #endregion

        #region Service Implementation

        protected override void Initialize() {
            this.FrameCounter = new FrameCounter(FRAME_DURATION_SAMPLES);

            if (!ApplicationSettings.Instance.DebugEnabled) {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            }
        }

        protected override void Load() {
            _funcTimes = new ConcurrentDictionary<string, DebugCounter>();

            this.OverlayTexts = new OverlayStrings();
            this.OverlayTexts.TryAdd("entityCount", _ => $"3D Entities Displayed: {Graphics.World.Entities.Count()}");
            this.OverlayTexts.TryAdd("renderLate",  gameTime => "Render Late: "     + (gameTime.IsRunningSlowly ? "Yes" : "No"));
            this.OverlayTexts.TryAdd("arcDps",      _ => "ArcDPS Bridge: "          + (ArcDps.RenderPresent ? "Yes" : "No"));
            this.OverlayTexts.TryAdd("volume",      _ => "Average In-Game Volume: " + GameIntegration.Audio.Volume);
            this.OverlayTexts.TryAdd("plrPos",      _ => $"Player Position: {GameService.Gw2Mumble.PlayerCharacter.Position}");
            this.OverlayTexts.TryAdd("cameraPos",   _ => $"Camera Position: {GameService.Gw2Mumble.PlayerCamera.Position}");
            this.OverlayTexts.TryAdd("mapCenter",   _ => $"Map Center: {(int)GameService.Gw2Mumble.UI.MapCenter.X} x {(int)GameService.Gw2Mumble.UI.MapCenter.Y}");
            this.OverlayTexts.TryAdd("mapScale",    _ => $"Map Scale: {GameService.Gw2Mumble.UI.MapScale}");
            this.OverlayTexts.TryAdd("mapId",       _ => $"Map Id: {GameService.Gw2Mumble.CurrentMap.Id}");
            this.OverlayTexts.TryAdd("isComp",      _ => $"Is Comp: {GameService.Gw2Mumble.CurrentMap.IsCompetitiveMode}");
            this.OverlayTexts.TryAdd("mapType",     _ => $"Map Type: {GameService.Gw2Mumble.CurrentMap.Type}");
            this.OverlayTexts.TryAdd("characterCameraDist",     _ => $"Cam/Plr Distance: {Vector3.Distance(GameService.Gw2Mumble.PlayerCharacter.Position, GameService.Gw2Mumble.PlayerCamera.Position)}");
        }

        protected override void Update(GameTime gameTime) {
            this.FrameCounter.Update(gameTime.GetElapsedSeconds());
        }

        protected override void Unload() {
            /* NOOP */
        }

        #endregion

    }

}