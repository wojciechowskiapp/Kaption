using System;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// ONNX Runtime's native library failed to load. The runtime caches that
    /// failure for the life of the process, so only a restart can retry it.
    /// </summary>
    internal sealed class OcrRuntimeUnavailableException : Exception
    {
        public OcrRuntimeUnavailableException(string errorCode, string diagnostics, Exception innerException)
            : base(ComposeMessage(errorCode, diagnostics), innerException)
        {
            ErrorCode = errorCode;
            Diagnostics = diagnostics;
        }

        /// <summary>Load error code, for example "0x8007045A".</summary>
        public string ErrorCode { get; }

        /// <summary><see cref="OcrRuntimeDiagnostics.Probe"/> output taken when the failure was classified.</summary>
        public string Diagnostics { get; }

        /// <summary>First line of <see cref="Exception.Message"/>. Kept stable per error
        /// code because Sentry titles and groups issues on that line.</summary>
        public string Summary => Summarise(ErrorCode);

        private static string Summarise(string errorCode) => string.IsNullOrEmpty(errorCode)
            ? "ONNX Runtime failed to load."
            : $"ONNX Runtime failed to load ({errorCode}).";

        private static string ComposeMessage(string errorCode, string diagnostics) =>
            string.IsNullOrWhiteSpace(diagnostics)
                ? Summarise(errorCode)
                : Summarise(errorCode) + Environment.NewLine + diagnostics;
    }
}
