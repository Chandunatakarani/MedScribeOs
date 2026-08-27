using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MedScribeOS.Services;

/// <summary>
/// The signed-in HFMG user, exactly the four fields the web app's frontend
/// reads off its /api/login response: { account, name, mail, role }.
/// </summary>
public sealed record AuthenticatedUser(string Account, string Name, string Mail, string Role);

/// <summary>
/// Authenticates against the same internal .NET endpoint the other HFMG app
/// uses:
///
///     POST {OrgBaseUrl}/login
///     { "email": "...", "password": "..." }   ->   { account, name, mail, role }
///
/// The web app reaches this through a FastAPI proxy (ORG_BASE in main.py);
/// this desktop app calls it directly, so the machine running MedScribe OS
/// has to be able to see 172.22.6.188:177 - i.e. be on the HFMG network. If
/// MedScribe ever moves off that network, point the URL at a proxy instead
/// via %AppData%\MedScribeOS\config.json -> "OrgApiBaseUrl" (same config file
/// OpenAiClient already reads), no rebuild needed.
///
/// The endpoint returns no token - just the user's identity - so there is
/// nothing to attach to later requests. Sign-in here is purely an access
/// gate plus "who is using this machine", and it lasts for the life of the
/// process (reopening from the tray does not re-prompt; "Sign out" does).
/// </summary>
public static class AuthService
{
    // Same host as ORG_BASE in the FastAPI backend's main.py.
    private const string DefaultOrgBaseUrl = "http://172.22.6.188:177";

    private static readonly HttpClient Http = new()
    {
        // An internal server that may simply be unreachable from an
        // off-network machine - fail in a few seconds instead of hanging the
        // Sign In button on HttpClient's default 100-second timeout.
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>The user from the last successful <see cref="LoginAsync"/>, or null before sign-in / after <see cref="SignOut"/>.</summary>
    public static AuthenticatedUser? CurrentUser { get; private set; }

    /// <summary>
    /// Bearer token from the login response if the endpoint returns one
    /// ("token" / "access_token" / "jwt"). The HFMG endpoint currently returns
    /// identity only, so this is normally null - it's captured defensively so
    /// callers (SessionService) don't have to change if a token is added later.
    /// </summary>
    public static string? AuthToken { get; private set; }

    public static bool IsSignedIn => CurrentUser != null;

    /// <summary>Resolved once: the config.json override if present and non-empty, otherwise the compiled-in default.</summary>
    public static string OrgBaseUrl { get; } = ResolveOrgBaseUrl();

    /// <summary>
    /// Posts email + password to {OrgBaseUrl}/login. On success, stores the
    /// returned user in <see cref="CurrentUser"/> and returns it. Throws
    /// <see cref="AuthException"/> - whose message is safe to show the user
    /// as-is - for bad credentials, an unreachable server, or a response
    /// MedScribe can't make sense of.
    /// </summary>
    public static async Task<AuthenticatedUser> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new AuthException("Enter both your email and password.");

        var payload = JsonSerializer.Serialize(new { email = email.Trim(), password });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync($"{OrgBaseUrl}/login", content, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthException(
                $"The sign-in server didn't respond ({OrgBaseUrl}). Check that you're on the HFMG network.");
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException(
                $"Couldn't reach the sign-in server ({OrgBaseUrl}). Check that you're on the HFMG network.\n\n{ex.Message}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var reason = ExtractErrorMessage(body);
            var badCredentials = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            throw new AuthException(
                reason
                ?? (badCredentials
                    ? "Incorrect email or password."
                    : $"Sign-in failed (HTTP {(int)response.StatusCode})."));
        }

        AuthenticatedUser user;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string Str(string prop) =>
                root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? ""
                    : "";
            user = new AuthenticatedUser(Str("account"), Str("name"), Str("mail"), Str("role"));

            var token = Str("token");
            if (string.IsNullOrEmpty(token)) token = Str("access_token");
            if (string.IsNullOrEmpty(token)) token = Str("jwt");
            AuthToken = string.IsNullOrEmpty(token) ? null : token;
        }
        catch (JsonException)
        {
            throw new AuthException("The sign-in server returned a response MedScribe didn't understand.");
        }

        // Some backends answer a bad login with 200 + an error body rather
        // than a 4xx - no identity fields means it wasn't a real sign-in.
        if (string.IsNullOrWhiteSpace(user.Mail) && string.IsNullOrWhiteSpace(user.Account))
            throw new AuthException(ExtractErrorMessage(body) ?? "Incorrect email or password.");

        CurrentUser = user;
        return user;
    }

    public static void SignOut()
    {
        CurrentUser = null;
        AuthToken = null;
    }

    /// <summary>
    /// Pulls a human-readable message out of whatever the endpoint sent - a
    /// JSON { "message" | "error" | "detail": "..." } object, or a bare JSON
    /// string, or a short plain-text body - or null if there's nothing useful
    /// (e.g. an empty body or a full HTML error page).
    /// </summary>
    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString();

            foreach (var key in new[] { "message", "error", "detail", "Message", "Error" })
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
        }
        catch (JsonException)
        {
            var trimmed = body.Trim();
            if (trimmed.Length is > 0 and <= 200) return trimmed;
        }
        return null;
    }

    private static string ResolveOrgBaseUrl()
    {
        // Mirrors OpenAiClient's config.json fallback so the URL can be
        // repointed (at a proxy, a staging host, ...) without a rebuild.
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MedScribeOS", "config.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("OrgApiBaseUrl", out var el))
                {
                    var url = el.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url.TrimEnd('/');
                }
            }
        }
        catch
        {
            // Any problem reading/parsing config - just use the default.
        }
        return DefaultOrgBaseUrl;
    }
}

/// <summary>A sign-in failure whose <see cref="Exception.Message"/> is safe to show the user directly.</summary>
public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}
