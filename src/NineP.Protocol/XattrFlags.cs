namespace NineP.Protocol;

/// <summary><c>Txattrcreate</c> flags (reference §5.9).</summary>
[Flags]
public enum XattrFlags
{
    /// <summary>No flag is set: create the attribute or replace it.</summary>
    None = 0,

    /// <summary>Fail if the attribute already exists.</summary>
    Create = 1,

    /// <summary>Fail if the attribute does not already exist.</summary>
    Replace = 2,
}
