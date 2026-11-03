using HomePlayer.AI;

namespace HomePlayer;

public sealed record AiDeviceOption(AiComputeDevice Device, string DisplayName)
{
    public static IReadOnlyList<AiDeviceOption> Items { get; } =
    [
        new(AiComputeDevice.Auto, "自动（优先 NVIDIA CUDA）"),
        new(AiComputeDevice.Cuda, "NVIDIA CUDA GPU"),
        new(AiComputeDevice.Cpu, "CPU（兼容回退）")
    ];
}

public sealed record AiEngineOption(bool UseSherpa, string DisplayName)
{
    public static IReadOnlyList<AiEngineOption> Items { get; } =
    [
        new(true, "Sherpa-ONNX SenseVoice（英/日/韩，低延迟）"),
        new(false, "Whisper（多语言）")
    ];
}
