using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private void ReviewAndExport(object source, string name, bool jsonOnly = false)
    {
        try {
            var review = new ExportReviewWindow(source, jsonOnly) { Owner = this };
            if (review.ShowDialog() != true) return;
            var format = review.Format;
            var dialog = new SaveFileDialog { Filter = format == "json" ? "JSON report (*.json)|*.json" : "Markdown report (*.md)|*.md",
                FileName = name + (format == "json" ? ".json" : ".md"), AddExtension = true, DefaultExt = format == "json" ? ".json" : ".md" };
            if (dialog.ShowDialog(this) != true) return;
            ReportExport.Write(dialog.FileName, format, source, review.Options);
            model!.Status = "Reviewed report saved locally. Original evidence is unchanged.";
        } catch (Exception ex) { model!.Error = ex.Message; }
    }
    private void ExportCpuStacksClicked(object sender, RoutedEventArgs e)
    {
        if (model?.Busy == false && model.CpuStacks is { } page) ReviewAndExport(page, "cpu-stacks", jsonOnly: true);
    }
    private Task VerifyExportReview(object? source, string snapshot, string name)
    {
        if (source == null) throw new InvalidOperationException("Missing export review source: " + name);
        static IEnumerable<T> Controls<T>(DependencyObject parent) where T : DependencyObject {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {
                if (child is T found) yield return found;
                foreach (var nested in Controls<T>(child)) yield return nested;
            }
        }
        var destination = Path.ChangeExtension(snapshot, name + ".reviewed.json");
        var cancel = new ExportReviewWindow(source) { Owner = this };
        cancel.Loaded += (_, _) => cancel.DialogResult = false;
        if (cancel.ShowDialog() == true) throw new InvalidOperationException("Cancel accepted an export.");
        Exception? failure = null;
        var jsonOnly = source is CpuStackPage or IntervalComparisonPage;
        var review = new ExportReviewWindow(source, jsonOnly) { Owner = this };
        review.Loaded += async (_, _) => {
            try {
                var fields = Controls<CheckBox>(review).ToArray();
                if (fields.Length != 6 || fields.Any(x => x.IsChecked == true) || fields[5].IsEnabled)
                    throw new InvalidOperationException("Export review defaults include identifying fields.");
                var preview = Controls<TextBox>(review).Single();
                void ExactPreview() {
                    var text = review.ReviewedText;
                    if (preview.Text != text[..Math.Min(65536, text.Length)]) throw new InvalidOperationException("Review preview differs from the exported payload.");
                }
                ExactPreview();
                foreach (var field in fields) field.IsChecked = true;
                if (!review.Options.IncludeFreeText) throw new InvalidOperationException("Explicit full export was not selectable.");
                ExactPreview();
                fields[0].IsChecked = false;
                if (fields[5].IsEnabled || review.Options.IncludeFreeText) throw new InvalidOperationException("Free text bypassed an excluded identifier category.");
                foreach (var field in fields) field.IsChecked = false;
                var formats = Controls<ComboBox>(review).Single();
                if (formats.Items.Count != (jsonOnly ? 1 : 2)) throw new InvalidOperationException("Unexpected export formats.");
                if (!jsonOnly) { formats.SelectedItem = "markdown"; ExactPreview(); formats.SelectedItem = "json"; }
                ExactPreview();
                await Task.Delay(100); review.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)review.ActualWidth, (int)review.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(review); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.ChangeExtension(snapshot, name + ".review.png"))) encoder.Save(stream);
                Controls<Button>(review).Single(x => Equals(x.Content, "Choose destination")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } catch (Exception ex) { failure = ex; review.DialogResult = false; }
        };
        var accepted = review.ShowDialog();
        if (failure != null) throw failure;
        if (accepted != true) throw new InvalidOperationException("Export review did not accept the selected fields.");
        ReportExport.Write(destination, review.Format, source, review.Options);
        if (File.ReadAllText(destination) != review.ReviewedText) throw new InvalidOperationException("Saved report differs from review.");
        return Task.CompletedTask;
    }
}
