using System.Text.Json;
using Vessel.Application.Persistence;
using Vessel.Domain.Settings;

namespace Vessel.Application.Settings;

/// <summary>
///     Typed access to instance-wide (<see cref="SettingScope.System" />, no team) settings stored as JSON in the
///     <c>settings</c> table. Missing or unreadable values fall back to the caller's default, so a corrupt row can never
///     stop the control plane. Writes are staged on the context; the caller saves.
/// </summary>
public sealed class SystemSettingsStore(IVesselDbContext dbContext, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public T Get<T>(string key, T fallback)
    {
        SettingEntry? entry = Find(key);
        if (entry is null) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(entry.Value, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public bool Exists(string key)
    {
        return Find(key) is not null;
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        SettingEntry? entry = Find(key);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (entry is null)
            await dbContext.SettingRepository.AddAsync(SettingEntry.Create(SettingScope.System, key, json, now),
                cancellationToken);
        else
            entry.UpdateValue(json, now);
    }

    private SettingEntry? Find(string key)
    {
        return dbContext.Settings.FirstOrDefault(setting =>
            setting.Scope == SettingScope.System && setting.TeamId == null && setting.Key == key);
    }
}
