using System;
using System.IO;
using PaddleOCRSharp;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Stable, user-facing OCR model profiles. The persisted value is the
    /// profile id, never a model path, so release layouts can change without
    /// invalidating Config.json.
    /// </summary>
    internal sealed class OcrModelProfile
    {
        public string Id { get; init; }
        public string ModelName { get; init; }
        public string DetectionModelRelativePath { get; init; }
        public string RecognitionModelRelativePath { get; init; }
        public string RecognitionDictionaryRelativePath { get; init; }
        public float DetectionThreshold { get; init; }
        public float DetectionBoxThreshold { get; init; }
        public float DetectionUnclipRatio { get; init; }
        public int RecognitionImageHeight { get; init; } = 48;
        public bool FallsBackToCompatibilityProfile { get; init; }

        public OCRModelConfig CreateModelConfig(string inferenceRoot)
        {
            if (string.IsNullOrWhiteSpace(inferenceRoot))
                throw new ArgumentException("Inference root is required.", nameof(inferenceRoot));

            return new OCRModelConfig
            {
                model_name = ModelName,
                det_infer = Path.Combine(inferenceRoot, DetectionModelRelativePath),
                rec_infer = Path.Combine(inferenceRoot, RecognitionModelRelativePath),
                keys = string.IsNullOrWhiteSpace(RecognitionDictionaryRelativePath)
                    ? null
                    : Path.Combine(inferenceRoot, RecognitionDictionaryRelativePath),
            };
        }
    }

    /// <summary>
    /// Resolves the configured OCR profile and its language-specific assets.
    /// PP-OCRv6 Small uses one unified 50-language recognizer for EN and JP;
    /// the compatibility profile retains the separate PP-OCRv4 recognizers.
    /// </summary>
    internal static class OcrModelProfiles
    {
        public const string RecommendedId = "ppocr-v6-small";
        public const string CompatibilityId = "ppocr-v4-mobile";

        public static string NormalizeId(string profileId) =>
            string.Equals(profileId, CompatibilityId, StringComparison.OrdinalIgnoreCase)
                ? CompatibilityId
                : RecommendedId;

        public static OcrModelProfile Resolve(string profileId, string inputLanguage)
        {
            string normalizedId = NormalizeId(profileId);
            if (normalizedId == RecommendedId)
            {
                return new OcrModelProfile
                {
                    Id = RecommendedId,
                    ModelName = "PP-OCRv6 Small",
                    DetectionModelRelativePath = Path.Combine(
                        "Det", "V6", "PP-OCRv6_small_det_infer", "inference.onnx"),
                    RecognitionModelRelativePath = Path.Combine(
                        "Rec", "V6", "PP-OCRv6_small_rec_infer", "inference.onnx"),
                    // The official V6 ONNX package stores its 18,708-character
                    // dictionary directly in inference.yml next to the model.
                    RecognitionDictionaryRelativePath = null,
                    // Model-specific values from PP-OCRv6_small_det's official
                    // inference.yml. Recognition confidence remains a separate
                    // product policy (0.50) in SettingsWindow.LoadEngine.
                    DetectionThreshold = 0.20f,
                    DetectionBoxThreshold = 0.45f,
                    DetectionUnclipRatio = 1.40f,
                    RecognitionImageHeight = 48,
                    FallsBackToCompatibilityProfile = true,
                };
            }

            bool japanese = string.Equals(inputLanguage, "JP", StringComparison.OrdinalIgnoreCase);
            string recognitionFolder = japanese
                ? "jp_PP-OCRv4_mobile_rec_infer"
                : "PP-OCRv4_mobile_rec_infer";

            return new OcrModelProfile
            {
                Id = CompatibilityId,
                ModelName = japanese ? "PP-OCRv4 Mobile (Japanese)" : "PP-OCRv4 Mobile (English)",
                DetectionModelRelativePath = Path.Combine(
                    "Det", "V4", "PP-OCRv4_mobile_det_infer", "slim.onnx"),
                RecognitionModelRelativePath = Path.Combine(
                    "Rec", "V4", recognitionFolder, "slim.onnx"),
                RecognitionDictionaryRelativePath = Path.Combine(
                    "Rec", "V4", recognitionFolder, "dict.txt"),
                DetectionThreshold = 0.30f,
                DetectionBoxThreshold = 0.60f,
                DetectionUnclipRatio = 2.00f,
                RecognitionImageHeight = 48,
                FallsBackToCompatibilityProfile = false,
            };
        }
    }
}
