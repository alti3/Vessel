using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Secrets;
using Vessel.Domain.Sources;
using Vessel.Domain.Ssh;
using Vessel.Domain.Teams;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Git sources (DEP-08): private Git source connections, the source reference on applications and per-team pinned
///     SSH host keys of Git hosts. Secrets are vault references only.
/// </summary>
public sealed partial class VesselDbContext
{
    private IRepository<GitKnownHost, GitKnownHostId>? _gitKnownHostRepository;
    private IRepository<GitSourceConnection, GitSourceConnectionId>? _gitSourceConnectionRepository;

    public DbSet<GitSourceConnection> GitSourceConnectionSet => Set<GitSourceConnection>();

    public DbSet<GitKnownHost> GitKnownHostSet => Set<GitKnownHost>();

    public IQueryable<GitSourceConnection> GitSourceConnections => GitSourceConnectionSet;

    public IQueryable<GitKnownHost> GitKnownHosts => GitKnownHostSet;

    public IRepository<GitSourceConnection, GitSourceConnectionId> GitSourceConnectionRepository =>
        _gitSourceConnectionRepository ??= new EfRepository<GitSourceConnection, GitSourceConnectionId>(this);

    public IRepository<GitKnownHost, GitKnownHostId> GitKnownHostRepository =>
        _gitKnownHostRepository ??= new EfRepository<GitKnownHost, GitKnownHostId>(this);

    private static void ConfigureSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GitSourceConnection>(builder =>
        {
            builder.ToTable("git_sources");
            builder.HasKey(source => source.Id);
            builder.Property(source => source.Id)
                .HasConversion(id => id.Value, value => new GitSourceConnectionId(value));
            builder.Property(source => source.TeamId).HasTeamIdConversion();
            builder.Property(source => source.Name).HasConversion(ValueObjectConversions.ResourceName)
                .HasMaxLength(120).IsRequired();
            builder.Property(source => source.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(source => source.IsSystemWide).IsRequired();
            builder.Property(source => source.HtmlUrl).HasMaxLength(GitProviderEndpoints.MaxUrlLength);
            builder.Property(source => source.ApiUrl).HasMaxLength(GitProviderEndpoints.MaxUrlLength);
            builder.Property(source => source.Organization).HasMaxLength(GitSourceConnection.MaxOrganizationLength);
            builder.Property(source => source.CustomSshUser).HasMaxLength(64);
            builder.Property(source => source.ClientId).HasMaxLength(GitSourceConnection.MaxClientIdLength);
            builder.Property(source => source.Username).HasMaxLength(GitSourceConnection.MaxUsernameLength);
            builder.Property(source => source.SshKeyId).HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new SshKeyId(value.Value) : null);
            SecretReference(builder.Property(source => source.PrivateKeyReferenceId));
            SecretReference(builder.Property(source => source.ClientSecretReferenceId));
            SecretReference(builder.Property(source => source.WebhookSecretReferenceId));
            SecretReference(builder.Property(source => source.AccessTokenReferenceId));
            SecretReference(builder.Property(source => source.RefreshTokenReferenceId));
            builder.Property(source => source.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(source => source.DomainEvents);
            builder.Ignore(source => source.IsAppKind);
            builder.HasIndex(source => new { source.TeamId, source.Name }).IsUnique();
            builder.HasIndex(source => source.IsSystemWide);
            builder.HasIndex(source => source.SshKeyId);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(source => source.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            // A deploy key in use by a source cannot be deleted.
            builder.HasOne<SshKey>()
                .WithMany()
                .HasForeignKey(source => source.SshKeyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GitKnownHost>(builder =>
        {
            builder.ToTable("git_known_hosts");
            builder.HasKey(host => host.Id);
            builder.Property(host => host.Id).HasConversion(id => id.Value, value => new GitKnownHostId(value));
            builder.Property(host => host.TeamId).HasTeamIdConversion();
            builder.Property(host => host.Host).HasMaxLength(GitKnownHost.MaxHostLength).IsRequired();
            builder.Property(host => host.Port).IsRequired();
            builder.Property(host => host.KeyType).HasMaxLength(64).IsRequired();
            builder.Property(host => host.Key).HasMaxLength(GitKnownHost.MaxKeyLength).IsRequired();
            builder.Property(host => host.Origin).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(host => host.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(host => host.DomainEvents);
            builder.Ignore(host => host.KnownHostsLine);
            builder.HasIndex(host => new { host.TeamId, host.Host, host.Port, host.KeyType }).IsUnique();
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(host => host.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppEntity>(builder =>
        {
            builder.Property(application => application.SourceId).HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new GitSourceConnectionId(value.Value) : null);
            builder.HasIndex(application => application.SourceId);
            // Sources in use cannot be deleted (the service also refuses with a clear message).
            builder.HasOne<GitSourceConnection>()
                .WithMany()
                .HasForeignKey(application => application.SourceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void SecretReference(PropertyBuilder<SecretReferenceId?> property)
    {
        property.HasConversion(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            value => value.HasValue ? new SecretReferenceId(value.Value) : null);
    }
}
