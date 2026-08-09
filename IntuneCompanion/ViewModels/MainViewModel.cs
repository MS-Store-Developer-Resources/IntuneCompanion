using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntuneCompanion.Models;
using IntuneCompanion.Services;

namespace IntuneCompanion.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NukeCommand))]
    private IntuneApp? _selectedApp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NukeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _uninstallAsWell;

    public ObservableCollection<IntuneApp> Apps { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    public MainViewModel()
    {
        Refresh();
    }

    private void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            LogEntries.Add(entry);
        else
            Application.Current?.Dispatcher.Invoke(() => LogEntries.Add(entry));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh()
    {
        IsBusy = true;
        Log("Scanning registry for Intune apps...");

        try
        {
            var apps = await Task.Run(IntuneAppService.GetInstalledApps);

            Apps.Clear();
            foreach (var app in apps)
                Apps.Add(app);

            Log($"Found {Apps.Count} app(s).");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand]
    private void DumpDiagnostics()
    {
        try
        {
            var path = IntuneAppService.DumpRegistryDiagnostics();
            Log($"Diagnostics saved to: {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanNuke))]
    private async Task Nuke()
    {
        var app = SelectedApp;
        if (app is null) return;

        var result = MessageBox.Show(
            $"This will remove all Intune registry traces for:\n\n{app.DisplayName}\n({app.AppId})\n\nand restart the IME service.\n\nContinue?",
            "Confirm Nuke",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        Log($"Nuking '{app.DisplayName}' ({app.AppId})...");

        try
        {
            bool registryClean = false;

            var shouldUninstall = UninstallAsWell;

            await Task.Run(() =>
            {
                if (shouldUninstall)
                {
                    IntuneAppService.UninstallApp(app.AppId, app.DisplayName, Log);
                }

                IntuneAppService.NukeApp(app.AppId, app.AppType, Log);

                // Verify registry removal before restarting IME
                registryClean = IntuneAppService.VerifyAppRemoved(app.AppId);

                IntuneAppService.RestartImeService(Log);
            });

            if (registryClean)
            {
                Log($"\u2705 Registry traces for '{app.DisplayName}' successfully removed.");
            }
            else
            {
                Log($"\u26a0\ufe0f Some registry traces for '{app.DisplayName}' could not be removed (may require elevated privileges).");
            }

            Log("\u23f3 IME service restarted. Waiting for app re-evaluation...");
            Log("   IME delays Win32 Required apps by ~4-5 min, Available apps by ~2-3 min after restart.");
            Log("   The app will remain physically installed until IME re-processes it.");
            Log("   Check logs at C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs after ~5 min.");

            // Wait briefly then refresh to show updated registry state
            await Task.Delay(TimeSpan.FromSeconds(5));
            await Refresh();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanNuke() => SelectedApp is not null && !IsBusy;
}
