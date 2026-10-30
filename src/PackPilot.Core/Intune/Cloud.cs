namespace PackPilot.Core.Intune;

/// <summary>A Microsoft cloud: where to sign in, where Graph lives, and where the Intune admin center is.</summary>
public record Cloud(string Id, string Name, string LoginHost, string GraphHost, string? PortalHost, string Note)
{
    public static readonly Cloud[] All =
    {
        new("Public", "Commercial / GCC", "https://login.microsoftonline.com", "https://graph.microsoft.com", "https://intune.microsoft.com",
            "Worldwide commercial tenants. GCC (Moderate) uses these endpoints too."),
        new("USGovHigh", "GCC High", "https://login.microsoftonline.us", "https://graph.microsoft.us", "https://intune.microsoft.us",
            "US Government Community Cloud High."),
        new("USGovDoD", "DoD", "https://login.microsoftonline.us", "https://dod-graph.microsoft.us", "https://intune.microsoft.us",
            "US Department of Defense tenants."),
        new("China", "China (21Vianet)", "https://login.chinacloudapi.cn", "https://microsoftgraph.chinacloudapi.cn", null,
            "Microsoft 365 operated by 21Vianet."),
    };

    public static Cloud ById(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];

    /// <summary>Delegated permissions PackPilot asks for: manage apps, and read groups to pick an assignment.</summary>
    public string[] Scopes => new[] { $"{GraphHost}/DeviceManagementApps.ReadWrite.All", $"{GraphHost}/Group.Read.All" };

    /// <summary>The app's page in the Intune admin center, when this cloud has a known portal.</summary>
    public string? AppPortalUrl(string appId) =>
        PortalHost == null ? null : $"{PortalHost}/#view/Microsoft_Intune_Apps/SettingsMenu/~/0/appId/{appId}";

    public override string ToString() => Name;

    /// <summary>
    /// Microsoft Graph Command Line Tools: Microsoft's public multi-tenant client, the one Connect-MgGraph uses.
    /// Works in every cloud with no app registration, but a Global or Privileged Role Administrator has to consent
    /// to the two permissions once. Organizations that restrict it can enter their own app registration instead.
    /// </summary>
    public const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
}
