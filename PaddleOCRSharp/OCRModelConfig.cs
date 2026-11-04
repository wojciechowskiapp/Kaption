using System.IO;

namespace PaddleOCRSharp
{
    /// <summary>
    /// OCR model configuration
    /// </summary>
    public class OCRModelConfig
    {
        /// <summary>
        /// Human-readable model family used for diagnostics.
        /// </summary>
        public string model_name { get; set; }

        /// <summary>
        /// Detection model path
        /// </summary>
        public string det_infer { get; set; }

        /// <summary>
        /// Classification model path
        /// </summary>
        public string cls_infer { get; set; }

        /// <summary>
        /// Recognition model path
        /// </summary>
        public string rec_infer { get; set; }

        /// <summary>
        /// Character dictionary path
        /// </summary>
        public string keys { get; set; }

        public OCRModelConfig()
        {
            var root = GetRootDirectory();
            var modelPathRoot = Path.Combine(root, "inference");
            // Keep this low-level default on the compatibility model's English
            // pair so direct library consumers remain bootable. Kaption resolves
            // its profiles in OcrModelProfiles. The dictionary is in inference.yml.
            det_infer = Path.Combine(modelPathRoot, "Det", "V4", "PP-OCRv4_mobile_det_infer", "slim.onnx");
            model_name = "PP-OCRv5 Mobile (English)";
            cls_infer = Path.Combine(modelPathRoot, "ch_ppocr_mobile_v2.0_cls_infer"); // Optional, not used
            rec_infer = Path.Combine(modelPathRoot, "Rec", "V5", "en_PP-OCRv5_mobile_rec_infer", "inference.onnx");
            keys = null;
        }

        /// <summary>
        /// Get root directory
        /// </summary>
        private static string GetRootDirectory()
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(exePath);
        }
    }
}
