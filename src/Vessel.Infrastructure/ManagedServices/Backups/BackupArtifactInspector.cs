using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Vessel.Domain.Databases;

namespace Vessel.Infrastructure.ManagedServices.Backups;

public enum BackupPayloadFormat
{
    Unknown,
    PostgreSqlArchive,
    PostgreSqlScript,
    MySqlScript,
    GenericSqlScript,
    RedisRdb
}

/// <summary>
/// Detects artifact compression and payload format from content (magic bytes and dump headers), never from file
/// names or stored metadata, so a mislabeled or foreign artifact is rejected before any database is touched.
/// </summary>
public static class BackupArtifactInspector
{
    public const int HeaderSampleLength = 8192;

    private static readonly byte[] PostgreSqlArchiveMagic = "PGDMP"u8.ToArray();
    private static readonly byte[] RedisMagic = "REDIS"u8.ToArray();
    private static readonly byte[] TarMagic = "ustar"u8.ToArray();

    public static bool IsGzip(ReadOnlySpan<byte> header)
    {
        return header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B;
    }

    public static BackupPayloadFormat DetectPayloadFormat(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(PostgreSqlArchiveMagic)) return BackupPayloadFormat.PostgreSqlArchive;
        if (header.Length >= 262 && header.Slice(257, 5).SequenceEqual(TarMagic))
            return BackupPayloadFormat.PostgreSqlArchive;
        if (header.StartsWith(RedisMagic)) return BackupPayloadFormat.RedisRdb;
        if (!LooksLikeText(header)) return BackupPayloadFormat.Unknown;

        var text = Encoding.UTF8.GetString(header);
        if (text.Contains("PostgreSQL database dump", StringComparison.Ordinal) ||
            text.Contains("PostgreSQL database cluster dump", StringComparison.Ordinal))
            return BackupPayloadFormat.PostgreSqlScript;
        if (text.Contains("-- MySQL dump", StringComparison.Ordinal) ||
            text.Contains("-- MariaDB dump", StringComparison.Ordinal) ||
            text.Contains(MySqlRestoreScriptFilter.EmptyDumpHeader, StringComparison.Ordinal))
            return BackupPayloadFormat.MySqlScript;
        return BackupPayloadFormat.GenericSqlScript;
    }

    /// <summary>Whether a payload format can be restored into the given engine.</summary>
    public static bool IsCompatible(BackupPayloadFormat format, DatabaseEngine engine)
    {
        return format switch
        {
            BackupPayloadFormat.PostgreSqlArchive or BackupPayloadFormat.PostgreSqlScript =>
                engine == DatabaseEngine.PostgreSql,
            BackupPayloadFormat.MySqlScript => engine is DatabaseEngine.MySql or DatabaseEngine.MariaDb,
            BackupPayloadFormat.GenericSqlScript =>
                engine is DatabaseEngine.PostgreSql or DatabaseEngine.MySql or DatabaseEngine.MariaDb,
            BackupPayloadFormat.RedisRdb => engine == DatabaseEngine.Redis,
            _ => false
        };
    }

    public static string Describe(BackupPayloadFormat format, bool gzip)
    {
        var name = format switch
        {
            BackupPayloadFormat.PostgreSqlArchive => "postgresql-archive",
            BackupPayloadFormat.PostgreSqlScript => "postgresql-sql",
            BackupPayloadFormat.MySqlScript => "mysql-sql",
            BackupPayloadFormat.GenericSqlScript => "sql",
            BackupPayloadFormat.RedisRdb => "redis-rdb",
            _ => "unknown"
        };
        return gzip ? name + "+gzip" : name;
    }

    /// <summary>
    /// Decompresses a single-member gzip artifact and verifies it is complete: the decoder validates the deflate
    /// stream and CRC, and the trailer's ISIZE must match the decompressed length (detects truncation).
    /// </summary>
    public static async Task<long> DecompressGzipAsync(Stream compressed, Stream destination,
        CancellationToken cancellationToken)
    {
        if (!compressed.CanSeek) throw new InvalidOperationException("Gzip verification requires a seekable artifact.");
        if (compressed.Length < 18) throw new InvalidDataException("The gzip artifact is truncated.");

        compressed.Seek(-4, SeekOrigin.End);
        var trailer = new byte[4];
        await compressed.ReadExactlyAsync(trailer, cancellationToken);
        var expectedLengthModulo = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        compressed.Seek(0, SeekOrigin.Begin);

        long written;
        await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress, true))
        await using (var counter = new HashingWriteStream(destination, true))
        {
            try
            {
                await gzip.CopyToAsync(counter, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException("The gzip artifact is corrupt and cannot be decompressed.", exception);
            }

            written = counter.BytesWritten;
        }

        if ((uint)(written & 0xFFFFFFFF) != expectedLengthModulo)
            throw new InvalidDataException("The gzip artifact is truncated or corrupt.");
        return written;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return false;
        foreach (var value in sample)
            if (value == 0 || (value < 0x09) || (value is > 0x0D and < 0x20 && value != 0x1B))
                return false;
        return true;
    }
}
