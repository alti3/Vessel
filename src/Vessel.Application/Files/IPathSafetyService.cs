namespace Vessel.Application.Files;

public interface IPathSafetyService
{
    string EnsureOwnedPath(string rootDirectory, string candidatePath, bool mustAlreadyExist = false);

    string EnsureOwnedRelativePath(string rootDirectory, string relativePath);
}
