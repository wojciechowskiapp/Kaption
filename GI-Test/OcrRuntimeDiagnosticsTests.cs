using System;
using System.IO;
using GI_Subtitles.Services.OCR;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Classification and evidence behind the engine-init "runtime unavailable"
    /// path (GlitchTip 8439591/8439592: onnxruntime.dll failed to initialise with
    /// 0x8007045A, and the cached type-initializer failure made every retry futile).
    /// </summary>
    [TestClass]
    public class OcrRuntimeDiagnosticsTests
    {
        private const string OnnxNativeMethods = "Microsoft.ML.OnnxRuntime.NativeMethods";

        // Verbatim from the field event (Polish Windows message text).
        private const string FieldDllInitFailure =
            "Unable to load DLL 'onnxruntime' or one of its dependencies: " +
            "Procedura inicjowania biblioteki dołączanej dynamicznie (DLL) nie powiodła się. (0x8007045A)";

        [TestMethod]
        public void IsRuntimeUnavailable_FieldDllInitFailure_ReturnsTrueWithWin32Code()
        {
            var error = new TypeInitializationException(
                OnnxNativeMethods, new DllNotFoundException(FieldDllInitFailure));

            Assert.IsTrue(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.AreEqual("0x8007045A", code);
        }

        [TestMethod]
        public void IsRuntimeUnavailable_FailureWrappedByCaller_WalksTheInnerChain()
        {
            var error = new Exception(
                "Failed to load both PP-OCRv6 Small and the compatibility OCR model.",
                new TypeInitializationException(
                    OnnxNativeMethods, new DllNotFoundException(FieldDllInitFailure)));

            Assert.IsTrue(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.AreEqual("0x8007045A", code);
        }

        [TestMethod]
        public void IsRuntimeUnavailable_MissingEntryPoint_FallsBackToTheManagedHResult()
        {
            // The shape Windows 10 produces when Kaption's own onnxruntime.dll is
            // gone: the loader binds System32's Windows ML stub, which has no
            // OrtGetApiBase, and the message carries no Win32 code.
            var error = new TypeInitializationException(
                OnnxNativeMethods,
                new EntryPointNotFoundException(
                    "Unable to find an entry point named 'OrtGetApiBase' in DLL 'onnxruntime'."));

            Assert.IsTrue(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.AreEqual("0x80131523", code, "COR_E_ENTRYPOINTNOTFOUND");
        }

        [TestMethod]
        public void IsRuntimeUnavailable_OnnxDllNotFoundWithoutTypeInitializer_ReturnsTrue()
        {
            var error = new DllNotFoundException(
                "Unable to load DLL 'onnxruntime' or one of its dependencies: " +
                "The specified module could not be found. (0x8007007e)");

            Assert.IsTrue(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.AreEqual("0x8007007E", code, "Codes are normalised to upper-case hex.");
        }

        [TestMethod]
        public void IsRuntimeUnavailable_OrdinaryException_ReturnsFalse()
        {
            Assert.IsFalse(OcrRuntimeDiagnostics.IsRuntimeUnavailable(
                new InvalidOperationException("Sequence contains no elements"), out string code));
            Assert.IsNull(code);
            Assert.IsFalse(OcrRuntimeDiagnostics.IsRuntimeUnavailable(null, out code));
            Assert.IsNull(code);
        }

        [TestMethod]
        public void IsRuntimeUnavailable_OtherNativeLibrary_ReturnsFalse()
        {
            const string openCvMissing =
                "Unable to load DLL 'OpenCvSharpExtern' or one of its dependencies: " +
                "The specified module could not be found. (0x8007007E)";

            Assert.IsFalse(OcrRuntimeDiagnostics.IsRuntimeUnavailable(
                new DllNotFoundException(openCvMissing), out string code));
            Assert.IsNull(code);
            Assert.IsFalse(OcrRuntimeDiagnostics.IsRuntimeUnavailable(
                new TypeInitializationException(
                    "OpenCvSharp.Internal.NativeMethods", new DllNotFoundException(openCvMissing)),
                out code));
            Assert.IsNull(code);
        }

        [TestMethod]
        public void IsRuntimeUnavailable_BadModelFile_ReturnsFalse()
        {
            // A real OnnxRuntimeException: the runtime loaded and rejected the model,
            // which a retry with another model can still fix.
            Exception error = null;
            try
            {
                using var session = new InferenceSession(new byte[] { 0x00, 0x01, 0x02, 0x03 });
            }
            catch (Exception ex)
            {
                error = ex;
            }

            Assert.IsNotNull(error, "Four garbage bytes must not load as a model.");
            if (error is TypeInitializationException)
                Assert.Inconclusive("ONNX Runtime's native library does not load in this test host.");
            Assert.IsInstanceOfType(error, typeof(OnnxRuntimeException));
            Assert.IsFalse(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.IsNull(code);
        }

        [TestMethod]
        public void Probe_OnThisMachine_DescribesTheRuntimeWithoutThrowing()
        {
            string report = OcrRuntimeDiagnostics.Probe();

            Assert.IsFalse(string.IsNullOrWhiteSpace(report));
            foreach (string label in new[]
            {
                "app dir:", "onnxruntime.dll:", "onnxruntime_providers_shared.dll:", "DirectML.dll:",
                "msvcp140.dll:", "vcruntime140.dll:", "vcruntime140_1.dll:", "OS:", "GPU 0:",
            })
            {
                StringAssert.Contains(report, label);
            }

            var onnx = new FileInfo(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"));
            if (onnx.Exists)
                StringAssert.Contains(report, $"onnxruntime.dll: {onnx.Length} bytes");
        }

        [TestMethod]
        [DataRow(@"C:\Users\Bob\AppData\Local\Kaption\current\", @"%LOCALAPPDATA%\Kaption\current\")]
        [DataRow(@"c:\users\bob\appdata\local\Kaption\current", @"%LOCALAPPDATA%\Kaption\current")]
        [DataRow(@"C:\Users\Bob\Downloads\Kaption\", @"%USERPROFILE%\Downloads\Kaption\")]
        [DataRow(@"C:\Users\Bobby\Kaption\", @"C:\Users\Bobby\Kaption\")]
        [DataRow(@"D:\Games\Kaption\", @"D:\Games\Kaption\")]
        [DataRow(@"C:\WINDOWS\SYSTEM32\MSVCP140.dll", @"C:\WINDOWS\SYSTEM32\MSVCP140.dll")]
        [DataRow(@"load failed (DllNotFoundException: Unable to load DLL 'C:\Users\Bob\AppData\Local\Kaption\current\DirectML.dll' or one of its dependencies)",
                 @"load failed (DllNotFoundException: Unable to load DLL '%LOCALAPPDATA%\Kaption\current\DirectML.dll' or one of its dependencies)")]
        public void SanitizePath_ReplacesOnlyTheUserProfileWithAToken(string path, string expected)
        {
            Assert.AreEqual(expected, OcrRuntimeDiagnostics.SanitizePath(
                path, localAppData: @"C:\Users\Bob\AppData\Local", userProfile: @"C:\Users\Bob"));
        }

        [TestMethod]
        public void RuntimeUnavailableException_FirstLineIsStableAndDiagnosticsFollow()
        {
            // GlitchTip titles an issue "{type}: {first line of value}" and groups on
            // it, so the machine-specific probe must stay below a stable first line.
            var inner = new TypeInitializationException(
                OnnxNativeMethods, new DllNotFoundException(FieldDllInitFailure));
            var error = new OcrRuntimeUnavailableException(
                "0x8007045A", "  onnxruntime.dll: 17328152 bytes" + Environment.NewLine + "  OS: test", inner);

            Assert.AreEqual("ONNX Runtime failed to load (0x8007045A).", error.Summary);
            Assert.AreEqual(error.Summary, error.Message.Split('\r', '\n')[0]);
            StringAssert.Contains(error.Message, "  OS: test");
            Assert.AreEqual("0x8007045A", error.ErrorCode);
            Assert.AreSame(inner, error.InnerException);
            Assert.IsTrue(OcrRuntimeDiagnostics.IsRuntimeUnavailable(error, out string code));
            Assert.AreEqual("0x8007045A", code);
        }
    }
}
