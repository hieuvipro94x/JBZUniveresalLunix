using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace JBZUniveresalLunix.Converters
{
    /// <summary>
    /// Chuyển mã màu dây trong file THT thành Brush hiển thị trên DataGrid.
    /// Hỗ trợ màu đơn: B, W, R, G, L, Y, Br, Or, P, Gr...
    /// và màu kép/sọc: W/B, W-B, R/Y, G+W...
    /// </summary>
    public sealed class WireColorToBrushConverter : IValueConverter
    {
        private static readonly Dictionary<string, Color> ColorMap =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                { "B", Colors.Black },
                { "BK", Colors.Black },
                { "BLACK", Colors.Black },

                { "W", Colors.White },
                { "WH", Colors.White },
                { "WHITE", Colors.White },

                { "R", Colors.Red },
                { "RED", Colors.Red },

                { "G", Color.FromRgb(0x00, 0xD0, 0x00) },
                { "GN", Color.FromRgb(0x00, 0xD0, 0x00) },
                { "GREEN", Color.FromRgb(0x00, 0xD0, 0x00) },

                // Quy ước THT: L = Blue.
                { "L", Colors.Blue },
                { "BL", Colors.Blue },
                { "BLU", Colors.Blue },
                { "BLUE", Colors.Blue },

                { "Y", Color.FromRgb(0xFF, 0xFF, 0x00) },
                { "YL", Color.FromRgb(0xFF, 0xFF, 0x00) },
                { "YELLOW", Color.FromRgb(0xFF, 0xFF, 0x00) },

                { "BR", Color.FromRgb(0xA6, 0x2A, 0x2A) },
                { "BN", Color.FromRgb(0xA6, 0x2A, 0x2A) },
                { "BROWN", Color.FromRgb(0xA6, 0x2A, 0x2A) },

                // Quy ước THT: O / Or / Orange = Orange.
                // Dùng cùng một RGB cho cả màu đơn và phần màu trong dây kép
                // (ví dụ B/Or => Đen/Cam), tránh alias Or bị rơi về xanh dương.
                { "OR", Color.FromRgb(0xFF, 0x8C, 0x00) },
                { "O", Color.FromRgb(0xFF, 0x8C, 0x00) },
                { "OG", Color.FromRgb(0xFF, 0x8C, 0x00) },
                { "ORANGE", Color.FromRgb(0xFF, 0x8C, 0x00) },

                { "P", Color.FromRgb(0xEA, 0xAD, 0xEA) },
                { "PK", Color.FromRgb(0xEA, 0xAD, 0xEA) },
                { "PINK", Color.FromRgb(0xEA, 0xAD, 0xEA) },

                { "GR", Color.FromRgb(0x60, 0x60, 0x60) },
                { "GY", Color.FromRgb(0x60, 0x60, 0x60) },
                { "GRAY", Color.FromRgb(0x60, 0x60, 0x60) },
                { "GREY", Color.FromRgb(0x60, 0x60, 0x60) },

                { "V", Color.FromRgb(0x7F, 0x00, 0xBB) },
                { "VI", Color.FromRgb(0x7F, 0x00, 0xBB) },
                { "VIOLET", Color.FromRgb(0x7F, 0x00, 0xBB) },

                { "LG", Color.FromRgb(0x77, 0xDD, 0x77) },
                { "LIGHTGREEN", Color.FromRgb(0x77, 0xDD, 0x77) },

                { "SB", Color.FromRgb(0x66, 0xCC, 0xFF) },
                { "SKY", Color.FromRgb(0x66, 0xCC, 0xFF) },
                { "SKYBLUE", Color.FromRgb(0x66, 0xCC, 0xFF) },

                { "T", Color.FromRgb(210, 180, 140) },
                { "TAN", Color.FromRgb(210, 180, 140) }
            };

        private static readonly Dictionary<string, string> VietnameseColorNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "B", "Đen" }, { "BK", "Đen" }, { "BLACK", "Đen" },
                { "W", "Trắng" }, { "WH", "Trắng" }, { "WHITE", "Trắng" },
                { "R", "Đỏ" }, { "RED", "Đỏ" },
                { "G", "Xanh lá" }, { "GN", "Xanh lá" }, { "GREEN", "Xanh lá" },
                { "L", "Xanh dương" }, { "BL", "Xanh dương" }, { "BLU", "Xanh dương" }, { "BLUE", "Xanh dương" },
                { "Y", "Vàng" }, { "YL", "Vàng" }, { "YELLOW", "Vàng" },
                { "BR", "Nâu" }, { "BN", "Nâu" }, { "BROWN", "Nâu" },
                { "OR", "Cam" }, { "O", "Cam" }, { "OG", "Cam" }, { "ORANGE", "Cam" },
                { "P", "Hồng" }, { "PK", "Hồng" }, { "PINK", "Hồng" },
                { "GR", "Xám" }, { "GY", "Xám" }, { "GRAY", "Xám" }, { "GREY", "Xám" },
                { "V", "Tím" }, { "VI", "Tím" }, { "VIOLET", "Tím" },
                { "LG", "Xanh lá nhạt" }, { "LIGHTGREEN", "Xanh lá nhạt" },
                { "SB", "Xanh da trời" }, { "SKYBLUE", "Xanh da trời" },
                { "T", "Nâu nhạt" }, { "TAN", "Nâu nhạt" }
            };

        private static readonly ConcurrentDictionary<string, Brush> BrushCache =
            new(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return ToBrush(value?.ToString());
        }

        /// <summary>
        /// Chuyển mã màu dây thành Brush mà không cần XAML khởi tạo custom converter.
        /// </summary>
        public static Brush ToBrush(string? value)
        {
            string code = value?.Trim() ?? string.Empty;
            string cacheKey = string.IsNullOrWhiteSpace(code)
                ? "<empty>"
                : code.ToUpperInvariant();

            return BrushCache.GetOrAdd(cacheKey, _ => CreateBrush(code));
        }

        private static Brush CreateBrush(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Brushes.Transparent;

            if (TryGetColor(code, out Color singleColor))
                return CreateSolidBrush(singleColor);

            string[] parts = Regex
                .Split(code, @"[\/+,;|\-\s]+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(2)
                .ToArray();

            if (parts.Length >= 1 && TryGetColor(parts[0], out Color baseColor))
            {
                if (parts.Length >= 2 && TryGetColor(parts[1], out Color stripeColor))
                    return CreateStripedBrush(baseColor, stripeColor);

                return CreateSolidBrush(baseColor);
            }

            return CreateSolidBrush(Color.FromRgb(224, 224, 224));
        }


        /// <summary>
        /// Tên màu tiếng Việt dùng cho thanh trạng thái đầu dò. Với dây sọc,
        /// trả về dạng "Trắng/Đen" theo đúng thứ tự mã trong THT.
        /// </summary>
        public static string ToVietnameseName(string? value)
        {
            string code = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            string[] parts = Regex
                .Split(code, @"[\/+,;|\-\s]+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            if (parts.Length == 0)
                return code;

            string[] names = parts
                .Take(2)
                .Select(part =>
                {
                    string normalized = part.Trim().Replace(".", string.Empty).ToUpperInvariant();
                    return VietnameseColorNames.TryGetValue(normalized, out string? name)
                        ? name
                        : part.Trim();
                })
                .ToArray();

            return string.Join("/", names);
        }

        public static string ToDisplayCode(string? value) => (value ?? string.Empty).Trim();

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static bool TryGetColor(string code, out Color color)
        {
            string normalized = code
                .Trim()
                .Replace(".", string.Empty)
                .ToUpperInvariant();

            return ColorMap.TryGetValue(normalized, out color);
        }

        private static SolidColorBrush CreateSolidBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush CreateStripedBrush(Color firstColor, Color secondColor)
        {
            LinearGradientBrush brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };

            // Màu kép được chia đúng hai nửa. Màu đứng trước trong THT luôn ở bên trái,
            // màu đứng sau luôn ở bên phải (R/W khác W/R).
            brush.GradientStops.Add(new GradientStop(firstColor, 0.00));
            brush.GradientStops.Add(new GradientStop(firstColor, 0.50));
            brush.GradientStops.Add(new GradientStop(secondColor, 0.50));
            brush.GradientStops.Add(new GradientStop(secondColor, 1.00));
            brush.Freeze();

            return brush;
        }
    }
}
