using System.Windows;
using System.Windows.Input;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Views;

public partial class PartSelectionWindow : Window
{
    private readonly IReadOnlyList<ProductModel> _models;

    public ProductModel? SelectedModel { get; private set; }

    public PartSelectionWindow(IReadOnlyList<ProductModel> models, string preferredPartKey)
    {
        InitializeComponent();
        _models = models;
        PartList.ItemsSource = models.Select(model => new PartChoice(
            model.PartNumber,
            $"{model.ProductName}  ECO:{model.Eco}  NCO:{model.Nco}  ALC:{model.Alc}",
            model)).ToArray();
        PartList.SelectedIndex = Math.Max(0, models
            .Select((model, index) => (model, index))
            .FirstOrDefault(item => PartKey(item.model).Equals(preferredPartKey, StringComparison.OrdinalIgnoreCase))
            .index);
    }

    private void Select_Click(object sender, RoutedEventArgs e) => Accept();
    private void PartList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (PartList.SelectedItem is not PartChoice choice)
            return;
        SelectedModel = choice.Model;
        DialogResult = true;
    }

    public static string PartKey(ProductModel model) =>
        string.Join("|", model.PartNumber, model.ProductName, model.Eco, model.Nco, model.Alc);

    private sealed record PartChoice(string PartNumber, string Description, ProductModel Model);
}
