// GiggleGarden Token Capture — run ONCE on the Windows machine.
// Opens a browser, you sign in as the channel owner, and the refresh token
// is saved to %APPDATA%\GiggleGarden\token store (encrypted at rest by Google.Apis FileDataStore).
//
// Usage: dotnet run -- <path-to-client_secret.json>

using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: TokenCapture <path-to-client_secret.json>");
    return 1;
}

var storePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GiggleGarden");

using var stream = new FileStream(args[0], FileMode.Open, FileAccess.Read);

var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    GoogleClientSecrets.FromStream(stream).Secrets,
    new[] { YouTubeService.Scope.YoutubeUpload, YouTubeService.Scope.YoutubeReadonly },
    "gigglegarden-channel-owner",          // user key — must match the uploader
    CancellationToken.None,
    new FileDataStore(storePath, fullPath: true));

if (credential.Token?.RefreshToken is null)
{
    Console.Error.WriteLine("No refresh token received. Remove the app at " +
        "https://myaccount.google.com/permissions and run again to force a fresh consent.");
    return 1;
}

Console.WriteLine("Refresh token saved.");
Console.WriteLine($"Token store: {storePath}");
Console.WriteLine("You will not need to log in again. Keep this folder backed up and private.");
return 0;
