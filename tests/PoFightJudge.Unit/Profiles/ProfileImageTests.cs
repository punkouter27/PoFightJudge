using PoFightJudge.Api.Common;
using PoFightJudge.Api.Features.Profiles;
using PoFightJudge.TestSupport;

namespace PoFightJudge.Unit.Profiles;

public class ProfileImageTests
{
    /// <summary>The server decodes nothing: a 512 px square PNG is kept byte for byte, and anything else is refused with a reason.</summary>
    [Fact]
    public async Task Only_a_512_square_png_is_kept()
    {
        var square = TestPng.Solid(ProfileImageService.Size, ProfileImageService.Size);
        var kept = await ReadAsync(square);
        kept.IsSuccess.Should().BeTrue(kept.Error);
        kept.Value.Should().Equal(square);

        foreach (var (bytes, refusal) in new (byte[], string)[]
        {
            ([], "No image"),
            ("definitely not an image, however long it goes on"u8.ToArray(), "Not a PNG"),
            (TestPng.Solid(64, 64), "512"),
            (new byte[ProfileImageService.MaxUploadBytes + 1], "larger than"),
        })
        {
            var read = await ReadAsync(bytes);
            read.IsSuccess.Should().BeFalse(refusal);
            read.Error.Should().Contain(refusal);
        }
    }

    private static async Task<Outcome<byte[]>> ReadAsync(byte[] bytes)
    {
        using var body = new MemoryStream(bytes);
        return await ProfileImageService.ReadPngAsync(body, CancellationToken.None);
    }
}
