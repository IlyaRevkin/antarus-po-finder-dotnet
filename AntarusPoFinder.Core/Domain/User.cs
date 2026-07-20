namespace AntarusPoFinder.Core.Domain;

/// <summary>A Windows-login-identified user, recorded as the author of an upload.</summary>
public class User
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string WindowsLogin { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
