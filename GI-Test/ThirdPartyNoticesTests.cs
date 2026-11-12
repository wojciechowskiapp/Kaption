using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// THIRD_PARTY_LICENSES.txt is written by hand and ships next to Kaption.exe. The runtime it names must be the one the
    /// Release build bundles (the csproj's RuntimeFrameworkVersion pin), or every installer states the wrong version: it
    /// said 10.0.14 through the 10.0.15 pin and the move to .NET 11.
    /// </summary>
    [TestClass]
    public class ThirdPartyNoticesTests
    {
        [TestMethod]
        public void NoticesNameTheRuntimeTheReleaseBuildBundles()
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("GI-Subtitles.sln not found above the test binaries");
            string csproj = File.ReadAllText(Path.Combine(root, "GI-Subtitles", "GI-Subtitles.csproj"));
            var pin = Regex.Match(csproj,
                @"<RuntimeFrameworkVersion\s+Condition=""'\$\(Configuration\)'\s*==\s*'Release'"">\s*([0-9.]+)\s*</RuntimeFrameworkVersion>");
            Assert.IsTrue(pin.Success, "GI-Subtitles.csproj has no Release RuntimeFrameworkVersion pin");

            string notices = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_LICENSES.txt"));
            var named = Regex.Matches(notices, @"\.NET (\d+\.\d+\.\d+) runtime").Select(m => m.Groups[1].Value).ToList();
            Assert.IsTrue(named.Count >= 3, $"expected the runtime in the index, its heading and its notice; found {named.Count}");
            CollectionAssert.AreEqual(Enumerable.Repeat(pin.Groups[1].Value, named.Count).ToList(), named,
                $"THIRD_PARTY_LICENSES.txt names .NET {string.Join(", ", named.Distinct())}; the Release build bundles {pin.Groups[1].Value}");
        }
    }
}
