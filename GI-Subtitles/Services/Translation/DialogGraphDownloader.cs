using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Downloads raw game data from DimbreathBot/AnimeGameData and builds
    /// compact preprocessed dialogue graph files for the prediction engine.
    ///
    /// This is the C# equivalent of tools/build_dialogue_graph.py.
    /// Runs on first launch (when DialogGraph.json doesn't exist) and
    /// downloads ~93MB of raw dialog data, processes it into ~25MB of
    /// compact files, then saves them alongside the TextMap files.
    /// </summary>
    public static class DialogGraphDownloader
    {
        private const string GITHUB_RAW = "https://raw.githubusercontent.com/DimbreathBot/AnimeGameData/master";

        // Net8: WebClient is obsolete (SYSLIB0014). Single static HttpClient
        // (correct net8 singleton pattern — see KaptionApiClient.CreateHttpClient
        // for the same rationale) with SocketsHttpHandler + Brotli + pooled
        // connection lifetime for CF-edge friendliness. Timeout is infinite
        // because dialog-graph builds pull ~93 MB of raw dialog data in one
        // pass and the orchestrator times out around it.
        private static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        // Known obfuscated dialog ID field names (changes with game versions)
        private static readonly string[] DialogIdCandidates = { "GFLDJMJKIKE" };

        /// <summary>
        /// Check if dialogue graph files exist in the data directory.
        /// Checks both encrypted (.gisub) and plaintext (.json) variants.
        /// </summary>
        public static bool GraphExists(string gameDataDir, FileProtectionHelper protectionHelper = null)
        {
            var jsonPath = Path.Combine(gameDataDir, "DialogGraph.json");
            if (protectionHelper != null)
                return protectionHelper.FileExists(jsonPath);
            return File.Exists(jsonPath);
        }

        /// <summary>
        /// Download raw ExcelBin data from GitHub and build the dialogue graph.
        /// Downloads DialogExcelConfigData.json (~93MB), NpcExcelConfigData.json
        /// (~14MB), and TalkExcelConfigData files (~127MB total).
        ///
        /// Session 24 (2026-04-16): this path is now a FALLBACK. The preferred
        /// source is <see cref="GamedataSyncService"/>, which pulls a prebuilt
        /// bundle from Kaption R2 (version-locked to the translation pack, so
        /// there's no drift between graph and pack). The bundle sync runs in
        /// GameDataBootstrapService BEFORE DialogueContextEngine.Load, and
        /// writes the same five filenames this method produces. So by the
        /// time DialogueContextEngine calls DownloadAndBuild, the files
        /// usually already exist and <see cref="GraphExists"/> short-circuits.
        ///
        /// DownloadAndBuild still runs when:
        ///   * Bundle hasn't been published yet for the user's game/version.
        ///   * User is offline or has no session (bundle sync skipped).
        ///   * User is on a build older than the bundle publish date.
        /// In those cases we need SOMETHING to populate the prediction
        /// indexes, and GitHub is the only source of truth. Keep it.
        /// </summary>
        public static void DownloadAndBuild(string gameDataDir, string textMapEnPath,
            IProgress<(int percent, string message)> progress = null,
            FileProtectionHelper protectionHelper = null)
        {
            Logger.Log.Info(
                "DialogGraphDownloader: FALLBACK path active — rebuilding graph from " +
                "GitHub ExcelBin. This runs only when the R2 gamedata bundle is " +
                "unavailable (unpublished, offline, unlicensed).");

            var cacheDir = Path.Combine(gameDataDir, "cache");
            Directory.CreateDirectory(cacheDir);

            // Step 1: Download raw files
            progress?.Report((1, "Downloading dialogue data from GitHub..."));

            var dialogPath = Path.Combine(cacheDir, "DialogExcelConfigData.json");
            if (!File.Exists(dialogPath))
            {
                DownloadFile($"{GITHUB_RAW}/ExcelBinOutput/DialogExcelConfigData.json",
                    dialogPath, "DialogExcelConfigData", progress, 1, 30);
            }
            else
            {
                progress?.Report((30, "Dialog data cached"));
            }

            var npcPath = Path.Combine(cacheDir, "NpcExcelConfigData.json");
            if (!File.Exists(npcPath))
            {
                DownloadFile($"{GITHUB_RAW}/ExcelBinOutput/NpcExcelConfigData.json",
                    npcPath, "NpcExcelConfigData", progress, 30, 35);
            }

            var talk0Path = Path.Combine(cacheDir, "TalkExcelConfigData_0.json");
            if (!File.Exists(talk0Path))
            {
                DownloadFile($"{GITHUB_RAW}/ExcelBinOutput/TalkExcelConfigData_0.json",
                    talk0Path, "TalkExcelConfigData_0", progress, 35, 50);
            }

            var talk1Path = Path.Combine(cacheDir, "TalkExcelConfigData_1.json");
            if (!File.Exists(talk1Path))
            {
                DownloadFile($"{GITHUB_RAW}/ExcelBinOutput/TalkExcelConfigData_1.json",
                    talk1Path, "TalkExcelConfigData_1", progress, 50, 60);
            }

            var questPath = Path.Combine(cacheDir, "MainQuestExcelConfigData.json");
            if (!File.Exists(questPath))
            {
                DownloadFile($"{GITHUB_RAW}/ExcelBinOutput/MainQuestExcelConfigData.json",
                    questPath, "MainQuestExcelConfigData", progress, 60, 63);
            }

            // Step 2: Build graph
            progress?.Report((65, "Building dialogue graph..."));
            BuildGraph(dialogPath, npcPath, talk0Path, talk1Path, questPath,
                textMapEnPath, gameDataDir, progress, protectionHelper);

            progress?.Report((100, "Dialogue graph ready"));
        }

        private static void DownloadFile(string url, string destPath, string label,
            IProgress<(int percent, string message)> progress, int pctStart, int pctEnd)
        {
            // Net8: WebClient (SYSLIB0014) replaced with HttpClient stream-to-
            // file pattern. HttpCompletionOption.ResponseHeadersRead keeps peak
            // RAM flat during download (no full-body materialization before
            // disk write). This matches the KaptionApiClient download pattern.
            string tmpPath = destPath + ".tmp";
            try
            {
                progress?.Report((pctStart, $"Downloading {label}..."));

                using (var response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    using (var netStream = response.Content.ReadAsStream())
                    using (var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        netStream.CopyTo(fileStream);
                    }
                }

                File.Move(tmpPath, destPath);
                var sizeMb = new FileInfo(destPath).Length / (1024.0 * 1024.0);
                progress?.Report((pctEnd, $"Downloaded {label} ({sizeMb:F1} MB)"));
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Failed to download {label}: {ex.Message}");
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
                throw;
            }
        }

        private static void BuildGraph(string dialogPath, string npcPath,
            string talk0Path, string talk1Path, string questPath,
            string textMapEnPath, string outputDir,
            IProgress<(int percent, string message)> progress,
            FileProtectionHelper protectionHelper = null)
        {
            // Load TextMapEN for name resolution
            Dictionary<string, string> textMapEN = null;
            if (File.Exists(textMapEnPath))
            {
                progress?.Report((66, "Loading TextMapEN for name resolution..."));
                using (var stream = File.OpenRead(textMapEnPath))
                using (var reader = new StreamReader(stream))
                {
                    textMapEN = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
                }
            }

            // Parse DialogExcelConfigData (streaming for memory efficiency)
            progress?.Report((70, "Parsing dialog data..."));
            var dialogGraph = new Dictionary<string, object>();
            var hashToDialogs = new Dictionary<string, List<long>>();

            using (var stream = File.OpenRead(dialogPath))
            using (var sr = new StreamReader(stream))
            using (var jr = new JsonTextReader(sr))
            {
                string idField = null;
                jr.Read(); // StartArray

                while (jr.Read())
                {
                    if (jr.TokenType == JsonToken.StartObject)
                    {
                        var entry = JObject.Load(jr);

                        // Detect dialog ID field on first entry
                        if (idField == null)
                        {
                            foreach (var candidate in DialogIdCandidates)
                            {
                                if (entry[candidate] != null)
                                {
                                    idField = candidate;
                                    break;
                                }
                            }
                            // Fallback: find first int field > 0 that's not a known field
                            if (idField == null)
                            {
                                foreach (var prop in entry.Properties())
                                {
                                    if (prop.Value.Type == JTokenType.Integer &&
                                        prop.Value.Value<long>() > 0 &&
                                        prop.Name != "talkContentTextMapHash" &&
                                        prop.Name != "talkTitleTextMapHash" &&
                                        prop.Name != "talkRoleNameTextMapHash")
                                    {
                                        idField = prop.Name;
                                        break;
                                    }
                                }
                            }
                            if (idField == null) idField = DialogIdCandidates[0];
                            Logger.Log.Info($"Dialog ID field detected: {idField}");
                        }

                        long dialogId = entry.Value<long>(idField);
                        if (dialogId == 0) continue;

                        long contentHash = entry.Value<long>("talkContentTextMapHash");
                        long nameHash = entry.Value<long>("talkRoleNameTextMapHash");
                        var nextDialogs = entry["nextDialogs"] as JArray;
                        var talkRole = entry["talkRole"] as JObject;

                        var node = new Dictionary<string, object>();
                        if (contentHash != 0) node["h"] = contentHash;
                        if (nameHash != 0) node["nh"] = nameHash;

                        if (nextDialogs != null && nextDialogs.Count > 0)
                        {
                            var nextList = new List<long>();
                            foreach (var n in nextDialogs)
                                nextList.Add(n.Value<long>());
                            node["n"] = nextList;
                        }

                        if (talkRole != null)
                        {
                            var roleType = talkRole.Value<string>("type");
                            var roleId = talkRole.Value<string>("id");
                            if (!string.IsNullOrEmpty(roleType)) node["rt"] = roleType;
                            if (!string.IsNullOrEmpty(roleId)) node["ri"] = roleId;
                        }

                        dialogGraph[dialogId.ToString()] = node;

                        // Build reverse hash index
                        if (contentHash != 0)
                        {
                            string hKey = contentHash.ToString();
                            if (!hashToDialogs.TryGetValue(hKey, out var list))
                            {
                                list = new List<long>(1);
                                hashToDialogs[hKey] = list;
                            }
                            list.Add(dialogId);
                        }
                    }
                }
            }

            Logger.Log.Info($"Built dialog graph: {dialogGraph.Count:N0} nodes, {hashToDialogs.Count:N0} hashes");
            progress?.Report((80, $"Dialog graph: {dialogGraph.Count:N0} nodes"));

            // Parse NPC names
            var npcNames = new Dictionary<string, long>();
            if (File.Exists(npcPath))
            {
                using (var stream = File.OpenRead(npcPath))
                using (var sr = new StreamReader(stream))
                using (var jr = new JsonTextReader(sr))
                {
                    jr.Read(); // StartArray
                    while (jr.Read())
                    {
                        if (jr.TokenType == JsonToken.StartObject)
                        {
                            var entry = JObject.Load(jr);
                            long id = entry.Value<long>("id");
                            long nameHash = entry.Value<long>("nameTextMapHash");
                            if (id > 0 && nameHash > 0)
                                npcNames[id.ToString()] = nameHash;
                        }
                    }
                }
            }
            progress?.Report((85, $"NPC names: {npcNames.Count:N0}"));

            // Parse Talk data
            var talkIndex = new Dictionary<string, object>();
            foreach (var talkPath in new[] { talk0Path, talk1Path })
            {
                if (!File.Exists(talkPath)) continue;
                using (var stream = File.OpenRead(talkPath))
                using (var sr = new StreamReader(stream))
                using (var jr = new JsonTextReader(sr))
                {
                    jr.Read(); // StartArray
                    while (jr.Read())
                    {
                        if (jr.TokenType == JsonToken.StartObject)
                        {
                            var entry = JObject.Load(jr);
                            long talkId = entry.Value<long>("id");
                            if (talkId == 0) continue;

                            var node = new Dictionary<string, object>();
                            long initDialog = entry.Value<long>("initDialog");
                            if (initDialog != 0) node["init"] = initDialog;

                            long questId = entry.Value<long>("questId");
                            if (questId != 0) node["quest"] = questId;

                            var npcIdArr = entry["npcId"] as JArray;
                            if (npcIdArr != null && npcIdArr.Count > 0)
                            {
                                var npcList = new List<long>();
                                foreach (var n in npcIdArr) npcList.Add(n.Value<long>());
                                node["npc"] = npcList;
                            }

                            var nextArr = entry["nextTalks"] as JArray;
                            if (nextArr != null && nextArr.Count > 0)
                            {
                                var nextList = new List<long>();
                                foreach (var n in nextArr) nextList.Add(n.Value<long>());
                                node["next"] = nextList;
                            }

                            talkIndex[talkId.ToString()] = node;
                        }
                    }
                }
            }
            progress?.Report((90, $"Talk index: {talkIndex.Count:N0} entries"));

            // Parse Quest data
            var questInfo = new Dictionary<string, object>();
            if (File.Exists(questPath))
            {
                using (var stream = File.OpenRead(questPath))
                using (var sr = new StreamReader(stream))
                using (var jr = new JsonTextReader(sr))
                {
                    jr.Read(); // StartArray
                    while (jr.Read())
                    {
                        if (jr.TokenType == JsonToken.StartObject)
                        {
                            var entry = JObject.Load(jr);
                            long id = entry.Value<long>("id");
                            if (id == 0) continue;

                            var node = new Dictionary<string, object>();
                            long titleHash = entry.Value<long>("titleTextMapHash");
                            if (titleHash != 0) node["title"] = titleHash;
                            string type = entry.Value<string>("type");
                            if (!string.IsNullOrEmpty(type)) node["type"] = type;

                            questInfo[id.ToString()] = node;
                        }
                    }
                }
            }

            // Save compact files — encrypted if protection is available
            progress?.Report((93, "Saving preprocessed files..."));

            if (protectionHelper != null)
            {
                progress?.Report((93, "Encrypting and saving preprocessed files..."));
                protectionHelper.SaveProtectedJson(Path.Combine(outputDir, "DialogGraph.json"), dialogGraph);
                protectionHelper.SaveProtectedJson(Path.Combine(outputDir, "HashToDialogs.json"), hashToDialogs);
                protectionHelper.SaveProtectedJson(Path.Combine(outputDir, "NpcNames.json"), npcNames);
                protectionHelper.SaveProtectedJson(Path.Combine(outputDir, "TalkIndex.json"), talkIndex);
                protectionHelper.SaveProtectedJson(Path.Combine(outputDir, "QuestInfo.json"), questInfo);
            }
            else
            {
                SaveCompactJson(Path.Combine(outputDir, "DialogGraph.json"), dialogGraph);
                SaveCompactJson(Path.Combine(outputDir, "HashToDialogs.json"), hashToDialogs);
                SaveCompactJson(Path.Combine(outputDir, "NpcNames.json"), npcNames);
                SaveCompactJson(Path.Combine(outputDir, "TalkIndex.json"), talkIndex);
                SaveCompactJson(Path.Combine(outputDir, "QuestInfo.json"), questInfo);
            }

            progress?.Report((98, "Dialogue graph files saved"));
        }

        private static void SaveCompactJson(string path, object data)
        {
            using (var stream = File.Create(path))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                var serializer = new JsonSerializer();
                serializer.Formatting = Formatting.None;
                serializer.Serialize(writer, data);
            }
            var sizeMb = new FileInfo(path).Length / (1024.0 * 1024.0);
            Logger.Log.Info($"Saved {Path.GetFileName(path)}: {sizeMb:F1} MB");
        }
    }
}
