using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Woi.Core;

namespace Woi.Desktop;

public sealed class ExportReviewWindow : Window
{
    private readonly object source;
    private readonly CheckBox[] fields;
    private readonly ComboBox format;
    private readonly TextBox preview;
    private readonly TextBlock details;
    public ExportPrivacyOptions Options => new(fields[0].IsChecked == true, fields[1].IsChecked == true,
        fields[2].IsChecked == true, fields[3].IsChecked == true, fields[4].IsChecked == true, fields[5].IsChecked == true);
    public string Format => (string?)format.SelectedItem ?? "json";
    public string ReviewedText => ReportExport.Render(source, Format, Options);
    public ExportReviewWindow(object source, bool jsonOnly = false)
    {
        this.source = source;
        Title = "Review report before export"; Width = 900; Height = 740; MinWidth = 650; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Brushes.White;
        var grid = new Grid { Margin = new Thickness(18) }; Content = grid;
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new() { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new StackPanel(); grid.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Choose which fields this report includes", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Safe defaults omit identifying text. Raw recordings and full snapshot evidence are never attached to reports. Saving .woi evidence is a separate operation.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        var choices = new WrapPanel(); header.Children.Add(choices);
        string[] labels = ["Process / symbol names", "Machine name / OS", "Local paths", "Endpoint fields", "Command-line fields", "Free text / notes / findings"];
        var review = ReportExport.Review(source);
        fields = labels.Select((label, i) => new CheckBox { Content = $"{label} ({review[i].Fields:N0} fields)", Margin = new(0, 4, 16, 4), IsChecked = false }).ToArray();
        foreach (var field in fields) { choices.Children.Add(field); field.Checked += Changed; field.Unchecked += Changed; }
        var settings = new StackPanel { Margin = new(0, 8, 0, 8) }; Grid.SetRow(settings, 1); grid.Children.Add(settings);
        format = new ComboBox { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = jsonOnly ? new[] { "json" } : new[] { "json", "markdown" }, SelectedIndex = 0 };
        AutomationProperties.SetName(format, "Report format"); settings.Children.Add(format);
        settings.Children.Add(new TextBlock { Text = "Free text may contain every identifier category, so it requires all five identifier choices. Opted-in fields are verbatim: field selection does not detect secrets hidden in other fields. Fingerprints, numeric IDs and exact timestamps remain; this is not anonymization.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) }; settings.Children.Add(details);
        preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(preview, "Exact report preview"); Grid.SetRow(preview, 2); grid.Children.Add(preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new(14, 6, 14, 6), Margin = new(0, 0, 8, 0) };
        var save = new Button { Content = "Choose destination", IsDefault = true, Padding = new(14, 6, 14, 6) };
        save.Click += (_, _) => DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(save);
        format.SelectionChanged += Changed; RefreshPreview();
    }
    private void Changed(object sender, RoutedEventArgs e) => RefreshPreview();
    private void RefreshPreview()
    {
        fields[5].IsEnabled = fields.Take(5).All(x => x.IsChecked == true);
        if (!fields[5].IsEnabled) fields[5].IsChecked = false;
        var text = ReviewedText;
        const int maximumPreview = 65536;
        preview.Text = text.Length <= maximumPreview ? text : text[..maximumPreview];
        details.Text = text.Length <= maximumPreview ? $"Complete preview · {text.Length:N0} characters · exported as shown." :
            $"Preview shows the first {maximumPreview:N0} of {text.Length:N0} characters. The complete reviewed payload will be written; omissions apply throughout it.";
    }
}
