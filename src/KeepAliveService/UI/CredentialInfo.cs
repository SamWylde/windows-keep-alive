namespace KeepAliveService.UI;

public enum AccountType
{
    MicrosoftAccount,
    LocalAccount,
    DomainOrWorkAccount,
}

public sealed record CredentialInfo(
    string Username,
    string Password,
    AccountType AccountType,
    string? Domain)
{
    public override string ToString()
    {
        return $"{nameof(CredentialInfo)} {{ Username = {Username}, Password = <redacted>, AccountType = {AccountType}, Domain = {Domain} }}";
    }

    public string ResolveDomain()
    {
        return AccountType switch
        {
            AccountType.MicrosoftAccount => "MicrosoftAccount",
            AccountType.LocalAccount => string.IsNullOrWhiteSpace(Domain)
                ? Environment.MachineName
                : Domain.Trim(),
            AccountType.DomainOrWorkAccount => string.IsNullOrWhiteSpace(Domain)
                ? Environment.UserDomainName
                : Domain.Trim(),
            _ => Environment.MachineName,
        };
    }
}
