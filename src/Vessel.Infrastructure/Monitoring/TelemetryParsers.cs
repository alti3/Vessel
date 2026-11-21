using System.Globalization;
using System.Text.Json;
using Vessel.Application.Docker;
using Vessel.Application.Monitoring.HostMetrics;

namespace Vessel.Infrastructure.Monitoring;

/// <summary>
///     Pure parsers for the agentless telemetry command output: <c>/proc/stat</c>, <c>/proc/meminfo</c>,
///     <c>/proc/loadavg</c>, <c>/proc/net/dev</c>, <c>/proc/uptime</c>, <c>df -Pk</c>, <c>docker stats</c> and the
///     structured container <c>inspect</c>. Every parser is lenient: a malformed section yields "unknown" values, never
///     an exception, so one odd host cannot break collection for the fleet.
/// </summary>
public static class TelemetryParsers
{
    /// <summary>Splits <c>@@name</c>-delimited sections.</summary>
    public static IReadOnlyDictionary<string, string> Sections(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is not null) sections[current] = string.Join('\n', buffer);
                current = line[2..].Trim();
                buffer.Clear();
                continue;
            }

            if (current is not null) buffer.Add(line);
        }

        if (current is not null) sections[current] = string.Join('\n', buffer);
        return sections;
    }

    /// <summary>Total and idle jiffies of the aggregate <c>cpu</c> line (idle includes iowait).</summary>
    public static (ulong Total, ulong Idle)? ParseCpuTimes(string? stat)
    {
        var line = (stat ?? string.Empty).Split('\n')
            .FirstOrDefault(item => item.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        if (fields.Length < 4) return null;
        var values = new ulong[Math.Min(fields.Length, 8)];
        for (var index = 0; index < values.Length; index++)
            if (!ulong.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]))
                return null;
        ulong total = 0;
        foreach (var value in values) total += value;
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total, idle);
    }

    public static decimal? CpuPercent((ulong Total, ulong Idle)? first, (ulong Total, ulong Idle)? second)
    {
        if (first is not { } a || second is not { } b || b.Total <= a.Total) return null;
        var total = (decimal)(b.Total - a.Total);
        var idle = b.Idle >= a.Idle ? (decimal)(b.Idle - a.Idle) : 0m;
        return Math.Round(Math.Clamp((total - idle) * 100m / total, 0m, 100m), 2);
    }

    /// <summary>Total and used memory in bytes (used = MemTotal - MemAvailable, with a MemFree+Buffers+Cached fallback).</summary>
    public static (long Total, long Used)? ParseMemory(string? meminfo)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in (meminfo ?? string.Empty).Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var parts = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib)) continue;
            values[line[..separator].Trim()] = kib * 1024;
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0) return null;
        long available;
        if (!values.TryGetValue("MemAvailable", out available))
            available = values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") +
                        values.GetValueOrDefault("Cached");
        return (total, Math.Clamp(total - available, 0, total));
    }

    public static (decimal Load1, decimal Load5, decimal Load15)? ParseLoad(string? loadavg)
    {
        var fields = (loadavg ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3) return null;
        return decimal.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one) &&
               decimal.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five) &&
               decimal.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen)
            ? (one, five, fifteen)
            : null;
    }

    public static decimal? ParseUptimeSeconds(string? uptime)
    {
        var field = (uptime ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return decimal.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    public static int? ParseCpuCount(string? nproc)
    {
        return int.TryParse((nproc ?? string.Empty).Trim().Split('\n')[0].Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : null;
    }

    /// <summary>
    ///     Received and transmitted bytes summed over physical interfaces (<c>lo</c> and container/bridge interfaces such
    ///     as <c>veth*</c>, <c>docker*</c>, <c>br-*</c> are skipped so container traffic is not counted twice).
    /// </summary>
    public static (ulong Rx, ulong Tx)? ParseNetwork(string? netDev)
    {
        ulong rx = 0, tx = 0;
        var any = false;
        foreach (var line in (netDev ?? string.Empty).Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            if (IsVirtualInterface(name)) continue;
            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 ||
                !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var received) ||
                !ulong.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var sent)) continue;
            rx += received;
            tx += sent;
            any = true;
        }

        return any ? (rx, tx) : null;
    }

    public static long? Rate(ulong? first, ulong? second, decimal? elapsedSeconds)
    {
        if (first is not { } a || second is not { } b || b < a) return null;
        var seconds = elapsedSeconds is > 0.05m ? elapsedSeconds.Value : 1m;
        return (long)Math.Round((b - a) / seconds);
    }

    /// <summary>Parses <c>df -Pk</c> (1024-byte blocks), one row per distinct mount point.</summary>
    public static IReadOnlyList<DiskUsageReading> ParseDisks(string? df)
    {
        var disks = new Dictionary<string, DiskUsageReading>(StringComparer.Ordinal);
        foreach (var line in (df ?? string.Empty).Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || fields[0] == "Filesystem") continue;
            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var available) ||
                total <= 0) continue;
            var mount = string.Join(' ', fields.Skip(5));
            if (!mount.StartsWith('/')) continue;
            disks.TryAdd(mount, new DiskUsageReading(mount, fields[0], total * 1024, used * 1024, available * 1024));
        }

        return disks.Values.ToArray();
    }

    /// <summary>Parses one JSON object per line of <c>docker stats --no-stream</c> with the collector's format.</summary>
    public static IReadOnlyList<ContainerStatsReading> ParseStats(string? stats)
    {
        var readings = new List<ContainerStatsReading>();
        foreach (var line in (stats ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                JsonElement root = document.RootElement;
                var id = Text(root, "id");
                if (id.Length == 0) continue;
                (long? memUsed, long? memLimit) = SizePair(Text(root, "mem"));
                (long? rx, long? tx) = SizePair(Text(root, "net"));
                readings.Add(new ContainerStatsReading(id, Text(root, "name").TrimStart('/'),
                    ParsePercent(Text(root, "cpu")), memUsed, memLimit, rx, tx));
            }
            catch (JsonException)
            {
                // A malformed line (for example a container that disappeared mid-read) is skipped.
            }
        }

        return readings;
    }

    /// <summary>Parses the collector's structured <c>inspect</c> output (one JSON object per line).</summary>
    public static IReadOnlyList<ContainerStateReading> ParseContainers(string? inspect)
    {
        var containers = new List<ContainerStateReading>();
        foreach (var line in (inspect ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                JsonElement root = document.RootElement;
                var id = Text(root, "id");
                if (id.Length == 0) continue;
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("labels", out JsonElement labelElement) &&
                    labelElement.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty label in labelElement.EnumerateObject())
                        if (label.Value.ValueKind == JsonValueKind.String)
                            labels[label.Name] = label.Value.GetString() ?? string.Empty;
                var health = Text(root, "health");
                containers.Add(new ContainerStateReading(
                    id,
                    Text(root, "name").TrimStart('/'),
                    Text(root, "image"),
                    Text(root, "imageId"),
                    Text(root, "state"),
                    health.Length == 0 ? null : health,
                    Int(root, "restartCount") ?? 0,
                    Int(root, "exitCode"),
                    Timestamp(Text(root, "startedAt")),
                    Timestamp(Text(root, "finishedAt")),
                    labels));
            }
            catch (JsonException)
            {
                // Skipped: a container removed between listing and inspecting it.
            }
        }

        return containers;
    }

    /// <summary>Parses a percentage such as <c>12.5%</c>.</summary>
    public static decimal? ParsePercent(string? value)
    {
        var text = (value ?? string.Empty).Trim().TrimEnd('%').Trim();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            ? Math.Round(percent, 2)
            : null;
    }

    /// <summary>Parses a Docker human size (<c>1.5GiB</c>, <c>12kB</c>, <c>0B</c>) into bytes.</summary>
    public static long? ParseSize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text == "--") return null;
        var index = 0;
        while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.')) index++;
        if (index == 0 ||
            !decimal.TryParse(text[..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        decimal multiplier = text[index..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1m,
            "KB" or "K" => 1_000m,
            "KIB" => 1_024m,
            "MB" or "M" => 1_000_000m,
            "MIB" => 1_048_576m,
            "GB" or "G" => 1_000_000_000m,
            "GIB" => 1_073_741_824m,
            "TB" or "T" => 1_000_000_000_000m,
            "TIB" => 1_099_511_627_776m,
            "PB" => 1_000_000_000_000_000m,
            _ => -1m
        };
        return multiplier < 0 ? null : (long)Math.Round(number * multiplier);
    }

    /// <summary>Parses the space a prune reports (<c>Total reclaimed space: 1.2GB</c>).</summary>
    public static long ParseReclaimedSpace(string? output)
    {
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var marker = line.IndexOf("reclaimed space:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;
            return ParseSize(line[(marker + "reclaimed space:".Length)..]) ?? 0;
        }

        return 0;
    }

    private static (long?, long?) SizePair(string value)
    {
        var parts = value.Split('/', 2);
        return parts.Length == 2 ? (ParseSize(parts[0]), ParseSize(parts[1])) : (ParseSize(value), null);
    }

    private static bool IsVirtualInterface(string name)
    {
        return name == "lo" || name.StartsWith("veth", StringComparison.Ordinal) ||
               name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal) ||
               name.StartsWith("cni", StringComparison.Ordinal) || name.StartsWith("flannel", StringComparison.Ordinal) ||
               name.StartsWith("virbr", StringComparison.Ordinal) || name.StartsWith("podman", StringComparison.Ordinal) ||
               name.StartsWith("cali", StringComparison.Ordinal) || name.StartsWith("vxlan", StringComparison.Ordinal);
    }

    private static string Text(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? Int(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : null;
    }

    public static DateTimeOffset? Timestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("0001-01-01", StringComparison.Ordinal)) return null;
        value = value.Trim();
        // RFC 3339 nano timestamps carry nine fraction digits; .NET parses at most seven.
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
            if (end - dot - 1 > 7) value = value[..(dot + 8)] + value[end..];
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
