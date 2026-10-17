return await App.Run(args);

public record SessionIdentity(string? UserId);
public record ClientParameters(string Name = "Ikon");

[App]
public partial class OmabiloApp(IApp<SessionIdentity, ClientParameters> app)
{
    private UI UI { get; } = new(app, new IkonTheme
    {
        ["background"] = "stone-50",
        ["foreground"] = "zinc-900",
        ["card"] = "white",
        ["muted-foreground"] = "zinc-500",
        ["border"] = "zinc-200",
        ["primary"] = "blue-600",
        ["radius"] = "rounded-xl",

        DarkMode = new IkonTheme
        {
            ["background"] = "zinc-900",
            ["foreground"] = "zinc-100",
            ["card"] = "zinc-800",
            ["muted-foreground"] = "zinc-400",
            ["border"] = "zinc-700",
            ["primary"] = "blue-400",
        },
    });

    public async Task Main()
    {
        UI.Root([Page.Default], content: view =>
        {
            view.Column([Layout.Page], content: view =>
            {
                view.Text([Text.H2], nameof(OmabiloApp));
            });
        });
    }
}