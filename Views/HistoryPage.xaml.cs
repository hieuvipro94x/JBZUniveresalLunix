using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using Microsoft.Win32;

namespace JBZUniveresalLunix.Views;

/// <summary>
/// V12.9: lịch sử là một page trong MainWindow, không phải Window/ShowDialog.
/// </summary>
public partial class HistoryPage : UserControl
{
    private readonly string _historyPath;
    private readonly object _storeGate = new();
    private TestHistoryStore? _store;
    private int _reloadGeneration;
    private const int PageSize = 200;
    private readonly ObservableCollection<TestHistoryRecord> _records = [];
    private HistorySearchCriteria? _activeCriteria;
    private HistorySummary _summary = new(0, 0, 0, 0, 0, 0);
    private bool _loadingPage;

    public event EventHandler? RequestClose;

    public HistoryPage()
    {
        InitializeComponent();
        DataContext = this;
        _historyPath = RuntimePaths.DatabaseFile;

        SetDefaultFilters();
        HistoryGrid.ItemsSource = _records;
        Loaded += HistoryPage_Loaded;
    }

    private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HistoryPage_Loaded;
        try
        {
            IReadOnlyList<HistoryPartOption> parts = await Task.Run(() => GetStore().GetHistoryPartOptions());
            PartComboBox.ItemsSource = parts;
            PartComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"History part list failed: {ex}");
        }
        Reload();
    }

    public void ReleasePageResources()
    {
        Loaded -= HistoryPage_Loaded;
        Interlocked.Increment(ref _reloadGeneration);
        _records.Clear();
        HistoryGrid.ItemsSource = null;
        DataContext = null;
    }

    private Window? HostWindow => Window.GetWindow(this) ?? Application.Current?.MainWindow;

    private void SetDefaultFilters()
    {
        FromDatePicker.SelectedDate = DateTime.Today.AddDays(-7);
        ToDatePicker.SelectedDate = DateTime.Today;
        LotTextBox.Text = string.Empty;
        if (PartComboBox is not null)
            PartComboBox.SelectedIndex = 0;
        ResultComboBox.SelectedIndex = 0;
        InspectionTypeComboBox.SelectedIndex = 0;
    }

    private void Search_Click(object sender, RoutedEventArgs e) => Reload();

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        SetDefaultFilters();
        Reload();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        Reload();
        e.Handled = true;
    }

    private TestHistoryStore GetStore()
    {
        lock (_storeGate)
            return _store ??= new TestHistoryStore(_historyPath);
    }

    private async void Reload()
    {
        int generation = Interlocked.Increment(ref _reloadGeneration);
        try
        {
            HistorySearchCriteria criteria = CreateSearchCriteria();
            _activeCriteria = criteria;
            _records.Clear();
            Task<HistorySummary> summaryTask = Task.Run(() => GetStore().GetHistorySummary(criteria));
            Task<IReadOnlyList<TestHistoryRecord>> pageTask = Task.Run(() =>
                GetStore().SearchSummary(criteria with { MaxRows = PageSize, Offset = 0 }));
            await Task.WhenAll(summaryTask, pageTask);
            if (generation != Volatile.Read(ref _reloadGeneration))
                return;

            _summary = summaryTask.Result;
            IReadOnlyList<TestHistoryRecord> rows = pageTask.Result;
            foreach (TestHistoryRecord row in rows)
                _records.Add(row);

            long productCount = _summary.ProductTotal;
            long pass = _summary.ProductPass;
            long fail = _summary.ProductFail;
            long master = _summary.MasterTotal;
            long leakRetest = _summary.LeakRetestTotal;

            TotalCountText.Text = productCount.ToString("N0");
            PassCountText.Text = pass.ToString("N0");
            FailCountText.Text = fail.ToString("N0");
            LoadMoreButton.IsEnabled = _records.Count < _summary.Total;

            SummaryText.Text =
                $"{rows.Count:N0} bản ghi | SẢN PHẨM {productCount:N0} " +
                $"(PASS {pass:N0} / FAIL {fail:N0}) | LEAK RETEST {leakRetest:N0} | MASTER {master:N0}";
            SummaryText.Text =
                $"Đang hiển thị {_records.Count:N0} / {_summary.Total:N0} bản ghi | " +
                $"SẢN PHẨM {productCount:N0} (PASS {pass:N0} / FAIL {fail:N0}, {_summary.ProductPassRate:0.00}%) | " +
                $"LEAK RETEST {leakRetest:N0} | MASTER {master:N0}";
        }
        catch (Exception ex)
        {
            TotalCountText.Text = "0";
            PassCountText.Text = "0";
            FailCountText.Text = "0";
            SummaryText.Text = "Không thể đọc dữ liệu lịch sử.";
            AsyncFileLogService.Current.Error($"History search failed: {ex}");
            ShowMessage("Chưa đọc được lịch sử kiểm tra. Vui lòng thử lại.", "CHƯA ĐỌC ĐƯỢC LỊCH SỬ", MessageBoxImage.Error);
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingPage || _activeCriteria is null || _records.Count == 0 || _records.Count >= _summary.Total)
            return;

        _loadingPage = true;
        int generation = Volatile.Read(ref _reloadGeneration);
        LoadMoreButton.IsEnabled = false;
        try
        {
            TestHistoryRecord cursor = _records[^1];
            HistorySearchCriteria pageCriteria = _activeCriteria with
            {
                MaxRows = PageSize,
                Offset = 0,
                AfterHistoryAt = cursor.EffectiveTestStartedAt,
                AfterId = cursor.Id
            };
            IReadOnlyList<TestHistoryRecord> page = await Task.Run(() => GetStore().SearchSummary(pageCriteria));
            if (generation != Volatile.Read(ref _reloadGeneration))
                return;
            foreach (TestHistoryRecord row in page)
                _records.Add(row);
            SummaryText.Text = $"Đang hiển thị {_records.Count:N0} / {_summary.Total:N0} bản ghi";
        }
        finally
        {
            _loadingPage = false;
            LoadMoreButton.IsEnabled = _records.Count < _summary.Total;
        }
    }

    private HistorySearchCriteria CreateSearchCriteria()
    {
        DateTime? from = FromDatePicker.SelectedDate?.Date;
        DateTime? to = ToDatePicker.SelectedDate?.Date.AddDays(1);
        long? lot = long.TryParse(LotTextBox.Text?.Trim(), out long n) ? n : null;
        string result = (ResultComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ALL";
        string inspectionType =
            (InspectionTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        return new HistorySearchCriteria(
            from,
            to,
            lot,
            PartComboBox.SelectedValue?.ToString()?.Trim() ?? string.Empty,
            result,
            InspectionType: inspectionType);
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Xuất lịch sử CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"JBZ_TestHistory_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        bool? result = HostWindow is Window owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result != true)
            return;

        HistorySearchCriteria criteria = CreateSearchCriteria();
        try
        {
            int exportedCount = await Task.Run(() =>
            {
                IEnumerable<TestHistoryRecord> rows = GetStore().EnumerateForExport(criteria);
                return HistoryExportService.ExportCsv(dialog.FileName, rows);
            });
            ShowMessage(
                $"Đã xuất toàn bộ {exportedCount:N0} bản ghi theo mã hàng và ngày/giờ.\n\n{dialog.FileName}",
                "JBZ",
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"History CSV export failed: {ex}");
            ShowMessage("Chưa xuất được file CSV. Vui lòng thử lại.", "CHƯA XUẤT ĐƯỢC LỊCH SỬ", MessageBoxImage.Error);
        }
    }

    private async void ExportXlsx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Xuất lịch sử Excel theo mẫu chuẩn",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"JBZ_TestHistory_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        bool? result = HostWindow is Window owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (result != true)
            return;

        HistorySearchCriteria criteria = CreateSearchCriteria();
        try
        {
            int exportedCount = await Task.Run(() =>
            {
                IEnumerable<TestHistoryRecord> rows = GetStore().EnumerateForExport(criteria);
                return HistoryExportService.ExportXlsx(dialog.FileName, rows);
            });
            ShowMessage(
                $"Đã xuất toàn bộ {exportedCount:N0} bản ghi theo mã hàng và ngày/giờ.\n\n{dialog.FileName}",
                "JBZ",
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"History Excel export failed: {ex}");
            ShowMessage("Chưa xuất được file Excel. Vui lòng thử lại.", "CHƯA XUẤT ĐƯỢC LỊCH SỬ", MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        RequestClose?.Invoke(this, EventArgs.Empty);

    private void ShowMessage(string message, string title, MessageBoxImage image)
    {
        Window? owner = HostWindow;
        if (owner is not null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }
}
