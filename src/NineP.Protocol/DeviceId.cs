namespace NineP.Protocol;

/// <summary>A device major/minor pair for character and block devices (reference §4.7).</summary>
/// <param name="Major">The major device number.</param>
/// <param name="Minor">The minor device number.</param>
public readonly record struct DeviceId(uint Major, uint Minor);
