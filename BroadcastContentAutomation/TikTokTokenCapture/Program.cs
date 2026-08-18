// GiggleGarden TikTok Token Capture — run ONCE per TikTok Developer app.
//
// Opens a browser for TikTok's OAuth consent, captures the redirect via a
// local loopback listener (TikTok requires a pre-registered redirect URI —
// there's no manual copy-paste-a-code flow like Google's), exchanges the
// authorization code for a refresh token, and saves it where TikTokPublisher
// expects to find it.
//
// Prerequisite: in your TikTok Developer app settings, register a redirect
// URI of EXACTLY http://localhost:53682/callback — TikTok rejects the token
// exchange on any mismatch, including a trailing slash difference.
//
// Usage: dotnet run -- <client-key> <client-secret> [token-store-path]

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: TikTokTokenCapture <client-key> <client-secret> [token-store-path]");
    return 1;
}

var clientKey = args[0];
var clientSecret = args[1];
var tokenStorePath = args.Length > 2 ? args[2] : @"C:\Secure\GiggleGarden\tiktok-token.json";

const string RedirectUri = "http://localhost:53682/callback";

// video.publish is only granted to audited production apps with Direct Post
// approved (see TikTokConfig.DirectPost) — Sandbox apps and unaudited
// production apps can only request video.upload (draft-to-inbox).
const string Scope = "user.info.basic,video.upload";

// TikTok's desktop OAuth flow requires PKCE. Unlike the usual RFC 7636 shape
// (base64url-encoded SHA256), TikTok's own docs specify hex-encoded SHA256 —
// get this wrong and the authorize call fails with a vague "code_challenge" error.
const string CodeVerifierChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
var codeVerifier = RandomNumberGenerator.GetString(CodeVerifierChars, 64);
var codeChallenge = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))).ToLowerInvariant();

var state = Guid.NewGuid().ToString("N");
var authorizeUrl =
    $"https://www.tiktok.com/v2/auth/authorize/?client_key={Uri.EscapeDataString(clientKey)}" +
    $"&response_type=code&scope={Uri.EscapeDataString(Scope)}" +
    $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={state}" +
    $"&code_challenge={codeChallenge}&code_challenge_method=S256";

Console.WriteLine("Register this exact redirect URI in your TikTok Developer app first:");
Console.WriteLine($"  {RedirectUri}");
Console.WriteLine();
Console.WriteLine("Opening browser for TikTok consent. If it doesn't open, visit this URL manually:");
Console.WriteLine(authorizeUrl);

try
{
    Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
}
catch
{
    // Non-fatal — the printed URL above still works if launched by hand.
}

using var listener = new HttpListener();
listener.Prefixes.Add(RedirectUri.EndsWith('/') ? RedirectUri : RedirectUri + "/");
listener.Start();

Console.WriteLine();
Console.WriteLine("Waiting for the TikTok redirect...");
var context = await listener.GetContextAsync();
var query = context.Request.QueryString;

var responseBody = "<html><body>You can close this tab and return to the terminal.</body></html>";
var buffer = Encoding.UTF8.GetBytes(responseBody);
context.Response.ContentLength64 = buffer.Length;
await context.Response.OutputStream.WriteAsync(buffer);
context.Response.Close();
listener.Stop();

if (query["state"] != state)
{
    Console.Error.WriteLine("State mismatch (possible CSRF, or a stale browser tab). Aborting — run again.");
    return 1;
}

var code = query["code"];
if (string.IsNullOrEmpty(code))
{
    Console.Error.WriteLine($"No authorization code in the redirect. TikTok error: {query["error"]} {query["error_description"]}");
    return 1;
}

using var http = new HttpClient();
using var tokenResp = await http.PostAsync("https://open.tiktokapis.com/v2/oauth/token/",
    new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["client_key"] = clientKey,
        ["client_secret"] = clientSecret,
        ["code"] = code,
        ["grant_type"] = "authorization_code",
        ["redirect_uri"] = RedirectUri,
        ["code_verifier"] = codeVerifier,
    }));

var responseJson = await tokenResp.Content.ReadAsStringAsync();
if (!tokenResp.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Token exchange failed ({(int)tokenResp.StatusCode}): {responseJson}");
    return 1;
}

using var doc = JsonDocument.Parse(responseJson);
var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
if (string.IsNullOrEmpty(refreshToken))
{
    Console.Error.WriteLine($"TikTok response had no refresh_token: {responseJson}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(tokenStorePath)!);
await File.WriteAllTextAsync(tokenStorePath, JsonSerializer.Serialize(new { RefreshToken = refreshToken }));

Console.WriteLine();
Console.WriteLine("Refresh token saved.");
Console.WriteLine($"Token store: {tokenStorePath}");
Console.WriteLine("TikTok rotates this token on every use — TikTokPublisher keeps it updated automatically after that.");
return 0;
