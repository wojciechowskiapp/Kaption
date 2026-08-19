using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using OpenCvSharp.Extensions;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using log4net;

namespace PaddleOCRSharp
{
    /// <summary>
    /// Paddle OCR Engine
    /// </summary>
    public class PaddleOCREngine : IDisposable
    {
        private readonly InferenceSession _detSession;
        private readonly InferenceSession _recSession;
        private readonly List<string> _labels;
        private readonly OCRParameter _parameter;

        // Serialises every native Run() on the shared InferenceSessions.
        // Prevents three documented crash paths at RunImpl:
        //   1. engine.Dispose() racing an in-flight Run() during app shutdown
        //      or SettingsWindow.LoadEngine() (GPU toggle / language switch).
        //   2. Concurrent Run() from TriggerOcrAsync + RunAnswerOnlyOcrAsync +
        //      ForceOcrTranslate on the same DirectML session (ORT 1.23.x has
        //      known DML resource-pool races under concurrent Run).
        //   3. Dispose during re-entrant recognition (N crops → N Run() calls
        //      per frame) where any intermediate step could see a freed handle.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;

        /// <summary>
        /// Whether the engine is currently using GPU (DirectML) acceleration.
        /// False if GPU was not requested, unavailable, or failed to initialize.
        /// </summary>
        public bool IsUsingGpu { get; private set; }

        /// <summary>
        /// Name of the active ONNX Runtime execution provider (e.g. "DmlExecutionProvider" or "CPUExecutionProvider").
        /// </summary>
        public string ExecutionProvider { get; private set; } = "CPUExecutionProvider";

        /// <summary>
        /// Human-readable active model family, included in diagnostics and UI.
        /// </summary>
        public string ModelName { get; private set; } = "PaddleOCR";

        // Detection model parameters
        private const int DetMinSize = 3;

        // Read the public OCRParameter values instead of silently shadowing them with
        // unrelated constants. The old hard-coded box score (0.7) was notably stricter
        // than both our declared default and PaddleOCR's current pipeline default (0.6).
        private int DetMaxSize => Math.Max(32, _parameter.max_side_len);
        private float DetBoxScoreThreshold => Clamp(_parameter.det_db_box_thresh, 0f, 1f);
        private float DetBoxThreshold => Clamp(_parameter.det_db_thresh, 0f, 1f);
        private float DetUnclipRatio => Math.Max(0.1f, _parameter.det_db_unclip_ratio);
        private int RecImgHeight => Math.Max(8, _parameter.rec_img_h);
        private float RecScoreThreshold => Clamp(_parameter.rec_score_thresh, 0f, 1f);
        private const int RecognitionWidthStep = 320;
        private const int RecognitionMaxWidth = 3200;

        /// <summary>
        /// Clamp helper method - .NET Framework 4.8 does not contain Math.Clamp
        /// </summary>
        private static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        {
            if (value.CompareTo(min) < 0) return min;
            if (value.CompareTo(max) > 0) return max;
            return value;
        }

        /// <summary>
        /// Quantizes dynamic recognition widths to a small set of stable tensor
        /// shapes. DirectML compiles kernels per input shape; using every exact
        /// subtitle width caused cold compiles to repeat for each detected line.
        /// PaddleOCR's official V6 preprocessing uses 320 as its base width and
        /// supports dynamic widths up to 3200, so 320-wide buckets preserve the
        /// source aspect ratio while making the compiled shapes reusable.
        /// </summary>
        internal static int GetRecognitionInputWidth(
            int sourceWidth, int sourceHeight, int recognitionHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return RecognitionWidthStep;

            int height = Math.Max(8, recognitionHeight);
            double scaled = height * (sourceWidth / (double)sourceHeight);
            int contentWidth = Math.Max(16, (int)Math.Ceiling(scaled));
            int bucket = ((contentWidth + RecognitionWidthStep - 1) /
                          RecognitionWidthStep) * RecognitionWidthStep;
            return Clamp(bucket, RecognitionWidthStep, RecognitionMaxWidth);
        }

        /// <summary>
        /// Safely clone Mat object, avoid AccessViolationException
        /// Use CopyTo as the main method, if it fails, try Clone
        /// </summary>
        private static Mat SafeClone(Mat src)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            // Check if Mat is disposed
            if (src.IsDisposed)
                throw new ObjectDisposedException(nameof(src), "Mat object has been disposed");

            try
            {
                // Check if Mat is empty
                if (src.Empty())
                {
                    // If empty, return an empty Mat instead of throwing an exception
                    var size = src.Size();
                    var type = src.Type();
                    return new Mat(size, type);
                }

                // Use CopyTo method, it is usually safer than Clone
                // CopyTo will create a new Mat and copy data, without depending on the underlying pointer of the original Mat
                var result = new Mat();
                src.CopyTo(result);
                return result;
            }
            catch (AccessViolationException)
            {
                // If CopyTo fails, try using Clone as a backup solution
                try
                {
                    return src.Clone();
                }
                catch (AccessViolationException ex)
                {
                    // If both methods fail, provide detailed error information
                    string sizeInfo = "Unknown";
                    string typeInfo = "Unknown";
                    try
                    {
                        if (!src.IsDisposed)
                        {
                            sizeInfo = src.Size().ToString();
                            typeInfo = src.Type().ToString();
                        }
                    }
                    catch
                    {
                        Logger.Log.Error($"Failed to clone Mat object: Mat may be corrupted or memory has been released. Size={sizeInfo}, Type={typeInfo}, IsDisposed={src.IsDisposed}");
                    }

                    throw new InvalidOperationException(
                        $"Failed to clone Mat object: Mat may be corrupted or memory has been released. Size={sizeInfo}, Type={typeInfo}, IsDisposed={src.IsDisposed}", ex);
                }
            }
            catch (Exception ex)
            {
                // Skip AccessViolationException, it has already been handled above
                if (ex is AccessViolationException)
                    throw;

                // Handle other types of exceptions (e.g. OutOfMemoryException, etc.)
                string sizeInfo = "Unknown";
                string typeInfo = "Unknown";
                try
                {
                    if (!src.IsDisposed)
                    {
                        sizeInfo = src.Size().ToString();
                        typeInfo = src.Type().ToString();
                    }
                }
                catch
                {
                    Logger.Log.Error(
                        $"Failed to clone Mat object: Mat may be corrupted or memory has been released. Size={sizeInfo}, Type={typeInfo}, IsDisposed={src.IsDisposed} ex = {ex.Message}");
                }

                throw new InvalidOperationException(
                    $"Failed to clone Mat object: {ex.GetType().Name} - {ex.Message}. Size={sizeInfo}, Type={typeInfo}, IsDisposed={src.IsDisposed}", ex);
            }
        }

        /// <summary>
        /// Load character dictionary from YAML file
        /// </summary>
        internal static List<string> LoadLabelsFromYaml(string yamlPath)
        {
            var labels = new List<string>();
            var lines = File.ReadAllLines(yamlPath, System.Text.Encoding.UTF8);
            bool inCharacterDict = false;
            var regex = new System.Text.RegularExpressions.Regex(@"^\s*-\s*(.+)");

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("character_dict:"))
                {
                    inCharacterDict = true;
                    continue;
                }
                else if (inCharacterDict)
                {
                    // Use regular expression to match list items
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        var label = ParseYamlListScalar(match.Groups[1].Value);
                        labels.Add(label);
                    }
                    else if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        // If not a list item, end character dictionary
                        break;
                    }
                }
            }

            if (labels.Count == 0)
            {
                throw new InvalidOperationException($"Failed to read character dictionary from YAML file: {yamlPath}");
            }

            return labels;
        }

        /// <summary>
        /// Parses the small YAML scalar subset used by PaddleOCR's
        /// character_dict. PP-OCRv6 quotes punctuation such as '#', quotes
        /// and backslashes; retaining those quote marks would shift/corrupt
        /// every decoded character.
        /// </summary>
        internal static string ParseYamlListScalar(string rawValue)
        {
            string value = (rawValue ?? string.Empty).TrimStart();
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t");
            }

            return value.TrimEnd();
        }

        /// <summary>
        /// PaddleOCR Engine initialization
        /// </summary>
        /// <param name="config">Model configuration object</param>
        /// <param name="parameter">Recognition parameters</param>
        public PaddleOCREngine(OCRModelConfig config, OCRParameter parameter = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (parameter == null)
                parameter = new OCRParameter();
            _parameter = parameter;
            ModelName = string.IsNullOrWhiteSpace(config.model_name)
                ? "PaddleOCR"
                : config.model_name;

            // Check if model files exist
            if (!File.Exists(config.det_infer))
                throw new FileNotFoundException($"Detection model file not found: {config.det_infer}");
            if (!File.Exists(config.rec_infer))
                throw new FileNotFoundException($"Recognition model file not found: {config.rec_infer}");

            // Load character dictionary - first from inference.yml, if not, from keys file
            var inferenceYmlPath = Path.Combine(Path.GetDirectoryName(config.rec_infer), "inference.yml");
            if (File.Exists(inferenceYmlPath))
            {
                _labels = LoadLabelsFromYaml(inferenceYmlPath);
            }
            else if (!string.IsNullOrEmpty(config.keys) && File.Exists(config.keys))
            {
                _labels = File.ReadAllLines(config.keys).ToList();
            }
            else
            {
                throw new FileNotFoundException($"Character dictionary file not found: {inferenceYmlPath} or {config.keys}");
            }

            // Create ONNX Runtime sessions with optional DirectML GPU acceleration
            if (parameter.use_gpu)
            {
                try
                {
                    using var gpuOptions = CreateSessionOptions(parameter, useDirectML: true);
                    _detSession = new InferenceSession(config.det_infer, gpuOptions);
                    _recSession = new InferenceSession(config.rec_infer, gpuOptions);
                    IsUsingGpu = true;
                    ExecutionProvider = "DmlExecutionProvider";
                    Logger.Log.Info($"OCR engine initialized with DirectML GPU acceleration (device {parameter.gpu_id})");
                }
                catch (Exception ex)
                {
                    // DirectML failed — dispose any partially created sessions and fall back to CPU
                    IsUsingGpu = false;
                    ExecutionProvider = "CPUExecutionProvider";
                    Logger.Log.Warn($"DirectML GPU initialization failed, falling back to CPU: {ex.Message}");

                    _detSession?.Dispose();
                    _recSession?.Dispose();
                    _detSession = null;
                    _recSession = null;

                    using var cpuOptions = CreateSessionOptions(parameter, useDirectML: false);
                    _detSession = new InferenceSession(config.det_infer, cpuOptions);
                    _recSession = new InferenceSession(config.rec_infer, cpuOptions);
                    Logger.Log.Info("OCR engine initialized with CPU execution provider (GPU fallback)");
                }
            }
            else
            {
                using var cpuOptions = CreateSessionOptions(parameter, useDirectML: false);
                _detSession = new InferenceSession(config.det_infer, cpuOptions);
                _recSession = new InferenceSession(config.rec_infer, cpuOptions);
                IsUsingGpu = false;
                ExecutionProvider = "CPUExecutionProvider";
                Logger.Log.Info("OCR engine initialized with CPU execution provider (GPU disabled in settings)");
            }
        }

        /// <summary>
        /// Creates ONNX Runtime SessionOptions with the appropriate execution provider.
        /// When useDirectML is true, appends the DML provider first (GPU), then CPU as fallback.
        /// The DML append itself may throw if the DirectML native library is not available.
        /// </summary>
        /// <param name="parameter">OCR parameters containing thread counts and GPU device ID</param>
        /// <param name="useDirectML">Whether to attempt adding the DirectML execution provider</param>
        /// <returns>Configured SessionOptions instance</returns>
        private static SessionOptions CreateSessionOptions(OCRParameter parameter, bool useDirectML)
        {
            var options = new SessionOptions
            {
                IntraOpNumThreads = parameter.cpu_math_library_num_threads > 0
                    ? parameter.cpu_math_library_num_threads
                    : 2,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // DirectML explicitly forbids memory-pattern optimisation and
                // parallel graph execution. Keeping these options explicit
                // prevents a capable GPU from failing session creation and
                // silently dropping to CPU when ORT defaults change.
                EnableMemoryPattern = !useDirectML,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };

            if (useDirectML)
            {
                // DirectML provider must be appended before CPU.
                // ONNX Runtime evaluates providers in order and uses the first one that supports the graph.
                // This call will throw if the native onnxruntime.dll was not built with DirectML support
                // or if DirectML.dll is not available on the system.
                options.AppendExecutionProvider_DML(parameter.gpu_id);
                Logger.Log.Debug($"Appended DirectML execution provider (device {parameter.gpu_id})");
            }

            // CPU always present as fallback (or as primary when GPU is not used)
            options.AppendExecutionProvider_CPU();

            return options;
        }

        /// <summary>
        /// Text recognition for image file
        /// </summary>
        /// <param name="imagefile">Image file</param>
        /// <returns>OCR recognition result</returns>
        public OCRResult DetectText(string imagefile)
        {
            if (!File.Exists(imagefile))
                throw new FileNotFoundException($"File not found: {imagefile}");

            using var image = new Bitmap(imagefile);
            return DetectText(image);
        }

        /// <summary>
        /// Text recognition for image object
        /// </summary>
        /// <param name="image">Image</param>
        /// <returns>OCR recognition result</returns>
        public OCRResult DetectText(Image image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            var bitmap = image as Bitmap;
            if (bitmap == null)
                throw new ArgumentException("Image must be a Bitmap", nameof(image));

            using var mat = bitmap.ToMat();
            return DetectTextFromMat(mat);
        }

        /// <summary>
        /// Text recognition for image byte array
        /// </summary>
        /// <param name="imagebyte">Image byte array</param>
        /// <returns>OCR recognition result</returns>
        public OCRResult DetectText(byte[] imagebyte)
        {
            if (imagebyte == null)
                throw new ArgumentNullException(nameof(imagebyte));

            using var ms = new MemoryStream(imagebyte);
            using var image = new Bitmap(ms);
            return DetectText(image);
        }

        /// <summary>
        /// Text recognition for image base64 string
        /// </summary>
        /// <param name="imagebase64">Image base64</param>
        /// <returns>OCR recognition result</returns>
        public OCRResult DetectTextBase64(string imagebase64)
        {
            if (string.IsNullOrEmpty(imagebase64))
                throw new ArgumentNullException(nameof(imagebase64));

            var imageBytes = Convert.FromBase64String(imagebase64);
            return DetectText(imageBytes);
        }

        /// <summary>
        /// 从Mat进行OCR识别
        /// </summary>
        public OCRResult DetectTextFromMat(Mat src, CancellationToken cancellationToken = default)
        {
            if (src == null || src.IsDisposed || src.Empty())
                throw new ArgumentException("Invalid Mat object", nameof(src));

            // Serialise all native Run() calls against this engine — both
            // detection and recognition — and guarantee Dispose cannot free
            // the native session handle while a Run is on the stack.
            if (_disposed)
                throw new ObjectDisposedException(nameof(PaddleOCREngine));

            _gate.Wait(cancellationToken);
            try
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PaddleOCREngine));

                using var runOptions = new RunOptions();
                using var cancellationRegistration = cancellationToken.Register(() =>
                {
                    try { runOptions.Terminate = true; }
                    catch (ObjectDisposedException) { /* inference already completed */ }
                });

                string inferenceStage = "detection";
                var totalTimer = Stopwatch.StartNew();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Text detection
                    var stageTimer = Stopwatch.StartNew();
                    var rects = DetectTextRegions(src, runOptions, cancellationToken);
                    long detectionMs = stageTimer.ElapsedMilliseconds;

                    // Text recognition
                    var textBlocks = new List<TextBlock>();
                    long recognitionMs = 0;
                    if (rects.Length > 0)
                    {
                        var croppedMats = new List<Mat>();
                        var validRectIndices = new List<int>(); // Record indices of valid rectangles
                        try
                        {
                            var srcSize = src.Size();
                            for (int i = 0; i < rects.Length; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var rect = rects[i];
                                var croppedRect = GetCroppedRect(rect.BoundingRect(), srcSize);

                                // Additional safety check: ensure rectangle is within Mat boundaries
                                if (croppedRect.X < 0 || croppedRect.Y < 0 ||
                                    croppedRect.X + croppedRect.Width > srcSize.Width ||
                                    croppedRect.Y + croppedRect.Height > srcSize.Height ||
                                    croppedRect.Width <= 0 || croppedRect.Height <= 0)
                                {
                                    // If rectangle is invalid, skip this region
                                    continue;
                                }

                                var roi = src[croppedRect];
                                croppedMats.Add(roi);
                                validRectIndices.Add(i); // Record original index of valid rectangles
                            }

                            inferenceStage = "recognition";
                            stageTimer.Restart();
                            var results = RecognizeText(croppedMats.ToArray(), runOptions, cancellationToken);
                            recognitionMs = stageTimer.ElapsedMilliseconds;
                            for (int i = 0; i < results.Count && i < validRectIndices.Count; i++)
                            {
                                if (string.IsNullOrWhiteSpace(results[i].Text) ||
                                    results[i].Score < RecScoreThreshold)
                                {
                                    continue;
                                }

                                var originalIndex = validRectIndices[i];
                                var textBlock = new TextBlock
                                {
                                    Text = results[i].Text,
                                    Score = results[i].Score,
                                    BoxPoints = GetBoxPoints(rects[originalIndex])
                                };
                                textBlocks.Add(textBlock);
                            }
                        }
                        finally
                        {
                            foreach (var mat in croppedMats)
                                mat.Dispose();
                        }
                    }

                    totalTimer.Stop();
                    string timing =
                        $"OCR inference: provider={ExecutionProvider}, source={src.Width}x{src.Height}, " +
                        $"detection={detectionMs}ms, regions={rects.Length}, " +
                        $"recognition={recognitionMs}ms, accepted={textBlocks.Count}, " +
                        $"total={totalTimer.ElapsedMilliseconds}ms";
                    if (totalTimer.ElapsedMilliseconds >= 2000)
                        Logger.Log.Warn(timing);
                    else
                        Logger.Log.Debug(timing);

                    return new OCRResult
                    {
                        TextBlocks = textBlocks,
                        Text = string.Join("\n", textBlocks.Select(tb => tb.Text))
                    };
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.Log.Warn(
                        $"OCR inference cancelled during {inferenceStage} after " +
                        $"{totalTimer.ElapsedMilliseconds}ms (provider={ExecutionProvider}, " +
                        $"source={src.Width}x{src.Height}).");
                    throw new OperationCanceledException(
                        $"OCR inference was cancelled during {inferenceStage}.",
                        ex,
                        cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Text detection
        /// </summary>
        private RotatedRect[] DetectTextRegions(
            Mat src, RunOptions runOptions, CancellationToken cancellationToken)
        {
            if (src == null || src.IsDisposed || src.Empty())
                throw new ArgumentException("Invalid Mat object", nameof(src));

            using var padded = src.Channels() switch
            {
                4 => src.CvtColor(ColorConversionCodes.BGRA2BGR),
                1 => src.CvtColor(ColorConversionCodes.GRAY2BGR),
                _ => SafeClone(src)
            };

            // Resize
            using var resized = ResizeImage(padded, DetMaxSize);
            var resizedSize = new CvSize(resized.Width, resized.Height);
            using var padded32 = PadTo32(resized);

            // Normalize
            var inputTensor = NormalizeImage(padded32);

            // Run detection model
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_detSession.InputNames[0], inputTensor)
            };

            cancellationToken.ThrowIfCancellationRequested();
            using var outputs = _detSession.Run(inputs, _detSession.OutputNames, runOptions);
            var output = outputs.First().AsTensor<float>();

            // Convert to Mat
            using var pred = TensorToMat(output);

            // Post-processing
            using var cbuf = new Mat();
            using var roi = pred[new Rect(0, 0, resizedSize.Width, resizedSize.Height)];
            roi.ConvertTo(cbuf, MatType.CV_8UC1, 255);

            using var binary = cbuf.Threshold((int)(DetBoxThreshold * 255), 255, ThresholdTypes.Binary);
            using var dilated = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new CvSize(2, 2));
            Cv2.Dilate(binary, dilated, kernel);

            var contours = dilated.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            var scaleRate = 1.0 * src.Width / resizedSize.Width;

            var rects = contours
                .Where(x => GetScore(x, pred) > DetBoxScoreThreshold)
                .Select(Cv2.MinAreaRect)
                .Where(x => x.Size.Width > DetMinSize && x.Size.Height > DetMinSize)
                .Select(rect =>
                {
                    var minEdge = Math.Min(rect.Size.Width, rect.Size.Height);
                    var newSize = new Size2f(
                        (rect.Size.Width + DetUnclipRatio * minEdge) * scaleRate,
                        (rect.Size.Height + DetUnclipRatio * minEdge) * scaleRate);
                    return new RotatedRect(rect.Center * scaleRate, newSize, rect.Angle);
                })
                .OrderBy(v => v.Center.Y)
                .ThenBy(v => v.Center.X)
                .ToArray();

            return rects;
        }

        /// <summary>
        /// Text recognition
        /// </summary>
        private List<(string Text, float Score)> RecognizeText(
            Mat[] srcs, RunOptions runOptions, CancellationToken cancellationToken)
        {
            if (srcs.Length == 0)
                return new List<(string Text, float Score)>();

            var results = new List<(string Text, float Score)>();
            foreach (var src in srcs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (src == null || src.IsDisposed || src.Empty())
                {
                    results.Add((string.Empty, 0f));
                    continue;
                }

                using var channel3 = src.Channels() switch
                {
                    4 => src.CvtColor(ColorConversionCodes.BGRA2BGR),
                    1 => src.CvtColor(ColorConversionCodes.GRAY2BGR),
                    _ => SafeClone(src)
                };

                // Preserve the text aspect ratio, then pad to a reusable width
                // bucket. Padding with mid-gray maps to 0 after normalization,
                // matching PaddleOCR's normalized zero-padding. The old path fed
                // each exact crop width to DirectML and repeatedly paid the
                // dynamic-shape compilation cost.
                var ratio = channel3.Width / (double)channel3.Height;
                int contentWidth = (int)Math.Ceiling(RecImgHeight * ratio);
                contentWidth = Clamp(contentWidth, 16, RecognitionMaxWidth);
                int inputWidth = GetRecognitionInputWidth(
                    channel3.Width, channel3.Height, RecImgHeight);
                using var padded = new Mat(
                    RecImgHeight,
                    inputWidth,
                    MatType.CV_8UC3,
                    new Scalar(127.5, 127.5, 127.5));
                using (var content = new Mat(padded, new Rect(0, 0, contentWidth, RecImgHeight)))
                {
                    Cv2.Resize(channel3, content, new CvSize(contentWidth, RecImgHeight));
                }

                // Normalize to [-1, 1]
                using var blob = CvDnn.BlobFromImage(
                    padded,
                    2.0 / 255.0,
                    default,
                    new Scalar(127.5, 127.5, 127.5),
                    false,
                    false);

                // Get blob data
                var blobData = new float[blob.Total()];
                Marshal.Copy(blob.Data, blobData, 0, blobData.Length);

                var inputTensor = new DenseTensor<float>(
                    blobData,
                    new[] { 1, padded.Channels(), padded.Rows, padded.Cols });

                // Run recognition model
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_recSession.InputNames[0], inputTensor)
                };

                using var outputs = _recSession.Run(inputs, _recSession.OutputNames, runOptions);
                var output = outputs.First().AsTensor<float>();

                // Decode text
                results.Add(DecodeText(output, _labels));
            }

            return results;
        }

        /// <summary>
        /// Detector heights (post-resize, post-PadTo32) that real capture
        /// regions produce. <see cref="DetectTextRegions"/> scales the long edge
        /// down to <see cref="DetMaxSize"/> (960) and then pads both edges to a
        /// multiple of 32, so a 1262x277 or 1434x248 dialogue region never
        /// reaches the model wider than 960 — only the height varies:
        ///
        ///   1296x204 (ratio 6.35) -> 960x151 -> pad -> 960x160
        ///   1378x234 (ratio 5.89) -> 960x163 -> pad -> 960x192
        ///   1262x277 (ratio 4.56) -> 960x211 -> pad -> 960x224
        ///
        /// The pre-2026-08 set was {64, 160}, which left 192 and 224 to compile
        /// lazily on the first in-game dialogue — a multi-second stall on a slow
        /// DirectML device even though warm-up had "passed". Feeding 960-wide
        /// sources here means the shapes travel the exact production
        /// resize/pad path rather than being asserted by hand.
        /// </summary>
        private static readonly int[] WarmUpDetectorHeights = { 64, 160, 192, 224 };

        /// <summary>
        /// Recognition width buckets from <see cref="GetRecognitionInputWidth"/>
        /// that real dialogue lines land in. 320 is a name or a short line,
        /// 1280-1600 a full-width sentence; the pre-2026-08 set skipped the
        /// 640/960/1600 middle entirely.
        /// </summary>
        private static readonly int[] WarmUpRecognitionWidths = { 320, 640, 960, 1280, 1600 };

        /// <summary>
        /// Executes representative detector and recognizer shapes before the
        /// application reports the engine as ready. ONNX Runtime's DirectML
        /// provider compiles device-specific kernels lazily, so doing this on
        /// the background engine-initialization task prevents the first real
        /// dialogue from paying that cost (or being killed by the live timeout).
        ///
        /// Every shape runs under one cumulative budget. The
        /// CancellationTokenSource enforces the hard limit; we additionally
        /// stop before starting a shape once the remaining budget is smaller
        /// than the slowest shape observed so far, because aborting mid-Run
        /// leaves the kernel half-compiled anyway.
        /// </summary>
        /// <returns>How long the warm-up actually took. Callers use this to
        /// decide whether a provider that technically succeeded is still too
        /// slow to be worth using.</returns>
        public TimeSpan WarmUp(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (_disposed)
                throw new ObjectDisposedException(nameof(PaddleOCREngine));

            using var cts = new CancellationTokenSource(timeout);
            CancellationToken token = cts.Token;
            _gate.Wait(token);
            var timer = Stopwatch.StartNew();
            try
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PaddleOCREngine));

                using var runOptions = new RunOptions();
                using var cancellationRegistration = token.Register(() =>
                {
                    try { runOptions.Terminate = true; }
                    catch (ObjectDisposedException) { }
                });

                // Slowest single shape so far; the early-abort predictor.
                long slowestShapeMs = 0;
                int shapesWarmed = 0;
                bool budgetExhausted = false;

                bool HasBudgetForAnotherShape()
                {
                    if (slowestShapeMs <= 0) return true;
                    long remainingMs = (long)timeout.TotalMilliseconds - timer.ElapsedMilliseconds;
                    if (remainingMs > slowestShapeMs) return true;
                    budgetExhausted = true;
                    return false;
                }

                // Blank images are intentional: the graph is compiled and
                // validated without manufacturing OCR text.
                foreach (int height in WarmUpDetectorHeights)
                {
                    token.ThrowIfCancellationRequested();
                    if (!HasBudgetForAnotherShape()) break;
                    long shapeStartMs = timer.ElapsedMilliseconds;
                    using (var detectorInput = new Mat(
                        height, 960, MatType.CV_8UC3, Scalar.All(0)))
                    {
                        _ = DetectTextRegions(detectorInput, runOptions, token);
                    }
                    slowestShapeMs = Math.Max(slowestShapeMs, timer.ElapsedMilliseconds - shapeStartMs);
                    shapesWarmed++;
                }

                // Recognition remains batch=1, so this also matches the exact
                // production tensor layout.
                foreach (int width in WarmUpRecognitionWidths)
                {
                    token.ThrowIfCancellationRequested();
                    if (!HasBudgetForAnotherShape()) break;
                    long shapeStartMs = timer.ElapsedMilliseconds;
                    using (var recognitionInput = new Mat(
                        RecImgHeight, width, MatType.CV_8UC3,
                        new Scalar(127.5, 127.5, 127.5)))
                    {
                        _ = RecognizeText(new[] { recognitionInput }, runOptions, token);
                    }
                    slowestShapeMs = Math.Max(slowestShapeMs, timer.ElapsedMilliseconds - shapeStartMs);
                    shapesWarmed++;
                }

                timer.Stop();
                int totalShapes = WarmUpDetectorHeights.Length + WarmUpRecognitionWidths.Length;
                string budgetNote = budgetExhausted
                    ? $" — stopped early, {timeout.TotalSeconds:F0}s budget would not fit another shape " +
                      $"(slowest so far {slowestShapeMs}ms)"
                    : string.Empty;
                Logger.Log.Info(
                    $"OCR warm-up completed in {timer.ElapsedMilliseconds}ms " +
                    $"(provider={ExecutionProvider}, model={ModelName}, " +
                    $"shapes={shapesWarmed}/{totalShapes}){budgetNote}.");
                return timer.Elapsed;
            }
            catch (Exception ex) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"OCR warm-up exceeded {timeout.TotalSeconds:F0}s " +
                    $"(provider={ExecutionProvider}, model={ModelName}).",
                    ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Decode recognition result
        /// </summary>
        internal static (string Text, float Score) DecodeText(
            Tensor<float> output, IReadOnlyList<string> labels)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (labels == null) throw new ArgumentNullException(nameof(labels));

            var dimensions = output.Dimensions;
            if (dimensions.Length != 3 || dimensions[0] != 1)
                throw new ArgumentException("Recognition output must have shape [1, time, labels].", nameof(output));

            var charCount = dimensions[1];
            var labelCount = dimensions[2];

            var text = new System.Text.StringBuilder(charCount);
            var lastIndex = 0;
            var score = 0f;
            var validChars = 0;

            for (var n = 0; n < charCount; n++)
            {
                var maxIdx = 0;
                var maxVal = float.MinValue;

                for (var i = 0; i < labelCount; i++)
                {
                    var val = output[0, n, i];
                    if (val > maxVal)
                    {
                        maxVal = val;
                        maxIdx = i;
                    }
                }

                if (maxIdx > 0 && !(n > 0 && maxIdx == lastIndex))
                {
                    score += maxVal;
                    validChars++;
                    // Index mapping rules:
                    // Index 0 = blank (CTC blank character, skip)
                    // Index 1 to _labels.Count = characters in dictionary (index 1 corresponds to _labels[0])
                    // Index _labels.Count + 1 = space character
                    if (maxIdx <= labels.Count)
                    {
                        text.Append(labels[maxIdx - 1]);
                    }
                    else if (maxIdx == labels.Count + 1)
                    {
                        // Handle space character
                        text.Append(' ');
                    }
                    // If index is out of range, skip
                }

                lastIndex = maxIdx;
            }

            return (text.ToString(), validChars > 0 ? score / validChars : 0f);
        }

        /// <summary>
        /// Convert Bitmap to Mat
        /// </summary>
        private Mat BitmapToMat(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                // Use FromPixelData to create Mat, then immediately clone to ensure independent data copy
                // This can avoid the problem of memory failure after UnlockBits
                using var tempMat = Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride);
                // Create independent data copy
                var mat = new Mat();
                tempMat.CopyTo(mat);
                // Convert BGR to RGB
                Cv2.CvtColor(mat, mat, ColorConversionCodes.BGR2RGB);
                return mat;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        /// <summary>
        /// Resize image
        /// </summary>
        private Mat ResizeImage(Mat src, int maxSize)
        {
            if (src == null || src.IsDisposed || src.Empty())
                throw new ArgumentException("Invalid Mat object", nameof(src));

            var size = src.Size();
            var longEdge = Math.Max(size.Width, size.Height);
            var scaleRate = 1.0 * maxSize / longEdge;
            return scaleRate < 1.0 ? src.Resize(default, scaleRate, scaleRate) : SafeClone(src);
        }

        /// <summary>
        /// Pad to 32's multiple
        /// </summary>
        private Mat PadTo32(Mat src)
        {
            var size = src.Size();
            var newSize = new OpenCvSharp.Size(
                32 * (int)Math.Ceiling(1.0 * size.Width / 32),
                32 * (int)Math.Ceiling(1.0 * size.Height / 32));
            return src.CopyMakeBorder(0, newSize.Height - size.Height, 0, newSize.Width - size.Width, BorderTypes.Constant, Scalar.Black);
        }

        /// <summary>
        /// Normalize image
        /// </summary>
        private Tensor<float> NormalizeImage(Mat src)
        {
            var mean = new[] { 0.485f, 0.456f, 0.406f };
            var std = new[] { 0.229f, 0.224f, 0.225f };
            var scale = 1.0f / 255.0f;

            using var stdMat = new Mat();
            var channels = src.Split();
            try
            {
                for (var i = 0; i < channels.Length; i++)
                {
                    channels[i].ConvertTo(channels[i], MatType.CV_32FC1, scale / std[i], -mean[i] / std[i]);
                }
                Cv2.Merge(channels, stdMat);
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }

            using var blob = CvDnn.BlobFromImage(stdMat);
            var blobData = new float[blob.Total()];
            Marshal.Copy(blob.Data, blobData, 0, blobData.Length);
            return new DenseTensor<float>(blobData, new[] { 1, 3, stdMat.Rows, stdMat.Cols });
        }

        /// <summary>
        /// Convert Tensor to Mat
        /// </summary>
        private Mat TensorToMat(Tensor<float> tensor)
        {
            var dimensions = tensor.Dimensions;
            if (dimensions.Length != 4 || dimensions[0] != 1 || dimensions[1] != 1)
                throw new ArgumentException($"错误的tensor形状: {string.Join(",", dimensions.ToString())}");

            var data = tensor.ToArray();
            return Mat.FromPixelData(dimensions[2], dimensions[3], MatType.CV_32FC1, data);
        }

        /// <summary>
        /// Get contour score
        /// </summary>
        private float GetScore(CvPoint[] contour, Mat pred)
        {
            var width = pred.Width;
            var height = pred.Height;
            var boxX = contour.Select(v => v.X).ToArray();
            var boxY = contour.Select(v => v.Y).ToArray();

            var xmin = Clamp(boxX.Min(), 0, width - 1);
            var xmax = Clamp(boxX.Max(), 0, width - 1);
            var ymin = Clamp(boxY.Min(), 0, height - 1);
            var ymax = Clamp(boxY.Max(), 0, height - 1);

            var rootPoints = contour.Select(v => new CvPoint(v.X - xmin, v.Y - ymin)).ToArray();
            using var mask = new Mat(ymax - ymin + 1, xmax - xmin + 1, MatType.CV_8UC1, Scalar.Black);
            Cv2.FillPoly(mask, new[] { rootPoints }, new Scalar(1));

            using var croppedMat = pred[new Rect(xmin, ymin, xmax - xmin + 1, ymax - ymin + 1)];
            return (float)croppedMat.Mean(mask).Val0;
        }

        /// <summary>
        /// Get cropped region, ensure it does not exceed Mat boundaries
        /// </summary>
        private Rect GetCroppedRect(Rect rect, CvSize size)
        {
            // Ensure starting coordinates are within valid range
            var x = Clamp(rect.X, 0, size.Width - 1);
            var y = Clamp(rect.Y, 0, size.Height - 1);

            // Calculate maximum available width and height
            var maxWidth = size.Width - x;
            var maxHeight = size.Height - y;

            // Ensure width and height are within valid range, and do not exceed boundaries
            var width = Clamp(rect.Width, 1, maxWidth);
            var height = Clamp(rect.Height, 1, maxHeight);

            // Final validation: ensure X + Width <= size.Width and Y + Height <= size.Height
            if (x + width > size.Width)
                width = size.Width - x;
            if (y + height > size.Height)
                height = size.Height - y;

            // Ensure width and height are at least 1
            if (width < 1) width = 1;
            if (height < 1) height = 1;

            return new Rect(x, y, width, height);
        }

        /// <summary>
        /// Get four corners of rotated rectangle
        /// </summary>
        private PointF[] GetBoxPoints(RotatedRect rect)
        {
            unsafe
            {
                var points = rect.Points();
                var result = new PointF[4];
                for (int i = 0; i < 4; i++)
                {
                    result[i] = new PointF(points[i].X, points[i].Y);
                }
                return result;
            }
        }

        /// <summary>
        /// Release resources.
        /// Waits up to 1.5s for any in-flight Run() to finish before freeing
        /// native session handles. If the wait times out (rare — a Run stuck
        /// >1.5s implies GPU TDR or a pathological frame), we skip the native
        /// Dispose and let OnnxRuntime's finalizer reclaim the sessions.
        /// Freeing while native code holds the handle causes AccessViolation
        /// in InferenceSession.RunImpl — that is exactly what this guards.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            bool acquired = false;
            try
            {
                acquired = _gate.Wait(TimeSpan.FromSeconds(1.5));
            }
            catch (ObjectDisposedException)
            {
                // Gate already disposed — nothing to coordinate with.
                acquired = true;
            }

            try
            {
                if (acquired)
                {
                    _detSession?.Dispose();
                    _recSession?.Dispose();
                }
                else
                {
                    Logger.Log.Warn(
                        "PaddleOCREngine.Dispose: timed out waiting for in-flight Run(); " +
                        "skipping native session dispose to avoid AccessViolation. " +
                        "Sessions will be reclaimed by the ORT finalizer.");
                }
            }
            finally
            {
                if (acquired)
                {
                    try { _gate.Release(); } catch { /* disposed */ }
                    try { _gate.Dispose(); } catch { /* idempotent */ }
                }
                // On timeout an in-flight Run still owns the semaphore and
                // will Release it. Disposing the gate here would turn that
                // normal unwind into ObjectDisposedException. The engine is
                // already marked disposed, so leaving the tiny gate alive is
                // safer than racing the active native call.
            }
        }
    }
}
