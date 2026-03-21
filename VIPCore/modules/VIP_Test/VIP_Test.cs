using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using VipCoreApi;

namespace VIP_Test;

public class VipTest : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Test";
    public override string ModuleVersion => "v1.3.4";

    private const string VipTestCountFeature = "vip_test_count";
    private const string VipTestCooldownFeature = "vip_test_cooldown_until";
    private const string LegacyVipTestTable = "vipcore_test";
    private IVipCoreApi? _api;
    private IVipCoreApi Api => _api ?? throw new InvalidOperationException("VIPCore API is not initialized.");
    private Config _config = null!;
    private int _coreTimeMode;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _config = LoadConfig();
        _coreTimeMode = LoadCoreTimeMode();
        Task.Run(DropLegacyVipTestTableAsync);
    }

    [ConsoleCommand("css_viptest")]
    public void OnCommandVipTest(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        if (!_config.VipTestEnabled) return;

        if (Api.IsClientVip(controller))
        {
            Api.PrintToChat(controller, Api.GetTranslatedText("vip.AlreadyVipPrivileges"));
            return;
        }

        var authorizedSteamId = controller.AuthorizedSteamID;

        if (authorizedSteamId == null) return;

        Task.Run(() => GivePlayerVipTestAsync(controller, authorizedSteamId.SteamId64, _config));
    }

    private Task GivePlayerVipTestAsync(CCSPlayerController player, ulong steamId64, Config vipTest)
    {
        if (!player.IsValid)
            return Task.CompletedTask;

        var vipTestEndTime = Api.GetPlayerCookie<long>(steamId64, VipTestCooldownFeature);
        var vipTestCount = Api.GetPlayerCookie<int>(steamId64, VipTestCountFeature);

        if (vipTestCount >= vipTest.VipTestCount)
        {
            Server.NextFrame(() =>
                Api.PrintToChat(player, Api.GetTranslatedText("viptest.YouCanNoLongerTakeTheVip")));
            return Task.CompletedTask;
        }

        if (vipTestEndTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(vipTestEndTime) - DateTimeOffset.UtcNow;
            var timeRemainingFormatted =
                $"{(time.Days == 0 ? "" : $"{time.Days}d")} {time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";

            Server.NextFrame(() =>
                Api.PrintToChat(player, Api.GetTranslatedText("viptest.RetakenThrough", timeRemainingFormatted)));
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var coolDownTime = now + vipTest.VipTestCooldown;
        var durationInSeconds = GetDurationInSeconds(vipTest.VipTestDuration);

        Api.SetPlayerCookie(steamId64, VipTestCooldownFeature, coolDownTime);
        Api.SetPlayerCookie(steamId64, VipTestCountFeature, vipTestCount + 1);

        var timeRemaining = TimeSpan.FromSeconds(durationInSeconds);
        var durationText = vipTest.VipTestDuration == 0
            ? "Permanent"
            : timeRemaining.ToString(timeRemaining.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss");

        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;

            Api.PrintToChat(player,
                Api.GetTranslatedText("viptest.SuccessfullyPassed", durationText));
            Api.GiveClientVip(player, vipTest.VipTestGroup, vipTest.VipTestDuration);
        });

        return Task.CompletedTask;
    }

    private long GetDurationInSeconds(int duration)
    {
        if (duration <= 0)
            return 0;

        return _coreTimeMode switch
        {
            1 => duration * 60L,
            2 => duration * 3600L,
            3 => duration * 86400L,
            _ => duration
        };
    }

    private int LoadCoreTimeMode()
    {
        var coreConfigPath = Path.Combine(Api.CoreConfigDirectory, "vip_core.json");
        if (!File.Exists(coreConfigPath))
            return 0;

        try
        {
            var config = JsonSerializer.Deserialize<CoreConfigSnapshot>(File.ReadAllText(coreConfigPath),
                _jsonSerializerOptions);
            if (config == null)
                return 0;

            return config.TimeMode is >= 0 and <= 3 ? config.TimeMode : 0;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to read vip_core.json for TimeMode, fallback to seconds.");
            return 0;
        }
    }

    private async Task DropLegacyVipTestTableAsync()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(Api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();
            await using var command = dbConnection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS `{LegacyVipTestTable}`;";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Failed to drop legacy table {table}.", LegacyVipTestTable);
        }
    }

    private Config LoadConfig()
    {
        var configPath = Path.Combine(Api.ModulesConfigDirectory, "vip_test.json");

        if (!File.Exists(configPath)) return CreateConfig(configPath);

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), _jsonSerializerOptions)!;

        return config;
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            VipTestEnabled = true,
            VipTestDuration = 3600,
            VipTestCooldown = 86400,
            VipTestGroup = "group_name",
            VipTestCount = 2
        };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

        return config;
    }

    private sealed class CoreConfigSnapshot
    {
        public int TimeMode { get; init; }
    }
}

public class Config
{
    public bool VipTestEnabled { get; init; }
    public int VipTestDuration { get; init; }
    public int VipTestCooldown { get; init; }
    public required string VipTestGroup { get; init; }
    public int VipTestCount { get; init; }
}
