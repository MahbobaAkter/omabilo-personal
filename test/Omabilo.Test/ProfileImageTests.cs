using Xunit;

// Profile picture and cover: checked by content, kept as a preview until saved, replacing and removing
// deletes the old file, and one person can never save or remove another person's picture.

public class ProfileImageTests
{
    private sealed class MemoryFiles : IProfileFiles
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public bool FailWrites;

        public Task WriteAsync(string path, byte[] data, string mimeType, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("storage down");
            Files[path] = data;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            Files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 1, 2, 3];
    private static readonly byte[] Webp = [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBPVP8 "u8.ToArray()];

    private readonly InMemoryCompanionStore _store = new();
    private readonly MemoryFiles _files = new();
    private readonly List<string> _logs = [];
    private DateTime _now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    private readonly ProfileImageService _service;

    public ProfileImageTests() => _service = new ProfileImageService(_store, _files, () => _now, _logs.Add);

    private async Task<ProfileImage> SaveAsync(string userId, ProfileImageKind kind, byte[] data, int focus = 50)
    {
        var staged = await _service.StageAsync(userId, kind, data, default);
        Assert.True(staged.Ok, staged.Outcome.ToString());
        var saved = await _service.CommitAsync(userId, staged.Image!, focus, default);
        Assert.True(saved.Ok);
        _now = _now.AddSeconds(1);
        return saved.Image!;
    }

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("webp", "image/webp")]
    public void Pictures_are_recognised_by_their_content(string kind, string mime)
    {
        var data = kind switch { "png" => Png, "jpeg" => Jpeg, _ => Webp };
        Assert.Equal(mime, ProfileImages.SniffMime(data));
    }

    [Fact]
    public async Task Files_that_are_not_pictures_or_too_large_are_refused_before_anything_is_written()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var pdf = "%PDF-1.7 fake"u8.ToArray();
        var huge = new byte[ProfileImages.MaxAvatarBytes + 1];
        Png.CopyTo(huge, 0);

        Assert.Equal(ProfileImageOutcome.NotAnImage, (await _service.StageAsync("alice", ProfileImageKind.Avatar, svg, default)).Outcome);
        Assert.Equal(ProfileImageOutcome.NotAnImage, (await _service.StageAsync("alice", ProfileImageKind.Avatar, pdf, default)).Outcome);
        Assert.Equal(ProfileImageOutcome.Empty, (await _service.StageAsync("alice", ProfileImageKind.Avatar, [], default)).Outcome);
        Assert.Equal(ProfileImageOutcome.TooLarge, (await _service.StageAsync("alice", ProfileImageKind.Avatar, huge, default)).Outcome);

        // A cover may be larger than a profile picture
        Assert.True((await _service.StageAsync("alice", ProfileImageKind.Cover, huge, default)).Ok);
        Assert.Single(_files.Files);
        Assert.Empty(_store.ProfileImages);
        Assert.Contains("5 MB", new ProfileImageResult(ProfileImageOutcome.TooLarge).Message(ProfileImageKind.Cover));
    }

    [Fact]
    public async Task A_preview_changes_nothing_until_it_is_saved()
    {
        var staged = await _service.StageAsync("alice", ProfileImageKind.Avatar, Png, default);

        Assert.True(staged.Ok);
        Assert.True(_files.Files.ContainsKey(staged.Image!.Path));
        Assert.Empty(await _service.GetAsync("alice", default));

        await _service.DiscardAsync("alice", staged.Image, default);
        Assert.Empty(_files.Files);
        Assert.Empty(await _service.GetAsync("alice", default));
    }

    [Fact]
    public async Task Saving_persists_the_picture_in_the_persons_own_folder()
    {
        var saved = await SaveAsync("alice", ProfileImageKind.Avatar, Jpeg);

        var stored = Assert.Single(await _store.GetProfileImagesAsync("alice", default));
        Assert.Equal(saved.Path, stored.Path);
        Assert.Equal("image/jpeg", stored.MimeType);
        Assert.StartsWith(ProfileImages.FolderFor("alice"), stored.Path);
        Assert.EndsWith(".jpg", stored.Path);
        Assert.DoesNotContain("alice", stored.Path);
        Assert.Equal(Jpeg, _files.Files[stored.Path]);
        Assert.Contains(_store.Audit, a => a.UserId == "alice" && a.Entry.Action == "profile_avatar_added");
    }

    [Fact]
    public async Task Replacing_a_picture_deletes_the_old_file_after_the_new_one_is_saved()
    {
        var first = await SaveAsync("alice", ProfileImageKind.Avatar, Png);
        var second = await SaveAsync("alice", ProfileImageKind.Avatar, Jpeg);

        Assert.NotEqual(first.Path, second.Path);
        Assert.False(_files.Files.ContainsKey(first.Path));
        Assert.True(_files.Files.ContainsKey(second.Path));
        Assert.Equal(second.Path, (await _store.GetProfileImageAsync("alice", ProfileImageKind.Avatar, default))!.Path);
        Assert.Contains(_store.Audit, a => a.Entry.Action == "profile_avatar_replaced");
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_saved_picture()
    {
        var kept = await SaveAsync("alice", ProfileImageKind.Avatar, Png);
        _files.FailWrites = true;

        var failed = await _service.StageAsync("alice", ProfileImageKind.Avatar, Jpeg, default);

        Assert.Equal(ProfileImageOutcome.Failed, failed.Outcome);
        Assert.Equal(kept.Path, (await _store.GetProfileImageAsync("alice", ProfileImageKind.Avatar, default))!.Path);
        Assert.True(_files.Files.ContainsKey(kept.Path));
        Assert.Contains("try again", failed.Message(ProfileImageKind.Avatar));
    }

    [Fact]
    public async Task The_cover_keeps_its_crop_position_and_can_be_moved_later()
    {
        await SaveAsync("alice", ProfileImageKind.Cover, Webp, focus: 30);
        Assert.Equal(30, (await _store.GetProfileImageAsync("alice", ProfileImageKind.Cover, default))!.FocusY);

        var moved = await _service.SetCoverFocusAsync("alice", 140, default);

        Assert.Equal(100, moved!.FocusY);
        Assert.Equal(100, (await _store.GetProfileImageAsync("alice", ProfileImageKind.Cover, default))!.FocusY);
        Assert.Null(await _service.SetCoverFocusAsync("bob", 20, default));
    }

    [Fact]
    public async Task Removing_the_cover_brings_back_the_default_and_deletes_the_file()
    {
        var cover = await SaveAsync("alice", ProfileImageKind.Cover, Png);
        var avatar = await SaveAsync("alice", ProfileImageKind.Avatar, Jpeg);

        Assert.True(await _service.RemoveAsync("alice", ProfileImageKind.Cover, default));

        Assert.Null(await _store.GetProfileImageAsync("alice", ProfileImageKind.Cover, default));
        Assert.False(_files.Files.ContainsKey(cover.Path));
        Assert.True(_files.Files.ContainsKey(avatar.Path));
        Assert.False(await _service.RemoveAsync("alice", ProfileImageKind.Cover, default));
    }

    [Fact]
    public async Task One_person_cannot_save_discard_or_remove_another_persons_picture()
    {
        var alices = await SaveAsync("alice", ProfileImageKind.Avatar, Png);
        var aliceStaged = (await _service.StageAsync("alice", ProfileImageKind.Cover, Jpeg, default)).Image!;

        // Bob holding Alice's preview (or a forged one pointing into her folder) gets nowhere
        Assert.False((await _service.CommitAsync("bob", aliceStaged, 50, default)).Ok);
        Assert.False((await _service.CommitAsync("bob", aliceStaged with { UserId = "bob" }, 50, default)).Ok);
        await _service.DiscardAsync("bob", aliceStaged, default);
        Assert.False(await _service.RemoveAsync("bob", ProfileImageKind.Avatar, default));

        Assert.True(_files.Files.ContainsKey(aliceStaged.Path));
        Assert.True(_files.Files.ContainsKey(alices.Path));
        Assert.Empty(await _store.GetProfileImagesAsync("bob", default));
        Assert.Equal(alices.Path, (await _store.GetProfileImageAsync("alice", ProfileImageKind.Avatar, default))!.Path);
        Assert.NotEqual(ProfileImages.FolderFor("alice"), ProfileImages.FolderFor("bob"));
    }

    [Fact]
    public async Task Discarding_a_preview_never_deletes_the_saved_picture()
    {
        var saved = await SaveAsync("alice", ProfileImageKind.Avatar, Png);

        await _service.DiscardAsync("alice", saved, default);

        Assert.True(_files.Files.ContainsKey(saved.Path));
    }

    [Fact]
    public async Task Pictures_show_in_the_snapshot_and_go_with_all_data()
    {
        var rig = new CompanionRig();
        var files = new MemoryFiles();
        var pictures = new ProfileImageService(rig.Store, files, () => rig.Now, rig.Logs.Add);
        var service = new CompanionService(rig.Store, rig.Engine, () => rig.Now, pictures);
        var staged = (await pictures.StageAsync("alice", ProfileImageKind.Cover, Png, default)).Image!;
        await pictures.CommitAsync("alice", staged, 40, default);

        // A new page load (refresh or the next sign-in) reads it back from the store
        var snap = await service.LoadAsync("alice", default);
        Assert.Equal(staged.Path, snap.Cover!.Path);
        Assert.Equal(40, snap.Cover.FocusY);
        Assert.Null(snap.Avatar);
        Assert.Null((await service.LoadAsync("bob", default)).Cover);

        await service.DeleteAllDataAsync("alice", default);

        Assert.Empty(files.Files);
        Assert.Null((await service.LoadAsync("alice", default)).Cover);
    }
}
