// The person's profile picture and cover image: upload from the device, preview it (with the cover's
// crop position) before it replaces anything, save, replace, and remove to get the default back. The
// files and rows are handled by ProfileImageService (plain C#, tested); this file draws the card and
// passes the signed-in person's id from the server session, never anything the browser names.

public partial class OmabiloApp
{
    /// <summary>A picture uploaded but not saved yet: shown as a preview with Save and Cancel.</summary>
    private readonly ClientReactive<ProfileImage?> _pfPending = new((ProfileImage?)null);

    /// <summary>The cover's crop position on the preview or the saved cover, 0 (top) to 100 (bottom).</summary>
    private readonly ClientReactive<int?> _pfFocus = new((int?)null);

    /// <summary>Which picture is uploading or saving ("Avatar" or "Cover"), for the loading state.</summary>
    private readonly ClientReactive<string?> _pfBusy = new((string?)null);
    private readonly ClientReactive<string?> _pfError = new((string?)null);

    private const string DefaultCover = "/home-cover.svg";

    private AssetUri PictureUri(ProfileImage image) => app.Files.Data.GetAssetUri(image.Path);

    /// <summary>The cover crop as an object-position class: the picture keeps its width and slides up or down.</summary>
    private static string CoverPosition(int focusY) => $"object-[50%_{ProfileImages.ClampFocus(focusY)}%]";

    private void RenderProfileCard(UIView view, CompanionSnapshot snap)
    {
        var pending = _pfPending.Value;
        var avatar = pending is { Kind: ProfileImageKind.Avatar } ? pending : snap.Avatar;
        var cover = pending is { Kind: ProfileImageKind.Cover } ? pending : snap.Cover;
        var focus = _pfFocus.Value ?? cover?.FocusY ?? ProfileImages.DefaultFocusY;
        var busy = _pfBusy.Value != null;

        view.Box([Card.Default, "p-0 overflow-hidden flex flex-col"], key: "profile-card", content: card =>
        {
            // The cover as Home shows it, with the picture over its lower edge
            card.Box(["relative h-32 sm:h-44 bg-emerald-950"], content: top =>
            {
                if (cover != null)
                {
                    top.Image(["absolute inset-0 w-full h-full object-cover", CoverPosition(focus)], assetUri: PictureUri(cover), alt: "Your cover image", key: $"pf-cover-{cover.Path}");
                }
                else
                {
                    top.Image(["absolute inset-0 w-full h-full object-cover"], src: DefaultCover, alt: "The default Nordic cover", key: "pf-cover-default");
                }

                if (pending != null)
                {
                    top.Box(["absolute top-3 left-3 rounded-full bg-white/90 px-3 py-1 shadow-xs"], content: b =>
                        b.Text([Text.Label, "text-emerald-950"], pending.Kind == ProfileImageKind.Avatar ? "Preview: picture not saved yet" : "Preview: cover not saved yet"));
                }
            });

            card.Column(["gap-4 px-5 pb-5"], content: body =>
            {
                body.Row(["items-end gap-4 -mt-10 sm:-mt-12 min-w-0"], content: r =>
                {
                    r.Box(["w-20 h-20 sm:w-24 sm:h-24 rounded-full ring-4 ring-card bg-card shrink-0 overflow-hidden flex items-center justify-center"], content: ring =>
                    {
                        if (avatar != null)
                        {
                            ring.Image(["w-full h-full object-cover"], assetUri: PictureUri(avatar), alt: "Your profile picture", key: $"pf-avatar-{avatar.Path}");
                        }
                        else
                        {
                            ring.Box(["w-full h-full rounded-full bg-brand-solid text-primary-on-brand flex items-center justify-center text-2xl font-semibold"],
                                content: b => b.Text([], _me.Value.Initials));
                        }
                    });
                    r.Column(["gap-0 min-w-0 pb-1"], content: c =>
                    {
                        c.Text([Text.BodyStrong, "truncate text-foreground"], _me.Value.Name);
                        c.Text([Text.Caption, "text-muted-foreground"], "Your picture and cover show only to you, on Home and beside your name.");
                    });
                });

                if (pending != null)
                {
                    PendingControls(body, pending, focus, busy);
                }
                else
                {
                    PictureControls(body, snap, focus, busy);
                }

                if (_pfError.Value is { } error)
                {
                    body.Alert("Nothing was changed", tone: SemanticTone.Warning, description: error, key: "pf-error", onDismiss: async () => _pfError.Value = null);
                }

                body.Text([Text.Caption, "text-muted-foreground"],
                    $"JPG, PNG or WebP. Profile picture up to {ProfileImages.MaxAvatarBytes / (1024 * 1024)} MB, cover up to {ProfileImages.MaxCoverBytes / (1024 * 1024)} MB. " +
                    "Stored privately with your account; deleting your data deletes them.");
            });
        });
    }

    /// <summary>Save or Cancel for an uploaded preview, with the crop position for a cover.</summary>
    private void PendingControls(UIView view, ProfileImage pending, int focus, bool busy)
    {
        if (pending.Kind == ProfileImageKind.Cover)
        {
            CoverSlider(view, focus, busy);
        }

        view.Row(["gap-2 flex-wrap"], content: r =>
        {
            r.Button([Button.PrimaryMd], text: busy ? "Saving…" : pending.Kind == ProfileImageKind.Avatar ? "Save picture" : "Save cover", icon: "check",
                disabled: busy, onClick: async () => await CommitPictureAsync(pending, focus));
            r.Button([Button.GhostMd], text: "Cancel", disabled: busy, onClick: async () => await DiscardPictureAsync(pending));
        });
    }

    private void PictureControls(UIView view, CompanionSnapshot snap, int focus, bool busy)
    {
        view.Column(["gap-3 sm:flex-row sm:flex-wrap"], content: rows =>
        {
            rows.Row(["gap-2 flex-wrap items-center"], content: r =>
            {
                PictureUpload(r, ProfileImageKind.Avatar, snap.Avatar == null ? "Upload picture" : "Replace picture", "user-round", busy);

                if (snap.Avatar != null)
                {
                    r.Button([Button.GhostSm], text: "Remove picture", icon: "trash-2", disabled: busy,
                        onClick: async () => await RemovePictureAsync(ProfileImageKind.Avatar));
                }
            });

            rows.Row(["gap-2 flex-wrap items-center"], content: r =>
            {
                PictureUpload(r, ProfileImageKind.Cover, snap.Cover == null ? "Upload cover" : "Replace cover", "image", busy);

                if (snap.Cover != null)
                {
                    r.Button([Button.GhostSm], text: "Restore Nordic cover", icon: "rotate-ccw", disabled: busy,
                        onClick: async () => await RemovePictureAsync(ProfileImageKind.Cover));
                }
            });
        });

        // A saved cover can be moved later without uploading it again
        if (snap.Cover is { } cover)
        {
            CoverSlider(view, focus, busy);

            if (focus != cover.FocusY)
            {
                view.Row(["gap-2 flex-wrap"], content: r =>
                {
                    r.Button([Button.PrimarySm], text: busy ? "Saving…" : "Save position", disabled: busy, onClick: async () => await SaveCoverFocusAsync(focus));
                    r.Button([Button.GhostSm], text: "Undo", disabled: busy, onClick: () => _pfFocus.Value = null);
                });
            }
        }
    }

    private void CoverSlider(UIView view, int focus, bool busy)
    {
        view.Column(["gap-1"], content: c =>
        {
            c.Text([Text.Label, "text-foreground"], "Cover position");
            c.Slider(value: (double)focus, style: ["w-full max-w-md"], min: 0, max: 100, step: 5, disabled: busy, ariaLabel: "Cover position, from top to bottom",
                onValueChange: async v => _pfFocus.Value = (int)Math.Round(v));
            c.Text([Text.Caption, "text-muted-foreground"], "Slide to choose which part of the picture shows.");
        });
    }

    /// <summary>The upload control. Home passes its own style for the camera buttons on the cover and picture.</summary>
    private void PictureUpload(UIView view, ProfileImageKind kind, string label, string icon, bool busy, string[]? style = null, string labelStyle = "", string keyPrefix = "pf")
    {
        var uploading = _pfBusy.Value == kind.ToString();

        view.FileUpload(style ?? ["inline-flex items-center gap-2 rounded-lg border border-secondary bg-background hover:border-brand px-3 h-10 cursor-pointer"],
            accept: ["image/jpeg", "image/png", "image/webp", ".jpg", ".jpeg", ".png", ".webp"], disabled: busy, key: $"{keyPrefix}-upload-{kind}",
            ariaLabel: label,
            // Checked on the server before any byte is sent; the content is checked again when it arrives
            onUploadPreStart: async args =>
            {
                _pfError.Value = null;

                if (args.Size is > 0 && args.Size <= ProfileImages.MaxBytes(kind))
                {
                    _pfBusy.Value = kind.ToString();
                    return true;
                }

                _pfError.Value = new ProfileImageResult(args.Size > ProfileImages.MaxBytes(kind) ? ProfileImageOutcome.TooLarge : ProfileImageOutcome.Empty).Message(kind);
                return false;
            },
            onUploadComplete: async args => await StagePictureAsync(kind, args),
            onUploadError: async _ =>
            {
                _pfBusy.Value = null;
                _pfError.Value = $"The picture didn't upload. Use a JPG, PNG or WebP under {ProfileImages.MaxBytes(kind) / (1024 * 1024)} MB and try again.";
            },
            content: zone =>
            {
                zone.Icon([Icon.Sm, style == null ? "text-brand-secondary" : "", uploading ? "animate-spin" : ""], name: uploading ? "loader-circle" : icon);

                if (labelStyle != "none")
                {
                    zone.Text([Text.Label, style == null ? "text-foreground" : "", labelStyle], uploading ? "Uploading…" : label);
                }
            });
    }

    private async Task StagePictureAsync(ProfileImageKind kind, FileUploadCompleteArgs args)
    {
        var path = args.LocalTempFilePath;

        try
        {
            if (path == null || MyUserId is not { } uid)
            {
                _pfError.Value = path == null ? "The picture didn't arrive. Try uploading it again." : "Sign in to save a picture to your account.";
                return;
            }

            // Checked before reading, so an oversized file never fills memory
            var data = new FileInfo(path).Length <= ProfileImages.MaxBytes(kind) ? await File.ReadAllBytesAsync(path, args.CancellationToken) : null;
            var result = data == null ? new ProfileImageResult(ProfileImageOutcome.TooLarge) : await ProfilePictures.StageAsync(uid, kind, data, CancellationToken.None);

            if (!result.Ok)
            {
                _pfError.Value = result.Message(kind);
                return;
            }

            // A second upload before saving replaces the first preview
            if (_pfPending.Value is { } earlier)
            {
                await ProfilePictures.DiscardAsync(uid, earlier, CancellationToken.None);
            }

            _pfPending.Value = result.Image;
            _pfFocus.Value = null;
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"profile picture upload failed: {ex.GetType().Name}");
            _pfError.Value = "Something went wrong with this picture. Please try again.";
        }
        finally
        {
            _pfBusy.Value = null;

            if (path != null)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private async Task CommitPictureAsync(ProfileImage pending, int focus)
    {
        if (MyUserId is not { } uid || _pfBusy.Value != null)
        {
            return;
        }

        _pfBusy.Value = pending.Kind.ToString();

        try
        {
            var result = await ProfilePictures.CommitAsync(uid, pending, focus, CancellationToken.None);

            if (!result.Ok)
            {
                _pfError.Value = result.Message(pending.Kind);
                return;
            }

            _pfPending.Value = null;
            _pfFocus.Value = null;
            await LoadMyAsync();
            _toasts.Success(result.Message(pending.Kind));
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"profile picture save failed: {ex.GetType().Name}");
            _pfError.Value = "The picture couldn't be saved. Please try again.";
        }
        finally
        {
            _pfBusy.Value = null;
        }
    }

    private async Task DiscardPictureAsync(ProfileImage pending)
    {
        _pfPending.Value = null;
        _pfFocus.Value = null;

        if (MyUserId is { } uid)
        {
            await ProfilePictures.DiscardAsync(uid, pending, CancellationToken.None);
        }
    }

    private async Task SaveCoverFocusAsync(int focus)
    {
        if (MyUserId is not { } uid || _pfBusy.Value != null)
        {
            return;
        }

        _pfBusy.Value = nameof(ProfileImageKind.Cover);

        try
        {
            if (await ProfilePictures.SetCoverFocusAsync(uid, focus, CancellationToken.None) == null)
            {
                _pfError.Value = "There's no cover of your own to move. Upload one first.";
                return;
            }

            _pfFocus.Value = null;
            await LoadMyAsync();
            _toasts.Success("Cover position saved.");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"cover position save failed: {ex.GetType().Name}");
            _pfError.Value = "The position couldn't be saved. Please try again.";
        }
        finally
        {
            _pfBusy.Value = null;
        }
    }

    private async Task RemovePictureAsync(ProfileImageKind kind)
    {
        if (MyUserId is not { } uid || _pfBusy.Value != null)
        {
            return;
        }

        _pfBusy.Value = kind.ToString();

        try
        {
            await ProfilePictures.RemoveAsync(uid, kind, CancellationToken.None);
            _pfFocus.Value = null;
            await LoadMyAsync();
            _toasts.Success(kind == ProfileImageKind.Avatar ? "Profile picture removed." : "The Nordic cover is back.");
        }
        catch (Exception ex)
        {
            Log.Instance.Warning($"profile picture remove failed: {ex.GetType().Name}");
            _pfError.Value = "That didn't work. Please try again.";
        }
        finally
        {
            _pfBusy.Value = null;
        }
    }
}
