// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace PCButler;

/// <summary>How the butler is feeling, from what the PC is doing.</summary>
public enum ButlerMood
{
    /// <summary>Standing by: breathing, blinking now and then.</summary>
    Ready,
    /// <summary>Nobody has touched the mouse or keyboard for a while: eyes shut, Zzz.</summary>
    Napping,
    /// <summary>The PC runs hot (or flat out): sweat drops and a red face.</summary>
    Hot,
    /// <summary>Files are being dragged over him: arms up, mouth open.</summary>
    Catching,
    /// <summary>He just caught something: a big smile and a little hop.</summary>
    Pleased,
}

/// <summary>
/// The drop zone's little butler, drawn from shapes (no pictures, so he stays sharp at any
/// size) on a 100 x 120 canvas: tailcoat, bow tie, white gloves, slicked hair and a moustache.
/// <see cref="Mood"/> picks the face and the animation.
/// </summary>
public sealed class Butler : Canvas
{
    private static readonly Brush Coat = Freeze(new SolidColorBrush(Color.FromRgb(0x1f, 0x23, 0x30)));
    private static readonly Brush CoatEdge = Freeze(new SolidColorBrush(Color.FromRgb(0x3a, 0x40, 0x52)));
    private static readonly Brush Shirt = Freeze(new SolidColorBrush(Color.FromRgb(0xf6, 0xf7, 0xf9)));
    private static readonly Brush Skin = Freeze(new SolidColorBrush(Color.FromRgb(0xf4, 0xcf, 0xa8)));
    private static readonly Brush HotSkin = Freeze(new SolidColorBrush(Color.FromRgb(0xf7, 0xb1, 0x93)));
    private static readonly Brush Hair = Freeze(new SolidColorBrush(Color.FromRgb(0x2b, 0x24, 0x22)));
    private static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x2b, 0x24, 0x22)));
    private static readonly Brush Tie = Freeze(new SolidColorBrush(Color.FromRgb(0xf4, 0x5a, 0x56)));
    private static readonly Brush Blush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xf4, 0x7a, 0x7a)));
    private static readonly Brush Sweat = Freeze(new SolidColorBrush(Color.FromRgb(0x7c, 0xc8, 0xf5)));

    private readonly ScaleTransform breath = new(1, 1);
    private readonly TranslateTransform hop = new();
    private readonly ScaleTransform eyeScale = new(1, 1);
    private readonly RotateTransform leftArm = new(0, 26, 72);
    private readonly RotateTransform rightArm = new(0, 74, 72);
    private readonly Ellipse face;
    private readonly Ellipse leftCheek, rightCheek;
    private readonly Canvas openEyes = new();
    private readonly Path shutEyes;
    private readonly Path smile, grin, wobble;
    private readonly Ellipse gasp;
    private readonly Canvas sweat = new();
    private readonly Canvas snore = new();
    private readonly DispatcherTimer blinker = new();
    private readonly Random random = new();
    private ButlerMood mood = (ButlerMood)(-1);

    public Butler()
    {
        Width = 100;
        Height = 120;
        ClipToBounds = false;

        // His shadow on the desk.
        Children.Add(At(new Ellipse { Width = 58, Height = 7, Fill = new SolidColorBrush(Color.FromArgb(0x45, 0, 0, 0)) }, 21, 110));

        // Everything that breathes and hops, scaled from his feet.
        var body = new Canvas { Width = 100, Height = 120 };
        body.RenderTransformOrigin = new Point(0.5, 0.95);
        body.RenderTransform = new TransformGroup { Children = { breath, hop } };
        Children.Add(body);

        // Arms behind the coat, so the shoulders hide where they join.
        body.Children.Add(Arm(leftArm, 26));
        body.Children.Add(Arm(rightArm, 74));

        // The tailcoat, the shirt front, its buttons, and the bow tie.
        body.Children.Add(new Path
        {
            Data = Geometry.Parse("M 30,68 Q 50,62 70,68 L 80,108 Q 50,114 20,108 Z"), Fill = Coat, Stroke = CoatEdge, StrokeThickness = 1,
        });
        body.Children.Add(new Path { Data = Geometry.Parse("M 40,67 L 60,67 L 50,98 Z"), Fill = Shirt });
        body.Children.Add(new Path { Data = Geometry.Parse("M 40,67 L 47,67 L 50,98 Z M 60,67 L 53,67 L 50,98 Z"), Fill = CoatEdge, Opacity = 0.55 });
        body.Children.Add(At(Dot(2.6, Ink), 48.7, 84));
        body.Children.Add(At(Dot(2.6, Ink), 48.7, 91));
        body.Children.Add(new Path { Data = Geometry.Parse("M 50,70 L 41,65.5 L 41,74.5 Z M 50,70 L 59,65.5 L 59,74.5 Z"), Fill = Tie });
        body.Children.Add(At(Dot(4, Tie), 48, 68));

        // The head: face, ears, hair, eyes, brows, cheeks, moustache and the mouths he can make.
        var head = new Canvas { Width = 100, Height = 120 };
        body.Children.Add(head);
        head.Children.Add(At(new Ellipse { Width = 8, Height = 10, Fill = Skin }, 25, 41));
        head.Children.Add(At(new Ellipse { Width = 8, Height = 10, Fill = Skin }, 67, 41));
        face = new Ellipse { Width = 44, Height = 46, Fill = Skin };
        head.Children.Add(At(face, 28, 22));
        head.Children.Add(new Path { Data = Geometry.Parse("M 28.5,42 Q 27,22 50,21 Q 73,22 71.5,42 Q 68,31 58,29 Q 46,27 40,33 Q 34,30 28.5,42 Z"), Fill = Hair });
        head.Children.Add(new Path { Data = Geometry.Parse("M 38,39 Q 42,36.5 46,39 M 54,39 Q 58,36.5 62,39"), Stroke = Ink, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        openEyes.RenderTransformOrigin = new Point(0, 0);
        openEyes.RenderTransform = eyeScale;
        openEyes.Children.Add(At(new Ellipse { Width = 5, Height = 6.5, Fill = Ink }, 39.5, 42));
        openEyes.Children.Add(At(new Ellipse { Width = 5, Height = 6.5, Fill = Ink }, 55.5, 42));
        openEyes.Children.Add(At(Dot(1.6, Shirt), 41.6, 43));
        openEyes.Children.Add(At(Dot(1.6, Shirt), 57.6, 43));
        // Blinking squeezes the eyes towards their middle line.
        eyeScale.CenterY = 45.25;
        head.Children.Add(openEyes);
        shutEyes = new Path
        {
            Data = Geometry.Parse("M 38.5,45 Q 42,47.5 45.5,45 M 54.5,45 Q 58,47.5 61.5,45"), Stroke = Ink, StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed,
        };
        head.Children.Add(shutEyes);
        leftCheek = new Ellipse { Width = 7, Height = 4, Fill = Blush, Opacity = 0.45 };
        rightCheek = new Ellipse { Width = 7, Height = 4, Fill = Blush, Opacity = 0.45 };
        head.Children.Add(At(leftCheek, 33, 50));
        head.Children.Add(At(rightCheek, 60, 50));
        head.Children.Add(new Path { Data = Geometry.Parse("M 48.5,47 Q 50,51 51.5,47"), Stroke = Ink, StrokeThickness = 1, Opacity = 0.5 });
        smile = Mouth("M 45,57.5 Q 50,60.5 55,57.5");
        grin = new Path { Data = Geometry.Parse("M 43.5,56.5 Q 50,64 56.5,56.5 Z"), Fill = Ink, Visibility = Visibility.Collapsed };
        wobble = Mouth("M 44.5,58.5 Q 47,56.5 49.5,58.5 Q 52,60.5 55.5,58");
        gasp = new Ellipse { Width = 5.5, Height = 6.5, Fill = Ink, Visibility = Visibility.Collapsed };
        head.Children.Add(smile);
        head.Children.Add(grin);
        head.Children.Add(wobble);
        head.Children.Add(At(gasp, 47.25, 55.5));
        // The moustache sits over the top of the mouth.
        head.Children.Add(new Path
        {
            Data = Geometry.Parse("M 50,52.5 Q 45,50.5 41.5,53 Q 38.5,55 36.5,53 Q 38,57.5 43,56 Q 47.5,55 50,54 Q 52.5,55 57,56 Q 62,57.5 63.5,53 Q 61.5,55 58.5,53 Q 55,50.5 50,52.5 Z"),
            Fill = Hair,
        });

        // Sweat drops by the temples, and the Zzz of a nap: drawn always, shown when the mood calls for them.
        sweat.Children.Add(At(SweatDrop(), 70, 28));
        sweat.Children.Add(At(SweatDrop(), 25, 33));
        sweat.Children.Add(At(SweatDrop(), 64, 22));
        sweat.Visibility = Visibility.Collapsed;
        head.Children.Add(sweat);
        foreach (var (size, x, y) in new[] { (11.0, 70.0, 20.0), (14.0, 78.0, 10.0), (17.0, 86.0, -2.0) })
            snore.Children.Add(At(new TextBlock { Text = "z", FontSize = size, FontWeight = FontWeights.Bold, Foreground = Sweat, Opacity = 0 }, x, y));
        snore.Visibility = Visibility.Collapsed;
        Children.Add(snore);

        blinker.Tick += (_, _) => Blink();
        Loaded += (_, _) =>
        {
            if (mood == (ButlerMood)(-1))
                Mood = ButlerMood.Ready;
        };
        Unloaded += (_, _) => blinker.Stop();
    }

    /// <summary>What he looks like and does now. Setting the same mood again changes nothing.</summary>
    public ButlerMood Mood
    {
        get => mood == (ButlerMood)(-1) ? ButlerMood.Ready : mood;
        set
        {
            if (value == mood)
                return;
            mood = value;
            Show();
        }
    }

    private void Show()
    {
        var napping = mood == ButlerMood.Napping;
        var hot = mood == ButlerMood.Hot;
        var catching = mood == ButlerMood.Catching;
        var pleased = mood == ButlerMood.Pleased;

        openEyes.Visibility = napping ? Visibility.Collapsed : Visibility.Visible;
        shutEyes.Visibility = napping ? Visibility.Visible : Visibility.Collapsed;
        smile.Visibility = mood is ButlerMood.Ready or ButlerMood.Napping ? Visibility.Visible : Visibility.Collapsed;
        grin.Visibility = pleased ? Visibility.Visible : Visibility.Collapsed;
        wobble.Visibility = hot ? Visibility.Visible : Visibility.Collapsed;
        gasp.Visibility = catching ? Visibility.Visible : Visibility.Collapsed;
        face.Fill = hot ? HotSkin : Skin;
        leftCheek.Opacity = rightCheek.Opacity = hot ? 1 : pleased ? 0.8 : 0.45;
        sweat.Visibility = hot ? Visibility.Visible : Visibility.Collapsed;
        snore.Visibility = napping ? Visibility.Visible : Visibility.Collapsed;

        // Breathing: slow and deep asleep, quick and shallow when hot, calm otherwise.
        var (depth, seconds) = napping ? (1.035, 2.6) : hot ? (1.018, 0.7) : (1.015, 1.7);
        breath.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, depth, seconds));
        breath.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1 + (depth - 1) / 3, seconds));

        // Arms: up to catch, a little wave when pleased, down otherwise.
        Raise(leftArm, catching ? 150 : pleased ? 35 : 0);
        Raise(rightArm, catching ? -150 : pleased ? -35 : 0);
        eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        eyeScale.ScaleY = catching ? 1.25 : 1;

        hop.BeginAnimation(TranslateTransform.YProperty, pleased
            ? new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = new QuadraticEase() }
            : null);

        Animate(sweat, hot, index => new DoubleAnimation(0, 13, TimeSpan.FromSeconds(1.1)) { BeginTime = TimeSpan.FromSeconds(index * 0.37), RepeatBehavior = RepeatBehavior.Forever },
                index => new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = TimeSpan.FromSeconds(index * 0.37), RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.1),
                    KeyFrames = { new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)), new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)), new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)) },
                });
        Animate(snore, napping, index => new DoubleAnimation(0, -16, TimeSpan.FromSeconds(2.4)) { BeginTime = TimeSpan.FromSeconds(index * 0.8), RepeatBehavior = RepeatBehavior.Forever },
                index => new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = TimeSpan.FromSeconds(index * 0.8), RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.4),
                    KeyFrames = { new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)), new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3)), new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)) },
                });

        // He blinks only with his eyes open and not mid-catch.
        if (mood is ButlerMood.Ready or ButlerMood.Hot or ButlerMood.Pleased)
        {
            blinker.Interval = TimeSpan.FromSeconds(2.5 + random.NextDouble() * 3);
            blinker.Start();
        }
        else
            blinker.Stop();
    }

    private void Blink()
    {
        blinker.Interval = TimeSpan.FromSeconds(2.5 + random.NextDouble() * 3.5);
        eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(70)) { AutoReverse = true });
    }

    /// <summary>Start (or stop) the drift-and-fade animation of each child of a group, one after another.</summary>
    private static void Animate(Canvas group, bool on, Func<int, AnimationTimeline> move, Func<int, AnimationTimeline> fade)
    {
        for (var index = 0; index < group.Children.Count; index++)
        {
            var child = (UIElement)group.Children[index];
            if (child.RenderTransform is not TranslateTransform shift)
                child.RenderTransform = shift = new TranslateTransform();
            shift.BeginAnimation(TranslateTransform.YProperty, on ? move(index) : null);
            if (group.Children[index] is TextBlock)
                shift.BeginAnimation(TranslateTransform.XProperty, on ? new DoubleAnimation(0, 7, TimeSpan.FromSeconds(2.4)) { BeginTime = TimeSpan.FromSeconds(index * 0.8), RepeatBehavior = RepeatBehavior.Forever } : null);
            child.BeginAnimation(OpacityProperty, on ? fade(index) : null);
        }
    }

    private static void Raise(RotateTransform arm, double angle) =>
        arm.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.4 } });

    private static DoubleAnimation Loop(double from, double to, double seconds) => new(from, to, TimeSpan.FromSeconds(seconds))
    {
        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    /// <summary>A sleeve hanging from the shoulder at x, with a white glove at its end; it turns about the shoulder.</summary>
    private static Canvas Arm(RotateTransform turn, double x)
    {
        var arm = new Canvas { RenderTransform = turn };
        arm.Children.Add(At(new Rectangle { Width = 9, Height = 30, RadiusX = 4.5, RadiusY = 4.5, Fill = Coat, Stroke = CoatEdge, StrokeThickness = 1 }, x - 4.5, 70));
        arm.Children.Add(At(new Ellipse { Width = 11, Height = 11, Fill = Shirt, Stroke = CoatEdge, StrokeThickness = 0.8 }, x - 5.5, 96));
        return arm;
    }

    private static Path Mouth(string data) => new()
    {
        Data = Geometry.Parse(data), Stroke = Ink, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    private static Path SweatDrop() => new() { Data = Geometry.Parse("M 3,0 Q 6,5 6,7 A 3,3 0 0 1 0,7 Q 0,5 3,0 Z"), Fill = Sweat, Opacity = 0 };

    private static Ellipse Dot(double size, Brush fill) => new() { Width = size, Height = size, Fill = fill };

    private static T At<T>(T element, double x, double y) where T : UIElement
    {
        SetLeft(element, x);
        SetTop(element, y);
        return element;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
