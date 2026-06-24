using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.ViewModels.Pages;

public partial class ExtensionSettingsSectionViewModel : SettingsSectionViewModel
{
    private readonly ILyricSourceService lyricSourceService;

    public ExtensionSettingsSectionViewModel(ILyricSourceService lyricSourceService)
        : base("Extensions", "Manage third-party lyric sources.")
    {
        this.lyricSourceService = lyricSourceService;

        LyricSources = new ObservableCollection<LyricSourceModel>();
        RemoveSourceCommand = new RelayCommand<LyricSourceModel>(RemoveSource);
        ReimportSourcesCommand = new RelayCommand(ReimportSources);
        Reload();
    }

    public ObservableCollection<LyricSourceModel> LyricSources { get; }

    public IRelayCommand<LyricSourceModel> RemoveSourceCommand { get; }

    public IRelayCommand ReimportSourcesCommand { get; }

    public bool HasLyricSources => LyricSources.Count > 0;

    private void ReimportSources()
    {
        lyricSourceService.ProvisionBundledSources();
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
            Title = "Add lyric source",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("JavaScript") { Patterns = ["*.js"] }],
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            lyricSourceService.AddScript(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
        }

        Reload();
    }

    private void RemoveSource(LyricSourceModel? source)
    {
        if (source is null)
        {
            return;
        }

        lyricSourceService.RemoveScript(source.Name);
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
