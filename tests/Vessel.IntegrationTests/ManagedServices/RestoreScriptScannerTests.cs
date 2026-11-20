using System.Text;
using Vessel.Domain.Databases;
using Vessel.Infrastructure.ManagedServices.Backups;
using Vessel.Infrastructure.Storage;

namespace Vessel.IntegrationTests.ManagedServices;

public sealed class RestoreScriptScannerTests
{
    [Theory]
    [InlineData("\\! id")]
    [InlineData("SELECT 1; \\! id")]
    [InlineData("\\o | sh")]
    [InlineData("\\copy t to program 'id'")]
    [InlineData("\\i /etc/passwd")]
    [InlineData("\\setenv PAGER sh")]
    [InlineData("\\set x `id`")]
    [InlineData("COPY t TO PROGRAM 'id';")]
    [InlineData("COPY t FROM\n  PROGRAM 'id';")]
    [InlineData("COPY t TO /* hidden */ PROGRAM 'id';")]
    [InlineData("COPY t TO -- hidden\nPROGRAM 'id';")]
    [InlineData("SELECT '--'; COPY t TO PROGRAM 'id';")]
    [InlineData("DO $$ BEGIN EXECUTE 'COPY t TO PROGRAM ''id'''; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE 'COPY t TO /**/ PROGRAM ''id'''; END $$;")]
    [InlineData("SELECT lo_import('/etc/passwd');")]
    [InlineData("SELECT lo_export(16385, '/tmp/x');")]
    [InlineData("ALTER SYSTEM SET archive_command = 'id';")]
    public async Task PostgresScanner_BlocksCodeExecution(string script)
    {
        Assert.NotNull(await ScanPostgresAsync(script));
    }

    [Fact]
    public async Task PostgresScanner_AllowsOrdinaryPgDumpOutput()
    {
        const string script = """
                              --
                              -- PostgreSQL database dump
                              --
                              \restrict 3c9aF0b1
                              SET standard_conforming_strings = on;
                              \connect postgres
                              CREATE TABLE public.notes (id integer, body text DEFAULT E'line\nnext');
                              CREATE FUNCTION public.f() RETURNS text LANGUAGE sql AS $_$ SELECT 'to be' $_$;
                              COPY public.notes (id, body) FROM stdin;
                              1	how to program in \\! style
                              2	back\\slash
                              \.
                              SELECT pg_catalog.lo_create('16385');
                              \unrestrict 3c9aF0b1
                              """;

        Assert.Null(await ScanPostgresAsync(script));
    }

    [Fact]
    public async Task PostgresScanner_DoesNotSkipFakeCopyBlocksInsideStrings()
    {
        const string script = "DO $$ BEGIN RAISE NOTICE '\nCOPY x FROM stdin;\n'; END $$;\n\\! id\n\\.\n";

        Assert.NotNull(await ScanPostgresAsync(script));
    }

    [Fact]
    public async Task MySqlFilter_DropsSystemDatabasesAndKeepsUserData()
    {
        const string dump = """
                            -- MySQL dump 10.13
                            -- Current Database: `mysql`
                            CREATE DATABASE /*!32312 IF NOT EXISTS*/ `mysql` /*!40100 DEFAULT CHARACTER SET utf8mb4 */;
                            USE `mysql`;
                            INSERT INTO `user` VALUES ('root','hash');
                            -- Current Database: `app`
                            CREATE DATABASE /*!32312 IF NOT EXISTS*/ `app`;
                            USE `app`;
                            INSERT INTO `t` VALUES ('a\\.b','c\\\\!d');
                            """;
        await using var output = new MemoryStream();

        MySqlFilterResult result = await MySqlRestoreScriptFilter.FilterAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(dump)), output, TestContext.Current.CancellationToken);

        var restored = Encoding.UTF8.GetString(output.ToArray());
        Assert.Null(result.Violation);
        Assert.Equal(4, result.SkippedSystemLines);
        Assert.DoesNotContain("'root'", restored, StringComparison.Ordinal);
        Assert.Contains("USE `app`;", restored, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `t`", restored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\! id")]
    [InlineData("SELECT 1; \\! id")]
    [InlineData("system id")]
    [InlineData("  SOURCE /etc/passwd;")]
    [InlineData("tee /tmp/out")]
    [InlineData("\\T /tmp/out")]
    public async Task MySqlFilter_BlocksClientCommands(string line)
    {
        await using var output = new MemoryStream();

        MySqlFilterResult result = await MySqlRestoreScriptFilter.FilterAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("-- MySQL dump\n" + line + "\n")), output,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Violation);
    }

    [Theory]
    [InlineData("PGDMP\u0001\u000e", BackupPayloadFormat.PostgreSqlArchive)]
    [InlineData("REDIS0011", BackupPayloadFormat.RedisRdb)]
    [InlineData("--\n-- PostgreSQL database dump\n", BackupPayloadFormat.PostgreSqlScript)]
    [InlineData("-- MariaDB dump 10.19\n", BackupPayloadFormat.MySqlScript)]
    [InlineData("CREATE TABLE t (id int);\n", BackupPayloadFormat.GenericSqlScript)]
    [InlineData("\u0000\u0001binary", BackupPayloadFormat.Unknown)]
    public void Inspector_DetectsFormatFromContent(string header, BackupPayloadFormat expected)
    {
        Assert.Equal(expected, BackupArtifactInspector.DetectPayloadFormat(Encoding.UTF8.GetBytes(header)));
    }

    [Fact]
    public void Inspector_EnforcesEngineCompatibility()
    {
        Assert.True(BackupArtifactInspector.IsCompatible(BackupPayloadFormat.PostgreSqlArchive,
            DatabaseEngine.PostgreSql));
        Assert.False(BackupArtifactInspector.IsCompatible(BackupPayloadFormat.MySqlScript, DatabaseEngine.PostgreSql));
        Assert.False(BackupArtifactInspector.IsCompatible(BackupPayloadFormat.RedisRdb, DatabaseEngine.MySql));
        Assert.False(BackupArtifactInspector.IsCompatible(BackupPayloadFormat.PostgreSqlScript, DatabaseEngine.Redis));
    }

    [Theory]
    [InlineData(null, 5L * 1024 * 1024)]
    [InlineData(1024L, 5L * 1024 * 1024)]
    [InlineData(100L * 1024 * 1024 * 1024, 103L * 1024 * 1024)]
    public void S3PartSize_StaysWithinThousandParts(long? length, long expected)
    {
        long partSize = S3ObjectStorage.CalculatePartSize(length);

        Assert.Equal(expected, partSize);
        if (length is > 0) Assert.True((length.Value + partSize - 1) / partSize <= S3ObjectStorage.MaximumPartCount);
    }

    private static async Task<string?> ScanPostgresAsync(string script)
    {
        return await PostgresRestoreScriptScanner.ScanAsync(new MemoryStream(Encoding.UTF8.GetBytes(script)),
            TestContext.Current.CancellationToken);
    }
}
