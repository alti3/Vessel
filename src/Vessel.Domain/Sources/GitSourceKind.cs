namespace Vessel.Domain.Sources;

/// <summary>How a <see cref="GitSourceConnection" /> authenticates clones and provider API calls (DEP-08).</summary>
public enum GitSourceKind
{
    /// <summary>GitHub (or GitHub Enterprise) App: a JWT signed with the app private key mints installation tokens.</summary>
    GitHubApp = 0,

    /// <summary>GitLab (gitlab.com or self-hosted) OAuth application with an access/refresh token pair.</summary>
    GitLabApp = 1,

    /// <summary>Gitea, Forgejo or Codeberg OAuth application with an access/refresh token pair.</summary>
    GiteaApp = 2,

    /// <summary>A team SSH key (marked git-related) used as a repository deploy key.</summary>
    DeployKey = 3,

    /// <summary>HTTPS username plus password or access token, bound to one Git host.</summary>
    HttpsCredential = 4
}
