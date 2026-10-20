using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    private readonly EventFilterPresetStore presetStore;
    private readonly string? analysisCacheRoot;
    private EventQuery? appliedEventQuery;
    private readonly List<long?> pageCursors = [];
    private long? nextEventCursor;
    private int eventPageIndex;
    public ObservableCollection<EventFilterRule> FilterRules { get; } = [];
    public ObservableCollection<EventFilterPreset> FilterPresets { get; } = [];
    public EventFilterField[] FilterFields { get; } = Enum.GetValues<EventFilterField>();
    public EventFilterOperator[] FilterOperators { get; } = Enum.GetValues<EventFilterOperator>();
    public EventFilterAction[] FilterActions { get; } = Enum.GetValues<EventFilterAction>();
    [ObservableProperty] private EventFilterField filterField = EventFilterField.Operation;
    [ObservableProperty] private EventFilterOperator filterOperator = EventFilterOperator.Contains;
    [ObservableProperty] private EventFilterAction filterAction;
    [ObservableProperty] private string filterValue = "";
    [ObservableProperty] private EventFilterRule? selectedFilterRule;
    [ObservableProperty] private EventFilterPreset? selectedFilterPreset;
    [ObservableProperty] private string filterPresetName = "";
    [ObservableProperty] private string filterStoreStatus = "";
    [ObservableProperty] private string eventPageLabel = "Apply filters to show events.";
    [ObservableProperty] private bool hasPreviousEventPage;
    [ObservableProperty] private bool hasNextEventPage;

    public MainViewModel(string? filterPresetPath = null, string? analysisCacheRoot = null)
    {
        this.analysisCacheRoot = analysisCacheRoot;
        presetStore = new(filterPresetPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsOpenIntelligence", "event-filters.json"));
        try { ReloadPresets(); }
        catch (Exception ex) { FilterStoreStatus = ex.Message; }
    }
    private void ReloadPresets(string? name = null)
    {
        var presets = presetStore.Load();
        FilterPresets.Clear(); foreach (var preset in presets) FilterPresets.Add(preset);
        SelectedFilterPreset = FilterPresets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    public Task SaveFilterPreset() => Run(_ => {
        var preset = new EventFilterPreset(FilterPresetName.Trim(), Category, OperationFilter, TargetFilter, FilterRules.ToArray());
        presetStore.Save(preset); ReloadPresets(preset.Name);
        FilterStoreStatus = $"Saved ‘{preset.Name}’. Includes event filters only; interval and selected process are not saved.";
        return Task.CompletedTask;
    });
    public Task DeleteFilterPreset() => Run(_ => {
        if (SelectedFilterPreset is { } preset) { presetStore.Delete(preset.Name); ReloadPresets(); FilterStoreStatus = $"Deleted ‘{preset.Name}’."; }
        return Task.CompletedTask;
    });
    public Task LoadFilterPreset() => Run(async token => {
        if (SelectedFilterPreset is not { } preset) return;
        Category = preset.Category ?? "All categories"; OperationFilter = preset.Operation ?? ""; TargetFilter = preset.Target ?? "";
        FilterRules.Clear(); foreach (var rule in preset.Rules) FilterRules.Add(rule);
        FilterPresetName = preset.Name;
        await ApplyEventFiltersCore(token);
        FilterStoreStatus = $"Loaded ‘{preset.Name}’ in the current interval and selected process scope.";
    });
    public Task AddFilterRule() => Run(_ => {
        var rule = new EventFilterRule(FilterField, FilterOperator, FilterValue, FilterAction);
        EventFilterMatcher.Validate(FilterRules.Append(rule).ToArray());
        if (!FilterRules.Contains(rule)) FilterRules.Add(rule);
        return Task.CompletedTask;
    });
    public void RemoveFilterRule() { if (!Busy && SelectedFilterRule is { } rule) FilterRules.Remove(rule); }
    public Task ClearEventFilters() => Run(async token => {
        Category = "All categories"; OperationFilter = ""; TargetFilter = ""; FilterRules.Clear();
        await ApplyEventFiltersCore(token);
    });
    public Task FilterFromEvent(Observation? row, EventFilterField field, EventFilterAction action) => Run(async token => {
        if (row == null) return;
        var value = field switch { EventFilterField.ProcessName => row.ProcessName, EventFilterField.Category => row.Category,
            EventFilterField.Operation => row.Operation, _ => row.Target };
        var rule = new EventFilterRule(field, EventFilterOperator.Equals, value, action);
        EventFilterMatcher.Validate(FilterRules.Append(rule).ToArray());
        if (!FilterRules.Contains(rule)) FilterRules.Add(rule);
        await ApplyEventFiltersCore(token);
    });
    private EventQuery DraftEventQuery()
    {
        var interval = Interval();
        EventFilterMatcher.Validate(FilterRules);
        return new(interval.StartMs, interval.EndMs, interval.ProcessId,
            string.IsNullOrWhiteSpace(Category) || Category == "All categories" ? null : Category,
            OperationFilter, TargetFilter, Rules: FilterRules.ToArray());
    }
    private async Task ApplyEventFiltersCore(CancellationToken token, EventQuery? capturedQuery = null)
    {
        if (Summary == null) return;
        var query = capturedQuery ?? DraftEventQuery();
        var page = await AnalysisClient.Send<EventPage>("events", query, token);
        token.ThrowIfCancellationRequested();
        PublishFirstEventPage(query, page);
    }
    private void PublishFirstEventPage(EventQuery query, EventPage page)
    {
        appliedEventQuery = query; pageCursors.Clear(); pageCursors.Add(null); eventPageIndex = 0;
        PublishEventPage(page);
    }
    private void PublishEventPage(EventPage page)
    {
        Events.Clear(); foreach (var row in page.Events) Events.Add(row);
        nextEventCursor = page.NextCursor; HasNextEventPage = page.More; HasPreviousEventPage = eventPageIndex > 0;
        var query = appliedEventQuery!;
        var scope = query.ProcessId == null ? "all processes" : Summary?.Processes.FirstOrDefault(x => x.Id == query.ProcessId)?.DisplayLabel ?? query.ProcessId;
        EventPageLabel = $"Page {eventPageIndex + 1} · {page.Events.Count:n0} events · {query.StartMs / 1000:F3}–{query.EndMs / 1000:F3}s · {scope} · {query.Category ?? "all categories"} · {query.Rules?.Count ?? 0} rules" +
            (string.IsNullOrEmpty(query.Operation) ? "" : $" · operation contains ‘{query.Operation}’") +
            (string.IsNullOrEmpty(query.Target) ? "" : $" · target contains ‘{query.Target}’") + (page.More ? "" : " · end");
        Status = "Event filters applied. Rankings and exported resource reports use the interval and process scope only.";
    }
    private void ResetEventNavigation()
    {
        appliedEventQuery = null; pageCursors.Clear(); nextEventCursor = null; eventPageIndex = 0;
        HasNextEventPage = HasPreviousEventPage = false; EventPageLabel = "Apply filters to show events.";
    }
    public Task FirstPage() => Run(token => ApplyEventFiltersCore(token));
    public Task NextPage() => NavigateEventPage(true);
    public Task PreviousPage() => NavigateEventPage(false);
    private Task NavigateEventPage(bool forward) => Run(async token => {
        if (appliedEventQuery == null || (forward ? !HasNextEventPage : !HasPreviousEventPage)) return;
        var destination = eventPageIndex + (forward ? 1 : -1);
        var cursor = forward ? nextEventCursor : pageCursors[destination];
        var page = await AnalysisClient.Send<EventPage>("events", appliedEventQuery with { Cursor = cursor }, token);
        token.ThrowIfCancellationRequested();
        if (destination == pageCursors.Count) pageCursors.Add(cursor);
        eventPageIndex = destination; PublishEventPage(page);
    });
}
