using System.Text.Json;
using System.Text.Json.Serialization;

namespace Woi.Core;

public enum EventFilterField { ProcessName, Category, Operation, Target }
public enum EventFilterOperator { Equals, Contains, StartsWith, EndsWith }
public enum EventFilterAction { Include, Exclude }
public record EventFilterRule([property: JsonRequired] EventFilterField Field, [property: JsonRequired] EventFilterOperator Operator,
    [property: JsonRequired] string Value, [property: JsonRequired] EventFilterAction Action);
public record EventFilterPreset(string Name, string? Category, string? Operation, string? Target, IReadOnlyList<EventFilterRule> Rules);

public sealed class EventFilterMatcher
{
    private readonly EventFilterRule[] excludes;
    private readonly EventFilterRule[][] includes;
    public EventFilterMatcher(IReadOnlyList<EventFilterRule>? rules)
    {
        Validate(rules);
        excludes = (rules ?? []).Where(x => x.Action == EventFilterAction.Exclude).ToArray();
        includes = (rules ?? []).Where(x => x.Action == EventFilterAction.Include).GroupBy(x => x.Field).Select(x => x.ToArray()).ToArray();
    }
    public static void Validate(IReadOnlyList<EventFilterRule>? rules)
    {
        if (rules?.Count > 32 || rules?.Any(x => x == null || !Enum.IsDefined(x.Field) || !Enum.IsDefined(x.Operator) ||
            !Enum.IsDefined(x.Action) || string.IsNullOrWhiteSpace(x.Value) || x.Value.Length > 512) == true)
            throw new WoiException("InvalidFilter", "Use at most 32 rules with supported fields and nonempty values up to 512 characters.");
    }
    public bool Matches(Observation value) => !excludes.Any(x => MatchesRule(value, x)) && includes.All(group => group.Any(x => MatchesRule(value, x)));
    private static bool MatchesRule(Observation value, EventFilterRule rule)
    {
        var text = rule.Field switch { EventFilterField.ProcessName => value.ProcessName, EventFilterField.Category => value.Category,
            EventFilterField.Operation => value.Operation, EventFilterField.Target => value.Target, _ => "" };
        return rule.Operator switch {
            EventFilterOperator.Equals => text.Equals(rule.Value, StringComparison.OrdinalIgnoreCase),
            EventFilterOperator.Contains => text.Contains(rule.Value, StringComparison.OrdinalIgnoreCase),
            EventFilterOperator.StartsWith => text.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase),
            EventFilterOperator.EndsWith => text.EndsWith(rule.Value, StringComparison.OrdinalIgnoreCase), _ => false
        };
    }
    public static bool SameQuery(EventQuery? left, EventQuery right) => left != null &&
        left with { Rules = null, Cursor = null } == right with { Rules = null, Cursor = null } &&
        (left.Rules ?? []).SequenceEqual(right.Rules ?? []);
}

// Presets contain event display filters only; intervals and trace-local lifetime IDs never travel across recordings.
public sealed class EventFilterPresetStore(string path)
{
    private const int MaximumBytes = 1024 * 1024;
    private sealed record Document(int Version, IReadOnlyList<EventFilterPreset> Presets);
    public IReadOnlyList<EventFilterPreset> Load()
    {
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new WoiException("InvalidPresets", "The saved filter file exceeds 1 MiB.");
        Document document;
        try { document = JsonSerializer.Deserialize<Document>(stream, Wire.Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new WoiException("InvalidPresets", "The saved filter file is damaged. It has not been overwritten."); }
        if (document.Version != 1 || document.Presets == null || document.Presets.Count > 32)
            throw new WoiException("InvalidPresets", "Unsupported saved filter file version or preset count.");
        foreach (var preset in document.Presets) Validate(preset);
        if (document.Presets.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Presets.Count)
            throw new WoiException("InvalidPresets", "The saved filter file contains duplicate names.");
        return document.Presets;
    }
    public void Save(EventFilterPreset preset)
    {
        Validate(preset);
        var presets = Load().Where(x => !x.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)).Append(preset).ToArray();
        if (presets.Length > 32) throw new WoiException("InvalidPresets", "Remove a saved filter before adding more than 32 presets.");
        Write(presets);
    }
    public void Delete(string name) => Write(Load().Where(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray());
    private static void Validate(EventFilterPreset preset)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 || preset.Name != preset.Name.Trim() ||
            preset.Rules == null || new[] { preset.Category, preset.Operation, preset.Target }.Any(x => x?.Length > 512))
            throw new WoiException("InvalidPresets", "Use a preset name of 1–80 characters and filter text up to 512 characters.");
        EventFilterMatcher.Validate(preset.Rules);
    }
    private void Write(IReadOnlyList<EventFilterPreset> presets)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, presets), Wire.Json);
        if (bytes.Length > MaximumBytes) throw new WoiException("InvalidPresets", "The saved filters exceed 1 MiB.");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
