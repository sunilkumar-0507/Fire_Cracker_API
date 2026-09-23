using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace GopiCrackers.Api.Tests;

/// <summary>
/// Photo uploads from the product editor: closed without the passcode, strict
/// about what counts as a photo, and served back publicly once stored.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UploadEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    /// <summary>The smallest valid PNG: one transparent pixel.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly ApiFixture _fixture = fixture;

    private async Task<HttpResponseMessage> UploadAsync(byte[] bytes, string fileName, bool withPasscode = true)
    {
        using var client = _fixture.CreateClient();
        if (withPasscode) client.DefaultRequestHeaders.Add("X-Admin-Passcode", AdminPasscode);

        var file = new ByteArrayContent(bytes);
        // Deliberately claims to be a JPEG whatever it is: the API must judge
        // the bytes, not the label.
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        using var form = new MultipartFormDataContent { { file, "file", fileName } };
        return await client.PostAsync("/api/admin/uploads", form);
    }

    [Fact]
    public async Task Upload_needs_the_passcode()
    {
        var response = await UploadAsync(Png, "pixel.png", withPasscode: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_stores_a_photo_and_serves_it_back()
    {
        var response = await UploadAsync(Png, "../../pixel.png");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var url = body.GetProperty("url").GetString()!;
        var fileName = body.GetProperty("fileName").GetString()!;

        // The name is the API's own, never the caller's — traversal included.
        Assert.EndsWith(".png", fileName);
        Assert.DoesNotContain("..", fileName);
        Assert.True(File.Exists(Path.Combine(_fixture.UploadsPath, fileName)));
        Assert.StartsWith("http", url);

        var served = await Client.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Upload_refuses_a_file_that_is_not_a_photo()
    {
        var response = await UploadAsync("<script>alert(1)</script>"u8.ToArray(), "photo.jpg");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(_fixture.UploadsPath, "*.jpg"));
    }

    [Fact]
    public async Task Upload_refuses_a_request_with_no_file()
    {
        var (status, _) = await PostAdminAsync("/api/admin/uploads", new { });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
