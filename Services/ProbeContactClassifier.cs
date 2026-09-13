using JBZUniversalTester.Models;

namespace JBZUniversalTester.Services;

/// <summary>
/// Nhận dạng contact của que dò ngay trong stream Production.
///
/// V12.8: một frame có thể chứa nhiều điểm contact hợp lệ (thực tế cần tối thiểu
/// hai chân I/O cùng lúc). Classifier trả về tối đa số contact được yêu cầu,
/// ưu tiên các I/O mạnh nhất thay vì ép về một I/O duy nhất. Các contact này chỉ dùng cho UI
/// chẩn đoán; tuyệt đối không được biến thành WrongWiring/Short và không điều khiển relay.
/// </summary>
public static class ProbeContactClassifier
{
    public sealed record Detection(
        int Io,
        int Score,
        string Signature,
        int Hits = 0,
        int FanIn = 0,
        bool IsMapped = false);

    private sealed record Candidate(
        int Io,
        int Hits,
        int FanIn,
        bool Mapped,
        bool SourceWordMissing,
        int Score,
        bool IsProbeLike);

    public static bool TryDetect(
        ScanFrame frame,
        ProductModel? model,
        out Detection detection)
    {
        IReadOnlyList<Detection> detections = DetectMany(frame, model, 1);
        if (detections.Count > 0)
        {
            detection = detections[0];
            return true;
        }

        detection = new Detection(0, 0, string.Empty);
        return false;
    }

    /// <summary>
    /// Trả về các I/O đang có chữ ký contact que dò trong cùng một frame.
    /// Sắp xếp theo độ tin cậy giảm dần và loại trùng I/O.
    /// </summary>
    public static IReadOnlyList<Detection> DetectMany(
        ScanFrame frame,
        ProductModel? model,
        int maxContacts = 2,
        BoardCapacity? boardCapacity = null)
    {
        // Chỉ classifier trên snapshot Production hoàn chỉnh và sạch.
        // Partial/target-only data rất dễ bị nhiễu điện dung (ví dụ tay người chạm IO)
        // và không đủ bằng chứng để kết luận đầu dò thật.
        if (maxContacts <= 0 ||
            frame.Mode != BoardScanMode.Production ||
            !frame.Complete ||
            frame.UnknownBytes != 0 ||
            !frame.TerminatorKnown)
        {
            return Array.Empty<Detection>();
        }

        int sourceCount = frame.Connections.Count;
        var fanInByTarget = new Dictionary<int, int>();
        var unexpectedFanInByTarget = new Dictionary<int, int>();
        HashSet<long>? expectedEdges = null;
        int edgeCount = 0;

        foreach (KeyValuePair<int, IReadOnlySet<int>> pair in frame.Connections)
        {
            foreach (int target in pair.Value)
            {
                fanInByTarget[target] = fanInByTarget.GetValueOrDefault(target) + 1;
                expectedEdges ??= BuildExpectedEdges(model);
                if (!expectedEdges.Contains(EdgeKey(pair.Key, target)))
                {
                    unexpectedFanInByTarget[target] =
                        unexpectedFanInByTarget.GetValueOrDefault(target) + 1;
                }
                edgeCount++;
            }
        }

        if (fanInByTarget.Count == 0 && frame.TargetHits.Count == 0)
            return Array.Empty<Detection>();

        int repeatedThreshold = GetRepeatedTargetThreshold(sourceCount);

        Candidate[] candidates = fanInByTarget.Keys
            .Concat(frame.TargetHits.Keys)
            .Distinct()
            .Where(io => boardCapacity?.ContainsGlobalIo(io) ??
                         (io > 0 && io <= BoardIoDecoder.MaxIoCount))
            .Select(io =>
            {
                int hits = frame.TargetHits.GetValueOrDefault(io);
                int fanIn = fanInByTarget.GetValueOrDefault(io);
                int unexpectedFanIn = unexpectedFanInByTarget.GetValueOrDefault(io);
                bool mapped = IsMapped(model, io);
                bool sourceWordMissing = !frame.Connections.ContainsKey(io);

                int score =
                    hits * 5 +
                    unexpectedFanIn * 6 +
                    (mapped ? 16 : 0) +
                    (sourceWordMissing ? 8 : 0);

                // Trace Htdrv cho thấy TP thường là một IO rỗng, không nằm trong
                // topology THT. Vì vậy mapping chỉ dùng xếp hạng, không phải gate.
                // Một TARGET lẻ vẫn bị loại: phải có fan-in/hit lặp qua nhiều SOURCE.
                bool repeatedTarget =
                    unexpectedFanIn >= repeatedThreshold ||
                    (fanIn == 0 && sourceWordMissing && hits >= repeatedThreshold);

                return new Candidate(
                    io,
                    hits,
                    unexpectedFanIn,
                    mapped,
                    sourceWordMissing,
                    score,
                    repeatedTarget);
            })
            .Where(candidate => candidate.IsProbeLike)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Mapped)
            .ThenByDescending(candidate => candidate.Hits)
            .ThenByDescending(candidate => candidate.FanIn)
            .ThenBy(candidate => candidate.Io)
            .ToArray();

        if (candidates.Length == 0)
            return Array.Empty<Detection>();

        // Nếu có hai đầu dò/contact thật, cả hai target thường đều có fan-in đáng kể.
        // Không lấy các target yếu chỉ vì frame có nhiều nhiễu: candidate thứ hai phải
        // đạt tối thiểu 35% score của candidate mạnh nhất hoặc tự đạt repeatedThreshold.
        int bestScore = Math.Max(1, candidates[0].Score);
        var result = new List<Detection>(Math.Min(maxContacts, candidates.Length));

        foreach (Candidate candidate in candidates)
        {
            if (result.Count >= maxContacts)
                break;

            if (result.Count > 0 &&
                candidate.Score * 100 < bestScore * 35 &&
                candidate.FanIn < repeatedThreshold &&
                candidate.Hits < repeatedThreshold)
            {
                continue;
            }

            string signature =
                $"TARGET IO{candidate.Io}: hits={candidate.Hits}, fan-in={candidate.FanIn}, " +
                $"sources={sourceCount}, edges={edgeCount}";

            result.Add(new Detection(
                candidate.Io,
                candidate.Score,
                signature,
                candidate.Hits,
                candidate.FanIn,
                candidate.Mapped));
        }

        return result;
    }

    public static int GetRepeatedTargetThreshold(int sourceCount) =>
        // Trace bo thật có các contact hợp lệ khoảng 12 fan-in trên sweep 256 IO.
        // Giữ sàn 6 để một/vài cạnh thường không thành Probe và trần 12 để không
        // bỏ lọt phần đầu/phần đuôi của thao tác chạm đang chuyển tiếp.
        Math.Clamp((sourceCount + 15) / 16, 6, 12);

    /// <summary>
    /// Htdrv trace phân biệt cạnh điện thật (mỗi target chỉ có một/vài source)
    /// với đầu dò (một target có fan-in lớn). Dùng dấu hiệu này để không giữ
    /// cạnh dây thật trong quarantine RELEASE của Probe.
    /// </summary>
    public static bool HasDirectConnectionEvidence(ScanFrame frame)
    {
        if (frame.Mode != BoardScanMode.Production ||
            !frame.Complete ||
            frame.UnknownBytes != 0 ||
            !frame.TerminatorKnown)
        {
            return false;
        }

        var fanInByTarget = new Dictionary<int, int>();
        foreach (KeyValuePair<int, IReadOnlySet<int>> pair in frame.Connections)
        {
            foreach (int target in pair.Value)
                fanInByTarget[target] = fanInByTarget.GetValueOrDefault(target) + 1;
        }

        return fanInByTarget.Any(pair => pair.Value is > 0 and <= 2);
    }

    private static HashSet<long> BuildExpectedEdges(ProductModel? model)
    {
        var result = new HashSet<long>();
        if (model is null)
            return result;

        foreach (WireNet net in model.Nets)
        {
            int[] ios = net.IoNumbers.Where(io => io > 0).Distinct().ToArray();
            for (int left = 0; left < ios.Length; left++)
            {
                for (int right = left + 1; right < ios.Length; right++)
                {
                    result.Add(EdgeKey(ios[left], ios[right]));
                    result.Add(EdgeKey(ios[right], ios[left]));
                }
            }
        }

        if (model.Clip is not null)
        {
            foreach (ClipBranch branch in model.Clip.Branches)
            {
                result.Add(EdgeKey(model.Clip.CommonIo, branch.TargetIo));
                result.Add(EdgeKey(branch.TargetIo, model.Clip.CommonIo));
            }
        }

        return result;
    }

    private static long EdgeKey(int source, int target) =>
        ((long)source << 32) | (uint)target;

    private static bool IsMapped(ProductModel? model, int io)
    {
        if (model is null)
            return false;

        if (model.Pins.Any(pin => pin.IoNumber == io))
            return true;

        if (model.Clip is null)
            return false;

        return model.Clip.CommonIo == io ||
               model.Clip.Branches.Any(branch => branch.TargetIo == io);
    }
}
