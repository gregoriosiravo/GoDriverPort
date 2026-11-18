using System.Windows.Controls;

namespace LegionGoConverter.App.Views;

/// <summary>Full-window confirmation sheet for <see cref="PromptRequest"/>. Focus placement is done by the window; each new
/// question starts with its message scrolled to the top.</summary>
public partial class PromptOverlay : UserControl
{
    public PromptOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => MessageScroll.ScrollToTop();
    }
}
