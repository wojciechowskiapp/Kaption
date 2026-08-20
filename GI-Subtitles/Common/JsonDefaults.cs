using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// Shared System.Text.Json options for the app.
    ///
    /// <para>Two defaults here exist to preserve Newtonsoft behaviour across the
    /// migration and must not be changed casually:</para>
    ///
    /// <para><b>PropertyNameCaseInsensitive.</b> Newtonsoft matches property
    /// names case-insensitively; System.Text.Json does not. Every payload this
    /// app reads — backend responses, Config.json, bundle sidecars, gamedata
    /// manifests — was written under the loose rule. Turning this off would make
    /// a differently-cased field deserialize to null silently rather than fail.</para>
    ///
    /// <para><b>UnsafeRelaxedJsonEscaping.</b> The default encoder escapes every
    /// non-ASCII character, which would rewrite Polish dialogue as \u sequences
    /// on any round-trip through a config or cache file. "Unsafe" refers to HTML
    /// contexts; nothing here writes JSON into a web page.</para>
    /// </summary>
    public static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions(Options)
        {
            WriteIndented = true,
        };

        /// <summary>Mirrors Newtonsoft's <c>NullValueHandling.Ignore</c>.</summary>
        public static readonly JsonSerializerOptions IgnoreNulls = new JsonSerializerOptions(Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static readonly JsonSerializerOptions IndentedIgnoreNulls = new JsonSerializerOptions(Indented)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
