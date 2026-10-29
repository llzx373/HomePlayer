using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class LibraryPage
{
    private async void MigrateRoot_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaLibrary library)
            return;
        string? folder;
        try
        {
            folder = await FolderDialog.PickAsync();
        }
        catch (Exception ex)
        {
            await Message(ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(folder))
            return;
        var dialog = new ContentDialog
        {
            Title = "更改媒体库位置",
            Content = $"请确认媒体文件已经从“{library.RootPath}”移动到“{folder}”。此操作只更新 HomePlayer 的位置记录，不移动磁盘文件；观看进度、已看状态和元数据会保留。",
            PrimaryButtonText = "更新位置",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await App.Host.Libraries.MigrateRootAsync(library.Id, folder);
            QueueLibraryReload();
        }
        catch (Exception ex)
        {
            await Message(ex.Message);
        }
    }
}
