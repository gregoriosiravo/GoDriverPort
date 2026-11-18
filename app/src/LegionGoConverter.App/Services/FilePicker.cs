using Microsoft.Win32;

namespace LegionGoConverter.App;

public sealed class FilePicker : IFilePicker
{
    /// <summary><paramref name="filter"/> is a standard dialog filter, e.g. <c>Installers (*.exe)|*.exe</c>; empty means .exe files.</summary>
    public string? PickFile(string title, string filter)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = string.IsNullOrWhiteSpace(filter) ? "Installers (*.exe)|*.exe" : filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
