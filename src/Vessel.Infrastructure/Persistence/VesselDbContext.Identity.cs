using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Auth;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;

namespace Vessel.Infrastructure.Persistence;

/// <summary>
///     Identity model added by IAM-07/IAM-08/IAM-09: external sign-in providers and linked identities, server-side
///     sessions, and the audit-log repository used by retention and anonymization. Kept in its own part of the context
///     so the additions stay reviewable.
/// </summary>
public sealed partial class VesselDbContext
{
    private static readonly ValueConverter<UserSessionId, Guid> UserSessionIdConverter =
        new(id => id.Value, value => new UserSessionId(value));

    private static readonly ValueConverter<AuthProviderId, Guid> AuthProviderIdConverter =
        new(id => id.Value, value => new AuthProviderId(value));

    private static readonly ValueConverter<OauthIdentityId, Guid> OauthIdentityIdConverter =
        new(id => id.Value, value => new OauthIdentityId(value));

    private IRepository<AuditLog, AuditLogId>? _auditLogRepository;
    private IRepository<AuthProvider, AuthProviderId>? _authProviderRepository;
    private IRepository<OauthIdentity, OauthIdentityId>? _oauthIdentityRepository;
    private IRepository<UserSession, UserSessionId>? _userSessionRepository;

    public DbSet<UserSession> UserSessionSet => Set<UserSession>();

    public DbSet<AuthProvider> AuthProviderSet => Set<AuthProvider>();

    public DbSet<OauthIdentity> OauthIdentitySet => Set<OauthIdentity>();

    public IQueryable<UserSession> UserSessions => UserSessionSet;

    public IQueryable<AuthProvider> AuthProviders => AuthProviderSet;

    public IQueryable<OauthIdentity> OauthIdentities => OauthIdentitySet;

    public IRepository<UserSession, UserSessionId> UserSessionRepository =>
        _userSessionRepository ??= new EfRepository<UserSession, UserSessionId>(this);

    public IRepository<AuthProvider, AuthProviderId> AuthProviderRepository =>
        _authProviderRepository ??= new EfRepository<AuthProvider, AuthProviderId>(this);

    public IRepository<OauthIdentity, OauthIdentityId> OauthIdentityRepository =>
        _oauthIdentityRepository ??= new EfRepository<OauthIdentity, OauthIdentityId>(this);

    public IRepository<AuditLog, AuditLogId> AuditLogRepository =>
        _auditLogRepository ??= new EfRepository<AuditLog, AuditLogId>(this);

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(builder =>
        {
            builder.Property(user => user.CurrentTeamId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new TeamId(value.Value) : null);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(user => user.CurrentTeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PersonalAccessToken>(builder =>
        {
            // The expiry-warning job scans unwarned tokens that expire soon.
            builder.HasIndex(token => new { token.ExpiresAt, token.ExpirationWarningSentAt });
        });

        modelBuilder.Entity<UserSession>(builder =>
        {
            builder.ToTable("user_sessions");
            builder.HasKey(session => session.Id);
            builder.Property(session => session.Id).HasConversion(UserSessionIdConverter);
            builder.Property(session => session.UserId).HasUserIdConversion();
            builder.Property(session => session.ActiveTeamId).HasTeamIdConversion();
            builder.Property(session => session.AuthenticationMethod)
                .HasMaxLength(UserSession.AuthenticationMethodMaxLength).IsRequired();
            builder.Property(session => session.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength);
            builder.Property(session => session.UserAgent).HasMaxLength(UserSession.UserAgentMaxLength);
            builder.Property(session => session.RevocationReason).HasMaxLength(UserSession.RevocationReasonMaxLength);
            // Deliberately no concurrency token: activity, team switches and revocation update separate columns.
            builder.Ignore(session => session.ConcurrencyStamp);
            builder.Ignore(session => session.DomainEvents);
            builder.HasIndex(session => new { session.UserId, session.RevokedAt });
            builder.HasIndex(session => session.ExpiresAt);
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(session => session.ActiveTeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthProvider>(builder =>
        {
            builder.ToTable("auth_providers");
            builder.HasKey(provider => provider.Id);
            builder.Property(provider => provider.Id).HasConversion(AuthProviderIdConverter);
            builder.Property(provider => provider.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(provider => provider.Name).HasMaxLength(AuthProvider.NameMaxLength).IsRequired();
            builder.Property(provider => provider.ClientId).HasMaxLength(AuthProvider.ClientIdMaxLength).IsRequired();
            builder.Property(provider => provider.ProtectedClientSecret)
                .HasMaxLength(AuthProvider.ProtectedSecretMaxLength);
            builder.Property(provider => provider.Authority).HasMaxLength(AuthProvider.AuthorityMaxLength);
            builder.Property(provider => provider.Scopes).HasMaxLength(AuthProvider.ScopesMaxLength);
            builder.Property(provider => provider.AllowedDomains).HasMaxLength(AuthProvider.AllowedDomainsMaxLength);
            builder.Property(provider => provider.RequireVerifiedEmail).HasDefaultValue(true).IsRequired();
            builder.Property(provider => provider.AutoJoinTeamId)
                .HasConversion(id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new TeamId(value.Value) : null);
            builder.Property(provider => provider.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(provider => provider.HasClientSecret);
            builder.Ignore(provider => provider.CanSignIn);
            builder.Ignore(provider => provider.DomainEvents);
            builder.HasOne<Team>()
                .WithMany()
                .HasForeignKey(provider => provider.AutoJoinTeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OauthIdentity>(builder =>
        {
            builder.ToTable("oauth_identities");
            builder.HasKey(identity => identity.Id);
            builder.Property(identity => identity.Id).HasConversion(OauthIdentityIdConverter);
            builder.Property(identity => identity.UserId).HasUserIdConversion();
            builder.Property(identity => identity.ProviderId).HasConversion(AuthProviderIdConverter);
            builder.Property(identity => identity.Issuer).HasMaxLength(OauthIdentity.IssuerMaxLength).IsRequired();
            builder.Property(identity => identity.Subject).HasMaxLength(OauthIdentity.SubjectMaxLength).IsRequired();
            builder.Property(identity => identity.Email).HasMaxLength(OauthIdentity.EmailMaxLength);
            builder.Property(identity => identity.ConcurrencyStamp).IsConcurrencyToken();
            builder.Ignore(identity => identity.DomainEvents);
            // Keyed by provider instance + subject (never email); one linked identity per account.
            builder.HasIndex(identity => new { identity.ProviderId, identity.Subject }).IsUnique();
            builder.HasIndex(identity => identity.UserId).IsUnique();
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(identity => identity.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<AuthProvider>()
                .WithMany()
                .HasForeignKey(identity => identity.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
