namespace LTC.Persistence.Encryption;

/// <summary>
/// Abstraction over the credential encryption mechanism. Production uses
/// <see cref="DpapiCredentialProtector"/>; tests use a no-op or in-memory fake.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Encrypt and base64-encode a plaintext credential.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypt a previously protected credential.</summary>
    string Unprotect(string protectedData);
}

/// <summary>Pass-through protector. Used by tests where DPAPI isn't appropriate.</summary>
public sealed class NoOpCredentialProtector : ICredentialProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedData) => protectedData;
}
