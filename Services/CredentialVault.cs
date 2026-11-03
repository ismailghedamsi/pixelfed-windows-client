using System.Text.Json;
using Windows.Security.Credentials;

namespace PixelfedClient.Services;

public static class CredentialVault
{
    private const string ResourceName = "PixelfedWindowsClient";

    public static bool HasCredentialsFor(string domain)
    {
        return GetCredentialsFor(domain) is not null;
    }

    public static SavedPixelfedCredentials? GetCredentialsFor(string domain)
    {
        try
        {
            var credential = new PasswordVault()
                .Retrieve(ResourceName, domain);
            credential.RetrievePassword();
            return JsonSerializer.Deserialize<SavedPixelfedCredentials>(credential.Password);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool HasWriteScope(string domain) =>
        GetCredentialsFor(domain)?.GrantedScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("write", StringComparer.Ordinal) == true;

    public static void Save(string domain, PixelfedOAuthClient client, PixelfedSignedInAccount account)
    {
        var vault = new PasswordVault();
        try
        {
            foreach (var existing in vault.RetrieveAll().Where(credential =>
                         credential.Resource == ResourceName
                         && credential.UserName.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                vault.Remove(existing);
            }
        }
        catch (Exception)
        {
            // A new install has no saved credentials yet.
        }

        var payload = JsonSerializer.Serialize(new SavedPixelfedCredentials(
            client.ClientId,
            client.ClientSecret,
            account.Username,
            account.DisplayName,
            account.AccessToken,
            account.RefreshToken,
            account.GrantedScopes,
            account.AccountId));

        vault.Add(new PasswordCredential(ResourceName, domain, payload));
    }

    public sealed record SavedPixelfedCredentials(
        string ClientId,
        string ClientSecret,
        string Username,
        string DisplayName,
        string AccessToken,
        string? RefreshToken,
        string GrantedScopes = "read",
        string AccountId = "");
}
