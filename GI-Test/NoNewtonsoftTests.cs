using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Newtonsoft.Json left the app in 2.3.1 and the tests on 2026-11-16 (owner: "you can replace newtonsoft for good").
    /// Everything reads and writes JSON with System.Text.Json; a package that brings Newtonsoft back, directly or through
    /// another package, fails here.
    /// </summary>
    [TestClass]
    public class NoNewtonsoftTests
    {
        private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".plan", "bin", "obj", "node_modules", "dist", "packages",
        };

        [TestMethod]
        public void NoProjectFileReferencesNewtonsoft()
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            var projectFiles = ProjectFiles(root).ToList();
            Assert.IsTrue(projectFiles.Any(f => f.EndsWith("GI-Subtitles.csproj", StringComparison.OrdinalIgnoreCase)), "the walk found no projects");
            var naming = projectFiles.Where(f => File.ReadAllText(f).Contains("Newtonsoft", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(root, f)).ToList();
            Assert.AreEqual(0, naming.Count, "Newtonsoft is named in " + string.Join(", ", naming));
        }

        /// <summary>The test run's dependency list holds the app's whole package closure too (it references the app).</summary>
        [TestMethod]
        public void NoPackageInTheBuildBringsNewtonsoft()
        {
            string deps = Path.Combine(AppContext.BaseDirectory, "GI-Test.deps.json");
            Assert.IsTrue(File.Exists(deps), "GI-Test.deps.json is next to the test binaries");
            string text = File.ReadAllText(deps);
            StringAssert.Contains(text, "\"Velopack/", "the app's packages are in the closure this test reads");
            Assert.IsFalse(text.Contains("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase),
                "a package brings Newtonsoft.Json back (dotnet nuget why GI-Test/GI-Test.csproj Newtonsoft.Json)");
        }

        private static IEnumerable<string> ProjectFiles(string dir)
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (Skipped.Contains(Path.GetFileName(sub))) continue;
                foreach (string file in ProjectFiles(sub)) yield return file;
            }
        }
    }
}
