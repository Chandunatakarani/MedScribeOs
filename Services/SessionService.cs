namespace MedScribeOS.Services;

/// <summary>
/// The app-wide "who is signed in" accessor the template feature needs.
///
/// The spec calls for an ISessionService holding the doctor's id + token. In
/// this codebase the login API (see <see cref="AuthService"/>) is already the
/// single source of truth for the session, so this is a thin typed view over
/// <see cref="AuthService.CurrentUser"/> rather than a second store. The login
/// endpoint returns no token (identity only), so <see cref="AuthToken"/> is
/// whatever the API happened to send, usually null - see deliverable notes.
/// </summary>
public interface ISessionService
{
    bool IsAuthenticated { get; }

    /// <summary>The unique doctor identifier from the login API - used to key the per-doctor template file.</summary>
    string DoctorId { get; }

    string DoctorDisplayName { get; }

    string? AuthToken { get; }
}

public sealed class SessionService : ISessionService
{
    /// <summary>Single instance used everywhere (matches the app's "new it where you need it / static singletons" style - there is no DI container).</summary>
    public static SessionService Instance { get; } = new();

    public bool IsAuthenticated => AuthService.IsSignedIn;

    public string DoctorId
    {
        get
        {
            var u = AuthService.CurrentUser;
            if (u == null) return "";
            // "account" is the API's stable unique id (e.g. "dr_smith_01");
            // fall back to email only if account came back empty.
            return !string.IsNullOrWhiteSpace(u.Account) ? u.Account.Trim() : u.Mail.Trim();
        }
    }

    public string DoctorDisplayName
    {
        get
        {
            var u = AuthService.CurrentUser;
            if (u == null) return "";
            return string.IsNullOrWhiteSpace(u.Name) ? u.Mail : u.Name;
        }
    }

    public string? AuthToken => AuthService.AuthToken;
}
