using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeSessionBackup.App.Converters;

/// <summary>
/// Bytes to a human string. Binary units (1024), because that is what Windows
/// Explorer shows for the same file and a copy tool disagreeing with Explorer
/// about a file's size is an instant credibility loss.
/// </summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            bytes = value is int i ? i : 0;
        }

        return Humanize(bytes);
    }

    /// <summary>
    /// The same formatting the converter applies, callable from a view model.
    /// </summary>
    /// <remarks>
    /// Exposed so the Dashboard metric cards, which build their text in C#,
    /// cannot drift from the sizes the grid shows through the converter.
    /// </remarks>
    public static string Humanize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        double v = bytes;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }

        // No decimals for raw bytes: "512.0 B" reads like a rounding error.
        return unit == 0
            ? $"{v:N0} {Units[unit]}"
            : $"{v:N1} {Units[unit]}";
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Seconds to a compact duration ("48s", "3m 12s", "1h 04m").</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var seconds = value switch
        {
            double d => d,
            int i => i,
            _ => 0d,
        };

        if (seconds < 60)
        {
            return $"{seconds:N0}s";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m"
            : $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Bool to Visibility. Pass "invert" as the parameter to negate.
/// </summary>
/// <remarks>
/// Collapsed, never Hidden: a hidden element still occupies its layout slot,
/// which leaves a gap where an empty-state panel used to be.
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>Non-zero count to Visibility - for badges.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };

        var visible = n > 0;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Empty or whitespace string to Visibility.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Negates a bool - for IsEnabled while a run is in flight.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is not bool b || !b;
}

/// <summary>
/// StoreStatus enum to a semantic brush key name for the status pill.
/// </summary>
public sealed class StoreStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ClaudeSessionBackup.Core.Model.StoreStatus status)
        {
            return "StatusMutedBrush";
        }

        return status switch
        {
            Core.Model.StoreStatus.Ok => "StatusOkBrush",
            Core.Model.StoreStatus.Verified => "StatusOkBrush",
            Core.Model.StoreStatus.SourceEmpty => "StatusMutedBrush",
            Core.Model.StoreStatus.SourceMissing => "StatusWarnBrush",
            Core.Model.StoreStatus.SourceEmptyBackupHasData => "StatusDangerBrush",
            Core.Model.StoreStatus.Failed => "StatusDangerBrush",
            Core.Model.StoreStatus.Error => "StatusDangerBrush",
            _ => "StatusMutedBrush",
        };
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Non-null to Visible, null to Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Epoch milliseconds to a local date-time display string.</summary>
public sealed class EpochMsToDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long ms || ms <= 0)
        {
            return "";
        }

        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
