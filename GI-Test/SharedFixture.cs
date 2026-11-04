using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Data the desktop shares with the site (a schedule, a planner contract). The private repository has the
    /// original; the public mirror ships only the desktop, so GI-Test/Fixtures keeps a copy. Where both exist
    /// they must be identical, so the copy cannot drift.
    /// </summary>
    internal static class SharedFixture
    {
        public static string Read(string repoRelativePath, string fixtureFileName)
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            string original = Path.Combine(root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string copy = Path.Combine(root, "GI-Test", "Fixtures", fixtureFileName);
            bool hasOriginal = File.Exists(original), hasCopy = File.Exists(copy);
            if (!hasOriginal && !hasCopy)
                throw new AssertInconclusiveException($"Neither {repoRelativePath} nor GI-Test/Fixtures/{fixtureFileName} is present.");
            string text = File.ReadAllText(hasOriginal ? original : copy);
            if (hasOriginal && hasCopy)
            {
                Assert.AreEqual(text, File.ReadAllText(copy),
                    $"GI-Test/Fixtures/{fixtureFileName} drifted from {repoRelativePath}: copy the file over it.");
            }
            return text;
        }
    }
}
