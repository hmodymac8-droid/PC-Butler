// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCButler.Core;
using QRCoder;

namespace PCButler;

/// <summary>
/// Get the phone app without the Play Store: a QR code to scan with the phone's camera and the
/// same link to copy or open. The APK is committed to the repository at a fixed path
/// (dist/android/PCButler.apk), so this link always gives the latest build.
/// </summary>
public sealed class PhoneAppWindow : Window
{
    public static string ApkLink => $"https://github.com/{AppInfo.Repo}/raw/main/dist/android/PCButler.apk";

    public PhoneAppWindow()
    {
        Ui.StyleWindow(this);
        Title = Ui.T("Get the phone app");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(24), MaxWidth = 420 };
        body.Children.Add(Ui.Text(Ui.T("PC Butler for Android"), 18, weight: FontWeights.Bold));
        body.Children.Add(Ui.Muted(Ui.T("Scan the code with your phone's camera to download the app. "
                                        + "When it has downloaded, open it and allow installing from this source."), 12.5, wrap: true)
                            .Margin(0, 6, 0, 16));

        // White behind the code whatever the theme, so every camera reads it.
        var code = new Image { Source = QrImage(ApkLink), Width = 240, Height = 240 };
        RenderOptions.SetBitmapScalingMode(code, BitmapScalingMode.NearestNeighbor);
        body.Children.Add(new Border
        {
            Background = Brushes.White, Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center, Child = code,
        });

        var link = new TextBox
        {
            Text = ApkLink, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 * Ui.TextScale,
            Margin = new Thickness(0, 16, 0, 10), FlowDirection = FlowDirection.LeftToRight,
        };
        body.Children.Add(link);
        var copied = Ui.Muted("", 12);
        body.Children.Add(Ui.Row(6,
            Ui.Button(Ui.T("Copy link"), () =>
            {
                try
                {
                    Clipboard.SetText(ApkLink);
                    copied.Text = Ui.T("Copied.");
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    copied.Text = Ui.T("The clipboard is busy. Try again.");
                }
            }, accent: true),
            Ui.Button(Ui.T("Open in browser"), () => Shell.Open(ApkLink)),
            copied));
        Content = body;
    }

    private static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
