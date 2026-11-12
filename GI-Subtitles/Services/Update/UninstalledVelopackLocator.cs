using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace GI_Subtitles.Services.Update
{
    /// <summary>
    /// Velopack's view of a build that is never installed: every path and the version are null, so
    /// <see cref="UpdateManager.IsInstalled"/> is false and <c>VelopackApp.Run</c> returns after its hooks, as with
    /// Velopack's own locator for an unpackaged exe. Unlike that one, it opens no log file: Velopack 1.x's
    /// <c>WindowsVelopackLocator</c> appends to the shared <c>%LOCALAPPDATA%\velopack\velopack.log</c> in its constructor.
    /// </summary>
    internal sealed class UninstalledVelopackLocator : VelopackLocator
    {
        public override string AppId => null;
        public override string RootAppDir => null;
        public override string PackagesDir => null;
        public override string UpdateExePath => null;
        public override string AppContentDir => null;
        public override string Channel => null;
        public override SemanticVersion CurrentlyInstalledVersion => null;
        public override IProcessImpl Process { get; } = new DefaultProcessImpl(NullVelopackLogger.Instance);
    }

    /// <summary>What <c>App.Main</c> hands to <c>VelopackApp</c> before <c>Run</c>.</summary>
    internal static class VelopackStartup
    {
        /// <summary>
        /// The end-to-end mode keeps every file the app writes in its data folder, so its never-installed Debug app gets
        /// <see cref="UninstalledVelopackLocator"/>. Everything else (a shipped build included) gets null: Velopack's own
        /// locator, which finds the install and keeps its log in <c>velopack_Kaption.log</c>.
        /// </summary>
        public static IVelopackLocator LocatorFor(bool e2eMode) => e2eMode ? new UninstalledVelopackLocator() : null;
    }
}
