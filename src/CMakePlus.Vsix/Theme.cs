using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;

namespace CMakePlus;

internal static class Theme
{
    public static void Apply(Control surface)
    {
        surface.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CMakePlus;component/ThemeResources.xaml", UriKind.Relative)
        });
        surface.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        surface.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
    }
}
