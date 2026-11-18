using System.Windows;
using System.Windows.Controls;

namespace LegionGoConverter.App.Views;

/// <summary>One segment of the header's step gauge: number, label and a bar, styled Done / Current / Todo.</summary>
public partial class StepSegment : UserControl
{
    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(nameof(Index), typeof(int), typeof(StepSegment),
        new PropertyMetadata(0, (d, _) => ((StepSegment)d).Update()));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StepSegment),
        new PropertyMetadata(""));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(WizardStep), typeof(StepSegment),
        new PropertyMetadata(WizardStep.Select, (d, _) => ((StepSegment)d).Update()));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(string), typeof(StepSegment),
        new PropertyMetadata("Todo"));

    public StepSegment()
    {
        InitializeComponent();
        Update();
    }

    public int Index { get => (int)GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public WizardStep Step { get => (WizardStep)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    /// <summary>"Done", "Current" or "Todo".</summary>
    public string Position { get => (string)GetValue(PositionProperty); private set => SetValue(PositionProperty, value); }

    public string Number => (Index + 1).ToString();

    void Update()
    {
        int step = (int)Step;
        Position = Index < step ? "Done" : Index == step ? "Current" : "Todo";
        if (NumberText is null) return; // before InitializeComponent
        NumberText.Text = Number;
        bool done = Position == "Done", current = Position == "Current";
        NumberText.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        DoneGlyph.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        Badge.SetResourceReference(Border.BackgroundProperty, current || done ? "Brush.Accent" : "Brush.Surface");
        Badge.SetResourceReference(Border.BorderBrushProperty, current || done ? "Brush.Accent" : "Brush.Line");
        NumberText.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.OnAccent" : "Brush.TextMuted");
        DoneGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");
        LabelText.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.Text" : "Brush.TextMuted");
        LabelText.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        Bar.SetResourceReference(Border.BackgroundProperty, current || done ? "Brush.Accent" : "Brush.Line");
        Bar.Opacity = done ? 0.55 : 1;
    }
}
