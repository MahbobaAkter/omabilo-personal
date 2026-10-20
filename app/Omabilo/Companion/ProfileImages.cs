// The person's own profile picture and cover image. The file goes to the app's private file storage
// (no public link; the page shows it through a signed URL that renews itself) and a small row in the
// database names its path, so the picture is there again after a refresh or the next sign-in.
// Every call takes the signed-in person's id from the server session: nothing a browser sends can
// name another person's picture. Plain C#, no IKON types; the IKON glue supplies IProfileFiles.

public enum ProfileImageKind { Avatar, Cover }

/// <summary>One saved picture. FocusY is the cover's vertical crop position, 0 (top) to 100 (bottom).</summary>
public sealed record ProfileImage(string UserId, ProfileImageKind Kind, string Path, string MimeType, long SizeBytes, int FocusY, DateTime UpdatedUtc);

/// <summary>The private file storage pictures are written to.</summary>
public interface IProfileFiles
{
    Task WriteAsync(string path, byte[] data, string mimeType, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

public enum ProfileImageOutcome { Saved, Empty, TooLarge, NotAnImage, Failed }

public sealed record ProfileImageResult(ProfileImageOutcome Outcome, ProfileImage? Image = null)
{
    public bool Ok => Outcome == ProfileImageOutcome.Saved;

    public string Message(ProfileImageKind kind) => Outcome switch
    {
        ProfileImageOutcome.Saved => kind == ProfileImageKind.Avatar ? "Profile picture saved." : "Cover image saved.",
        ProfileImageOutcome.Empty => "That file is empty. Choose another picture.",
        ProfileImageOutcome.TooLarge => $"That picture is too large. The limit is {ProfileImages.MaxBytes(kind) / (1024 * 1024)} MB.",
        ProfileImageOutcome.NotAnImage => "Use a JPG, PNG or WebP picture.",
        _ => "The picture couldn't be saved. Please try again.",
    };
}

public static class ProfileImages
{
    public const long MaxAvatarBytes = 2 * 1024 * 1024;
    public const long MaxCoverBytes = 5 * 1024 * 1024;
    public const int DefaultFocusY = 50;

    /// <summary>What the file picker offers; the content is checked again on the server.</summary>
    public const string Accept = "image/jpeg,image/png,image/webp";

    public static long MaxBytes(ProfileImageKind kind) => kind == ProfileImageKind.Avatar ? MaxAvatarBytes : MaxCoverBytes;

    /// <summary>The picture's type from its first bytes, never from its name or what the browser said:
    /// JPEG, PNG and WebP only (no SVG, which can carry script).</summary>
    public static string? SniffMime(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    public static ProfileImageOutcome? Reject(ProfileImageKind kind, byte[] data)
    {
        if (data.Length == 0) return ProfileImageOutcome.Empty;
        if (data.LongLength > MaxBytes(kind)) return ProfileImageOutcome.TooLarge;
        if (SniffMime(data) == null) return ProfileImageOutcome.NotAnImage;
        return null;
    }

    public static string Extension(string mime) => mime switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };

    /// <summary>A new path per upload, so a browser never shows a cached older picture, in one folder
    /// per person so their files can be found for deletion.</summary>
    public static string PathFor(string userId, ProfileImageKind kind, string mime, DateTime now) =>
        $"{FolderFor(userId)}{kind.ToString().ToLowerInvariant()}-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}.{Extension(mime)}";

    public static string FolderFor(string userId) => $"users/{Safe(userId)}/profile/";

    // A hash of the user id: one folder per person that no two ids can share, without the id in the path
    private static string Safe(string userId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)))[..24].ToLowerInvariant();

    public static int ClampFocus(int focusY) => Math.Clamp(focusY, 0, 100);
}

public sealed class ProfileImageService(ICompanionStore store, IProfileFiles files, Func<DateTime> clock, Action<string> log)
{
    public Task<IReadOnlyList<ProfileImage>> GetAsync(string userId, CancellationToken ct) => store.GetProfileImagesAsync(userId, ct);

    /// <summary>Checks a picture and writes it to the person's folder as a preview. Nothing the person
    /// already has changes until <see cref="CommitAsync"/>; <see cref="DiscardAsync"/> deletes the preview.</summary>
    public async Task<ProfileImageResult> StageAsync(string userId, ProfileImageKind kind, byte[] data, CancellationToken ct)
    {
        if (ProfileImages.Reject(kind, data) is { } rejected)
        {
            return new(rejected);
        }

        var mime = ProfileImages.SniffMime(data)!;
        var now = clock();
        var staged = new ProfileImage(userId, kind, ProfileImages.PathFor(userId, kind, mime, now), mime, data.LongLength, ProfileImages.DefaultFocusY, now);

        try
        {
            await files.WriteAsync(staged.Path, data, mime, ct);
            return new(ProfileImageOutcome.Saved, staged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"profile {kind} for user {userId}: upload failed with {ex.GetType().Name}");
            await TryDeleteFileAsync(staged.Path);
            return new(ProfileImageOutcome.Failed);
        }
    }

    /// <summary>Makes a previewed picture the person's own, replacing their previous one of that kind.
    /// The old file is deleted only after the new row is saved, so a failure keeps the old picture.
    /// A preview staged for someone else, or outside this person's folder, is refused.</summary>
    public async Task<ProfileImageResult> CommitAsync(string userId, ProfileImage staged, int focusY, CancellationToken ct)
    {
        if (staged.UserId != userId || !staged.Path.StartsWith(ProfileImages.FolderFor(userId), StringComparison.Ordinal))
        {
            log($"profile commit refused for user {userId}: not their preview");
            return new(ProfileImageOutcome.Failed);
        }

        var image = staged with { FocusY = staged.Kind == ProfileImageKind.Cover ? ProfileImages.ClampFocus(focusY) : ProfileImages.DefaultFocusY, UpdatedUtc = clock() };
        var old = await store.GetProfileImageAsync(userId, staged.Kind, ct);

        try
        {
            await store.SaveProfileImageAsync(image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"profile {staged.Kind} for user {userId}: save failed with {ex.GetType().Name}");
            return new(ProfileImageOutcome.Failed);
        }

        if (old != null && old.Path != image.Path)
        {
            await TryDeleteFileAsync(old.Path);
        }

        var name = staged.Kind.ToString().ToLowerInvariant();
        await store.AddAuditAsync(userId, old == null ? $"profile_{name}_added" : $"profile_{name}_replaced", null, ct);
        return new(ProfileImageOutcome.Saved, image);
    }

    /// <summary>Deletes a preview the person didn't keep. A saved picture is never deleted here.</summary>
    public async Task DiscardAsync(string userId, ProfileImage staged, CancellationToken ct)
    {
        if (staged.UserId != userId || !staged.Path.StartsWith(ProfileImages.FolderFor(userId), StringComparison.Ordinal))
        {
            return;
        }

        if ((await store.GetProfileImageAsync(userId, staged.Kind, ct))?.Path != staged.Path)
        {
            await TryDeleteFileAsync(staged.Path);
        }
    }

    /// <summary>Moves the cover's crop window. Null when the person has no cover of their own.</summary>
    public async Task<ProfileImage?> SetCoverFocusAsync(string userId, int focusY, CancellationToken ct)
    {
        if (await store.GetProfileImageAsync(userId, ProfileImageKind.Cover, ct) is not { } cover)
        {
            return null;
        }

        var moved = cover with { FocusY = ProfileImages.ClampFocus(focusY), UpdatedUtc = clock() };
        await store.SaveProfileImageAsync(moved, ct);
        return moved;
    }

    /// <summary>Removes the picture: the default avatar or the Nordic cover shows again.</summary>
    public async Task<bool> RemoveAsync(string userId, ProfileImageKind kind, CancellationToken ct)
    {
        if (await store.GetProfileImageAsync(userId, kind, ct) is not { } image)
        {
            return false;
        }

        await store.DeleteProfileImageAsync(userId, kind, ct);
        await TryDeleteFileAsync(image.Path);
        await store.AddAuditAsync(userId, $"profile_{kind.ToString().ToLowerInvariant()}_removed", null, ct);
        return true;
    }

    /// <summary>Deletes the person's picture files, before "delete all my data" removes the rows.</summary>
    public async Task DeleteFilesAsync(string userId, CancellationToken ct)
    {
        foreach (var image in await store.GetProfileImagesAsync(userId, ct))
        {
            await TryDeleteFileAsync(image.Path);
        }
    }

    private async Task TryDeleteFileAsync(string path)
    {
        try
        {
            await files.DeleteAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A leftover file has no link and is under the person's folder; the row is what shows it
            log($"profile file delete failed: {ex.GetType().Name}");
        }
    }
}
