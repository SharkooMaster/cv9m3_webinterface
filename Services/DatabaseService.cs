using Npgsql;

namespace WebInterface.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        // Get database connection from environment or config
        var dbHost = configuration["Database:Host"] ?? "postgres";
        var dbPort = configuration["Database:Port"] ?? "5432";
        var dbUser = configuration["Database:User"] ?? "crossuser";
        var dbPassword = configuration["Database:Password"] ?? "crosspass";
        var dbName = configuration["Database:Database"] ?? "compressiondb";

        _connectionString = $"Host={dbHost};Port={dbPort};Username={dbUser};Password={dbPassword};Database={dbName}";
        _logger = logger;
    }

    public async Task<bool> ClearAllDataAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Disable foreign key checks temporarily (PostgreSQL doesn't have this, but we'll delete in order)
            // Delete vectors first (has foreign key to bucket_keys)
            var deleteVectorsCmd = new NpgsqlCommand("TRUNCATE TABLE vectors CASCADE", conn);
            await deleteVectorsCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Cleared vectors table");

            // Delete bucket_keys (vectors are already deleted, so foreign key constraint is satisfied)
            var deleteBucketsCmd = new NpgsqlCommand("TRUNCATE TABLE bucket_keys CASCADE", conn);
            await deleteBucketsCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Cleared bucket_keys table");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear database");
            return false;
        }
    }

    public async Task<Dictionary<string, long>> GetTableCountsAsync()
    {
        var counts = new Dictionary<string, long>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var vectorsCmd = new NpgsqlCommand("SELECT COUNT(*) FROM vectors", conn);
            var vectorsCount = (long)(await vectorsCmd.ExecuteScalarAsync() ?? 0L);
            counts["vectors"] = vectorsCount;

            var bucketsCmd = new NpgsqlCommand("SELECT COUNT(*) FROM bucket_keys", conn);
            var bucketsCount = (long)(await bucketsCmd.ExecuteScalarAsync() ?? 0L);
            counts["buckets"] = bucketsCount;

            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get table counts");
            return counts;
        }
    }
}






