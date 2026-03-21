using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;

namespace VIPCore;

public static class Utils
{
    private const long SteamId64IdentifierOffset = 76561197960265728;

    public static bool IsValidEntity(CEntityInstance ent)
    {
        return ent.IsValid;
    }

    public static long GetAccountIdFromCommand(string steamId, out CCSPlayerController? player)
    {
        player = null;
        steamId = steamId.Trim();

        if (steamId.StartsWith("STEAM_1", StringComparison.Ordinal))
        {
            steamId = ReplaceFirstCharacter(steamId);
        }

        if (steamId.Contains("STEAM_", StringComparison.OrdinalIgnoreCase) ||
            steamId.StartsWith("765611", StringComparison.Ordinal))
        {
            player = GetPlayerFromSteamId(steamId);

            if (TryParseSteamId64(steamId, out var steamId64))
            {
                if (player?.AuthorizedSteamID != null)
                    return (long)player.AuthorizedSteamID.SteamId64;
                return steamId64;
            }

            return -1;
        }

        if (!long.TryParse(steamId, out var numericSteamId) || numericSteamId <= 0)
            return -1;

        player = GetPlayerFromSteamId(steamId);
        if (player?.AuthorizedSteamID != null)
            return (long)player.AuthorizedSteamID.SteamId64;

        return numericSteamId >= SteamId64IdentifierOffset
            ? numericSteamId
            : numericSteamId + SteamId64IdentifierOffset;
    }

    public static CCSPlayerController? GetPlayerFromSteamId(string steamId)
    {
        return Utilities.GetPlayers().Find(u =>
            u.AuthorizedSteamID != null &&
            (u.AuthorizedSteamID.SteamId2.ToString().Equals(steamId) ||
            u.AuthorizedSteamID.SteamId64.ToString().Equals(steamId) ||
            u.AuthorizedSteamID.AccountId.ToString().Equals(steamId)));
    }

    private static bool TryParseSteamId64(string steamId, out long steamId64)
    {
        steamId64 = -1;

        try
        {
            if (ulong.TryParse(steamId, out var numericSteamId))
            {
                steamId64 = numericSteamId >= (ulong)SteamId64IdentifierOffset
                    ? (long)numericSteamId
                    : (long)(numericSteamId + (ulong)SteamId64IdentifierOffset);
                return true;
            }

            steamId64 = (long)new SteamID(steamId).SteamId64;
            return steamId64 > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string ReplaceFirstCharacter(string input)
    {
        if (input.Length <= 6) return input;

        var charArray = input.ToCharArray();
        charArray[6] = '0';

        return new string(charArray);
    }
}
