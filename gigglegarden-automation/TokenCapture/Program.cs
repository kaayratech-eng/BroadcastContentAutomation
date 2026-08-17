// GiggleGarden Token Capture — run ONCE per channel on the Windows machine.
// Opens a browser, you sign in as that channel's owner, and the refresh token
// is saved to %APPDATA%\GiggleGarden\<channel>\ (encrypted at rest by Google.Apis FileDataStore).
//
// Usage: dotnet run -- <path-to-client_secret.json> <channel>
// <channel> must match the channel's key under Channels in Uploader's
// appsettings.json (e.g. "gigglegarden", "chronicleandchaos") and that
// channel's YouTube.TokenStorePath, or the Uploader will look in the wrong
// folder and find nothing.

using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;

if (args.Length < 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: TokenCapture <path-to-client_secret.json> <channel>");
    Console.Error.WriteLine("  <channel> e.g. gigglegarden, chronicleandchaos — must match");
    Console.Error.WriteLine("  Channels.<channel>.YouTube.TokenStorePath in Uploader's appsettings.json.");
    return 1;
}

var channel = args[1];
var storePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GiggleGarden", channel);

using var stream = new FileStream(args[0], FileMode.Open, FileAccess.Read);

var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    GoogleClientSecrets.FromStream(stream).Secrets,
    new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeReadonly },
    "gigglegarden-channel-owner",          // user key — must match the uploader; the per-channel
                                            // folder (not this key) is what keeps channels separate
    CancellationToken.None,
    new FileDataStore(storePath, fullPath: true));

if (credential.Token?.RefreshToken is null)
{
    Console.Error.WriteLine("No refresh token received. Remove the app at " +
        "https://myaccount.google.com/permissions and run again to force a fresh consent.");
    return 1;
}

Console.WriteLine($"Refresh token saved for channel \"{channel}\".");
Console.WriteLine($"Token store: {storePath}");
Console.WriteLine("You will not need to log in again. Keep this folder backed up and private.");
return 0;
