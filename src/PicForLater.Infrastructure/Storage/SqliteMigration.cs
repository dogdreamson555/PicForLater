using System.Security.Cryptography;
using System.Text;

namespace PicForLater.Infrastructure.Storage;

internal sealed record SqliteMigration
{
    public SqliteMigration(int version, string name, string sql)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Version = version;
        Name = name;
        Sql = sql;
        Checksum = ComputeChecksum(sql);
    }

    public int Version { get; }

    public string Name { get; }

    public string Sql { get; }

    public string Checksum { get; }

    private static string ComputeChecksum(string sql)
    {
        var normalizedSql = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSql))).ToLowerInvariant();
    }
}
