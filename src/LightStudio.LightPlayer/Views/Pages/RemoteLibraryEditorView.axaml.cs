using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using LightStudio.LightPlayer.ViewModels.Pages;

namespace LightStudio.LightPlayer.Views.Pages;

public partial class RemoteLibraryEditorView : UserControl
{
    private RemoteLibraryEditorViewModel? viewModel;

    public RemoteLibraryEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as RemoteLibraryEditorViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs) =>
        AttachViewModel(DataContext as RemoteLibraryEditorViewModel);

    private void AttachViewModel(RemoteLibraryEditorViewModel? value)
    {
        viewModel?.SetBrowserLauncher(null);
        viewModel = value;
        viewModel?.SetBrowserLauncher(OpenBrowserAsync);
    }

    private async Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launcher = TopLevel.GetTopLevel(this)?.Launcher
                ?? throw new InvalidOperationException("The system browser is unavailable.");
            if (!await launcher.LaunchUriAsync(uri))
            {
                throw new InvalidOperationException("The system browser did not accept the Nextcloud sign-in URL.");
            }
        });
    }
}
