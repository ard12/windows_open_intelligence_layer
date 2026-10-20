using System.Windows;

namespace Woi.Desktop;

public partial class MainWindow
{
    private async void ProcessFamilyClicked(object sender, RoutedEventArgs e)
    {
        if (model == null || model.Busy) return;
        await model.OpenProcessFamily(model.SelectedProcess?.Id);
        Views.SelectedItem = ProcessHistoryTab;
    }
    private async void HistoryRefreshClicked(object sender, RoutedEventArgs e) => await model!.LoadProcessHistory();
    private async void HistoryFocusClicked(object sender, RoutedEventArgs e) => await model!.FocusHistoryFamily();
    private async void HistoryParentClicked(object sender, RoutedEventArgs e) => await model!.HistoryParent();
    private async void HistoryAllClicked(object sender, RoutedEventArgs e) => await model!.AllHistoryFamilies();
    private async void HistoryPreviousClicked(object sender, RoutedEventArgs e) => await model!.HistoryPage(false);
    private async void HistoryNextClicked(object sender, RoutedEventArgs e) => await model!.HistoryPage(true);
    private async void HistoryExploreClicked(object sender, RoutedEventArgs e)
    {
        if (model?.SelectedHistoryRow == null || model.Busy) return;
        await model.ExploreHistoryProcess();
        Views.SelectedIndex = 1;
    }
    private async void HistoryEvidenceClicked(object sender, RoutedEventArgs e)
    {
        if (model?.SelectedHistoryRow == null || model.Busy) return;
        await model.ShowEntity(model.SelectedHistoryRow.Row.Entity);
        Views.SelectedIndex = 2;
    }
}
