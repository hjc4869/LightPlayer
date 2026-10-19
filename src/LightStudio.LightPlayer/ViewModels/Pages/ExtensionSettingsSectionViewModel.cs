using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using Strings = LightStudio.LightPlayer.Strings;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public partial class ExtensionSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly ILyricSourceService lyricSourceService;

    public ExtensionSettingsSectionViewModel(ILyricSourceService lyricSourceService)
        : base(
            nameof(Strings.Resources.ExtensionSettings_Title),
            nameof(Strings.Resources.ExtensionSettings_Subtitle))
    {
        this.lyricSourceService = lyricSourceService;

        LyricSources = new ObservableCollection<LyricSourceModel>();
        RemoveSourceCommand = new AsyncRelayCommand<LyricSourceModel>(
            source => AppErrorReporter.GuardAsync(() => RemoveSourceAsync(source)));
        ReimportSourcesCommand = new AsyncRelayCommand(() => AppErrorReporter.GuardAsync(ReimportSourcesAsync));
        Reload();
    }

    public ObservableCollection<LyricSourceModel> LyricSources { get; }

    public IAsyncRelayCommand<LyricSourceModel> RemoveSourceCommand { get; }

    public IAsyncRelayCommand ReimportSourcesCommand { get; }

    public bool HasLyricSources => LyricSources.Count > 0;

    private async Task ReimportSourcesAsync()
    {
        await lyricSourceService.ProvisionBundledSourcesAsync();
        Reload();
    }

    public async Task AddSourceAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Resources.ExtensionSettings_AddSourcePickerTitle,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }],
        });

        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            await lyricSourceService.AddScriptAsync(
                Path.GetFileNameWithoutExtension(file.Name),
                await reader.ReadToEndAsync());
        }

        Reload();
    }

    private async Task RemoveSourceAsync(LyricSourceModel? source)
    {
        if (source is null)
        {
            return;
        }

        await lyricSourceService.RemoveScriptAsync(source.Name);
        Reload();
    }

    private void Reload()
    {
        LyricSources.Clear();
        foreach (var name in lyricSourceService.SourceNames)
        {
            LyricSources.Add(new LyricSourceModel(name));
        }

        OnPropertyChanged(nameof(HasLyricSources));
    }
}
