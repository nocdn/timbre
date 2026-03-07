namespace timbre.Models;

public sealed class AudioInputDevice
{
    public AudioInputDevice(string id, string displayName, bool isDefault)
    {
        Id = id;
        DisplayName = displayName;
        IsDefault = isDefault;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool IsDefault { get; }
}
