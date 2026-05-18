namespace Vessel.Domain.Common;

public interface IStronglyTypedId
{
    Guid Value { get; }
}
