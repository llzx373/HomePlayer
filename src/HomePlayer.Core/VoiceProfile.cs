namespace HomePlayer.Core;

public sealed record VoiceProfile(string Id, string DisplayName, string AudioPath, string ReferenceText)
{
    public bool IsPreset => Id.StartsWith("preset-", StringComparison.OrdinalIgnoreCase);
}
