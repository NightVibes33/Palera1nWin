using System.Collections.Specialized;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _operationalDeferredHooksWired;

    private void WireOperationalDeferredHooks()
    {
        if (_operationalDeferredHooksWired) return;
        _operationalDeferredHooksWired = true;

        // The navigation service reuses this page. Keep one dashboard and one set
        // of monitors alive instead of tearing it down and inserting duplicates.
        Unloaded -= Operational_Unloaded;
        _firmwareItems.CollectionChanged += OperationalFirmwareItems_CollectionChanged;
    }

    private void OperationalFirmwareItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => _ = LoadKnownProfileAsync(_monitor.Current));
}
