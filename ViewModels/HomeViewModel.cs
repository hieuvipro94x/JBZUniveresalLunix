using System.Windows;
using JBZUniveresalLunix.Core;
using JBZUniveresalLunix.Views;

namespace JBZUniveresalLunix.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public MainViewModel Main => _main;
    public AsyncRelayCommand LoadModelCommand { get; }
    public RelayCommand StartCommand { get; }

    public string ModelName => _main.Model?.ModelName ?? "CHƯA CÓ MODEL";
    public string PartNumber => _main.Model?.PartNumber ?? "—";
    public string ProductName => _main.Model?.ProductName ?? "—";
    public string VehicleType => _main.Model?.VehicleType ?? "—";
    public string SourcePath => _main.Model?.SourcePath ?? string.Empty;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        LoadModelCommand = new AsyncRelayCommand(LoadModelAsync);
        StartCommand = new RelayCommand(
            () => _main.CurrentPage = _main.Test,
            () => _main.Model is not null && _main.HasEnoughCardsForModel);
    }

    private Task LoadModelAsync()
    {
        Window? owner = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(window => window.IsActive) ?? Application.Current?.MainWindow;
        var dialog = new JbzPartSelectionWindow(_main);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    public void Refresh()
    {
        Raise(nameof(ModelName)); Raise(nameof(PartNumber));
        Raise(nameof(ProductName)); Raise(nameof(VehicleType)); Raise(nameof(SourcePath));
        StartCommand.RaiseCanExecuteChanged();
    }
}
