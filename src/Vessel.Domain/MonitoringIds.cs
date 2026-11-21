using Vessel.Domain.Common;

namespace Vessel.Domain;

public readonly record struct ServerMetricSampleId(Guid Value) : IStronglyTypedId
{
    public static ServerMetricSampleId New()
    {
        return new ServerMetricSampleId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ContainerMetricSampleId(Guid Value) : IStronglyTypedId
{
    public static ContainerMetricSampleId New()
    {
        return new ContainerMetricSampleId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct DockerCleanupExecutionId(Guid Value) : IStronglyTypedId
{
    public static DockerCleanupExecutionId New()
    {
        return new DockerCleanupExecutionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

public readonly record struct ResourceRuntimeStatusId(Guid Value) : IStronglyTypedId
{
    public static ResourceRuntimeStatusId New()
    {
        return new ResourceRuntimeStatusId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
