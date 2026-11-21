using Microsoft.EntityFrameworkCore;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Certificates;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using AppEntity = Vessel.Domain.Applications.Application;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Routing model: application and service domains (route identity, policies), certificate inventory mirrored from
///     acme.json, proxy configuration history and user dynamic configuration files. Kept in its own part of the
///     context so routing changes stay reviewable in one place.
/// </summary>
public sealed partial class VesselDbContext
{
    private IRepository<ProxyDynamicConfiguration, ProxyDynamicConfigurationId>? _proxyDynamicConfigurationRepository;
    private IRepository<ServiceDomain, ServiceDomainId>? _serviceDomainRepository;

    public DbSet<ProxyDynamicConfiguration> ProxyDynamicConfigurationSet => Set<ProxyDynamicConfiguration>();

    public DbSet<ServiceDomain> ServiceDomainSet => Set<ServiceDomain>();

    public IQueryable<ProxyDynamicConfiguration> ProxyDynamicConfigurations => ProxyDynamicConfigurationSet;

    public IQueryable<ServiceDomain> ServiceDomains => ServiceDomainSet;

    public IRepository<ProxyDynamicConfiguration, ProxyDynamicConfigurationId> ProxyDynamicConfigurationRepository =>
        _proxyDynamicConfigurationRepository ??=
            new EfRepository<ProxyDynamicConfiguration, ProxyDynamicConfigurationId>(this);

    public IRepository<ServiceDomain, ServiceDomainId> ServiceDomainRepository =>
        _serviceDomainRepository ??= new EfRepository<ServiceDomain, ServiceDomainId>(this);

    private static void ConfigureRouting(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationDomain>(builder =>
        {
            builder.ToTable("application_domains");
            // Route identity within an application: host + case-preserved path prefix ("" for the root).
            builder.HasKey(domain => new { domain.ApplicationId, domain.Host, domain.PathPrefix });
            builder.Property(domain => domain.ApplicationId).HasApplicationIdConversion();
            // Normalized host (lower case, punycode, IPv6 without brackets); keeps the original column name.
            builder.Property(domain => domain.Host).HasColumnName("DomainName").HasMaxLength(253).IsRequired();
            builder.Property(domain => domain.HostKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(domain => domain.PathPrefix).HasMaxLength(1024).IsRequired();
            builder.Property(domain => domain.TargetPort);
            builder.Property(domain => domain.TlsEnabled).IsRequired();
            builder.Property(domain => domain.Canonical).IsRequired();
            builder.Property(domain => domain.RedirectToCanonical).IsRequired();
            builder.Property(domain => domain.Policy).HasConversion(RoutingConversions.Policy)
                .HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
            builder.Ignore(domain => domain.Address);
            // Save-time conflict lookups across every application.
            builder.HasIndex(domain => new { domain.Host, domain.PathPrefix });
        });

        modelBuilder.Entity<ServiceDomain>(builder =>
        {
            builder.ToTable("service_domains");
            builder.HasKey(domain => domain.Id);
            builder.Property(domain => domain.Id).HasConversion(RoutingConversions.ServiceDomainId);
            builder.Property(domain => domain.ServiceResourceId).HasServiceResourceIdConversion();
            builder.Property(domain => domain.ComposeService).HasMaxLength(ServiceDomain.MaxComposeServiceLength)
                .IsRequired();
            builder.Property(domain => domain.Host).HasMaxLength(253).IsRequired();
            builder.Property(domain => domain.HostKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            builder.Property(domain => domain.PathPrefix).HasMaxLength(1024).IsRequired();
            builder.Property(domain => domain.TargetPort).IsRequired();
            builder.Property(domain => domain.TlsEnabled).IsRequired();
            builder.Property(domain => domain.Policy).HasConversion(RoutingConversions.Policy)
                .HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
            builder.Property(domain => domain.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(domain => domain.Address);
            builder.Ignore(domain => domain.DomainEvents);
            builder.HasIndex(domain => new { domain.ServiceResourceId, domain.Host, domain.PathPrefix }).IsUnique();
            builder.HasIndex(domain => new { domain.Host, domain.PathPrefix });
            builder.HasOne<ServiceResource>()
                .WithMany()
                .HasForeignKey(domain => domain.ServiceResourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProxyConfigurationVersion>(builder =>
        {
            builder.ToTable("proxy_configuration_versions");
            builder.HasKey(version => version.Id);
            builder.Property(version => version.Id).HasProxyConfigurationVersionIdConversion();
            builder.Property(version => version.ServerId).HasServerIdConversion();
            builder.Property(version => version.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(version => version.Version).HasMaxLength(80).IsRequired();
            builder.Property(version => version.ConfigurationHash).HasMaxLength(128).IsRequired();
            // Encrypted with the secret key ring (ProxyConfigurationProtector); legacy rows may be plaintext.
            builder.Property(version => version.Configuration).IsRequired();
            builder.Property(version => version.PreviousVersionId)
                .HasConversion(StronglyTypedIdConversions.ProxyConfigurationVersionId);
            builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            // Existing rows are generated routes; the migration should default the column to 'Generated'.
            builder.Property(version => version.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(version => version.FileName).HasMaxLength(ProxyConfigurationVersion.MaxFileNameLength);
            builder.Property(version => version.RestoredFromId)
                .HasConversion(StronglyTypedIdConversions.ProxyConfigurationVersionId);
            builder.Property(version => version.ValidationError).HasMaxLength(2000);
            builder.Property(version => version.ApplyError).HasMaxLength(2000);
            builder.Property(version => version.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(version => version.DomainEvents);
            builder.HasIndex(version => new { version.ServerId, version.CreatedAt });
            builder.HasIndex(version => new { version.ServerId, version.ConfigurationHash });
            builder.HasIndex(version => new { version.ServerId, version.FileName, version.Status });
            builder.HasIndex(version => version.PreviousVersionId);
            builder.HasIndex(version => version.RestoredFromId);
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(version => version.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<ProxyConfigurationVersion>()
                .WithMany()
                .HasForeignKey(version => version.PreviousVersionId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne<ProxyConfigurationVersion>()
                .WithMany()
                .HasForeignKey(version => version.RestoredFromId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProxyDynamicConfiguration>(builder =>
        {
            builder.ToTable("proxy_dynamic_configurations");
            builder.HasKey(configuration => configuration.Id);
            builder.Property(configuration => configuration.Id)
                .HasConversion(RoutingConversions.ProxyDynamicConfigurationId);
            builder.Property(configuration => configuration.ServerId).HasServerIdConversion();
            builder.Property(configuration => configuration.FileName)
                .HasMaxLength(ProxyConfigurationVersion.MaxFileNameLength).IsRequired();
            // Encrypted with the secret key ring (ProxyConfigurationProtector).
            builder.Property(configuration => configuration.Content).IsRequired();
            builder.Property(configuration => configuration.ContentHash).HasMaxLength(128).IsRequired();
            builder.Property(configuration => configuration.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(configuration => configuration.DomainEvents);
            builder.HasIndex(configuration => new { configuration.ServerId, configuration.FileName }).IsUnique();
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(configuration => configuration.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Certificate>(builder =>
        {
            builder.ToTable("certificates");
            builder.HasKey(certificate => certificate.Id);
            builder.Property(certificate => certificate.Id).HasCertificateIdConversion();
            builder.Property(certificate => certificate.TeamId).HasTeamIdConversion();
            builder.Property(certificate => certificate.ServerId).HasServerIdConversion();
            builder.Property(certificate => certificate.ApplicationId)
                .HasConversion(StronglyTypedIdConversions.ApplicationId);
            builder.Property(certificate => certificate.Host).HasMaxLength(Certificate.MaxHostLength).IsRequired();
            builder.Property(certificate => certificate.SubjectAlternativeNames)
                .HasMaxLength(Certificate.MaxSansLength);
            builder.Property(certificate => certificate.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(certificate => certificate.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(certificate => certificate.Resolver).HasMaxLength(Certificate.MaxResolverLength);
            builder.Property(certificate => certificate.AcmeKey).HasMaxLength(Certificate.MaxAcmeKeyLength);
            builder.Property(certificate => certificate.InUse).IsRequired();
            builder.Property(certificate => certificate.LastError).HasMaxLength(2000);
            builder.Property(certificate => certificate.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(certificate => certificate.SubjectAlternativeNameList);
            builder.Ignore(certificate => certificate.DomainEvents);
            builder.HasIndex(certificate => new { certificate.ServerId, certificate.Host });
            builder.HasIndex(certificate => new { certificate.ServerId, certificate.AcmeKey }).IsUnique();
            builder.HasIndex(certificate => certificate.ApplicationId);
            builder.HasIndex(certificate => new { certificate.TeamId, certificate.ExpiresAt });
            builder.HasOne<Server>()
                .WithMany()
                .HasForeignKey(certificate => certificate.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<AppEntity>()
                .WithMany()
                .HasForeignKey(certificate => certificate.ApplicationId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
