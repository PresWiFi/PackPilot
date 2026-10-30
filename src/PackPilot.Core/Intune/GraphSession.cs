using Microsoft.Identity.Client;

namespace PackPilot.Core.Intune;

/// <summary>A signed-in admin in one cloud and tenant. Tokens are kept in memory only, for this run of PackPilot.</summary>
public sealed class GraphSession
{
    readonly IPublicClientApplication _app;
    IAccount? _account;

    public Cloud Cloud { get; }
    public string Tenant { get; }
    public string ClientId { get; }
    public string? UserName { get; private set; }
    public string? TenantId { get; private set; }
    public bool SignedIn => _account != null;

    public GraphSession(Cloud cloud, string? tenant, string? clientId)
    {
        Cloud = cloud;
        Tenant = string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim();
        ClientId = string.IsNullOrWhiteSpace(clientId) ? Cloud.DefaultClientId : clientId.Trim();
        _app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority($"{cloud.LoginHost}/{Tenant}")
            .WithRedirectUri("http://localhost")
            .Build();
    }

    /// <summary>Opens the browser for sign-in (and consent, the first time).</summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        var r = await _app.AcquireTokenInteractive(Cloud.Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct);
        _account = r.Account;
        UserName = r.Account.Username;
        TenantId = r.TenantId;
    }

    /// <summary>A current access token, refreshed silently; falls back to the browser if the session expired.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_account == null) throw new InvalidOperationException("Sign in first.");
        try
        {
            return (await _app.AcquireTokenSilent(Cloud.Scopes, _account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var r = await _app.AcquireTokenInteractive(Cloud.Scopes).WithAccount(_account).WithUseEmbeddedWebView(false).ExecuteAsync(ct);
            _account = r.Account;
            return r.AccessToken;
        }
    }

    public async Task SignOutAsync()
    {
        if (_account != null) await _app.RemoveAsync(_account);
        _account = null; UserName = null; TenantId = null;
    }
}
