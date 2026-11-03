namespace HomePlayer.AI;

public enum AiComputeDevice
{
    Auto,
    Cpu,
    Cuda
}

public static class AiComputeDeviceExtensions
{
    public static AiComputeDevice Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cpu" => AiComputeDevice.Cpu,
        "cuda" => AiComputeDevice.Cuda,
        _ => AiComputeDevice.Auto
    };
}
