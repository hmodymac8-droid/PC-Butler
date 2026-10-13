// SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
// Copyright (c) 2026 hmodymac8-droid
using System.Net;
using System.Net.Http.Headers;

namespace PCButler.Core;

/// <summary>
/// Searching Google for something copied: a text goes in the address as an ordinary search;
/// a picture is sent to Google Lens, which answers with the page of its results.
/// </summary>
public static class WebSearch
{
    /// <summary>The Google results page for a text (long texts are cut, as a search can't be a whole page).</summary>
    public static string Google(string text)
    {
        var query = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length > 1500)
            query = query[..1500];
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
    }

    public static bool IsPicture(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico";

    /// <summary>
    /// Send a picture to Google Lens and return the address of its results, to open in the
    /// browser. The picture leaves the PC (that is what searching by picture means). Throws
    /// IOException when Google can't be reached or doesn't answer as expected.
    /// </summary>
    public static async Task<string> GoogleLens(string picture, CancellationToken cancel = default)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(picture, cancel));
            file.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(picture).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/png",
            });
            form.Add(file, "encoded_image", Path.GetFileName(picture));
            using var answer = await http.PostAsync("https://lens.google.com/v3/upload?hl=" + Uri.EscapeDataString(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName), form, cancel);
            if (answer.StatusCode is HttpStatusCode.SeeOther or HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
                && answer.Headers.Location is { } results)
                return (results.IsAbsoluteUri ? results : new Uri(new Uri("https://lens.google.com"), results)).ToString();
            throw new IOException($"Google Lens answered {(int)answer.StatusCode}");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new IOException(error.Message, error);
        }
    }
}
