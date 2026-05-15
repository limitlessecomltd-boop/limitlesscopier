namespace LTC.App.Licensing;

/// <summary>
/// The data encoded inside a license key. When you mint a key with the
/// keygen tool, you fill in these fields and the tool signs them; the
/// signature gets baked into the key string. The app verifies the signature
/// before trusting any of these values.
///
/// Plan and ExpiresUtc are NOT enforced by the current "simple" license
/// system -- the app trusts any signed key regardless of plan or date. They
/// exist so we can add enforcement later without changing the key format
/// (the professional licensing pass).
/// </summary>
public sealed record LicenseInfo(
    /// <summary>Email of the customer this key was issued to. Used as the
    /// "name on the license" shown in Settings -> License.</summary>
    string Email,

    /// <summary>"Daily", "Lifetime", "Partner", or "Dev" -- corresponds to
    /// the three plans plus an internal/dev tier you might use for yourself
    /// and the team.</summary>
    string Plan,

    /// <summary>When this key was minted by the keygen tool. Shown in the
    /// Settings panel so customers can confirm their key is the right one.</summary>
    DateTime IssuedUtc,

    /// <summary>When the key stops being valid. Only meaningful for "Daily"
    /// plan keys (which expire after the paid period); Lifetime keys can use
    /// DateTime.MaxValue here. Currently NOT enforced by the app -- expiry
    /// will land in the professional licensing system.</summary>
    DateTime ExpiresUtc);
