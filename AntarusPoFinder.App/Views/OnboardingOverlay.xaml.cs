using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace AntarusPoFinder.App.Views;

/// <summary>One stop of the tour. ResolveTarget is lazy (not a fixed FrameworkElement) because a
/// step may need to navigate to a different page first (e.g. Поиск's search box only exists once
/// SearchView has actually been created/shown) — the resolver does that navigation itself, then
/// returns the now-real element. Return null to make the overlay skip straight to the next step
/// (e.g. a page hidden for the current role, or an element that's conditionally absent).</summary>
public record OnboardingStep(Func<FrameworkElement?> ResolveTarget, string Title, string Body);

/// <summary>Interactive first-launch walkthrough — a dimmed full-window overlay with a highlight
/// box drawn around the current step's real UI element (via TranslatePoint, not a screenshot) and
/// a callout with the explanation. Deliberately not a static PDF/instruction — see the naladchik/
/// programmer instruction PDFs in docs/ for that; this is for a first-run "what am I looking at".</summary>
public partial class OnboardingOverlay : UserControl
{
    public event EventHandler? Finished;

    private readonly List<OnboardingStep> _steps;
    private int _index;
    private FrameworkElement? _currentTarget;

    public OnboardingOverlay(List<OnboardingStep> steps)
    {
        InitializeComponent();
        _steps = steps;
        Loaded += (_, _) => ShowStep(0);
        // Re-translate against the already-resolved target on resize — must NOT re-invoke
        // ResolveTarget here, since resolvers with navigation side effects (Navigate("upload"))
        // would otherwise re-fire on every window resize while this step is showing.
        SizeChanged += (_, _) => { if (_currentTarget is not null) PositionAgainst(_currentTarget); };
    }

    private void ShowStep(int index)
    {
        _index = index;
        var step = _steps[index];
        _currentTarget = step.ResolveTarget();

        if (_currentTarget is null || !_currentTarget.IsVisible || _currentTarget.ActualWidth == 0)
        {
            // Target isn't reachable for this role/state (e.g. a page hidden for a naladchik, or
            // an element that's conditionally absent) — skip straight past it rather than
            // highlighting an invisible rectangle at (0,0).
            if (_index + 1 < _steps.Count) ShowStep(_index + 1);
            else Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        StepCounterText.Text = $"Шаг {index + 1} из {_steps.Count}";
        TitleText.Text = step.Title;
        BodyText.Text = step.Body;
        NextButton.Content = index == _steps.Count - 1 ? "Готово" : "Далее";
        PositionAgainst(_currentTarget);
    }

    private void PositionAgainst(FrameworkElement target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), this);
        var size = target.RenderSize;
        HighlightBorder.Margin = new Thickness(topLeft.X - 4, topLeft.Y - 4, 0, 0);
        HighlightBorder.Width = size.Width + 8;
        HighlightBorder.Height = size.Height + 8;

        var calloutX = topLeft.X + size.Width + 16;
        var calloutY = Math.Max(8, topLeft.Y);
        if (calloutX + CalloutBorder.Width + 8 > ActualWidth)
            calloutX = Math.Max(8, topLeft.X - CalloutBorder.Width - 16);
        if (calloutY + 220 > ActualHeight)
            calloutY = Math.Max(8, ActualHeight - 220);
        CalloutBorder.Margin = new Thickness(calloutX, calloutY, 0, 0);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index + 1 >= _steps.Count) { Finished?.Invoke(this, EventArgs.Empty); return; }
        ShowStep(_index + 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Finished?.Invoke(this, EventArgs.Empty);
}
