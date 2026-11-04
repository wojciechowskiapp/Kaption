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
    /// PP-OCRv6 Small uses one unified 50-language recognizer for EN and JP.
    /// The compatibility profile keeps the PP-OCRv4 detector; English reads
    /// with en_PP-OCRv5 (session 58: about half the wrong-line frames of v4 on
    /// CPU, no slower), Japanese keeps the PP-OCRv4 Japanese recognizer.
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

            return new OcrModelProfile
            {
                // The id stays "ppocr-v4-mobile": it is the persisted Config value.
                // ModelName must not contain "v6" (MainWindow's CPU recovery keys off it).
                Id = CompatibilityId,
                ModelName = japanese ? "PP-OCRv4 Mobile (Japanese)" : "PP-OCRv5 Mobile (English)",
                DetectionModelRelativePath = Path.Combine(
                    "Det", "V4", "PP-OCRv4_mobile_det_infer", "slim.onnx"),
                RecognitionModelRelativePath = japanese
                    ? Path.Combine("Rec", "V4", "jp_PP-OCRv4_mobile_rec_infer", "slim.onnx")
                    : Path.Combine("Rec", "V5", "en_PP-OCRv5_mobile_rec_infer", "inference.onnx"),
                // en_PP-OCRv5's official inference.yml carries its 436-character dictionary.
                RecognitionDictionaryRelativePath = japanese
                    ? Path.Combine("Rec", "V4", "jp_PP-OCRv4_mobile_rec_infer", "dict.txt")
                    : null,
                DetectionThreshold = 0.30f,
                DetectionBoxThreshold = 0.60f,
                DetectionUnclipRatio = 2.00f,
                RecognitionImageHeight = 48,
                FallsBackToCompatibilityProfile = false,
            };
        }
    }
}
