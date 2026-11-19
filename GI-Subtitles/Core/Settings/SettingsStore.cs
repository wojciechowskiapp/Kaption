namespace GI_Subtitles.Core.Settings
{
    /// <summary>
    /// Saved settings as a view model or a settings reader sees them: <see cref="Config.Config"/> in the app, a
    /// dictionary in tests. Same contract as Config: Get gives the fallback for a missing value, Set saves at once.
    /// </summary>
    public interface ISettingsStore
    {
        T Get<T>(string key, T fallback);
        void Set<T>(string key, T value);
    }

    /// <summary>The app's settings file (%APPDATA%\Kaption\Config.json).</summary>
    public sealed class ConfigSettingsStore : ISettingsStore
    {
        public static ConfigSettingsStore Instance { get; } = new();

        private ConfigSettingsStore() { }

        public T Get<T>(string key, T fallback) => Config.Config.Get(key, fallback);

        public void Set<T>(string key, T value) => Config.Config.Set(key, value);
    }
}
