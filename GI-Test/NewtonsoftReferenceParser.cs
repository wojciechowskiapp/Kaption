using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GI_Test
{
    /// <summary>
    /// Newtonsoft baseline for the flat-dictionary parse, kept so the streaming
    /// System.Text.Json implementation in <c>VoiceContentHelper</c> can be diffed
    /// against a second, independent parser on identical input.
    ///
    /// <para>Lives here rather than in GI-Subtitles because it is the last thing
    /// that needed Newtonsoft in the shipped assembly. The test project is free
    /// to depend on it; the app is not.</para>
    /// </summary>
    internal static class NewtonsoftReferenceParser
    {
        public static Dictionary<string, string> LoadFlatJsonDictionary(
            Stream utf8Json,
            bool flattenWrappedObjects)
        {
            using (var sr = new StreamReader(utf8Json, Encoding.UTF8, true, 4096, leaveOpen: true))
            using (var jr = new Newtonsoft.Json.JsonTextReader(sr))
            {
                var tokenMap = JObject.Load(jr);
                var raw = new Dictionary<string, string>(tokenMap.Count, System.StringComparer.Ordinal);
                foreach (var prop in tokenMap.Properties())
                {
                    string flat = FlattenDictValue(prop.Value, flattenWrappedObjects);
                    if (flat != null)
                        raw[prop.Name] = flat;
                }
                return raw;
            }
        }

        private static string FlattenDictValue(JToken tok, bool flattenWrappedObjects)
        {
            if (tok == null) return null;
            switch (tok.Type)
            {
                case JTokenType.String:
                    return (string)tok;
                case JTokenType.Null:
                    return null;
                case JTokenType.Object:
                {
                    if (!flattenWrappedObjects) return null;
                    var o = (JObject)tok;
                    var pick = o["value"] ?? o["text"] ?? o["str"];
                    return pick?.Type == JTokenType.String ? (string)pick : null;
                }
                default:
                    return null;
            }
        }
    }
}
