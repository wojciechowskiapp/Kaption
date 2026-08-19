// ─────────────────────────────────────────────────────────────────────────────
//  LogRedactorTests.cs
//  ---------------------------------------------------------------------------
//  Every diagnostic bundle a user sends us passes through LogRedactor, so these
//  tests are the thing standing between a support request and a leaked session
//  token. They cover the four categories it strips (tokens, secret-shaped
//  config values, emails, Windows usernames) and — just as importantly — assert
//  that the diagnostic payload itself survives, since a redactor that eats the
//  logs is a redactor nobody will keep enabled.
// ─────────────────────────────────────────────────────────────────────────────

using GI_Subtitles.Services.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class LogRedactorTests
    {
        // ── tokens ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Scrub_JwtInProse_IsReplaced()
        {
            const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            string result = LogRedactor.Scrub($"refreshing session with {jwt} now");

            StringAssert.Contains(result, LogRedactor.RedactedToken);
            Assert.IsFalse(result.Contains("eyJhbGci"), "The token body survived redaction.");
        }

        [TestMethod]
        public void Scrub_JwtWithShortSignature_IsStillReplaced()
        {
            // Regression: the first version of the pattern demanded 6+ characters
            // in every segment, so an unsigned or truncated token walked straight
            // through. A credential-shaped string is a credential.
            string result = LogRedactor.Scrub("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhIn0.c2ln here");

            Assert.IsFalse(result.Contains("eyJhbGci"), $"Short-signature token survived: {result}");
        }

        [TestMethod]
        public void Scrub_JwtInJsonValue_IsReplaced()
        {
            string result = LogRedactor.Scrub(
                "\"device_session_jwt\": \"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhYmMifQ.c2lnbmF0dXJlLWhlcmU\"");

            Assert.IsFalse(result.Contains("eyJhbGci"));
        }

        // ── secret-shaped config values ────────────────────────────────────

        [DataTestMethod]
        [DataRow("\"SentryDsn\": \"https://abc123@app.glitchtip.com/22273\"", "abc123")]
        [DataRow("\"ApiToken\": \"tok_live_9f8e7d6c\"", "tok_live_9f8e7d6c")]
        [DataRow("\"password\": \"hunter2\"", "hunter2")]
        [DataRow("api_key=sk-abcdef123456", "sk-abcdef123456")]
        [DataRow("\"machine_fingerprint\": \"a1b2c3d4e5f6\"", "a1b2c3d4e5f6")]
        public void Scrub_SecretShapedAssignments_LoseTheirValue(string input, string secret)
        {
            string result = LogRedactor.Scrub(input);

            Assert.IsFalse(result.Contains(secret), $"Secret survived redaction: {result}");
            StringAssert.Contains(result, LogRedactor.RedactedValue);
        }

        [TestMethod]
        public void Scrub_SecretRedaction_KeepsTheKeyVisible()
        {
            // The key name is diagnostic — knowing a DSN was configured at all
            // is often the answer. Only the value goes.
            string result = LogRedactor.Scrub("\"SentryDsn\": \"https://abc@example.com/1\"");

            StringAssert.Contains(result, "SentryDsn");
        }

        // ── emails ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Scrub_Email_KeepsDomainMasksLocalPart()
        {
            string result = LogRedactor.Scrub("signed in as michal.wojciechowski00@gmail.com");

            Assert.IsFalse(result.Contains("michal.wojciechowski00"));
            // The domain identifies the OAuth provider, which is worth keeping.
            StringAssert.Contains(result, "@gmail.com");
            StringAssert.Contains(result, "m***@gmail.com");
        }

        // ── Windows usernames ──────────────────────────────────────────────

        [DataTestMethod]
        [DataRow(@"C:\Users\Crisey\AppData\Roaming\Kaption\app.log", "Crisey")]
        [DataRow(@"D:\Users\Jan Kowalski\Desktop\thing.txt", "Kowalski")]
        [DataRow(@"\\fileserver\Users\mwojciechowski\share", "mwojciechowski")]
        public void Scrub_UserPaths_LoseTheUsername(string input, string username)
        {
            string result = LogRedactor.Scrub(input);

            Assert.IsFalse(result.Contains(username), $"Username survived: {result}");
            StringAssert.Contains(result, "<user>");
        }

        [TestMethod]
        public void Scrub_UserPath_KeepsTheRestOfThePath()
        {
            // Redacting the whole path would throw away which file we were
            // touching, which is usually the point of the log line.
            string result = LogRedactor.Scrub(@"C:\Users\Crisey\AppData\Roaming\Kaption\app.log");

            StringAssert.Contains(result, @"AppData\Roaming\Kaption\app.log");
        }

        // ── the payload must survive ───────────────────────────────────────

        [TestMethod]
        public void Scrub_OrdinaryLogLine_IsUntouched()
        {
            const string line = "[INFO ] OCR engine loaded and ready (DirectML, 2.4s)";

            Assert.AreEqual(line, LogRedactor.Scrub(line));
        }

        [TestMethod]
        public void Scrub_GameDialogue_IsUntouched()
        {
            const string line = "HOT CACHE HIT for \"Traveler, over here!\": \"Podróżniku, tutaj!\"";

            Assert.AreEqual(line, LogRedactor.Scrub(line));
        }

        [TestMethod]
        public void Scrub_VersionsAndPathsWithoutUsernames_AreUntouched()
        {
            const string line = @"Matcher corpus: 488102 entries from C:\Program Files\Kaption\data.gisub (v2.0.26040116)";

            Assert.AreEqual(line, LogRedactor.Scrub(line));
        }

        // ── null-safety ────────────────────────────────────────────────────

        [TestMethod]
        public void Scrub_NullOrEmpty_PassesThrough()
        {
            Assert.IsNull(LogRedactor.Scrub(null));
            Assert.AreEqual("", LogRedactor.Scrub(""));
        }

        // ── ShortId ────────────────────────────────────────────────────────

        [TestMethod]
        public void ShortId_LongValue_IsTruncated()
        {
            string result = LogRedactor.ShortId("a1b2c3d4e5f6a7b8c9d0", 8);

            StringAssert.StartsWith(result, "a1b2c3d4");
            Assert.IsFalse(result.Contains("e5f6a7b8"), "ShortId leaked the tail of the identifier.");
        }

        [TestMethod]
        public void ShortId_MissingValue_IsLabelled()
        {
            Assert.AreEqual("(none)", LogRedactor.ShortId(null));
            Assert.AreEqual("(none)", LogRedactor.ShortId("   "));
        }
    }
}
