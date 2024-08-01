using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DhDnsSync;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _Logger;
    private readonly IDnsProvider _DnsProvider;
    private readonly IOptions<DnsConfig> _Config;

    public Worker(ILogger<Worker> logger, IDnsProvider dnsProvider, IOptions<DnsConfig> config)
    {
        _Logger = logger;
        _DnsProvider = dnsProvider;
        _Config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _Logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            await DoUpdate(_Config.Value).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(_Config.Value.UpdateIntervalMinutes), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetPublicIpAsync()
    {
        var retrievalHosts = new[] { "https://icanhazip.com/", "https://ipinfo.io/ip" };
        foreach (var host in retrievalHosts)
        {
            try
            {
                var response = await new HttpClient().GetStringAsync(new Uri(host, UriKind.Absolute)).ConfigureAwait(false);
                if (IPAddress.TryParse(response, out _))
                {
                    return response.Trim();
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }

    private async Task DoUpdate(DnsConfig config)
    {
        string? publicIp = null;
        if (config.Zones.Any(z => z.DnsRecords.Any(d => d.UpdateMode == RecordUpdateMode.PublicIp)))
        {
            publicIp = await GetPublicIpAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                _Logger.LogError("Failed to retrieve public ip address. Update aborted.");
                return;
            }
        }

        foreach (var zoneConfig in config.Zones)
        {
            _Logger.LogInformation("Processing zone {Zone}", zoneConfig.Name);
            var existingDnsRecords = await _DnsProvider.GetDnsListAsync(config.ApiKey, zoneConfig.Name).ConfigureAwait(false);

            foreach (var configuredRecord in zoneConfig.DnsRecords)
            {
                var result = await ProcessRemovalAsync(config.ApiKey, zoneConfig, configuredRecord, existingDnsRecords, publicIp).ConfigureAwait(false);
                if (result == DnsActionResult.Error)
                {
                    continue;
                }

                result = await ProcessAddAsync(config.ApiKey, zoneConfig, configuredRecord, existingDnsRecords, publicIp).ConfigureAwait(false);
            }
        }
    }

    private static DnsRecord? Match(DnsZone zoneConfig, DnsRecord configuredRecord, IReadOnlyList<DnsRecord> dnsRecords)
    {
        return dnsRecords.FirstOrDefault(d => d.Name == zoneConfig.Qualify(configuredRecord.Name) && d.Type == configuredRecord.Type.ToString());
    }

    private async Task<DnsActionResult> ProcessAddAsync(string apiKey, DnsZone zoneConfig, DnsRecord configuredRecord, IReadOnlyList<DnsRecord> existingRecords, string? publicIp)
    {
        if (Match(zoneConfig, configuredRecord, existingRecords) != null)
        {
            return DnsActionResult.Skipped;
        }

        string valueToAdd = configuredRecord.UpdateMode switch
        {
            RecordUpdateMode.EnsureExists => configuredRecord.Value,
            RecordUpdateMode.PublicIp => publicIp ?? throw new Exception("Public ip is missing"),
            _ => throw new ArgumentOutOfRangeException()
        };

        if (!await _DnsProvider.AddDnsRecordAsync(apiKey, zoneConfig.Qualify(configuredRecord.Name), configuredRecord.Type.ToString(), valueToAdd).ConfigureAwait(false))
        {
            return DnsActionResult.Error;
        }

        return DnsActionResult.Success;
    }

    private async Task<DnsActionResult> ProcessRemovalAsync(string apiKey, DnsZone zoneConfig, DnsRecord configuredRecord, List<DnsRecord> existingRecords, string? publicIp)
    {
        var existingRecord = Match(zoneConfig, configuredRecord, existingRecords);
        if (existingRecord == default)
        {
            return DnsActionResult.Skipped;
        }

        if (configuredRecord.UpdateMode == RecordUpdateMode.PublicIp && existingRecord.Value != publicIp)
        {
            if (await _DnsProvider.RemoveDnsRecordAsync(apiKey, existingRecord).ConfigureAwait(false))
            {
                existingRecords.Remove(existingRecord);
                return DnsActionResult.Success;
            }

            _Logger.LogError("Failed to remove dns record {RecordName}", existingRecord.Name);
            return DnsActionResult.Error;
        }

        return DnsActionResult.Skipped;
    }
}

public interface IDnsProvider
{
    Task<List<DnsRecord>> GetDnsListAsync(string apiKey, string zoneName);
    Task<bool> AddDnsRecordAsync(string apiKey, string record, string type, string value);
    Task<bool> RemoveDnsRecordAsync(string apiKey, DnsRecord record);
}

public class DnsConfig
{
    public int UpdateIntervalMinutes { get; set; }
    public string ApiKey { get; set; }
    public List<DnsZone> Zones { get; set; }
}

public class DnsZone
{
    public string Name { get; set; }
    public List<DnsRecord> DnsRecords { get; set; }

    public string Qualify(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "@")
        {
            return Name;
        }

        return $"{name}.{Name}";
    }
}

public enum RecordUpdateMode
{
    EnsureExists,
    PublicIp
}

public class DnsRecord
{
    public RecordUpdateMode UpdateMode { get; set; }
    public string Type { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }
}

public enum DnsActionResult
{
    Success,
    Skipped,
    Error
}

public class DreamHostDnsProvider : IDnsProvider
{
    private static readonly HttpClient HttpClient = new() { BaseAddress = new Uri("https://api.dreamhost.com") };

    public async Task<List<DnsRecord>> GetDnsListAsync(string apiKey, string zoneName)
    {
        var response = await HttpClient.GetStringAsync($"/?key={apiKey}&format=json&cmd=dns-list_records").ConfigureAwait(false);
        var asDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response);
        if (asDict?.TryGetValue("result", out var successObj) != true || successObj.GetString() != "success")
        {
            return new List<DnsRecord>();
        }

        if (asDict.TryGetValue("data", out var data))
        {
            return data.Deserialize<List<DnsRecord>>() ?? new List<DnsRecord>();
        }

        return new List<DnsRecord>();
    }

    public async Task<bool> AddDnsRecordAsync(string apiKey, string record, string type, string value)
    {
        string recordArgs = $"&record={record}&type={type}&value={value}";
        var response = await HttpClient.GetStringAsync($"/?key={apiKey}&format=json&cmd=dns-add_record{recordArgs}").ConfigureAwait(false);
        var asDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response);
        return asDict?.TryGetValue("result", out var successObj) == true && successObj.GetString() == "success";
    }

    public async Task<bool> RemoveDnsRecordAsync(string apiKey, DnsRecord record)
    {
        string recordArgs = $"&record={record.Name}&type={record.Type}&value={record.Value}";
        var response = await HttpClient.GetStringAsync($"/?key={apiKey}&format=json&cmd=dns-remove_record{recordArgs}").ConfigureAwait(false);
        var asDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response);
        return asDict?.TryGetValue("result", out var successObj) == true && successObj.GetString() == "success";
    }
}
