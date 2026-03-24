using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VIP_Flags;

public class VipFlags : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Flags";
    public override string ModuleVersion => "v1.3.4";

    private IVipCoreApi? _api;
    private Flags _flags = null!;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();

        if (_api == null) return;

        _flags = new Flags(this, _api);
        _api.RegisterFeature(_flags, FeatureType.Hide);
    }

    public override void Unload(bool hotReload)
    {
        _api?.UnRegisterFeature(_flags);
    }
}

public class Flags : VipFeatureBase
{
    private sealed record AppliedAccess(string Value, bool IsPermission);

    private readonly VipFlags _vipFlags;
    public override string Feature => "flags";
    private readonly Dictionary<ulong, List<AppliedAccess>> _appliedAccessByPlayer = new();

    public Flags(VipFlags vipFlags, IVipCoreApi api) : base(api)
    {
        _vipFlags = vipFlags;
        vipFlags.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var player = @event.Userid;

            if (player is null || !player.IsValid) return HookResult.Continue;

            RemovePlayerPermissions(player);

            return HookResult.Continue;
        });

        vipFlags.RegisterEventHandler<EventRoundStart>((@event, _) =>
        {
            foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid))
            {
                if (player.IsBot || !player.IsValid || player.Handle == IntPtr.Zero || player.UserId == null)
                    continue;

                if (player.Connected != PlayerConnectedState.PlayerConnected)
                    continue;

                if (!PlayerHasFeature(player))
                    continue;

                var configuredAccess = GetFlagsOrGroups(player);
                if (configuredAccess.Count == 0 || HasAllConfiguredAccess(player, configuredAccess))
                    continue;

                if (TryApplyPlayerPermissions(player))
                {
                    _vipFlags.Logger.LogInformation(
                        "[VIP FLAGS] Applied missing permissions/groups on round start for player {SteamId64}.",
                        GetPlayerSteamId64(player));
                }
            }

            return HookResult.Continue;
        });
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (!PlayerHasFeature(player))
        {
            var steamId64 = GetPlayerSteamId64(player);
            var vipGroup = group;
            try
            {
                vipGroup = GetClientVipGroup(player);
            }
            catch
            {
                // Keep event group fallback.
            }

            _vipFlags.Logger.LogInformation(
                "[VIP FLAGS] Skipping apply for player {SteamId64}, group '{VipGroup}': feature 'flags' is missing or empty in vip.json.",
                steamId64,
                vipGroup);
            return;
        }

        // Apply immediately when player VIP is loaded (connection phase),
        // and retry briefly only while player state is still initializing.
        if (TryApplyPlayerPermissions(player))
            return;

        var attempts = 0;
        var maxAttempts = 20; // ~2 seconds total
        Timer retryTimer = null!;
        retryTimer = _vipFlags.AddTimer(0.1f, () =>
        {
            attempts++;

            if (!player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected || attempts >= maxAttempts)
            {
                retryTimer.Kill();
                return;
            }

            if (TryApplyPlayerPermissions(player))
                retryTimer.Kill();
        }, TimerFlags.REPEAT);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        RemovePlayerPermissions(player);
    }

    private void RemovePlayerPermissions(CCSPlayerController player)
    {
        var steamId64 = GetPlayerSteamId64(player);
        if (!_appliedAccessByPlayer.TryGetValue(steamId64, out var value)) return;

        var steamId = new SteamID(steamId64);
        foreach (var appliedAccess in value.ToList())
        {
            if (appliedAccess.IsPermission)
                AdminManager.RemovePlayerPermissions(steamId, appliedAccess.Value);
            else
                AdminManager.RemovePlayerFromGroup(steamId, removeInheritedFlags: true, groups: appliedAccess.Value);

            value.Remove(appliedAccess);
        }

        if (value.Count == 0)
            _appliedAccessByPlayer.Remove(steamId64);
    }

    private List<string> GetFlagsOrGroups(CCSPlayerController player)
    {
        try
        {
            var flagsOrGroupsElement = GetFeatureValue<JsonElement>(player);

            if (flagsOrGroupsElement.ValueKind == JsonValueKind.Array)
            {
                return flagsOrGroupsElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim())
                    .ToList();
            }

            if (flagsOrGroupsElement.ValueKind == JsonValueKind.String)
            {
                return (flagsOrGroupsElement.GetString() ?? string.Empty)
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            }
        }
        catch (Exception e)
        {
            _vipFlags.Logger.LogError(
                "[VIP FLAGS] Failed to parse flags/groups for player: {Error}",
                e.Message);
        }

        return [];
    }

    private static ulong GetPlayerSteamId64(CCSPlayerController player)
    {
        return player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;
    }

    private static bool HasAllConfiguredAccess(CCSPlayerController player, IReadOnlyCollection<string> configuredAccess)
    {
        foreach (var raw in configuredAccess)
        {
            var value = raw.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value.StartsWith('@'))
            {
                if (!AdminManager.PlayerHasPermissions(player, value))
                    return false;
            }
            else
            {
                if (!AdminManager.PlayerInGroup(player, value))
                    return false;
            }
        }

        return true;
    }

    private bool TryApplyPlayerPermissions(CCSPlayerController player)
    {
        if (!player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
            return false;

        var steamId64 = GetPlayerSteamId64(player);
        if (steamId64 == 0)
        {
            _vipFlags.Logger.LogWarning("[VIP FLAGS] Unable to resolve SteamID64 for player.");
            return false;
        }

        // Avoid duplicates/stale data when player VIP/group is reloaded.
        RemovePlayerPermissions(player);
        if (!_appliedAccessByPlayer.ContainsKey(steamId64))
            _appliedAccessByPlayer.Add(steamId64, []);

        var flagsOrGroups = GetFlagsOrGroups(player);
        if (flagsOrGroups.Count == 0)
        {
            _vipFlags.Logger.LogWarning("[VIP FLAGS] No flags/groups configured for player {SteamId64}.", steamId64);
            return true;
        }

        var steamId = new SteamID(steamId64);
        foreach (var rawFlagOrGroup in flagsOrGroups)
        {
            var flagOrGroup = rawFlagOrGroup.Trim();
            if (string.IsNullOrWhiteSpace(flagOrGroup))
                continue;

            if (flagOrGroup.StartsWith('@'))
            {
                if (AdminManager.PlayerHasPermissions(player, flagOrGroup))
                {
                    _vipFlags.Logger.LogInformation(
                        "[VIP FLAGS] Player {SteamId64} already has permission '{FlagOrGroup}'.",
                        steamId64,
                        flagOrGroup);
                    continue;
                }

                AdminManager.AddPlayerPermissions(steamId, flagOrGroup);
                _appliedAccessByPlayer[steamId64].Add(new AppliedAccess(flagOrGroup, true));
            }
            else if (flagOrGroup.StartsWith('#'))
            {
                if (AdminManager.PlayerInGroup(player, flagOrGroup))
                {
                    _vipFlags.Logger.LogInformation(
                        "[VIP FLAGS] Player {SteamId64} is already in group '{FlagOrGroup}'.",
                        steamId64,
                        flagOrGroup);
                    continue;
                }

                AdminManager.AddPlayerToGroup(steamId, flagOrGroup);
                _appliedAccessByPlayer[steamId64].Add(new AppliedAccess(flagOrGroup, false));
            }
            else
            {
                if (AdminManager.PlayerInGroup(player, flagOrGroup))
                {
                    _vipFlags.Logger.LogInformation(
                        "[VIP FLAGS] Player {SteamId64} is already in group '{FlagOrGroup}'.",
                        steamId64,
                        flagOrGroup);
                    continue;
                }

                AdminManager.AddPlayerToGroup(steamId, flagOrGroup);
                _appliedAccessByPlayer[steamId64].Add(new AppliedAccess(flagOrGroup, false));
            }

            _vipFlags.Logger.LogInformation(
                "[VIP FLAGS] Applied '{FlagOrGroup}' to player {SteamId64}.",
                flagOrGroup,
                steamId64);
        }

        return true;
    }
}
