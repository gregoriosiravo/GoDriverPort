using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LegionGoConverter.App.Views;

/// <summary>Progress screen. Code-behind only keeps the live log scrolled to the newest line.</summary>
public partial class ProgressView : UserControl
{
    MainViewModel? _vm;
    bool _scrollQueued;

    public ProgressView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.Log.CollectionChanged -= OnLogChanged;
        _vm = e.NewValue as MainViewModel;
        if (_vm is not null) _vm.Log.CollectionChanged += OnLogChanged;
    }

    void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _scrollQueued = false; LogScroll.ScrollToEnd(); });
    }
}
