namespace Vessel.Application.Security;

public interface ITokenGenerator
{
    string GenerateUrlSafeToken(int byteCount = 32);
}
