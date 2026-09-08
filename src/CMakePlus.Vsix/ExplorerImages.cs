using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using Microsoft.VisualStudio.Imaging;

namespace CMakePlus;

internal sealed class ExplorerImages : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string path;
        if (value is FileNode file)
        {
            if (file.IsDirectory) return KnownMonikers.FolderClosed;
            path = file.Path;
        }
        else if (value is TargetNode target)
        {
            if (target.Path == target.Target.DeclarationFile && target.Path.Length > 0) return KnownMonikers.Application;
            if (target.Children.Count > 0 || target.Path.Length == 0) return KnownMonikers.FolderClosed;
            path = target.Path;
        }
        else return KnownMonikers.Document;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".cpp": case ".cc": case ".cxx": case ".c": return KnownMonikers.CPPFileNode;
            case ".h": case ".hpp": return KnownMonikers.CPPHeaderFile;
            default: return KnownMonikers.Document;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
