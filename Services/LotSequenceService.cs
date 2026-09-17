using System.Globalization;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class LotSequenceService
{
    private readonly object _gate = new();
    private readonly ProductionSettings _settings;
    private readonly Action<ProductionSettings> _persist;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, LotReservation> _reservations = new(StringComparer.Ordinal);
    private string _activeProductKey = "DEFAULT";

    public LotSequenceService(
        ProductionSettings settings,
        Action<ProductionSettings>? persist = null,
        Func<DateTime>? now = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persist = persist ?? ProductionConfigService.Save;
        _now = now ?? (() => DateTime.Now);

        lock (_gate)
        {
            ProductionConfigService.GetOrCreateProductLot(
                _settings, _activeProductKey, migrateCurrentLot: true);
            EnsureCurrentProductionDateLocked(_activeProductKey, persist: false);
        }
    }

    public void SelectProduct(string productKey, bool migrateCurrentLotIfMissing)
    {
        lock (_gate)
        {
            string key = NormalizeProductKey(productKey);
            ProductLotSettings lot = ProductionConfigService.GetOrCreateProductLot(
                _settings, key, migrateCurrentLotIfMissing);
            _activeProductKey = key;
            SyncCompatibilityFieldsLocked(lot);
            EnsureCurrentProductionDateLocked(key, persist: false);
        }
    }

    public void RefreshActiveProduct()
    {
        lock (_gate)
        {
            ProductLotSettings lot = ProductionConfigService.GetOrCreateProductLot(
                _settings, _activeProductKey, migrateCurrentLot: true);
            SyncCompatibilityFieldsLocked(lot);
            EnsureCurrentProductionDateLocked(_activeProductKey, persist: false);
        }
    }

    public long NextLot
    {
        get
        {
            lock (_gate)
            {
                EnsureCurrentProductionDateLocked(_activeProductKey, persist: false);
                return ActiveLotLocked().LotNo;
            }
        }
    }

    public long StartLot
    {
        get
        {
            lock (_gate)
                return Math.Max(0, ActiveLotLocked().StartLotNo);
        }
    }

    public long ReserveForCycle(string cycleId)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
            throw new ArgumentException("CycleId is required for LOT reservation.", nameof(cycleId));

        lock (_gate)
        {
            if (_reservations.TryGetValue(cycleId, out LotReservation existing))
                return existing.LotNo;

            EnsureCurrentProductionDateLocked(_activeProductKey, persist: false);
            ProductLotSettings lot = ActiveLotLocked();
            int pendingForProduct = _reservations.Values
                .Where(item => string.Equals(item.ProductKey, _activeProductKey, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.LotNo)
                .Distinct()
                .Count();
            long reserved = checked(lot.LotNo + pendingForProduct);
            _reservations[cycleId] = new LotReservation(_activeProductKey, reserved);
            return reserved;
        }
    }

    public bool TryCommitSuccessfulPrint(string cycleId, long printedLot, out string error)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(cycleId, out LotReservation reservation) ||
                reservation.LotNo != printedLot)
            {
                error = $"LOT reservation mismatch for cycle {cycleId}.";
                return false;
            }

            ProductLotSettings lot = ProductionConfigService.GetOrCreateProductLot(
                _settings, reservation.ProductKey, migrateCurrentLot: false);
            long current = Math.Max(0, lot.LotNo);
            if (printedLot != current)
            {
                error = $"Cannot commit LOT {printedLot}; next persisted LOT is {current}.";
                return false;
            }

            try
            {
                lot.LotNo = checked(printedLot + 1);
                if (IsActiveProduct(reservation.ProductKey))
                    SyncCompatibilityFieldsLocked(lot);
                _persist(_settings);
                _reservations.Remove(cycleId);
                EnsureCurrentProductionDateLocked(reservation.ProductKey, persist: false);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                lot.LotNo = current;
                if (IsActiveProduct(reservation.ProductKey))
                    SyncCompatibilityFieldsLocked(lot);
                error = $"Cannot persist next LOT: {ex.Message}";
                return false;
            }
        }
    }

    public bool IsCommitCandidate(string cycleId, long lotNo)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(cycleId, out LotReservation reservation) ||
                reservation.LotNo != lotNo)
            {
                return false;
            }
            ProductLotSettings current = ProductionConfigService.GetOrCreateProductLot(
                _settings, reservation.ProductKey, migrateCurrentLot: false);
            return current.LotNo == lotNo;
        }
    }

    public bool TryRestoreReservation(string cycleId, long lotNo)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
            return false;

        lock (_gate)
        {
            if (_reservations.TryGetValue(cycleId, out LotReservation existing))
                return existing.LotNo == lotNo && IsActiveProduct(existing.ProductKey);

            ProductLotSettings current = ActiveLotLocked();
            if (lotNo < current.LotNo || _reservations.Values.Any(item =>
                    IsActiveProduct(item.ProductKey) && item.LotNo == lotNo))
            {
                return false;
            }

            _reservations[cycleId] = new LotReservation(_activeProductKey, lotNo);
            return true;
        }
    }

    public long GetReservedOrNext(string cycleId)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(cycleId, out LotReservation reservation))
                return reservation.LotNo;
            EnsureCurrentProductionDateLocked(_activeProductKey, persist: false);
            return ActiveLotLocked().LotNo;
        }
    }

    private bool EnsureCurrentProductionDateLocked(string productKey, bool persist)
    {
        if (_reservations.Values.Any(item =>
                string.Equals(item.ProductKey, productKey, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ProductLotSettings lot = ProductionConfigService.GetOrCreateProductLot(
            _settings, productKey, migrateCurrentLot: false);
        string today = _now().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string storedDate = (lot.LotNoDate ?? string.Empty).Trim();
        if (string.Equals(storedDate, today, StringComparison.Ordinal))
        {
            if (IsActiveProduct(productKey))
                SyncCompatibilityFieldsLocked(lot);
            return false;
        }

        long previousLot = lot.LotNo;
        string previousDate = lot.LotNoDate ?? string.Empty;
        if (DateOnly.TryParseExact(
                storedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            // Operator chỉ đặt LOTNO bắt đầu một lần cho từng mã hàng. Sang
            // ngày sản xuất mới quay về base đó, không được làm mất thành 0.
            lot.LotNo = Math.Max(0, lot.StartLotNo);
        }
        lot.LotNoDate = today;
        if (IsActiveProduct(productKey))
            SyncCompatibilityFieldsLocked(lot);

        if (!persist)
        {
            if (previousLot != lot.LotNo)
            {
                AsyncFileLogService.Current.Test(
                    $"LOTNO DAILY RESET product={productKey} date={today} previous={previousLot} next={lot.LotNo}");
            }
            return true;
        }

        try
        {
            _persist(_settings);
            return true;
        }
        catch (Exception ex)
        {
            lot.LotNo = previousLot;
            lot.LotNoDate = previousDate;
            if (IsActiveProduct(productKey))
                SyncCompatibilityFieldsLocked(lot);
            AsyncFileLogService.Current.Error(
                $"Không thể lưu ngày/reset LOTNO tự động cho {productKey}: {ex.Message}");
            return false;
        }
    }

    private ProductLotSettings ActiveLotLocked() =>
        ProductionConfigService.GetOrCreateProductLot(
            _settings, _activeProductKey, migrateCurrentLot: true);

    private void SyncCompatibilityFieldsLocked(ProductLotSettings lot)
    {
        lot.LotNo = Math.Max(0, lot.LotNo);
        _settings.LotNo = lot.LotNo;
        _settings.LotNoDate = lot.LotNoDate ?? string.Empty;
    }

    private bool IsActiveProduct(string productKey) =>
        string.Equals(productKey, _activeProductKey, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProductKey(string? productKey) =>
        string.IsNullOrWhiteSpace(productKey) ? "DEFAULT" : productKey.Trim();

    private readonly record struct LotReservation(string ProductKey, long LotNo);
}
