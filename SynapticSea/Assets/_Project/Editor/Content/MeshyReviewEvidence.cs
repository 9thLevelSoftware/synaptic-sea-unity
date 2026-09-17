// Ported from tools/meshy_runtime_review.py and scripts/validation/meshy_asset_review_capture.gd (Godot repository) @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Engine-free half of <see cref="MeshyRuntimeReview"/>: governed task inputs, the pixel gates, camera validation,
    /// the canonical <c>runtime-review.json</c> document and publication of the fixed preview leaves. Numbers and
    /// bytes follow <c>tools/python/meshy_runtime_review.py</c> exactly (pixel values are <c>byte / 255.0</c>, JSON is
    /// Python's <c>canonical_json_bytes</c>), so the Python <c>verify_evidence_chain</c> re-derives identical evidence.
    /// </summary>
    public static class MeshyReviewEvidence
    {
        public static readonly int[] Seeds = { 42, 777 };
        public static readonly string[] LightingModes = { "normal", "emergency", "dark" };
        public const int CaptureWidth = 1600;
        public const int CaptureHeight = 900;
        public static readonly int SampleStepX = Math.Max(1, CaptureWidth / 64);
        public static readonly int SampleStepY = Math.Max(1, CaptureHeight / 36);
        public static readonly int StagedSampleMax = CeilDiv(CaptureWidth, SampleStepX) * CeilDiv(CaptureHeight, SampleStepY);
        public const double OpaqueAlphaMin = 0.05;
        public const double StagedLumaRangeMin = 0.004;
        public const int ContextualReferenceMin = 2;
        public const int ContextualChangedMin = 2;
        public const double ContextualChangedFractionMin = 0.5;
        public const double ContextualRgbDeltaMin = 0.02;
        public const double ContextualRgbDeltaMax = 1.0;
        public static readonly double[] LockedCameraDirection = { 16.0, 14.0, 16.0 };
        public const double LockedCameraDirectionTolerance = 1e-4;
        public const double CameraDistanceMin = 4.0;
        public const double CameraSizeMin = 1.5;
        public const double CameraSizeMax = 4096.0;
        public const double CameraSizeDimensionScale = 16.0;
        public const string SchemaVersion = "1.0.0";
        public const string DocumentKind = "meshy_runtime_review";
        public const string ReportName = "runtime-review.json";
        public const string StagingRelative = "artifacts/_staging/meshy";
        public const string PreviewRelative = "artifacts/validation-previews/meshy";
        const long JsonMaxBytes = 4 * 1024 * 1024;

        static readonly Regex IdentifierRe = new Regex("^[a-z0-9][a-z0-9_-]*$");
        static readonly Regex TaskIdRe = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]*$");
        static readonly Regex HashRe = new Regex("^[0-9a-f]{64}$");

        static int CeilDiv(int a, int b) => (a + b - 1) / b;

        public sealed class ReviewException : Exception
        {
            public ReviewException(string message) : base(message) { }
        }

        public struct Sample
        {
            public int X, Y;
            public double R, G, B, A, Luma;
        }

        public sealed class StagedVisibility
        {
            public bool Pass;
            public long OpaquePixels;
            public double LumaRange;
        }

        public sealed class ContextualVisibility
        {
            public bool Pass;
            public long ReferencePixels;
            public long ChangedPixels;
            public double MaxDelta;
        }

        /// <summary>Locked-isometric orthographic camera in the Godot frame; <see cref="Size"/> is Godot's full height.</summary>
        public sealed class CameraTransform
        {
            public double[] Position;
            public double[] Target;
            public double Size;
        }

        public sealed class CaptureRecord
        {
            public int Seed;
            public string Lighting;
            public CameraTransform Camera;
            public StagedVisibility Staged;
            public ContextualVisibility Contextual;
            public string OutputSha256;
            public string StagedSha256;
            public string ReferenceSha256;
        }

        /// <summary>The governed task (<c>ValidatedTask</c>).</summary>
        public sealed class ReviewInputs
        {
            public string AssetId;
            public string TaskId;
            public string Category;
            public string TaskDir;
            public string CleanedGlbPath;
            public long CleanedGlbSize;
            public string ContractSha256;
            public string CleanedGlbSha256;
            public string BlenderValidationSha256;
            public double[] BoundsDimensions;
        }

        // ------------------------------------------------------------------ names

        public static string CaptureName(int seed, string lighting)
        {
            if (Array.IndexOf(Seeds, seed) < 0) throw new ReviewException("unsupported review seed: " + seed);
            if (Array.IndexOf(LightingModes, lighting) < 0) throw new ReviewException("unsupported review lighting mode: " + lighting);
            return $"seed-{seed}-{lighting}.png";
        }

        public static string AuxiliaryCaptureName(int seed, string lighting, string kind)
        {
            CaptureName(seed, lighting);
            if (kind != "staged" && kind != "reference") throw new ReviewException("unsupported auxiliary capture kind: " + kind);
            return $"seed-{seed}-{lighting}-{kind}.png";
        }

        /// <summary><c>FIXED_OUTPUT_NAMES</c>: six finals, then the staged/reference pairs, then the report.</summary>
        public static IReadOnlyList<string> FixedOutputNames()
        {
            var names = new List<string>();
            foreach (int seed in Seeds)
                foreach (string lighting in LightingModes)
                    names.Add(CaptureName(seed, lighting));
            foreach (int seed in Seeds)
                foreach (string lighting in LightingModes)
                    foreach (string kind in new[] { "staged", "reference" })
                        names.Add(AuxiliaryCaptureName(seed, lighting, kind));
            names.Add(ReportName);
            return names;
        }

        // ------------------------------------------------------------------ pixels

        /// <summary>
        /// <c>_decode_png_samples</c> over an RGBA32 buffer in Unity order (row 0 = bottom). Samples are returned
        /// top-down, row-major, like the PNG decoder: rows <c>y % stepY == 0</c> (or exactly <paramref name="rows"/>),
        /// columns <c>x % stepX == 0</c>.
        /// </summary>
        public static List<Sample> SampleGrid(byte[] rgba32BottomUp, int width, int height, int stepX, int stepY, ISet<int> rows = null)
        {
            if (rgba32BottomUp == null || rgba32BottomUp.Length != width * height * 4) throw new ReviewException("capture pixels do not match the capture size");
            var samples = new List<Sample>();
            for (int y = 0; y < height; y++)
            {
                if (rows != null ? !rows.Contains(y) : y % Math.Max(1, stepY) != 0) continue;
                int unityRow = height - 1 - y;
                for (int x = 0; x < width; x += Math.Max(1, stepX))
                {
                    int i = (unityRow * width + x) * 4;
                    double red = rgba32BottomUp[i] / 255.0;
                    double green = rgba32BottomUp[i + 1] / 255.0;
                    double blue = rgba32BottomUp[i + 2] / 255.0;
                    double alpha = rgba32BottomUp[i + 3] / 255.0;
                    samples.Add(new Sample { X = x, Y = y, R = red, G = green, B = blue, A = alpha, Luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue });
                }
            }
            return samples;
        }

        public static List<Sample> GovernedSamples(byte[] rgba32BottomUp) =>
            SampleGrid(rgba32BottomUp, CaptureWidth, CaptureHeight, SampleStepX, SampleStepY);

        /// <summary><c>_staged_pixel_evidence</c>.</summary>
        public static StagedVisibility Staged(IReadOnlyList<Sample> samples)
        {
            double minimum = 1.0, maximum = 0.0;
            long opaque = 0;
            bool any = false;
            foreach (var s in samples)
            {
                if (!any) { minimum = s.Luma; maximum = s.Luma; any = true; }
                else { minimum = Math.Min(minimum, s.Luma); maximum = Math.Max(maximum, s.Luma); }
                if (s.A >= OpaqueAlphaMin) opaque++;
            }
            double range = maximum - minimum;
            return new StagedVisibility { Pass = opaque >= 2 && samples.Count > 0 && range >= StagedLumaRangeMin, OpaquePixels = opaque, LumaRange = range };
        }

        /// <summary><c>_contextual_pixel_evidence</c>: over the staged silhouette, how much the asset changes the scene.</summary>
        public static ContextualVisibility Contextual(IReadOnlyList<Sample> staged, IReadOnlyList<Sample> reference, IReadOnlyList<Sample> final)
        {
            if (staged.Count != reference.Count || staged.Count != final.Count) throw new ReviewException("governed pixel evidence uses inconsistent sample grids");
            long referencePixels = 0, changed = 0;
            double maxDelta = 0.0;
            for (int i = 0; i < staged.Count; i++)
            {
                if (staged[i].X != reference[i].X || staged[i].Y != reference[i].Y || staged[i].X != final[i].X || staged[i].Y != final[i].Y)
                    throw new ReviewException("governed pixel evidence uses inconsistent coordinates");
                if (staged[i].A < OpaqueAlphaMin) continue;
                referencePixels++;
                double delta = Math.Max(Math.Max(Math.Abs(final[i].R - reference[i].R), Math.Abs(final[i].G - reference[i].G)), Math.Abs(final[i].B - reference[i].B));
                maxDelta = Math.Max(maxDelta, delta);
                if (delta >= ContextualRgbDeltaMin) changed++;
            }
            bool pass = referencePixels >= ContextualReferenceMin && referencePixels <= StagedSampleMax && changed >= ContextualChangedMin
                        && changed <= referencePixels && (double)changed / referencePixels >= ContextualChangedFractionMin
                        && maxDelta >= ContextualRgbDeltaMin && maxDelta <= ContextualRgbDeltaMax;
            return new ContextualVisibility { Pass = pass, ReferencePixels = referencePixels, ChangedPixels = changed, MaxDelta = maxDelta };
        }

        /// <summary><c>_png_is_visible</c>: the conservative non-blank gate on the final capture.</summary>
        public static bool IsVisible(byte[] rgba32BottomUp, int width = CaptureWidth, int height = CaptureHeight)
        {
            var rows = new HashSet<int> { 0, height / 8, height / 4, height / 2, (3 * height) / 4, height - 1 };
            var samples = SampleGrid(rgba32BottomUp, width, height, Math.Max(1, width / 32), 1, rows);
            var visible = samples.Where(s => s.A >= OpaqueAlphaMin).Select(s => s.Luma).ToList();
            if (visible.Count < Math.Max(2, samples.Count / 8)) return false;
            return visible.Max() - visible.Min() >= StagedLumaRangeMin;
        }

        // ------------------------------------------------------------------ gates

        /// <summary><c>_camera_size_limit</c>.</summary>
        public static double CameraSizeLimit(double[] boundsDimensions)
        {
            if (boundsDimensions == null) return CameraSizeMax;
            double diagonal = Math.Sqrt(boundsDimensions.Sum(v => v * v));
            if (double.IsNaN(diagonal) || double.IsInfinity(diagonal) || diagonal < 0.0) throw new ReviewException("runtime GLB bounds dimensions are invalid");
            return Math.Min(CameraSizeMax, Math.Max(CameraSizeMin, diagonal * CameraSizeDimensionScale));
        }

        /// <summary><c>_validate_camera_transform</c>.</summary>
        public static void ValidateCamera(CameraTransform camera, double[] boundsDimensions)
        {
            if (camera?.Position == null || camera.Target == null || camera.Position.Length != 3 || camera.Target.Length != 3
                || camera.Position.Concat(camera.Target).Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ReviewException("camera transform vector is invalid");
            var delta = new double[3];
            for (int i = 0; i < 3; i++) delta[i] = camera.Position[i] - camera.Target[i];
            double distance = Math.Sqrt(delta.Sum(v => v * v));
            if (double.IsNaN(distance) || distance < CameraDistanceMin) throw new ReviewException("camera distance is below the governed minimum");
            double lockedLength = Math.Sqrt(LockedCameraDirection.Sum(v => v * v));
            double error = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double d = delta[i] / distance - LockedCameraDirection[i] / lockedLength;
                error += d * d;
            }
            if (Math.Sqrt(error) > LockedCameraDirectionTolerance) throw new ReviewException("camera transform direction is not locked isometric");
            if (double.IsNaN(camera.Size) || double.IsInfinity(camera.Size) || camera.Size < CameraSizeMin || camera.Size > CameraSizeLimit(boundsDimensions))
                throw new ReviewException("camera size is outside the governed bound");
        }

        /// <summary><c>_validate_staged_visibility</c>.</summary>
        public static void ValidateStaged(StagedVisibility v)
        {
            if (v == null || !v.Pass || v.OpaquePixels < 2 || v.OpaquePixels > StagedSampleMax || double.IsNaN(v.LumaRange) || v.LumaRange < StagedLumaRangeMin || v.LumaRange > 1.0)
                throw new ReviewException("staged visibility evidence is outside the strict pixel gate");
        }

        /// <summary><c>_validate_contextual_visibility</c>.</summary>
        public static void ValidateContextual(ContextualVisibility v)
        {
            if (v == null || !v.Pass || v.ReferencePixels < ContextualReferenceMin || v.ReferencePixels > StagedSampleMax
                || v.ChangedPixels < ContextualChangedMin || v.ChangedPixels > v.ReferencePixels
                || (double)v.ChangedPixels / v.ReferencePixels < ContextualChangedFractionMin
                || double.IsNaN(v.MaxDelta) || v.MaxDelta < ContextualRgbDeltaMin || v.MaxDelta > ContextualRgbDeltaMax)
                throw new ReviewException("contextual visibility evidence is outside the strict pixel gate");
        }

        // ------------------------------------------------------------------ document

        /// <summary>
        /// <c>build_runtime_review_document</c>: validates six captures in the canonical order and returns the report as
        /// plain JSON values (<see cref="CanonicalJsonBytes"/> serialises it).
        /// </summary>
        public static SortedDictionary<string, object> BuildDocument(ReviewInputs inputs, IReadOnlyList<CaptureRecord> captures)
        {
            var byKey = new Dictionary<(int, string), CaptureRecord>();
            foreach (var c in captures)
            {
                CaptureName(c.Seed, c.Lighting);
                ValidateCamera(c.Camera, inputs.BoundsDimensions);
                ValidateStaged(c.Staged);
                ValidateContextual(c.Contextual);
                if (!HashRe.IsMatch(c.OutputSha256 ?? "") || !HashRe.IsMatch(c.StagedSha256 ?? "") || !HashRe.IsMatch(c.ReferenceSha256 ?? ""))
                    throw new ReviewException("runtime capture hash is invalid");
                if (byKey.ContainsKey((c.Seed, c.Lighting))) throw new ReviewException("duplicate runtime capture");
                byKey[(c.Seed, c.Lighting)] = c;
            }
            var expected = Seeds.SelectMany(s => LightingModes.Select(l => (s, l))).ToList();
            if (byKey.Count != expected.Count || expected.Any(k => !byKey.ContainsKey(k))) throw new ReviewException("runtime review requires exactly six captures");
            if (!TaskIdRe.IsMatch(inputs.TaskId ?? "")) throw new ReviewException("runtime task_id is invalid");
            if (!HashRe.IsMatch(inputs.ContractSha256 ?? "") || !HashRe.IsMatch(inputs.CleanedGlbSha256 ?? "")) throw new ReviewException("runtime input hashes are invalid");
            if (!HashRe.IsMatch(inputs.BlenderValidationSha256 ?? "")) throw new ReviewException("runtime Blender validation hash is invalid");

            var captureValues = new List<object>();
            var outputHashes = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var key in expected)
            {
                var c = byKey[key];
                captureValues.Add(new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    { "seed", (long)c.Seed },
                    { "lighting", c.Lighting },
                    {
                        "camera_transform", new SortedDictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "projection", "orthogonal" },
                            { "position", c.Camera.Position.Cast<object>().ToList() },
                            { "target", c.Camera.Target.Cast<object>().ToList() },
                            { "size", c.Camera.Size },
                        }
                    },
                    { "staged_visibility", StagedValue(c.Staged) },
                    { "contextual_visibility", ContextualValue(c.Contextual) },
                    { "output_sha256", c.OutputSha256 },
                    { "pass", true },
                    { "reason", "pass" },
                });
                outputHashes[CaptureName(c.Seed, c.Lighting)] = c.OutputSha256;
                outputHashes[AuxiliaryCaptureName(c.Seed, c.Lighting, "staged")] = c.StagedSha256;
                outputHashes[AuxiliaryCaptureName(c.Seed, c.Lighting, "reference")] = c.ReferenceSha256;
            }
            return new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema_version", SchemaVersion },
                { "document_kind", DocumentKind },
                { "asset_id", inputs.AssetId },
                { "task_id", inputs.TaskId },
                { "contract_sha256", inputs.ContractSha256 },
                { "cleaned_glb_sha256", inputs.CleanedGlbSha256 },
                { "blender_validation_sha256", inputs.BlenderValidationSha256 },
                { "seeds", Seeds.Select(s => (object)(long)s).ToList() },
                { "lighting", LightingModes.Cast<object>().ToList() },
                { "captures", captureValues },
                { "output_hashes", outputHashes },
                { "pass", true },
                { "reason", "pass" },
            };
        }

        static SortedDictionary<string, object> StagedValue(StagedVisibility v) => new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            { "pass", v.Pass }, { "opaque_pixels", v.OpaquePixels }, { "luma_range", v.LumaRange },
        };

        static SortedDictionary<string, object> ContextualValue(ContextualVisibility v) => new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            { "pass", v.Pass }, { "reference_pixels", v.ReferencePixels }, { "changed_pixels", v.ChangedPixels }, { "max_delta", v.MaxDelta },
        };

        // ------------------------------------------------------------------ canonical JSON (Python json.dumps)

        /// <summary>
        /// <c>canonical_json_bytes</c>: <c>json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"),
        /// allow_nan=False) + "\n"</c> as UTF-8. Values: dictionaries with string keys, lists, strings, bool, null,
        /// long/int (integers) and double (floats, written with Python's shortest <c>repr</c>).
        /// </summary>
        public static byte[] CanonicalJsonBytes(object value)
        {
            var sb = new StringBuilder();
            WriteJson(sb, value);
            sb.Append('\n');
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        static void WriteJson(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: WriteString(sb, s); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(PythonFloatRepr(d)); return;
                case float f: sb.Append(PythonFloatRepr(f)); return;
                case JToken token: WriteJson(sb, FromJToken(token)); return;
                case System.Collections.IDictionary dict:
                {
                    var keys = dict.Keys.Cast<object>().Select(k => k as string ?? throw new ReviewException("JSON keys must be strings"))
                        .OrderBy(k => k, StringComparer.Ordinal).ToList();
                    sb.Append('{');
                    for (int n = 0; n < keys.Count; n++)
                    {
                        if (n > 0) sb.Append(',');
                        WriteString(sb, keys[n]);
                        sb.Append(':');
                        WriteJson(sb, dict[keys[n]]);
                    }
                    sb.Append('}');
                    return;
                }
                case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteJson(sb, item);
                    }
                    sb.Append(']');
                    return;
                }
                default: throw new ReviewException("unsupported JSON value: " + value.GetType().Name);
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Python's <c>repr(float)</c> (shortest round-trip digits; exponent form below 1e-4 and from 1e16).</summary>
        public static string PythonFloatRepr(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new ReviewException("Out of range float values are not JSON compliant");
            if (d == 0.0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
            string e = null;
            for (int precision = 0; precision < 17; precision++)
            {
                e = d.ToString("E" + precision, CultureInfo.InvariantCulture);
                // Low precisions can round past double.MaxValue ("1.8E+308"), which Mono's parser rejects.
                if (double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed == d) break;
            }
            bool negative = e[0] == '-';
            int ePos = e.IndexOf('E');
            string mantissa = e.Substring(negative ? 1 : 0, ePos - (negative ? 1 : 0)).Replace(".", "");
            int exponent = int.Parse(e.Substring(ePos + 1), CultureInfo.InvariantCulture);
            string digits = mantissa.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            int decpt = exponent + 1;
            var sb = new StringBuilder();
            if (negative) sb.Append('-');
            if (decpt <= -4 || decpt > 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                int exp = decpt - 1;
                sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
            }
            else if (decpt <= 0)
            {
                sb.Append("0.").Append('0', -decpt).Append(digits);
            }
            else if (decpt >= digits.Length)
            {
                sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
            }
            return sb.ToString();
        }

        /// <summary>JSON token to plain values (integers stay long, floats double) for canonical re-serialisation.</summary>
        public static object FromJToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object: return ((JObject)token).Properties().ToDictionary(p => p.Name, p => FromJToken(p.Value), StringComparer.Ordinal);
                case JTokenType.Array: return ((JArray)token).Select(FromJToken).ToList();
                case JTokenType.Integer: return (long)token;
                case JTokenType.Float: return (double)token;
                case JTokenType.String: return (string)token;
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.Null: return null;
                default: throw new ReviewException("unsupported JSON token: " + token.Type);
            }
        }

        // ------------------------------------------------------------------ governed inputs

        /// <summary><c>strict_load_json_bytes</c>: bounded, UTF-8, one JSON object, duplicate keys rejected.</summary>
        public static (JObject doc, byte[] raw) StrictLoadJson(string path, string label)
        {
            if (!File.Exists(path)) throw new ReviewException($"missing {label}: {path}");
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > JsonMaxBytes) throw new ReviewException($"{label} is empty or too large");
            byte[] raw = File.ReadAllBytes(path);
            try
            {
                string text = new UTF8Encoding(false, true).GetString(raw);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                {
                    var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new JsonReaderException("trailing content");
                    if (!(token is JObject doc)) throw new ReviewException($"{label} must be a JSON object");
                    return (doc, raw);
                }
            }
            catch (Exception e) when (e is JsonException || e is DecoderFallbackException)
            {
                throw new ReviewException($"invalid JSON in {label}: {e.Message}");
            }
        }

        static void RequireCanonical(JObject doc, byte[] raw, string label)
        {
            if (!raw.SequenceEqual(CanonicalJsonBytes(doc))) throw new ReviewException($"{label} is not canonical JSON");
        }

        static string Str(JToken token) => token != null && token.Type == JTokenType.String ? (string)token : null;

        /// <summary>
        /// <c>_load_runtime_inputs</c>, without the POSIX-only parts: the task must be
        /// <c>artifacts/_staging/meshy/&lt;asset_id&gt;/&lt;task_id&gt;</c> with a canonical <c>review.json</c> in state
        /// selected or promotion_ready, a SUCCEEDED <c>generation.json</c> bound to the task and both contracts, a
        /// task-local <c>contract.json</c> equal to the caller's contract, a non-empty <c>cleaned.glb</c> with a valid GLB
        /// header, and a canonical R4 <c>blender-validation.json</c> (<c>_validate_report_record</c>) bound to that GLB.
        /// </summary>
        public static ReviewInputs LoadTaskInputs(string repoRoot, string contractPath, string taskDir)
        {
            string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string task = Path.GetFullPath(taskDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(task)) throw new ReviewException("missing task directory: " + taskDir);
            if ((File.GetAttributes(task) & FileAttributes.ReparsePoint) != 0) throw new ReviewException("task directory must not be a link: " + taskDir);
            string stage = Path.Combine(root, StagingRelative.Replace('/', Path.DirectorySeparatorChar));
            string relative = task.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? task.Substring(stage.Length + 1) : null;
            string[] parts = relative?.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts == null) throw new ReviewException("task directory must be physically under Meshy staging");
            if (parts.Length != 2) throw new ReviewException("task directory must be a direct asset/task child");
            string assetId = parts[0], taskId = parts[1];
            if (!IdentifierRe.IsMatch(assetId)) throw new ReviewException("task directory asset_id is invalid");
            if (!TaskIdRe.IsMatch(taskId)) throw new ReviewException("task directory task_id is invalid");

            var (callerContract, callerRaw) = StrictLoadJson(contractPath, "contract");
            ValidateContractIdentity(callerContract);
            string callerSha = PromoteStructuralSource.Sha256Hex(callerRaw);
            var (taskContract, taskContractRaw) = StrictLoadJson(Path.Combine(task, "contract.json"), "task contract");
            RequireCanonical(taskContract, taskContractRaw, "task contract");
            ValidateContractIdentity(taskContract);
            if (!JToken.DeepEquals(callerContract, taskContract)) throw new ReviewException("caller contract does not match task-local contract");
            if (Str(taskContract["asset_id"]) != assetId) throw new ReviewException("contract asset_id does not match task directory");

            var (review, reviewRaw) = StrictLoadJson(Path.Combine(task, "review.json"), "Meshy candidate review");
            RequireCanonical(review, reviewRaw, "review.json");
            if (Str(review["asset_id"]) != assetId || Str(review["task_id"]) != taskId) throw new ReviewException("review identity does not match task directory");
            string state = Str(review["state"]);
            if (state != "selected" && state != "promotion_ready") throw new ReviewException("runtime review requires a selected or promotion_ready candidate");

            var (generation, _) = StrictLoadJson(Path.Combine(task, "generation.json"), "generation.json");
            if (Str(generation["status"]) != "SUCCEEDED") throw new ReviewException("runtime review requires SUCCEEDED generation evidence");
            if (Str(generation["asset_id"]) != assetId || Str(generation["task_id"]) != taskId) throw new ReviewException("generation identity is not bound to the task");
            if (Str(generation["contract_artifact_sha256"]) != PromoteStructuralSource.Sha256Hex(taskContractRaw)) throw new ReviewException("generation contract artifact is not bound");
            if (Str(generation["contract_sha256"]) != callerSha) throw new ReviewException("generation contract hash is not bound to caller contract");

            string cleaned = Path.Combine(task, "cleaned.glb");
            if (!File.Exists(cleaned) || (File.GetAttributes(cleaned) & FileAttributes.ReparsePoint) != 0) throw new ReviewException("cleaned.glb must be a bounded regular file");
            long cleanedSize = new FileInfo(cleaned).Length;
            byte[] header = new byte[12];
            using (var stream = File.OpenRead(cleaned)) stream.Read(header, 0, 12);
            if (cleanedSize <= 0 || !PromoteStructuralSource.IsValidGlbHeader(header, cleanedSize)) throw new ReviewException("cleaned.glb is not a valid GLB");
            string cleanedSha = PromoteStructuralSource.Sha256Hex(cleaned);

            var (r4, r4Raw) = StrictLoadJson(Path.Combine(task, "blender-validation.json"), "Blender validation report");
            RequireCanonical(r4, r4Raw, "Blender validation report");
            ValidateBlenderReport(r4);
            if (Str(r4["task_id"]) != taskId || Str(r4["asset_id"]) != assetId || Str(r4["contract_sha256"]) != Str(generation["contract_sha256"])
                || Str(r4["sha256"]) != cleanedSha || (long)r4["byte_size"] != cleanedSize)
                throw new ReviewException("R4 Blender evidence does not match the current task");

            var dims = (JArray)r4["bounds"]["dimensions"];
            return new ReviewInputs
            {
                AssetId = assetId,
                TaskId = taskId,
                Category = Str(taskContract["category"]),
                TaskDir = task,
                CleanedGlbPath = cleaned,
                CleanedGlbSize = cleanedSize,
                ContractSha256 = callerSha,
                CleanedGlbSha256 = cleanedSha,
                BlenderValidationSha256 = PromoteStructuralSource.Sha256Hex(r4Raw),
                BoundsDimensions = dims.Select(t => (double)t).ToArray(),
            };
        }

        /// <summary>The contract identity checks <c>validate_contract</c> starts with (the full schema stays in Python).</summary>
        static void ValidateContractIdentity(JObject contract)
        {
            if (Str(contract["schema_version"]) != "1.0.0") throw new ReviewException("contract schema_version must be 1.0.0");
            if (Str(contract["document_kind"]) != "ai_asset_contract") throw new ReviewException("contract document_kind must be ai_asset_contract");
            if (!IdentifierRe.IsMatch(Str(contract["asset_id"]) ?? "")) throw new ReviewException("contract asset_id must be a lowercase identifier");
            if (!IdentifierRe.IsMatch(Str(contract["category"]) ?? "")) throw new ReviewException("contract category must be a lowercase identifier");
        }

        static readonly string[] R4Fields =
        {
            "schema_version", "document_kind", "status", "task_id", "asset_id", "contract_sha256", "sha256", "byte_size", "mesh_count",
            "triangle_count", "material_names", "bounds", "uvs_present", "uv_evidence", "blender_reimport_passed", "master_provenance",
        };

        static bool IsInt(JToken t, long minimum) => t != null && t.Type == JTokenType.Integer && (long)t >= minimum;

        /// <summary><c>meshy_blender_validate._validate_report_record</c>.</summary>
        public static void ValidateBlenderReport(JObject report)
        {
            var fields = report.Properties().Select(p => p.Name).ToList();
            if (fields.Count != R4Fields.Length || R4Fields.Any(f => !fields.Contains(f))) throw new ReviewException("validation report fields are not canonical");
            if (Str(report["schema_version"]) != "1.0.0" || Str(report["document_kind"]) != "meshy_blender_validation" || Str(report["status"]) != "PASS")
                throw new ReviewException("validation report document identity is invalid");
            foreach (string field in new[] { "task_id", "asset_id", "contract_sha256", "sha256" })
                if (string.IsNullOrWhiteSpace(Str(report[field]))) throw new ReviewException("validation report " + field + " is invalid");
            if (!TaskIdRe.IsMatch(Str(report["task_id"])) || !IdentifierRe.IsMatch(Str(report["asset_id"]))) throw new ReviewException("validation report identifier is invalid");
            if (!HashRe.IsMatch(Str(report["contract_sha256"])) || !HashRe.IsMatch(Str(report["sha256"]))) throw new ReviewException("validation report hash is invalid");
            if (!IsInt(report["byte_size"], 1)) throw new ReviewException("validation report byte_size is invalid");
            foreach (string field in new[] { "mesh_count", "triangle_count" })
                if (!IsInt(report[field], 1)) throw new ReviewException("validation report " + field + " is invalid");
            if (!(report["material_names"] is JArray names) || names.Any(n => string.IsNullOrWhiteSpace(Str(n))) || names.Select(Str).Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new ReviewException("validation report material_names is invalid");
            if (!(report["bounds"] is JObject bounds) || bounds.Count != 3 || !bounds.ContainsKey("min") || !bounds.ContainsKey("max") || !bounds.ContainsKey("dimensions"))
                throw new ReviewException("validation report bounds are invalid");
            var values = new Dictionary<string, double[]>();
            foreach (string field in new[] { "min", "max", "dimensions" })
            {
                if (!(bounds[field] is JArray arr) || arr.Count != 3 || arr.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                    throw new ReviewException("validation report bounds are invalid");
                values[field] = arr.Select(v => (double)v).ToArray();
                if (values[field].Any(v => double.IsNaN(v) || double.IsInfinity(v))) throw new ReviewException("validation report bounds are invalid");
            }
            for (int i = 0; i < 3; i++)
            {
                if (values["min"][i] > values["max"][i]) throw new ReviewException("validation report bounds are invalid");
                if (Math.Abs(values["dimensions"][i] - (values["max"][i] - values["min"][i])) > 1e-9) throw new ReviewException("validation report bounds dimensions are invalid");
            }
            if (report["uvs_present"]?.Type != JTokenType.Boolean || !(bool)report["uvs_present"] || report["blender_reimport_passed"]?.Type != JTokenType.Boolean || !(bool)report["blender_reimport_passed"])
                throw new ReviewException("validation report requires UV and Blender re-import evidence");
            if (report["master_provenance"].Type != JTokenType.Null) throw new ReviewException("master_provenance must be null");
            if (!(report["uv_evidence"] is JArray evidence) || evidence.Count == 0) throw new ReviewException("validation report UV evidence is empty");
            string[] evidenceFields = { "mesh_index", "primitive_index", "accessor", "vertex_count", "uv_count", "finite", "range_valid" };
            foreach (JToken item in evidence)
            {
                if (!(item is JObject e) || e.Count != evidenceFields.Length || evidenceFields.Any(f => !e.ContainsKey(f)))
                    throw new ReviewException("validation report UV evidence is not canonical");
                foreach (string f in new[] { "mesh_index", "primitive_index", "accessor", "vertex_count", "uv_count" })
                    if (!IsInt(e[f], f == "vertex_count" || f == "uv_count" ? 1 : 0)) throw new ReviewException("validation report UV evidence types are invalid");
                if (e["finite"].Type != JTokenType.Boolean || !(bool)e["finite"] || e["range_valid"].Type != JTokenType.Boolean || !(bool)e["range_valid"] || (long)e["uv_count"] != (long)e["vertex_count"])
                    throw new ReviewException("validation report UV evidence failed");
            }
        }

        // ------------------------------------------------------------------ publication

        /// <summary><c>_fixed_preview_path</c>: <c>artifacts/validation-previews/meshy/&lt;asset_id&gt;</c>, holding only fixed leaves.</summary>
        public static string FixedPreviewPath(string repoRoot, string assetId)
        {
            if (!IdentifierRe.IsMatch(assetId ?? "")) throw new ReviewException("asset_id must be a safe lowercase identifier");
            string path = Path.Combine(Path.GetFullPath(repoRoot), PreviewRelative.Replace('/', Path.DirectorySeparatorChar), assetId);
            if (File.Exists(path)) throw new ReviewException("preview directory must be a regular directory");
            if (Directory.Exists(path))
            {
                var allowed = new HashSet<string>(FixedOutputNames(), StringComparer.Ordinal);
                var unexpected = Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Where(n => !allowed.Contains(n)).ToList();
                if (unexpected.Count > 0) throw new ReviewException("preview directory contains an unexpected entry: " + unexpected[0]);
            }
            return path;
        }

        /// <summary>
        /// <c>publish_fixed_captures</c>: an existing report is authoritative (reused only when every leaf matches
        /// byte-for-byte); otherwise every existing leaf must already match, missing leaves are written atomically and the
        /// report is written last.
        /// </summary>
        public static void Publish(string previewDir, IReadOnlyDictionary<string, byte[]> payloads, byte[] reportBytes)
        {
            var fixedNames = FixedOutputNames();
            if (payloads.Count != fixedNames.Count - 1 || fixedNames.Take(fixedNames.Count - 1).Any(n => !payloads.ContainsKey(n)))
                throw new ReviewException("runtime pixel evidence publication is incomplete");
            Directory.CreateDirectory(previewDir);
            string reportPath = Path.Combine(previewDir, ReportName);
            if (File.Exists(reportPath))
            {
                if (!File.ReadAllBytes(reportPath).SequenceEqual(reportBytes)) throw new ReviewException("existing runtime-review.json differs from the requested evidence");
                foreach (var kv in payloads) RequireExistingLeaf(Path.Combine(previewDir, kv.Key), kv.Value, true);
                return;
            }
            foreach (var kv in payloads) RequireExistingLeaf(Path.Combine(previewDir, kv.Key), kv.Value, false);
            foreach (string name in fixedNames.Take(fixedNames.Count - 1))
            {
                string target = Path.Combine(previewDir, name);
                if (!File.Exists(target)) AtomicWrite(target, payloads[name]);
            }
            AtomicWrite(reportPath, reportBytes);
            if (!File.ReadAllBytes(reportPath).SequenceEqual(reportBytes)) throw new ReviewException("published runtime-review.json was not exact");
        }

        static void RequireExistingLeaf(string path, byte[] expected, bool mustExist)
        {
            if (!File.Exists(path))
            {
                if (mustExist) throw new ReviewException("authoritative preview directory is incomplete");
                return;
            }
            if (!File.ReadAllBytes(path).SequenceEqual(expected)) throw new ReviewException("existing capture " + Path.GetFileName(path) + " differs from the requested evidence");
        }

        static void AtomicWrite(string path, byte[] bytes)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + ".tmp");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path);
        }
    }
}
