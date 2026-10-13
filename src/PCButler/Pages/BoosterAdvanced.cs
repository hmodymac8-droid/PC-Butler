// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using PCButler.Core;

namespace PCButler;

/// <summary>
/// The Booster's Advanced tab: lasting changes to Windows for the whole PC, in presets from
/// low to extreme or a choice of your own, and one button that puts everything back.
/// </summary>
public sealed class BoosterAdvanced : DockPanel
{
    private static readonly (string Key, string Label, string Detail)[] Presets =
    [
        ("low", "Low", "Safe on every PC: nothing you would miss is switched off."),
        ("medium", "Medium", "Recommended. Adds the fastest power plan, solid windows and no telemetry."),
        ("high", "High", "Adds no animations, and holds Windows' own background work back."),
        ("extreme", "Extreme", "Everything in the list, including search indexing. For gaming, or an old, slow PC."),
        ("custom", "Custom", "Your own choice from the list below."),
    ];

    private readonly Dictionary<string, RadioButton> levels = [];
    private readonly Dictionary<string, CheckBox> switches = [];
    private readonly TextBlock levelDetail = Ui.Muted("", 12, wrap: true);
    private readonly TextBlock status = Ui.Muted("", 12);
    private readonly Button apply;
    private readonly Button undo;
    private bool showing; // set while the switches are set from a preset, so that is not taken as a click

    public BoosterAdvanced()
    {
        var tweaks = TuneUp.Catalogue.Where(tweak => tweak.Group == "tune").ToList();

        apply = Ui.Button(Ui.T("Apply"), Apply, accent: true);
        apply.MinWidth = 140;
        undo = Ui.Button(Ui.T("Undo all changes"), Undo);
        var bar = new DockPanel();
        bar.Children.Add(apply.Dock(Dock.Left));
        bar.Children.Add(undo.Margin(8, 0, 0, 0).Dock(Dock.Left));
        var note = Ui.Muted(Ui.T("Every change is written down first, so Undo puts each one back. A few show only after you sign out or restart."), 12, wrap: true);
        note.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(note.Margin(14, 0, 0, 0));
        Children.Add(new Border
        {
            BorderBrush = Ui.Paint("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 14, 24, 10), Child = bar,
        }.Dock(Dock.Bottom));

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var heading = new DockPanel { Margin = new Thickness(24, 8, 24, 4) };
        heading.Children.Add(new Border
        {
            Background = Ui.Paint("Secondary"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 5),
            VerticalAlignment = VerticalAlignment.Center, Child = status,
        }.Dock(Dock.Right));
        heading.Children.Add(Ui.Text(Ui.T("A lighter Windows, for the whole PC."), 15, weight: FontWeights.Bold));
        body.Children.Add(heading);
        body.Children.Add(Ui.Muted(Ui.T("Boost speeds one program up for a while. These changes stay, and make Windows itself do less in the "
                                        + "background. They help most on older or busy PCs; on a fast one the difference is small."), 12, wrap: true)
                            .Margin(24, 0, 24, 12));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 24, 6) };
        foreach (var (key, label, _) in Presets)
        {
            var button = new RadioButton { Content = Ui.T(label), GroupName = "tunelevel", Margin = new Thickness(0, 0, 22, 0), FontWeight = FontWeights.SemiBold };
            button.Checked += (_, _) => ChooseLevel(key);
            levels[key] = button;
            row.Children.Add(button);
        }
        body.Children.Add(row);
        body.Children.Add(levelDetail.Margin(24, 0, 24, 10));

        var first = true;
        foreach (var tweak in tweaks)
        {
            var toggle = Ui.Switch("", false, _ =>
            {
                if (!showing)
                    levels["custom"].IsChecked = true;
            });
            switches[tweak.Key] = toggle;
            var title = Ui.T(tweak.Title) + "  ·  " + Ui.T(Presets[tweak.Level - 1].Label) + (tweak.Admin ? "  ·  " + Ui.T("needs administrator rights") : "");
            body.Children.Add(Ui.SettingRow(title, Ui.T(tweak.Detail), toggle, first));
            first = false;
        }
        Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ShowState(selectToo: true);
    }

    private string Level => levels.FirstOrDefault(pair => pair.Value.IsChecked == true).Key ?? "custom";

    private void ChooseLevel(string key)
    {
        levelDetail.Text = Ui.T(Presets.First(preset => preset.Key == key).Detail);
        if (key == "custom")
            return;
        var on = TuneUp.ForLevel(key);
        showing = true;
        foreach (var (tweak, toggle) in switches)
            toggle.IsChecked = on.Contains(tweak);
        showing = false;
    }

    /// <summary>The pill and the Undo button; with selectToo, also the preset and switches as they were applied.</summary>
    private void ShowState(bool selectToo = false)
    {
        var state = TuneUp.Load();
        var applied = state.Applied.Keys.Where(switches.ContainsKey).ToList();
        status.Foreground = Ui.Paint(applied.Count > 0 ? "Primary" : "Muted");
        status.Text = applied.Count == 0
            ? Ui.T("Nothing applied")
            : Ui.T("{0}: {1} change(s) in force", Ui.T(Presets.FirstOrDefault(preset => preset.Key == state.Level).Label ?? "Custom"), applied.Count);
        undo.IsEnabled = applied.Count > 0;
        if (!selectToo)
            return;
        if (applied.Count == 0)
        {
            levels["medium"].IsChecked = true;
            return;
        }
        levels[levels.ContainsKey(state.Level) ? state.Level : "custom"].IsChecked = true;
        showing = true;
        foreach (var (tweak, toggle) in switches)
            toggle.IsChecked = applied.Contains(tweak);
        showing = false;
    }

    private async void Apply()
    {
        var keys = switches.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        if (keys.Count == 0)
        {
            Ui.Info("Booster", Ui.T("Nothing is switched on in the list. To put everything back, press Undo all changes."));
            return;
        }
        var level = Level;
        var needsAdmin = TuneUp.Catalogue.Any(tweak => tweak.Admin && keys.Contains(tweak.Key))
                         || TuneUp.Load().Applied.Keys.Any(key => TuneUp.Catalogue.Any(tweak => tweak.Key == key && tweak.Admin && tweak.Group == "tune"));
        if (level == "extreme" && !Ui.Ask("Booster", Ui.T("Extreme switches off search indexing and error reporting as well. Searching for files "
                                                          + "becomes slower.\n\nApply it? Undo all changes puts everything back."), warning: true))
            return;
        if (needsAdmin)
            await Admin.Ensure("Booster", Ui.T("Some of these changes need administrator rights. Without them, the rest is still applied."));
        apply.IsEnabled = undo.IsEnabled = false;
        var result = await Task.Run(() => TuneUp.Apply(keys, level));
        apply.IsEnabled = true;
        ShowState();
        var message = Ui.T("{0} change(s) applied.", result.Changed);
        if (result.NeedsHelper.Count > 0)
            message += "\n\n" + Ui.T("{0} need administrator rights and were left as they are.", result.NeedsHelper.Count);
        Ui.Info("Booster", message);
    }

    private async void Undo()
    {
        var applied = TuneUp.Load().Applied.Keys.ToList();
        if (TuneUp.Catalogue.Any(tweak => tweak.Admin && tweak.Group == "tune" && applied.Contains(tweak.Key)))
            await Admin.Ensure("Booster", Ui.T("Some of the changes were made with administrator rights, and putting them back needs them again."));
        apply.IsEnabled = undo.IsEnabled = false;
        var result = await Task.Run(() => TuneUp.Undo());
        apply.IsEnabled = true;
        ShowState();
        Ui.Info("Booster", result.NeedsHelper.Count > 0
            ? Ui.T("{0} change(s) put back. {1} need administrator rights and are still in force; press Undo all changes again and allow the helper.",
                   result.Changed, result.NeedsHelper.Count)
            : Ui.T("{0} change(s) put back. Windows is as it was before.", result.Changed));
    }
}
