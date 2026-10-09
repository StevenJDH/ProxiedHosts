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
using Avalonia.Layout;
using Avalonia.Media;
using ProxiedHosts.Core.Configuration;

namespace ProxiedHosts.Tray.Dialogs;

/// <summary>
/// Displays update-check results with an acknowledgment button
/// or an optional confirmation action.
/// </summary>
internal sealed class UpdateDialog : Window
{
    public UpdateDialog(string message, Action? onYes = null)
    {
        Title = $"{ProxyConfiguration.Instance.ApplicationName} - Check for updates";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowActivated = true;
        ShowInTaskbar = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (onYes is not null)
        {
            var noButton = new Button
            {
                Content = "No",
                MinWidth = 80
            };

            noButton.Click += (_, _) => Close();

            var yesButton = new Button
            {
                Content = "Yes",
                MinWidth = 80
            };

            yesButton.Click += (_, _) =>
            {
                Close();
                onYes();
            };

            buttons.Children.Add(noButton);
            buttons.Children.Add(yesButton);
        }
        else
        {
            var okButton = new Button
            {
                Content = "OK",
                MinWidth = 80
            };

            okButton.Click += (_, _) => Close();

            buttons.Children.Add(okButton);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                },
                buttons
            }
        };
    }
}