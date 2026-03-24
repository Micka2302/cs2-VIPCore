using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIPCore;

public class VipCore : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Core";
    public override string ModuleVersion => "v1.3.4";

    public Config Config { get; set; } = null!;
    public CoreConfig CoreConfig { get; set; } = null!;
    public VipCoreApi VipApi { get; set; } = null!;

    public Database Database = null!;

    public readonly bool[] IsClientVip = new bool[70];

    public readonly ConcurrentDictionary<ulong, User> Users = new();
    public readonly ConcurrentDictionary<string, Feature> Features = new();

    public readonly HashSet<string> ForcedDisabledFeatures = new();

    private readonly PluginCapability<IVipCoreApi> _pluginCapability = new("vipcore:core");

    public readonly FakeConVar<bool> IsCoreEnableConVar = new("css_vip_enable", "", true);

    public string DbConnectionString = string.Empty;


    private string[] _sortedItems = [];

    public override void Load(bool hotReload)
    {
        VipApi = new VipCoreApi(this);
        Capabilities.RegisterPluginCapability(_pluginCapability, () => VipApi);
        Server.NextWorldUpdate(() => VipApi.CoreReady());

        LoadConfig();

        DbConnectionString = BuildConnectionString();
        Database = new Database(this, Logger, DbConnectionString);

        Task.Run(() => Database.CreateTable());

        RegisterEventHandlers();
        SetupTimers();

        AddCommand("css_vip", "command that opens the VIP MENU", (player, _) => CreateMenu(player));
    }

    private void LoadConfig()
    {
        var coreConfigDirectory = VipApi.CoreConfigDirectory;

        Config = VipApi.LoadConfig<Config>("vip", coreConfigDirectory);
        CoreConfig = VipApi.LoadConfig<CoreConfig>("vip_core", coreConfigDirectory);

        var sortMenuPath = Path.Combine(coreConfigDirectory, "sort_menu.txt");

        if (!File.Exists(sortMenuPath))
            File.WriteAllLines(sortMenuPath, ["feature1", "feature2"]);

        _sortedItems = File.ReadAllLines(sortMenuPath);
    }

    private void RegisterEventHandlers()
    {
        RegisterListener<Listeners.OnClientAuthorized>((slot, id) =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid) return;

            Task.Run(() => OnClientAuthorizedAsync(player, id));
        });

        RegisterListener<Listeners.OnMapStart>(_ => VipApi.LoadCookies());
        RegisterListener<Listeners.OnMapEnd>(() => VipApi.SaveCookies());
        RegisterEventHandler<EventServerShutdown>((@event, info) =>
        {
            VipApi.SaveCookies();
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);
        RegisterEventHandler<EventPlayerSpawn>(EventPlayerSpawn);
    }

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsClientVip[player.Slot])
            return HookResult.Continue;

        var steamId64 = GetPlayerSteamId64(player);

        IsClientVip[player.Slot] = false;
        if (Users.TryGetValue(steamId64, out var user))
        {
            foreach (var featureState in user.FeatureState.Where(f =>
                         Features[f.Key].FeatureType is FeatureType.Toggle))
            {
                VipApi.SetPlayerCookie(steamId64, featureState.Key, (int)featureState.Value);
            }
        }

        Users.Remove(steamId64, out var _);

        var authAccId = player.AuthorizedSteamID;
        if (authAccId == null) return HookResult.Continue;

        var playerName = player.PlayerName;
        Task.Run(() => Database.UpdateUserVip((long)authAccId.SteamId64, name: playerName));

        return HookResult.Continue;
    }

    private HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid || player.Handle == IntPtr.Zero || player.UserId == null)
            return HookResult.Continue;

        if (player.IsBot || !IsClientVip[player.Slot])
            return HookResult.Continue;

        var playerSlot = player.Slot;

        AddTimer(Config.Delay, () =>
        {
            var delayedPlayer = Utilities.GetPlayerFromSlot(playerSlot);
            if (delayedPlayer is null || !delayedPlayer.IsValid || delayedPlayer.Handle == IntPtr.Zero ||
                delayedPlayer.UserId == null)
                return;

            if (delayedPlayer.IsBot || !IsClientVip[delayedPlayer.Slot])
                return;

            if (delayedPlayer.Connected != PlayerConnectedState.PlayerConnected) return;

            try
            {
                VipApi.PlayerSpawn(delayedPlayer);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception in VipApi.PlayerSpawn: {ex}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void SetupTimers()
    {
        AddTimer(300.0f, () =>
        {
            foreach (var player in Utilities.GetPlayers()
                         .Where(player => player.IsValid))
            {
                var authId = player.AuthorizedSteamID;
                if (authId == null) continue;

                Task.Run(() => Database.RemoveExpiredUsers(player, authId));

                IsClientVip[player.Slot] = IsUserActiveVip(player);
            }
        }, TimerFlags.REPEAT);
    }

    public async Task OnClientAuthorizedAsync(CCSPlayerController player, SteamID steamId)
    {
        try
        {
            var steamId64 = steamId.SteamId64;
            var accountId = (long)steamId64;

            // Refresh in-memory state and permissions at each connection.
            if (Users.TryRemove(steamId64, out var cachedUser))
            {
                await Server.NextFrameAsync(() =>
                {
                    if (!player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
                        return;

                    VipApi.OnPlayerRemoved(player, cachedUser.group);
                });
            }

            IsClientVip[player.Slot] = false;

            var user = await Database.GetExistingUserFromDb(accountId);
            if (user == null)
            {
                Logger.LogInformation(
                    "[VIP CHECK] AccountId {AccountId} -> no VIP found for this server.",
                    accountId);
                return;
            }

            if (!TryResolveVipGroup(user.group, out var resolvedVipGroup, out _))
            {
                Logger.LogWarning(
                    "[VIP CHECK] AccountId {AccountId} -> VIP group '{VipGroup}' not found in vip.json.",
                    accountId,
                    user.group);
            }
            else if (!string.Equals(user.group, resolvedVipGroup, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "[VIP CHECK] AccountId {AccountId} -> resolved VIP group '{InputGroup}' to '{ResolvedGroup}'.",
                    accountId,
                    user.group,
                    resolvedVipGroup);
                user.group = resolvedVipGroup;
            }

            var now = DateTime.UtcNow.GetUnixEpoch();
            var expirationDebugText = user.expires == 0
                ? "never"
                : DateTimeOffset.FromUnixTimeSeconds(user.expires).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            var isVipActive = user.expires == 0 || now < user.expires;

            Logger.LogInformation(
                "[VIP CHECK] User {UserName} [{AccountId}] -> group: {VipGroup}, active: {IsActive}, expires: {ExpiresUnix} ({ExpiresText})",
                user.name,
                accountId,
                user.group,
                isVipActive,
                user.expires,
                expirationDebugText);

            if (!isVipActive)
            {
                await Database.RemoveUserFromDb(accountId);
                await Server.NextFrameAsync(() => VipApi.OnPlayerRemoved(player, user.group));
                return;
            }

            Users[steamId64] = user;
            SetClientFeature(steamId64, user.group);

            var expirationText = user.expires == 0
                ? string.Empty
                : Localizer["vip.Expires", user.group, DateTimeOffset.FromUnixTimeSeconds(user.expires).ToString("G")];

            await Server.NextFrameAsync(() =>
            {
                if (TryFinalizeVipLoad(player, user, expirationText))
                    return;

                var attempts = 0;
                const int maxAttempts = 30; // ~3 seconds
                CounterStrikeSharp.API.Modules.Timers.Timer retryTimer = null!;
                retryTimer = AddTimer(0.1f, () =>
                {
                    attempts++;

                    if (TryFinalizeVipLoad(player, user, expirationText))
                    {
                        retryTimer.Kill();
                        return;
                    }

                    if (!player.IsValid || attempts >= maxAttempts)
                    {
                        Logger.LogWarning(
                            "[VIP CHECK] AccountId {AccountId} -> player not ready after authorization, skipped VIP load.",
                            accountId);
                        retryTimer.Kill();
                    }
                }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            });
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }
    }

    public void SetClientFeature(ulong steamId, string vipGroup)
    {
        if (!Users.TryGetValue(steamId, out var user))
            return;

        if (!TryResolveVipGroup(vipGroup, out var resolvedGroup, out var group))
        {
            foreach (var (key, _) in Features)
                user.FeatureState[key] = FeatureState.NoAccess;

            Logger.LogWarning(
                "[VIP CHECK] AccountId {AccountId} -> unable to apply features, group '{VipGroup}' not found.",
                user.account_id,
                vipGroup);
            return;
        }

        user.group = resolvedGroup;

        foreach (var (key, _) in Features)
        {
            if (!group.Values.TryGetValue(key, out _))
            {
                user.FeatureState[key] = FeatureState.NoAccess;
                continue;
            }

            var cookie = VipApi.GetPlayerCookie<int>(steamId, key);
            var cookieValue = cookie == 2 ? 0 : cookie;
            user.FeatureState[key] = (FeatureState)cookieValue;
        }
    }

    public User CreateNewUser(long accountId, string username, string group, int endTime)
    {
        return new User
        {
            account_id = accountId,
            name = username,
            lastvisit = DateTime.UtcNow.GetUnixEpoch(),
            sid = CoreConfig.ServerId,
            group = group,
            expires = endTime == 0 ? 0 : CalculateEndTimeInSeconds(endTime)
        };
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_adduser")]
    public void OnCmdAddUser(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is > 4 or < 4)
        {
            PrintLogInfo("Usage: css_vip_adduser {usage}", $"<steamid or accountid> <group> <time_{GetTimeUnitName}>");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        var vipGroup = command.GetArg(2);
        var endVipTime = Convert.ToInt32(command.GetArg(3));

        if (!TryResolveVipGroup(vipGroup, out vipGroup, out _))
        {
            PrintLogError("This {VIP} group was not found!", "VIP");
            return;
        }

        var username = player == null ? "unknown" : player.PlayerName;

        var user = CreateNewUser(accountId, username, vipGroup, endVipTime);

        AddVip(player, user);
    }

    public void AddVip(CCSPlayerController? player, User user) // :)
    {
        Task.Run(() => Database.AddUserToDb(user));

        if (player == null) return;

        var steamId64 = GetPlayerSteamId64(player);
        Users.TryAdd(steamId64, user);
        IsClientVip[player.Slot] = true;

        SetClientFeature(steamId64, user.group);
        VipApi.OnPlayerLoaded(player, user.group);
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_deleteuser")]
    public void OnCmdDeleteVipUser(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is < 2 or > 2)
        {
            ReplyToCommand(controller, "Using: css_vip_deleteuser <steamid or accountid>");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        RemoveVip(player, accountId);
    }

    public void RemoveVip(CCSPlayerController? player, long accountId) // :)
    {
        if (player != null)
        {
            var steamId64 = GetPlayerSteamId64(player);
            if (Users.TryGetValue(steamId64, out var user))
            {
                VipApi.OnPlayerRemoved(player, user.group);
            }

            Users.TryRemove(steamId64, out _);
            IsClientVip[player.Slot] = false;
        }

        Task.Run(() => Database.RemoveUserFromDb(accountId));
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_updateuser")]
    public void OnCmdUpdateUserGroup(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is > 4 or < 4)
        {
            PrintLogInfo("Usage: css_vip_updateuser {usage}\n{t}", "<steamid or accountid> [group or -s] [time or -s]",
                "if you don't want to update something, don't leave it blank, write `-` or `-s`\nExample of updating time: css_vip_updateuser \"STEAM_0:0:123456\" -s 3600");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        var vipGroup = command.GetArg(2);

        if (vipGroup is not ("-" or "-s"))
        {
            if (!TryResolveVipGroup(vipGroup, out vipGroup, out _))
            {
                PrintLogError("This {VIP} group was not found!", "VIP");
                return;
            }
        }
        else
            vipGroup = string.Empty;

        var time = int.TryParse(command.GetArg(3), out var arg) ? arg : -1;

        if (player != null)
        {
            var steamId64 = GetPlayerSteamId64(player);
            if (!Users.TryGetValue(steamId64, out var user)) return;

            user.group = vipGroup;
        }

        Task.Run(() => Database.UpdateUserVip(accountId, group: vipGroup, time: time));
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<steamid>")]
    [ConsoleCommand("css_reload_vip_player")]
    public void OnCommandVipReloadInfractions(CCSPlayerController? player, CommandInfo command)
    {
        var target = Utils.GetPlayerFromSteamId(command.GetArg(1));

        if (target == null) return;
        if (target.AuthorizedSteamID == null) return;

        var steamid = target.AuthorizedSteamID;

        Task.Run(async () => await OnClientAuthorizedAsync(target, steamid));
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_reload")]
    public void OnCommandReloadConfig(CCSPlayerController? controller, CommandInfo command)
    {
        LoadConfig();

        const string msg = "configuration successfully rebooted!";

        ReplyToCommand(controller, msg);
    }

    private void CreateMenu(CCSPlayerController? player)
    {
        if (player == null) return;

        if (!IsClientVip[player.Slot])
        {
            IsClientVip[player.Slot] = IsUserActiveVip(player);
        }

        if (!IsClientVip[player.Slot])
        {
            PrintToChat(player, Localizer["vip.NoAccess"]);
            return;
        }

        var steamId64 = GetPlayerSteamId64(player);
        if (!Users.TryGetValue(steamId64, out var user))
        {
            var authorizedSteamId = player.AuthorizedSteamID;
            if (authorizedSteamId != null)
            {
                Task.Run(() => OnClientAuthorizedAsync(player, authorizedSteamId));
            }

            PrintToChat(player, Localizer["vip.NoAccess"]);
            return;
        }

        if (!TryResolveVipGroup(user.group, out var resolvedGroup, out var vipGroup))
        {
            Logger.LogWarning(
                "[VIP MENU] AccountId {AccountId} -> VIP group '{VipGroup}' not found in vip.json.",
                user.account_id,
                user.group);
            PrintToChat(player, Localizer["vip.NoAccess"]);
            return;
        }

        user.group = resolvedGroup;

        var menu = VipApi.CreateMenu(Localizer["menu.Title", user.group]);
        if (Config.Groups.TryGetValue(user.group, out vipGroup))
        {
            var sortedFeatures = Features.Where(setting => setting.Value.FeatureType is not FeatureType.Hide)
                .OrderBy(setting => Array.IndexOf(_sortedItems, setting.Key))
                .ThenBy(setting => setting.Key);

            foreach (var (key, feature) in sortedFeatures)
            {
                if (!vipGroup.Values.TryGetValue(key, out var featureValue)) continue;
                if (string.IsNullOrEmpty(featureValue.ToString())) continue;
                if (!user.FeatureState.TryGetValue(key, out var featureState)) continue;

                var value = string.Empty;
                if (feature.FeatureType is FeatureType.Toggle)
                {
                    value = featureState switch
                    {
                        FeatureState.Enabled => $"{Localizer["chat.Enabled"]}",
                        FeatureState.Disabled => $"{Localizer["chat.Disabled"]}",
                        FeatureState.NoAccess => $"{Localizer["chat.NoAccess"]}",
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }

                var featureType = feature.FeatureType;

                menu.AddMenuOption(
                    Localizer[key] + (featureType == FeatureType.Selectable
                        ? string.Empty
                        : $" [{value}]"),
                    (controller, _) =>
                    {
                        var result = VipApi.PlayerUseFeature(player, key, featureState, featureType);

                        if (result == HookResult.Handled || result == HookResult.Stop)
                        {
                            CreateMenu(player);
                            return;
                        }

                        var returnState = featureState;
                        if (featureType != FeatureType.Selectable)
                        {
                            returnState = featureState switch
                            {
                                FeatureState.Enabled => FeatureState.Disabled,
                                FeatureState.Disabled => FeatureState.Enabled,
                                _ => returnState
                            };

                            VipApi.PrintToChat(player,
                                $"{Localizer[key]}: {(returnState == FeatureState.Enabled ? $"{Localizer["chat.Enabled"]}" : $"{Localizer["chat.Disabled"]}")}");
                        }

                        user.FeatureState[key] = returnState;
                        feature.OnSelectItem?.Invoke(controller, returnState);

                        if (CoreConfig.ReOpenMenuAfterItemClick && featureType != FeatureType.Selectable)
                        {
                            CreateMenu(controller);
                        }
                    }, featureState == FeatureState.NoAccess || ForcedDisabledFeatures.Contains(key));
            }
        }

        menu.Open(player);
    }


    private string BuildConnectionString()
    {
        var connection = CoreConfig.Connection;
        var builder = new MySqlConnectionStringBuilder
        {
            Database = connection.Database,
            UserID = connection.User,
            Password = connection.Password,
            Server = connection.Host,
            Port = (uint)connection.Port,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 640,
            ConnectionIdleTimeout = 30
        };

        Console.WriteLine("OK!");
        return builder.ConnectionString;
    }

    public bool IsPlayerVip(CCSPlayerController player)
    {
        return IsClientVip[player.Slot];
    }

    private static ulong GetPlayerSteamId64(CCSPlayerController player)
    {
        return player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
    }

    private bool IsUserActiveVip(CCSPlayerController player)
    {
        if (!IsCoreEnableConVar.Value || !Utils.IsValidEntity(player) || !player.IsValid || player.IsBot)
            return false;

        var authorizedSteamId = player.AuthorizedSteamID;
        if (authorizedSteamId == null)
        {
            PrintLogError("{steamid} is null", "AuthorizedSteamId");
            return false;
        }

        if (!Users.TryGetValue(authorizedSteamId.SteamId64, out var user))
            return false;

        if (user.expires != 0 && DateTime.UtcNow.GetUnixEpoch() > user.expires)
        {
            Users.Remove(authorizedSteamId.SteamId64, out _);
            return false;
        }

        return user.expires == 0 || DateTime.UtcNow.GetUnixEpoch() < user.expires;
    }

    private void ReplyToCommand(CCSPlayerController? controller, string msg)
    {
        if (controller != null)
            PrintToChat(controller, msg);
        else
            PrintLogInfo($"{msg}");
    }

    public void PrintToChat(CCSPlayerController player, string msg)
    {
        if (!player.IsValid) return;

        player.PrintToChat($"{Localizer["vip.Prefix"]} {msg}");
    }

    public void PrintToChatAll(string msg)
    {
        Server.PrintToChatAll($"{Localizer["vip.Prefix"]} {msg}");
    }

    public void PrintLogError(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogError($"{message}", args);
    }

    public void PrintLogInfo(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogInformation($"{message}", args);
    }

    public void PrintLogWarning(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogWarning($"{message}", args);
    }

    public bool TryResolveVipGroup(string? vipGroupName, out string resolvedGroupName, out VipGroup vipGroup)
    {
        resolvedGroupName = string.Empty;
        vipGroup = null!;

        if (string.IsNullOrWhiteSpace(vipGroupName))
            return false;

        var normalized = vipGroupName.Trim();
        var candidates = new List<string> { normalized };

        if (normalized.StartsWith('#'))
            candidates.Add(normalized[1..]);
        else
            candidates.Add($"#{normalized}");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Config.Groups.TryGetValue(candidate, out vipGroup))
            {
                resolvedGroupName = candidate;
                return true;
            }

            var caseInsensitiveMatchKey = Config.Groups.Keys.FirstOrDefault(entryKey =>
                string.Equals(entryKey, candidate, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(caseInsensitiveMatchKey) &&
                Config.Groups.TryGetValue(caseInsensitiveMatchKey, out vipGroup))
            {
                resolvedGroupName = caseInsensitiveMatchKey;
                return true;
            }
        }

        return false;
    }

    private bool TryFinalizeVipLoad(CCSPlayerController player, User user, string expirationText)
    {
        if (!player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
            return false;

        VipApi.OnPlayerLoaded(player, user.group);
        IsClientVip[player.Slot] = true;
        Logger.LogInformation(
            "[VIP CHECK] AccountId {AccountId} -> PlayerLoaded dispatched for group '{VipGroup}'.",
            user.account_id,
            user.group);

        AddTimer(5.0f, () =>
        {
            if (!player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
                return;

            PrintToChat(player, Localizer["vip.WelcomeToTheServer", user.name] + expirationText);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    private string GetTimeUnitName => CoreConfig.TimeMode switch
    {
        0 => "second",
        1 => "minute",
        2 => "hours",
        3 => "days",
        _ => throw new KeyNotFoundException("No such number was found!")
    };

    public long CalculateEndTimeInSeconds(int time) => DateTime.UtcNow.AddSeconds(CoreConfig.TimeMode switch
    {
        1 => time * 60,
        2 => time * 3600,
        3 => time * 86400,
        _ => time
    }).GetUnixEpoch();
}

public class User
{
    public long account_id { get; set; }
    public required string name { get; set; }
    public long lastvisit { get; set; }
    public long sid { get; set; }
    public required string group { get; set; }
    public long expires { get; set; }
    public DateTime? expiration { get; set; }
    public Dictionary<string, FeatureState> FeatureState { get; set; } = new();
}

public class PlayerCookie
{
    public ulong SteamId64 { get; set; }
    public ConcurrentDictionary<string, object> Features { get; set; } = new();
}

public class Feature
{
    public FeatureType FeatureType { get; set; }
    public Action<CCSPlayerController, FeatureState>? OnSelectItem { get; set; }
}

public static class GetUnixTime
{
    public static long GetUnixEpoch(this DateTime dateTime)
    {
        var unixTime = dateTime.ToUniversalTime() -
                       new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return (long)unixTime.TotalSeconds;
    }
}
