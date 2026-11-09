using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace MultiServerView.Core.Sync;

/// <summary>
/// A WebDAV folder as the store: works with Nextcloud/ownCloud, Synology, most NAS boxes, nginx/Apache with WebDAV and
/// many hosters. Only the encrypted vault blob is uploaded. Conflicts are detected with the ETag (If-Match / If-None-Match),
/// so two devices can never overwrite each other silently.
/// </summary>
public sealed class WebDavRemote : ISyncRemote
{
    public const string FileName = "MultiServerView-Vault.msv";

    private readonly HttpClient _http;
    private readonly Uri _file;
    private readonly string _auth;
    private string? _etag;

    public WebDavRemote(HttpClient http, string folderUrl, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(folderUrl)) throw new SyncException("Please enter the WebDAV folder address.");
        if (!Uri.TryCreate(folderUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var folder) ||
            !(folder.Scheme == Uri.UriSchemeHttps || (folder.Scheme == Uri.UriSchemeHttp && folder.IsLoopback)))
            throw new SyncException("The WebDAV address must start with https:// (plain http:// is only allowed for localhost).");
        _http = http;
        _file = new Uri(folder, FileName);
        _auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
    }

    public async Task<RemoteVault?> GetAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _etag = null;
            return null;
        }
        Check(response);
        var blob = await response.Content.ReadAsByteArrayAsync(ct);
        _etag = response.Headers.ETag?.ToString();
        return new RemoteVault(VersionOf(blob), blob);
    }

    public async Task<long> InsertAsync(byte[] blob, CancellationToken ct = default)
    {
        // If-None-Match: * = only create, never overwrite a file another device just uploaded
        using var response = await SendAsync(HttpMethod.Put, blob, ("If-None-Match", "*"), ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new SyncException("Another device just created a vault in this folder. Please try again in a moment.");
        Check(response);
        _etag = response.Headers.ETag?.ToString();
        return VersionOf(blob);
    }

    public async Task<long?> UpdateAsync(long expectedVersion, byte[] blob, CancellationToken ct = default)
    {
        if (_etag == null)
        {
            // Server without ETags: compare the content instead (small gap between check and upload, acceptable for a personal vault).
            var current = await GetAsync(ct);
            if (current == null || current.Version != expectedVersion) return null;
        }

        using var response = _etag != null
            ? await SendAsync(HttpMethod.Put, blob, ("If-Match", _etag), ct)
            : await SendAsync(HttpMethod.Put, blob, null, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) return null;
        Check(response);
        _etag = response.Headers.ETag?.ToString();
        return VersionOf(blob);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, byte[]? body, (string Name, string Value)? header, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _file);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _auth);
        if (body != null) request.Content = new ByteArrayContent(body);
        if (header is { } h) request.Headers.TryAddWithoutValidation(h.Name, h.Value);
        try
        {
            // Buffer the answer so the request can be disposed.
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException("WebDAV server not reachable: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("The WebDAV server does not answer (timeout).");
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new SyncException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "WebDAV sign-in refused: user name or (app) password is wrong.",
            HttpStatusCode.Forbidden => "The WebDAV account may not write to this folder.",
            HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed => "The WebDAV folder does not exist. Create it first and check the address.",
            _ => $"The WebDAV server answered {(int)response.StatusCode} {response.ReasonPhrase}."
        });
    }

    private static long VersionOf(byte[] blob) => BitConverter.ToInt64(SHA256.HashData(blob), 0);
}
