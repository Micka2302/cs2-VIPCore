using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace VIPCore;

public class Database(VipCore vipCore, ILogger logger, string dbConnectionString)
{
    private const long SteamId64IdentifierOffset = 76561197960265728;
    private static readonly Regex ValidFeatureColumnRegex = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$",
        RegexOptions.Compiled);
    private readonly HashSet<string> _knownFeatureColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _featureColumnLock = new(1, 1);

    public async Task CreateTable()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(dbConnectionString);
            await dbConnection.OpenAsync();

            const string createVipUsersTable = """
                                               CREATE TABLE IF NOT EXISTS `vip_users` (
                                                   `account_id` BIGINT NOT NULL,
                                                   `name` VARCHAR(64) NOT NULL,
                                                   `lastvisit` BIGINT NOT NULL,
                                                   `sid` BIGINT NOT NULL,
                                                   `group` VARCHAR(64) NOT NULL,
                                                   `expires` BIGINT NOT NULL,
                                                   `expiration` DATETIME NULL,
                                               PRIMARY KEY (`account_id`, `sid`));
                                               """;


            await dbConnection.ExecuteAsync(createVipUsersTable);
            await MigrateVipUsersTableAsync(dbConnection);

            const string createVipServersTable = """
                                                 CREATE TABLE IF NOT EXISTS `vip_servers` (
                                                     `serverId` BIGINT NOT NULL,
                                                     `serverIp` VARCHAR(45) NOT NULL,
                                                     `port` INT NOT NULL,
                                                     `created_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                                     `updated_at` TIMESTAMP,
                                                     PRIMARY KEY (`serverId`)
                                                 );
                                                 """;

            await dbConnection.ExecuteAsync(createVipServersTable);
            await CreateVipFeaturesTableAsync(dbConnection);

            const string upsertVipServerQuery = """
                                                INSERT INTO `vip_servers` (`serverId`, `serverIp`, `port`, `updated_at`)
                                                VALUES (@ServerId, @ServerIP, @ServerPort, CURRENT_TIMESTAMP)
                                                ON DUPLICATE KEY UPDATE
                                                    `serverIp` = VALUES(`serverIp`),
                                                    `port` = VALUES(`port`),
                                                    `updated_at` = CURRENT_TIMESTAMP;
                                                """;

            await dbConnection.ExecuteAsync(upsertVipServerQuery, new
            {
                ServerId = vipCore.CoreConfig.ServerId,
                ServerIP = vipCore.CoreConfig.ServerIp,
                ServerPort = vipCore.CoreConfig.ServerPort,
            });
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task<User?> GetExistingUserFromDb(long accountId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);

            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                new { AccId = accountId, sid = serverId });

            return existingUser ?? null;
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
            return null;
        }
    }

    public async Task AddUserToDb(User user)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);

            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = user.account_id,
                    sid = serverId
                });

            if (existingUser != null)
            {
                vipCore.PrintLogWarning("User already exists");
                return;
            }

            await connection.ExecuteAsync(@"
                INSERT INTO vip_users (account_id, name, lastvisit, sid, `group`, expires, expiration)
                VALUES (@account_id, @name, @lastvisit, @sid, @group, @expires, CASE WHEN @expires = 0 THEN NULL ELSE FROM_UNIXTIME(@expires) END);", user);

            vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully added", user.name, user.account_id);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task UpdateUserInDb(User user)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = user.account_id,
                    sid = serverId
                });

            if (existingUser == null)
            {
                vipCore.PrintLogWarning("User does not exist");
                return;
            }

            await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                lastvisit = @lastvisit,
                `group` = @group,
                expires = @expires,
                expiration = CASE WHEN @expires = 0 THEN NULL ELSE FROM_UNIXTIME(@expires) END
            WHERE account_id = @account_id AND sid = @sid;", user);

            vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully updated", user.name,
                user.account_id);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task UpdateUserVip(long accountId, string name = "", string group = "", int time = -1)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = accountId,
                    sid = serverId
                });

            if (existingUser == null)
            {
                vipCore.PrintLogWarning($"User with account ID '{accountId}' does not exist");
                return;
            }

            if (!string.IsNullOrEmpty(name))
                existingUser.name = name;

            if (!string.IsNullOrEmpty(group))
                existingUser.group = group;

            if (time > -1)
                existingUser.expires = time == 0 ? 0 : vipCore.CalculateEndTimeInSeconds(time);

            await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                `group` = @group,
                expires = @expires,
                expiration = CASE WHEN @expires = 0 THEN NULL ELSE FROM_UNIXTIME(@expires) END
            WHERE account_id = @account_id AND sid = @sid;", existingUser);

            vipCore.PrintLogInfo(
                $"Player '{existingUser.name} [{accountId}]' VIP information has been successfully updated");
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task RemoveUserFromDb(long accId)
    {
        try
        {
            var existingUser = await GetExistingUserFromDb(accId);

            if (existingUser == null)
                return;

            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            await connection.ExecuteAsync(@"
            DELETE FROM vip_users
        WHERE account_id = @AccId AND sid = @sid;", new { AccId = accId, sid = serverId });

            vipCore.PrintLogInfo("Player {name}[{accId}] has been successfully removed", existingUser.name, accId);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task<List<User?>?> GetUserFromDb(long accId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);
            var user = await connection.QueryAsync<User?>(
                "SELECT * FROM `vip_users` WHERE `account_id` = @AccId AND sid = @sid AND (expires > @CurrTime OR expires = 0)",
                new { AccId = accId, sid = serverId, CurrTime = DateTime.UtcNow.GetUnixEpoch() }
            );

            return user.ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }

        return null;
    }

    public async Task RemoveExpiredUsers(CCSPlayerController player, SteamID steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            var serverId = await GetServerId(connection);

            var expiredUsers = await connection.QueryAsync<User>(
                "SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid AND expires < @CurrentTime AND expires > 0",
                new
                {
                    AccId = (long)steamId.SteamId64,
                    sid = serverId,
                    CurrentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            Console.WriteLine($"Removing expired VIPS, Current time: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

            foreach (var user in expiredUsers)
            {
                await connection.ExecuteAsync("DELETE FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                    new
                    {
                        AccId = user.account_id,
                        user.sid
                    });

                await Server.NextFrameAsync(() =>
                {
                    var authSteamId = player.AuthorizedSteamID;
                    if (authSteamId != null && (long)authSteamId.SteamId64 == user.account_id)
                        vipCore.PrintToChat(player, vipCore.Localizer["vip.Expired", user.group]);

                    vipCore.VipApi.OnPlayerRemoved(player, user.group);
                });

                vipCore.PrintLogInfo("User '{name} [{accId}]' has been removed due to expired VIP status.", user.name,
                    user.account_id);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    private async Task<long> GetServerId(IDbConnection connection)
    {
        try
        {
            const string query = """
                                 SELECT `serverId`
                                 FROM `vip_servers`
                                 WHERE `serverIp` = @ServerIP AND `port` = @ServerPort;
                                 """;

            return await connection.ExecuteScalarAsync<long>(query,
                new
                {
                    ServerIP = vipCore.CoreConfig.ServerIp,
                    ServerPort = vipCore.CoreConfig.ServerPort
                });
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }

        return -1;
    }

    private async Task MigrateVipUsersTableAsync(MySqlConnection connection)
    {
        await EnsureExpirationColumnExistsAsync(connection);
        await MigrateLegacyAccountIdsAsync(connection);
        await SynchronizeExpirationColumnAsync(connection);
    }

    private async Task EnsureExpirationColumnExistsAsync(MySqlConnection connection)
    {
        const string columnExistsQuery = """
                                         SELECT COUNT(*)
                                         FROM INFORMATION_SCHEMA.COLUMNS
                                         WHERE TABLE_SCHEMA = DATABASE()
                                           AND TABLE_NAME = 'vip_users'
                                           AND COLUMN_NAME = 'expiration';
                                         """;

        var columnExists = await connection.ExecuteScalarAsync<int>(columnExistsQuery);
        if (columnExists > 0)
            return;

        await connection.ExecuteAsync("ALTER TABLE `vip_users` ADD COLUMN `expiration` DATETIME NULL AFTER `expires`;");
    }

    private async Task MigrateLegacyAccountIdsAsync(MySqlConnection connection)
    {
        const string legacyUsersQuery = """
                                        SELECT `account_id`, `sid`
                                        FROM `vip_users`
                                        WHERE `account_id` > 0 AND `account_id` < @SteamId64Offset;
                                        """;

        var legacyUsers = (await connection.QueryAsync<LegacyVipUserId>(legacyUsersQuery,
            new { SteamId64Offset = SteamId64IdentifierOffset })).ToList();

        if (legacyUsers.Count == 0)
            return;

        foreach (var legacyUser in legacyUsers)
        {
            var steamId64 = legacyUser.account_id + SteamId64IdentifierOffset;

            var hasSteamId64Entry = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM `vip_users` WHERE `account_id` = @AccId AND `sid` = @sid",
                new { AccId = steamId64, legacyUser.sid }) > 0;

            if (hasSteamId64Entry)
            {
                await connection.ExecuteAsync(
                    "DELETE FROM `vip_users` WHERE `account_id` = @AccId AND `sid` = @sid",
                    new { AccId = legacyUser.account_id, legacyUser.sid });
                continue;
            }

            await connection.ExecuteAsync(
                "UPDATE `vip_users` SET `account_id` = @NewAccId WHERE `account_id` = @AccId AND `sid` = @sid",
                new { NewAccId = steamId64, AccId = legacyUser.account_id, legacyUser.sid });
        }

        vipCore.PrintLogInfo("Migrated {count} legacy SteamID32 records to SteamID64 in `vip_users`.", legacyUsers.Count);
    }

    private static async Task SynchronizeExpirationColumnAsync(MySqlConnection connection)
    {
        const string syncExpirationQuery = """
                                           UPDATE `vip_users`
                                           SET `expiration` = CASE
                                               WHEN `expires` = 0 THEN NULL
                                               ELSE FROM_UNIXTIME(`expires`)
                                           END;
                                           """;

        await connection.ExecuteAsync(syncExpirationQuery);
    }


    public async Task EnsureFeatureColumnAsync(string featureKey)
    {
        if (!IsValidFeatureColumnName(featureKey))
        {
            vipCore.PrintLogWarning("Skipping invalid feature column name '{featureKey}'.", featureKey);
            return;
        }

        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            await CreateVipFeaturesTableAsync(connection);
            await EnsureFeatureColumnAsync(connection, featureKey);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task UpsertFeatureValueAsync(ulong steamId64, string featureKey, object? value)
    {
        if (!IsValidFeatureColumnName(featureKey))
        {
            vipCore.PrintLogWarning("Skipping invalid feature value key '{featureKey}'.", featureKey);
            return;
        }

        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            await CreateVipFeaturesTableAsync(connection);
            await UpsertFeatureValueAsync(connection, steamId64, featureKey, value);
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    public async Task<Dictionary<ulong, PlayerCookie>> LoadFeatureCookiesAsync()
    {
        var cookies = new Dictionary<ulong, PlayerCookie>();

        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            await CreateVipFeaturesTableAsync(connection);

            var featureColumns = await GetFeatureColumnsAsync(connection);
            if (featureColumns.Count == 0)
                return cookies;

            var selectColumns = string.Join(", ", featureColumns.Select(column => $"`{column}`"));
            var rows = await connection.QueryAsync($"SELECT `steamid64`, {selectColumns} FROM `vip_features`;");

            foreach (var row in rows)
            {
                if (row is not IDictionary<string, object> values ||
                    !values.TryGetValue("steamid64", out var steamIdValue) ||
                    steamIdValue is null or DBNull)
                    continue;

                var steamId64 = Convert.ToUInt64(steamIdValue, CultureInfo.InvariantCulture);
                var cookie = new PlayerCookie { SteamId64 = steamId64 };

                foreach (var featureColumn in featureColumns)
                {
                    if (!values.TryGetValue(featureColumn, out var featureValue) ||
                        featureValue is null or DBNull)
                        continue;

                    cookie.Features[featureColumn] = featureValue;
                }

                cookies[steamId64] = cookie;
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }

        return cookies;
    }

    public async Task SaveFeatureCookiesAsync(IEnumerable<PlayerCookie> cookies)
    {
        try
        {
            await using var connection = new MySqlConnection(dbConnectionString);
            await connection.OpenAsync();
            await CreateVipFeaturesTableAsync(connection);

            foreach (var cookie in cookies)
            {
                foreach (var (featureKey, value) in cookie.Features)
                {
                    await UpsertFeatureValueAsync(connection, cookie.SteamId64, featureKey, value);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.ToString());
        }
    }

    private static bool IsValidFeatureColumnName(string featureKey)
    {
        return !string.IsNullOrWhiteSpace(featureKey) && ValidFeatureColumnRegex.IsMatch(featureKey);
    }

    private static string NormalizeFeatureValue(object? value)
    {
        if (value == null)
            return "0";

        return value switch
        {
            bool boolValue => boolValue ? "1" : "0",
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString() ?? "0",
            JsonElement jsonElement => jsonElement.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0"
        };
    }

    private async Task CreateVipFeaturesTableAsync(MySqlConnection connection)
    {
        const string createVipFeaturesTable = """
                                             CREATE TABLE IF NOT EXISTS `vip_features` (
                                                 `steamid64` BIGINT NOT NULL,
                                                 `created_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                                 `updated_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                                                 PRIMARY KEY (`steamid64`)
                                             );
                                             """;

        await connection.ExecuteAsync(createVipFeaturesTable);
    }

    private async Task<List<string>> GetFeatureColumnsAsync(MySqlConnection connection)
    {
        const string getColumnsQuery = """
                                       SELECT `COLUMN_NAME`
                                       FROM `INFORMATION_SCHEMA`.`COLUMNS`
                                       WHERE `TABLE_SCHEMA` = DATABASE()
                                         AND `TABLE_NAME` = 'vip_features'
                                         AND `COLUMN_NAME` NOT IN ('steamid64', 'created_at', 'updated_at');
                                       """;

        var featureColumns = (await connection.QueryAsync<string>(getColumnsQuery)).ToList();
        foreach (var featureColumn in featureColumns)
        {
            _knownFeatureColumns.Add(featureColumn);
        }

        return featureColumns;
    }

    private async Task EnsureFeatureColumnAsync(MySqlConnection connection, string featureKey)
    {
        if (_knownFeatureColumns.Contains(featureKey))
            return;

        await _featureColumnLock.WaitAsync();
        try
        {
            if (_knownFeatureColumns.Contains(featureKey))
                return;

            var columnExists = await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM `INFORMATION_SCHEMA`.`COLUMNS`
                WHERE `TABLE_SCHEMA` = DATABASE()
                  AND `TABLE_NAME` = 'vip_features'
                  AND `COLUMN_NAME` = @ColumnName;
                """, new { ColumnName = featureKey });

            if (columnExists == 0)
            {
                var addColumnQuery = $"ALTER TABLE `vip_features` ADD COLUMN `{featureKey}` VARCHAR(255) NOT NULL DEFAULT '0';";
                try
                {
                    await connection.ExecuteAsync(addColumnQuery);
                }
                catch (MySqlException ex) when (ex.Number == 1060)
                {
                    // Column already exists (concurrent add from another server instance).
                }
            }

            _knownFeatureColumns.Add(featureKey);
        }
        finally
        {
            _featureColumnLock.Release();
        }
    }

    private async Task UpsertFeatureValueAsync(MySqlConnection connection, ulong steamId64, string featureKey, object? value)
    {
        await EnsureFeatureColumnAsync(connection, featureKey);

        var upsertQuery = $"""
                          INSERT INTO `vip_features` (`steamid64`, `{featureKey}`)
                          VALUES (@SteamId64, @FeatureValue)
                          ON DUPLICATE KEY UPDATE
                              `{featureKey}` = @FeatureValue,
                              `updated_at` = CURRENT_TIMESTAMP;
                          """;

        await connection.ExecuteAsync(upsertQuery, new
        {
            SteamId64 = (long)steamId64,
            FeatureValue = NormalizeFeatureValue(value)
        });
    }
    private sealed class LegacyVipUserId
    {
        public long account_id { get; init; }
        public long sid { get; init; }
    }
}
