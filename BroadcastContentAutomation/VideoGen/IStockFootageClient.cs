// Stock-footage lookup contract, shared by the direct client (config key)
// and the vault-backed helper client (see StockFootageHelperClient).
// Returns the downloaded file's path, or null when Pexels has no match -
// a missed b-roll shot is not fatal to the run.
interface IStockFootageClient
{
    Task<string?> DownloadBestMatchAsync(string query, string outputPathWithoutExtension, ImageClient.Orientation orientation);
}
