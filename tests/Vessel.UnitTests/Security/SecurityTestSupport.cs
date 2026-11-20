using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;
using Vessel.Domain.Teams;
using Vessel.Domain.Users;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;
using EnvironmentEntity = Vessel.Domain.Projects.Environment;

namespace Vessel.UnitTests.Security;

/// <summary>
///     List-backed <see cref="IVesselDbContext" /> built with <see cref="DispatchProxy" />, so security tests do not
///     have to re-implement every set. Every <c>IQueryable&lt;T&gt;</c> and repository is backed by one list per type;
///     memberships are derived from the stored teams like the EF model does.
/// </summary>
public class InMemoryVesselDb : DispatchProxy
{
    private readonly Dictionary<Type, IList> _sets = [];

    public IVesselDbContext Context => (IVesselDbContext)(object)this;

    public static InMemoryVesselDb Create()
    {
        return (InMemoryVesselDb)(object)DispatchProxy.Create<IVesselDbContext, InMemoryVesselDb>();
    }

    public List<T> Set<T>()
    {
        if (!_sets.TryGetValue(typeof(T), out IList? list))
        {
            list = new List<T>();
            _sets[typeof(T)] = list;
        }

        return (List<T>)list;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IUnitOfWork.SaveChangesAsync)) return Task.FromResult(1);
        if (targetMethod.Name == "get_" + nameof(IVesselDbContext.TeamMemberships))
            return Set<Team>().SelectMany(team => team.Memberships).AsQueryable();

        Type returnType = targetMethod.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            Type entityType = returnType.GetGenericArguments()[0];
            object list = GetType().GetMethod(nameof(Set))!.MakeGenericMethod(entityType).Invoke(this, null)!;
            return Queryable.AsQueryable((IEnumerable)list);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IRepository<,>))
        {
            Type[] arguments = returnType.GetGenericArguments();
            object list = GetType().GetMethod(nameof(Set))!.MakeGenericMethod(arguments[0]).Invoke(this, null)!;
            return Activator.CreateInstance(typeof(ListRepository<,>).MakeGenericType(arguments), list);
        }

        throw new NotSupportedException(targetMethod.Name);
    }

    private sealed class ListRepository<TEntity, TId>(List<TEntity> items) : IRepository<TEntity, TId>
        where TEntity : Entity<TId>
        where TId : notnull
    {
        public Task<TEntity?> GetByIdAsync(TId id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items.SingleOrDefault(item => EqualityComparer<TId>.Default.Equals(item.Id, id)));
        }

        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            items.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(TEntity entity)
        {
            items.Remove(entity);
        }
    }
}

/// <summary>Inert stand-in for collaborators that a rejected call must never reach.</summary>
public class InertStub : DispatchProxy
{
    public List<string> Calls { get; } = [];

    public static object Create(Type interfaceType)
    {
        return DispatchProxy.Create(interfaceType, typeof(InertStub));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Add(targetMethod.Name);
        Type returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsValueType && returnType != typeof(void)) return Activator.CreateInstance(returnType);
        if (returnType == typeof(string)) return string.Empty;
        return null;
    }
}

/// <summary>Constructs Application services, wiring the shared fake database and inert stubs for everything else.</summary>
public sealed class SecurityServiceFactory(InMemoryVesselDb store)
{
    private readonly Dictionary<Type, object> _instances = [];

    public List<object> Stubs { get; } = [];

    /// <summary>The request token layer seen by services built here (none by default).</summary>
    public IApiTokenAccessContext TokenContext { get; set; } = NoApiTokenAccessContext.Instance;

    public T Get<T>()
    {
        return (T)Get(typeof(T));
    }

    private object Get(Type type)
    {
        if (type == typeof(IVesselDbContext)) return store.Context;
        if (type == typeof(TimeProvider)) return TimeProvider.System;
        if (type == typeof(IApiTokenAccessContext)) return TokenContext;
        if (type == typeof(IAccessResolver)) return Get(typeof(AccessResolver));
        if (type == typeof(ITokenGenerator))
        {
            if (!_instances.TryGetValue(type, out object? generator))
                _instances[type] = generator = new SequentialTokenGenerator();
            return generator;
        }
        if (_instances.TryGetValue(type, out object? existing)) return existing;

        object instance;
        if (type.IsInterface)
        {
            instance = InertStub.Create(type);
            Stubs.Add(instance);
        }
        else
        {
            ConstructorInfo constructor = type.GetConstructors()
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .First();
            instance = constructor.Invoke(constructor.GetParameters().Select(p => Get(p.ParameterType)).ToArray());
        }

        _instances[type] = instance;
        return instance;
    }
}

/// <summary>
///     Two teams and one user who is <b>Admin</b> in team A (plus owner of a personal team) and a read-only
///     <b>Member</b> in team B. Team B owns a project, environment, server, application and deployment.
/// </summary>
public sealed class CrossTeamFixture
{
    private CrossTeamFixture()
    {
        Store = InMemoryVesselDb.Create();
        Services = new SecurityServiceFactory(Store);
    }

    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    public InMemoryVesselDb Store { get; }

    public SecurityServiceFactory Services { get; }

    public VesselAuthorizationService Authorization => new(Store.Context);

    public User Actor { get; private set; } = null!;

    public User OwnerOfB { get; private set; } = null!;

    public Team PersonalTeam { get; private set; } = null!;

    public Team TeamA { get; private set; } = null!;

    public Team TeamB { get; private set; } = null!;

    public Project ProjectA { get; private set; } = null!;

    public EnvironmentEntity EnvironmentA { get; private set; } = null!;

    public Server ServerA { get; private set; } = null!;

    public AppEntity ApplicationA { get; private set; } = null!;

    public Project ProjectB { get; private set; } = null!;

    public EnvironmentEntity EnvironmentB { get; private set; } = null!;

    public Server ServerB { get; private set; } = null!;

    public AppEntity ApplicationB { get; private set; } = null!;

    public Deployment DeploymentB { get; private set; } = null!;

    public static CrossTeamFixture Create()
    {
        var fixture = new CrossTeamFixture();
        DateTimeOffset now = fixture.Now;

        fixture.Actor = User.Create(new DisplayName("Actor"), new EmailAddress("actor@example.com"), now);
        fixture.OwnerOfB = User.Create(new DisplayName("Owner B"), new EmailAddress("owner-b@example.com"), now);
        var ownerOfA = User.Create(new DisplayName("Owner A"), new EmailAddress("owner-a@example.com"), now);

        fixture.PersonalTeam = Team.Create(new DisplayName("Actor's Team"), fixture.Actor.Id, true, now);
        fixture.TeamA = Team.Create(new DisplayName("Team A"), ownerOfA.Id, false, now);
        fixture.TeamA.AddMember(fixture.Actor.Id, TeamRole.Admin, now);
        fixture.TeamB = Team.Create(new DisplayName("Team B"), fixture.OwnerOfB.Id, false, now);
        fixture.TeamB.AddMember(fixture.Actor.Id, TeamRole.Member, now);

        (fixture.ProjectA, fixture.EnvironmentA, fixture.ServerA, fixture.ApplicationA) =
            fixture.AddResources(fixture.TeamA.Id, "a");
        (fixture.ProjectB, fixture.EnvironmentB, fixture.ServerB, fixture.ApplicationB) =
            fixture.AddResources(fixture.TeamB.Id, "b");
        fixture.DeploymentB = Deployment.Queue(fixture.ApplicationB.Id, fixture.ServerB.Id, fixture.OwnerOfB.Id,
            null, now);

        fixture.Store.Set<User>().AddRange([fixture.Actor, fixture.OwnerOfB, ownerOfA]);
        fixture.Store.Set<Team>().AddRange([fixture.PersonalTeam, fixture.TeamA, fixture.TeamB]);
        fixture.Store.Set<Deployment>().Add(fixture.DeploymentB);
        return fixture;
    }

    private (Project, EnvironmentEntity, Server, AppEntity) AddResources(TeamId teamId, string suffix)
    {
        var project = Project.Create(teamId, new ResourceName($"project-{suffix}"), Now);
        var environment = EnvironmentEntity.CreateProduction(project.Id, Now);
        var server = Server.Create(teamId, new ResourceName($"server-{suffix}"),
            new ServerAddress("127.0.0.1", new PortNumber(22)), ServerConnectionType.Local,
            ContainerRuntimeKind.Docker, Now);
        server.ChangeStatus(ServerStatus.Reachable, Now);
        var application = AppEntity.Create(environment.Id, server.Id, new ResourceName($"app-{suffix}"),
            new GitSource(new RepositoryUrl("https://example.com/repo.git"), "main"),
            BuildConfiguration.Default(ApplicationBuildPack.Dockerfile), Now);

        Store.Set<Project>().Add(project);
        Store.Set<EnvironmentEntity>().Add(environment);
        Store.Set<Server>().Add(server);
        Store.Set<AppEntity>().Add(application);
        return (project, environment, server, application);
    }
}

public sealed class FakePasswordHasher : IPasswordHasher
{
    public string HashPassword(string password)
    {
        return "hash:" + password;
    }

    public bool VerifyPassword(string passwordHash, string password)
    {
        return passwordHash == "hash:" + password;
    }
}

public sealed class SequentialTokenGenerator : ITokenGenerator
{
    private int _next;

    public string GenerateUrlSafeToken(int byteCount = 32)
    {
        return $"token{Interlocked.Increment(ref _next)}";
    }
}

/// <summary>Independent RFC 6238 implementation used to produce valid codes for <see cref="TotpService" />.</summary>
public static class TestTotp
{
    public const string Secret = "JBSWY3DPEHPK3PXP";

    public static string CurrentCode(DateTimeOffset now)
    {
        byte[] key = FromBase32(Secret);
        long timestep = now.ToUnixTimeSeconds() / 30;
        byte[] counter = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
#pragma warning disable CA5350 // RFC 6238 mandates HMAC-SHA1 for standard authenticator codes.
        byte[] hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) |
                     hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0, bits = 0;
        foreach (char character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
            buffer &= (1 << bits) - 1;
        }

        return [.. bytes];
    }
}
