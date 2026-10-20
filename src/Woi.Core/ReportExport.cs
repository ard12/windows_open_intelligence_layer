using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Woi.Core;

public record ExportPrivacyOptions(bool IncludeNames = false, bool IncludeMachineMetadata = false,
    bool IncludePaths = false, bool IncludeEndpoints = false, bool IncludeCommandLines = false, bool IncludeFreeText = false)
{
    public bool AllIdentifiers => IncludeNames && IncludeMachineMetadata && IncludePaths && IncludeEndpoints && IncludeCommandLines;
}
public record AttemptComparisonExport(Comparison Comparison, InvestigationView First, InvestigationView Second);
public record ExportFieldReview(string Category, int Fields);

public static class ReportExport
{
    private static readonly HashSet<string> IdentityFields = new(StringComparer.Ordinal) {
        "id", "sourceId", "processId", "threadId", "lifetimeId", "processLifetimeId", "firstId", "secondId", "ownerId", "parentId", "key", "focusKey", "path", "keys"
    };
    private static readonly HashSet<string> PublicLabels = new(StringComparer.Ordinal) {
        "WindowsApiSnapshot", InvestigationAnalyzer.Version, Wire.AnalysisVersion,
        "CPU", "Disk", "File", "FileBytes", "Network", "NetworkBytes", "Memory", "HardFault", "Process", "Thread", "Module", "Latency",
        "bytes", "ms", "seconds", "CPU-seconds", "samples", "operations", "percent", "%",
        "User-marked task duration", "Accounted CPU time", "Accounted reads", "Accounted writes",
        "Observed minimum available physical memory", "Observed peak process private allocation",
        "Recorded busy CPU samples", "Estimated sampled CPU", "Recorded physical disk transfers", "Recorded logical file requests",
        "Recorded network sends/receives", "Recorded disk operations"
    };
    private static JsonObject Document(object source)
    {
        if (source is not (Report or InvestigationView or AttemptComparisonExport or IntervalComparisonPage or CpuStackPage))
            throw new WoiException("InvalidReport", "Choose a supported report or comparison page.");
        return JsonSerializer.SerializeToNode(source, source.GetType(), Wire.Json)!.AsObject();
    }
    private static string Category(string key) => key switch {
        "name" or "processName" or "module" or "names" => "Names",
        "machine" or "operatingSystem" => "Machine metadata",
        "sourcePath" or "cacheDirectory" or "imagePath" => "Paths",
        "endpoint" or "localAddress" or "remoteAddress" => "Endpoints",
        "commandLine" => "Command lines",
        _ => "Free text"
    };
    public static IReadOnlyList<ExportFieldReview> Review(object source)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        void Visit(JsonNode? node, string key)
        {
            if (node is JsonObject obj) foreach (var item in obj) Visit(item.Value, item.Key);
            else if (node is JsonArray array) foreach (var item in array) Visit(item, key);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) && !IdentityFields.Contains(key) && !Public(text, key)) {
                var category = Category(key); counts[category] = counts.GetValueOrDefault(category) + 1;
            }
        }
        Visit(Document(source), "");
        return new[] { "Names", "Machine metadata", "Paths", "Endpoints", "Command lines", "Free text" }
            .Select(x => new ExportFieldReview(x, counts.GetValueOrDefault(x))).ToArray();
    }
    private static bool Public(string text, string key) =>
        key is "traceHash" or "sourceHash" && text.Length == 64 && text.All(Uri.IsHexDigit) ||
        key is "parserVersion" or "analysisVersion" && Version.TryParse(text, out _) ||
        key is "utc" or "startUtc" or "startedUtc" && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ||
        (key is "category" or "kind" or "version" or "ranking" or "unit" or "name") && PublicLabels.Contains(text) ||
        key == "provenance" && EnumText<Provenance>(text) ||
        key == "status" && EnumText<Availability>(text) ||
        key == "view" && EnumText<CpuStackView>(text) ||
        key == "grouping" && EnumText<CpuStackGrouping>(text) ||
        key == "metric" && EnumText<IntervalMetric>(text);
    private static bool EnumText<T>(string text) where T : struct, Enum => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value);
    private static bool RecordedIdentity(string value) => Guid.TryParse(value, out _) || value.Length <= 200 &&
        Regex.IsMatch(value, @"\A(?:[0-9a-fA-F]{16}:[pt]:[0-9]+|[0-9]+:[0-9]+:[0-9]+|(?:method|module):[0-9]+|address:(?:[0-9a-fA-F]{16}:p:[0-9]+|unattributed):[0-9]+|unknown-module:(?:[0-9a-fA-F]{16}:p:[0-9]+|unattributed)|missing-stack|truncated-stack)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string Render(object source, string format, ExportPrivacyOptions? options = null)
    {
        if (format is not ("json" or "markdown")) throw new WoiException("InvalidFormat", "Choose json or markdown export.");
        options ??= new();
        if (options.IncludeFreeText && !options.AllIdentifiers)
            throw new WoiException("InvalidExportPrivacy", "Free text can contain every identifier category. Include all identifier fields before including free text.");
        var data = Document(source); int omitted = 0;
        void Visit(JsonNode? node, string key)
        {
            if (node is JsonObject obj) {
                foreach (var item in obj.ToArray()) {
                    if (item.Value is JsonValue value && value.TryGetValue<string>(out var text)) obj[item.Key] = Clean(text, item.Key);
                    else Visit(item.Value, item.Key);
                }
            } else if (node is JsonArray array) {
                for (int i = 0; i < array.Count; i++) {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Clean(text, key);
                    else Visit(array[i], key);
                }
            }
        }
        string Clean(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || Public(text, key)) return text;
            if (IdentityFields.Contains(key))
                return options.IncludeFreeText || RecordedIdentity(text) ? text : "ref:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..24];
            bool include = Category(key) switch {
                "Names" => options.IncludeNames,
                "Machine metadata" => options.IncludeMachineMetadata,
                "Paths" => options.IncludePaths,
                "Endpoints" => options.IncludeEndpoints,
                "Command lines" => options.IncludeCommandLines,
                _ => options.IncludeFreeText
            };
            if (include) return text;
            omitted++; return "";
        }
        Visit(data, "");
        data["exportPrivacy"] = new JsonObject {
            ["schema"] = 1, ["options"] = JsonSerializer.SerializeToNode(options, Wire.Json), ["omittedStringFields"] = omitted,
            ["notice"] = "Field selection is not anonymization. Source fingerprints, numeric IDs, exact timestamps, measurements and evidence coordinates remain. Opted-in fields are verbatim and may contain other identifiers. Free-form findings and warnings are omitted unless all categories are included; review the original capture quality locally. Raw ETL and full snapshot evidence are excluded."
        };
        var json = data.ToJsonString(new JsonSerializerOptions(Wire.Json) { WriteIndented = true });
        if (format == "json") return json;
        var result = new StringBuilder("# Windows Open Intelligence reviewed report\n\n");
        result.AppendLine("Unknown measurements are unavailable, never assumed zero. Snapshot evidence has no ETLX event index; ETLX coordinates are indexed trace coordinates, not raw ETL offsets.");
        result.AppendLine(data["exportPrivacy"]!["notice"]!.GetValue<string>()).AppendLine();
        var table = data["contributors"] as JsonArray ?? data["investigation"]?["contributors"] as JsonArray ??
            data["comparison"]?["metrics"] as JsonArray ?? data["totals"] as JsonArray;
        if (table is { Count: > 0 } && table[0] is JsonObject first) {
            var columns = first.Where(x => x.Value is not (JsonArray or JsonObject)).Select(x => x.Key).ToArray();
            static string Cell(JsonNode? value) => value == null ? "unavailable" : value.ToString()
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\\", "\\\\")
                .Replace("|", "\\|").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`")
                .Replace("*", "\\*").Replace("_", "\\_").Replace("\r", " ").Replace("\n", " ");
            result.AppendLine("| " + string.Join(" | ", columns) + " |").AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
            foreach (var row in table) result.AppendLine("| " + string.Join(" | ", columns.Select(x => Cell(row?[x]))) + " |");
        }
        // Four backticks keep captured triple-backtick text inside the structured payload.
        result.AppendLine().AppendLine("## Complete reviewed payload").AppendLine().AppendLine("````json").AppendLine(json).AppendLine("````");
        return result.ToString();
    }
    public static void ValidatePath(string path, string format, object source)
    {
        path = InvestigationStore.Local(path);
        var extension = format == "json" ? ".json" : format == "markdown" ? ".md" : throw new WoiException("InvalidFormat", "Choose json or markdown.");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new WoiException("InvalidExportPath", "Report exports must use .json or .md; saved evidence and recordings cannot be overwritten.");
        void Check(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var item in obj) {
                if (item.Key is "sourcePath" or "cacheDirectory" && item.Value is JsonValue value && value.TryGetValue<string>(out var recorded) && !string.IsNullOrWhiteSpace(recorded)) {
                    var full = Path.GetFullPath(recorded);
                    if (path.Equals(full, StringComparison.OrdinalIgnoreCase) || item.Key == "cacheDirectory" && path.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new WoiException("InvalidExportPath", "Choose a report destination outside the original evidence and derived cache.");
                }
                Check(item.Value);
            } else if (node is JsonArray array) foreach (var item in array) Check(item);
        }
        Check(Document(source));
    }
    public static void Write(string path, string format, object source, ExportPrivacyOptions? options = null, CancellationToken token = default)
    {
        path = InvestigationStore.Local(path); ValidatePath(path, format, source);
        var text = Render(source, format, options); token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".woi-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
            }
            for (int attempt = 0; ; attempt++) {
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, true); break; }
                // Give transient Windows reader/scanner locks at most one second; never remove the destination first.
                catch (Exception ex) when (attempt < 20 && (ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xffff) is 5 or 32 or 33) {
                    if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
                }
            }
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
