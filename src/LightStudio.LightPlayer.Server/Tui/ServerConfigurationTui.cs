using System;
using System.Collections.Generic;
using System.IO;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LightStudio.LightPlayer.Server.Tui;

/// <summary>
/// Terminal front end for <see cref="ServerConfiguration"/>. It edits the JSON file the server reads
/// on startup and can index the library in place, so a deployment without a graphical session still
/// gets the library and hosting settings the desktop app offers.
/// </summary>
internal static class ServerConfigurationTui
{
    public static int Run()
    {
        ServerConfiguration config = ServerConfiguration.Load();
        string path = ServerConfiguration.ConfigPath;

        using IApplication app = Application.Create();
        app.Init();

        using var window = new Window { Title = $"Light Player server -- {path}" };

        var serverTab = new ServerSettingsTab(config);
        var libraryTab = new LibrarySettingsTab(app, config);
        var transcodeTab = new TranscodeSettingsTab(config);
        var scanTab = new LibraryScanTab(app, config);
        IConfigurationTab[] tabs = [serverTab, libraryTab, transcodeTab];

        var status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = Overrides(),
        };

        var save = new Button { X = 0, Y = Pos.AnchorEnd(2), Text = "_Save" };
        save.Accepting += (_, e) =>
        {
            e.Handled = true;
            if (TrySave(app, config, path, tabs))
            {
                status.Text = $"Saved to {path}. Restart the server to apply it.";
            }
        };

        var quit = new Button { X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(2), Text = "_Quit" };
        quit.Accepting += (_, e) =>
        {
            e.Handled = true;
            Leave(app, window, config, path, tabs);
        };

        // Escape is the toolkit's quit key and would otherwise drop the edits without asking.
        window.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                key.Handled = true;
                Leave(app, window, config, path, tabs);
            }
        };

        var pages = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
        pages.Add(serverTab, libraryTab, transcodeTab, scanTab);
        scanTab.PrepareConfiguration = () => ApplyAll(config, tabs);

        window.Add(pages, save, quit, status);
        app.Run(window, exception =>
        {
            // Showing the failure keeps the terminal in the alternate screen; letting it escape
            // would leave the raw trace over a half-restored console.
            MessageBox.ErrorQuery(app, "Unexpected error", exception.ToString(), "OK");
            return true;
        });

        return 0;
    }

    private static void Leave(
        IApplication app,
        Window window,
        ServerConfiguration config,
        string path,
        IReadOnlyList<IConfigurationTab> tabs)
    {
        switch (MessageBox.Query(app, "Leave", "Save the configuration before leaving?", "_Save", "_Discard", "_Cancel"))
        {
            case 0:
                if (TrySave(app, config, path, tabs))
                {
                    app.RequestStop(window);
                }

                break;

            case 1:
                app.RequestStop(window);
                break;
        }
    }

    /// <summary>Collects every page into the configuration, returning the first page that refused.</summary>
    private static string? ApplyAll(ServerConfiguration config, IReadOnlyList<IConfigurationTab> tabs)
    {
        foreach (IConfigurationTab tab in tabs)
        {
            if (tab.Apply(config) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static bool TrySave(
        IApplication app,
        ServerConfiguration config,
        string path,
        IReadOnlyList<IConfigurationTab> tabs)
    {
        if (ApplyAll(config, tabs) is { } error)
        {
            MessageBox.ErrorQuery(app, "Cannot save", error, "OK");
            return false;
        }

        try
        {
            config.Save(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.ErrorQuery(app, "Cannot save", $"{path} could not be written: {exception.Message}", "OK");
            return false;
        }
    }

    /// <summary>
    /// Environment variables win over the file, so a deployment that sets them has to be told which
    /// of the values on screen the server is going to ignore.
    /// </summary>
    private static string Overrides()
    {
        IReadOnlyList<string> names = ServerConfiguration.OverridingEnvironmentVariables();
        return names.Count == 0
            ? "Tab moves between fields, Alt and an underlined letter jumps to a page or a button, Escape leaves."
            : $"Set in the environment and used instead of this file: {string.Join(", ", names)}";
    }
}
