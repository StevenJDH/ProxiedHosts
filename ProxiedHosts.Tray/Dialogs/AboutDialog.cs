/*
 * This file is part of ProxiedHosts <https://github.com/StevenJDH/ProxiedHosts>.
 * Copyright (C) 2026 Steven Jenkins De Haro.
 *
 * ProxiedHosts is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProxiedHosts is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProxiedHosts.  If not, see <http://www.gnu.org/licenses/>.
 */

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProxiedHosts.Core.Infrastructure;
using System.Reflection;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;

namespace ProxiedHosts.Tray.Dialogs;

/// <summary>
/// Displays application metadata, repository information, license
/// terms, and a link for voluntary donations.
/// </summary>
internal sealed class AboutDialog : Window
{
    private readonly Bitmap _logo;
    private readonly Action<string>? _logError;

    public AboutDialog(Action<string>? logError = null)
    {
        _logError = logError;

        var assembly = typeof(App).Assembly;
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ProxiedHosts";
        var version = assembly.GetName().Version?.ToString() ?? "Unknown";
        var authors = GetMetadata(assembly, "Authors");
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        var repositoryUrl = GetMetadata(assembly, "RepositoryUrl");
        var donationUrl = GetMetadata(assembly, "DonationUrl");

        Title = $"About {product}";
        Width = 732;
        Height = 530;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        ShowActivated = true;

        using (var stream = AssetLoader.Open(new Uri("avares://proxiedhosts-tray/Assets/ProxiedHosts.ico")))
        {
            Icon = new WindowIcon(stream);
        }

        using (var stream = AssetLoader.Open(new Uri("avares://proxiedhosts-tray/Assets/ProxiedHostsAbout.png")))
        {
            _logo = new Bitmap(stream);
        }

        Closed += (_, _) => _logo.Dispose();

        // Application icon and version information.
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Children =
            {
                new Image
                {
                    Source = _logo,
                    Width = 80,
                    Height = 80,
                    Stretch = Stretch.Uniform
                },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{product} v{version}",
                            FontSize = 22,
                            FontWeight = FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = $"{copyright} {authors}"
                        }
                    }
                }
            }
        };

        string licenseSummary = $"{product} is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.";

        // Licensing summary.
        var summary = new TextBlock
        {
            Text = licenseSummary,
            TextWrapping = TextWrapping.Wrap
        };

        // Clickable repository URL.
        var repositoryLink = new HyperlinkButton
        {
            Content = string.IsNullOrWhiteSpace(repositoryUrl) ? "Repository URL unavailable" : repositoryUrl,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = IsHttpsUrl(repositoryUrl)
        };

        repositoryLink.Click += (_, _) => OpenUrl(repositoryUrl);
        repositoryLink.Padding = new Thickness(0);
        repositoryLink.VerticalAlignment = VerticalAlignment.Center;

        var repositorySection = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "C# source code is available on GitHub:",
                    VerticalAlignment = VerticalAlignment.Center
                },
                repositoryLink
            }
        };

        // Full scrollable GPL license.
        var licenseViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = ReadLicense(assembly),
                FontFamily = new FontFamily("monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12)
            }
        };

        var licenseBorder = new Border
        {
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1, 1, 0, 0),
                Child = new Border
                {
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(1),
                    Child = licenseViewer
                }
            }
        };

        var licenseSection = new GroupBox
        {
            Header = "GNU General Public License",
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Content = licenseBorder
        };

        // Donation and close buttons.
        using var donationStream = AssetLoader.Open(new Uri("avares://proxiedhosts-tray/Assets/donation-button.png"));
        var donationBitmap = new Bitmap(donationStream);

        Closed += (_, _) => donationBitmap.Dispose();

        var donationImage = new Image
        {
            Source = donationBitmap,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        var hoverOverlay = new Border
        {
            Background = Brushes.Black,
            Opacity = 0.50,
            OpacityMask = new ImageBrush(donationBitmap)
            {
                Stretch = Stretch.Fill
            },
            IsVisible = false,
            IsHitTestVisible = false
        };

        var donationText = new TextBlock
        {
            Text = "Donate...",
            FontSize = 14,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var donateButton = new Button
        {
            Content = new Grid
            {
                Children =
                {
                    donationImage,
                    hoverOverlay,
                    donationText
                }
            },
            Width = 112,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsEnabled = IsHttpsUrl(donationUrl)
        };

        // Prevent the Fluent theme from drawing a rectangular background.
        foreach (var state in new[] { ":pointerover", ":pressed" })
        {
            var style = new Style(x => x
                .OfType<Button>()
                .Class(state)
                .Template()
                .OfType<ContentPresenter>()
                .Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                    new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)
                }
            };

            donateButton.Styles.Add(style);
        }

        donateButton.PointerEntered += (_, _) =>
        {
            hoverOverlay.IsVisible = true;
        };

        donateButton.PointerExited += (_, _) =>
        {
            hoverOverlay.IsVisible = false;
        };

        donateButton.Click += (_, _) => OpenUrl(donationUrl);

        var okButton = new Button
        {
            Content = "OK",
            Width = 160,
            Height = 32,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        okButton.Click += (_, _) => Close();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*")
        };

        Grid.SetColumn(donateButton, 0);
        Grid.SetColumn(okButton, 1);

        footer.Children.Add(donateButton);
        footer.Children.Add(okButton);

        // Overall window layout.
        var layout = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            RowSpacing = 14
        };

        Grid.SetRow(header, 0);
        Grid.SetRow(summary, 1);
        Grid.SetRow(repositorySection, 2);
        Grid.SetRow(licenseSection, 3);
        Grid.SetRow(footer, 4);

        layout.Children.Add(header);
        layout.Children.Add(summary);
        layout.Children.Add(repositorySection);
        layout.Children.Add(licenseSection);
        layout.Children.Add(footer);

        Content = layout;
    }

    private static string GetMetadata(Assembly assembly, string key)
    {
        return assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?
            .Value ?? string.Empty;
    }

    private static string ReadLicense(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream("ProxiedHosts.LICENSE")
            ?? throw new InvalidOperationException("The embedded GPL license could not be found.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static bool IsHttpsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }

    private void OpenUrl(string url)
    {
        try
        {
            FileLauncher.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Failed to open URL '{url}': {ex}");
        }
    }
}