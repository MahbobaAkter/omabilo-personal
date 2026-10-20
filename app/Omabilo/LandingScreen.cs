// The public landing page, shown before sign-in: a deep pine page with its own menu (Home, How it
// works, Plans & Pricing, For Business, Help & Support), the hero with an example dashboard, what
// Omabilo does and the two plans. It holds nothing personal; every start button opens sign-in.
// The palette is fixed (it does not follow light/dark) so the page always matches the brand artwork.

public partial class OmabiloApp
{
    private readonly ClientReactive<string> _landingTab = new("home");
    private readonly ClientReactive<bool> _landingMenuOpen = new(false);

    private static readonly (string Key, string Label, string Icon)[] LandingNav =
    [
        ("home", "Home", "house"),
        ("how", "How it works", "circle-play"),
        ("pricing", "Plans & Pricing", "tag"),
        ("business", "For Business", "briefcase-business"),
    ];

    private const string MintButton = "inline-flex flex-row items-center justify-center gap-2 h-12 px-6 rounded-full bg-emerald-300 text-emerald-950 font-semibold hover:bg-emerald-200 transition-colors cursor-pointer disabled:opacity-60 disabled:cursor-not-allowed focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-emerald-200";
    private const string GhostPillButton = "inline-flex flex-row items-center justify-center gap-2 h-12 px-6 rounded-full border border-white/70 text-white font-semibold hover:bg-white/10 transition-colors cursor-pointer focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-emerald-200";

    private void LandingGo(string tab)
    {
        _landingTab.Value = tab;
        _landingMenuOpen.Value = false;
    }

    private Task StartFromLandingAsync(string reason) => SignInAsync(reason);

    private void RenderLanding(UIView view)
    {
        view.Row(["h-full w-full min-h-0 bg-emerald-950 text-white"], content: view =>
        {
            RenderLandingSidebar(view);

            view.Column(["flex-1 min-w-0 min-h-0 h-full bg-gradient-to-br from-emerald-950 via-emerald-950 to-teal-950"], content: view =>
            {
                RenderLandingTopBar(view);

                view.ScrollArea(rootStyle: ["flex-1 min-h-0"], key: $"landing-{_landingTab.Value}", content: view =>
                {
                    view.Column(["mx-auto w-full max-w-7xl px-4 sm:px-6 xl:px-8 pt-1 pb-12 gap-8",
                        "motion-[0:opacity-0_translate-y-[6px],100:opacity-100_translate-y-0] motion-duration-300ms"],
                        props: new Dictionary<string, object> { ["role"] = "main" }, content: view =>
                    {
                        RenderRemovalBanner(view);

                        switch (_landingTab.Value)
                        {
                            case "how": RenderLandingHow(view); break;
                            case "pricing": RenderLandingPricing(view, standalone: true); break;
                            case "business": RenderLandingBusiness(view); break;
                            case "help": RenderLandingHelp(view); break;
                            default:
                                RenderLandingHero(view);
                                RenderLandingFeatures(view);
                                RenderLandingPricing(view, standalone: false);
                                break;
                        }
                    });
                });
            });
        });

        view.Sheet(open: _landingMenuOpen.Value, onOpenChange: async open => _landingMenuOpen.Value = open, side: Side.Left, title: "Menu",
            content: sheet => sheet.Column(["gap-1 pb-4"], content: list =>
            {
                foreach (var (key, label, icon) in LandingNav.Append(("help", "Help & Support", "circle-help")))
                {
                    list.Button([NavItem.Md, _landingTab.Value == key ? NavItem.ActiveBrand : NavItem.Default, "h-12 rounded-xl"], key: $"lm-{key}",
                        onClick: () => LandingGo(key), content: v =>
                        {
                            v.Icon([NavItem.Icon], name: icon);
                            v.Text([NavItem.Label, "flex-1 text-left"], text: label);
                        });
                }
            }));
    }

    private void LandingNavButton(UIView view, string key, string label, string icon)
    {
        var active = _landingTab.Value == key;
        view.Button([$"w-full h-12 px-4 rounded-xl flex flex-row items-center gap-3 text-left transition-colors cursor-pointer focus-visible:outline-2 focus-visible:outline-emerald-200 {(active ? "bg-white/10 text-white font-semibold" : "text-white/80 hover:bg-white/5 hover:text-white")}"],
            key: $"ln-{key}", onClick: () => LandingGo(key), content: v =>
            {
                v.Icon([Icon.Md], name: icon);
                v.Text(["text-base"], label);
            });
    }

    private void RenderLandingSidebar(UIView view)
    {
        view.Column(["hidden lg:flex w-72 shrink-0 h-full flex-col border-r border-white/10 bg-emerald-950 px-5 py-7 gap-8"],
            props: new Dictionary<string, object> { ["role"] = "navigation" }, ariaLabel: "Main", content: view =>
        {
            view.Row(["items-center gap-3 px-2"], content: r =>
            {
                r.Box(["w-11 h-11 rounded-xl bg-emerald-300 flex items-center justify-center"], content: b =>
                    b.Icon([Icon.Md, "text-emerald-950"], name: "sprout"));
                r.Text(["text-2xl font-bold tracking-tight text-white"], "Omabilo");
            });

            view.Column(["gap-1"], content: nav =>
            {
                foreach (var (key, label, icon) in LandingNav)
                {
                    LandingNavButton(nav, key, label, icon);
                }

                nav.Box(["my-3 h-px bg-white/10"]);
                LandingNavButton(nav, "help", "Help & Support", "circle-help");
            });

            view.Box(["flex-1"]);

            view.Column(["relative gap-3 rounded-2xl border border-white/10 bg-white/5 p-4 pt-10"], content: card =>
            {
                card.Box(["absolute -top-6 left-1/2 -translate-x-1/2 w-12 h-12 rounded-full bg-emerald-800 flex items-center justify-center shadow-lg"], content: b =>
                    b.Icon([Icon.Md, "text-emerald-300"], name: "sprout"));
                card.Text(["text-lg font-semibold text-white"], "New to Omabilo?");
                card.Text([Text.BodySm, "text-white/80"], "Create your free account and see how easy it is to manage your money.");
                card.Button([MintButton, "h-11 px-3 gap-1.5 text-sm whitespace-nowrap"], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in or create your free Omabilo account"), content: b =>
                {
                    b.Icon([Icon.Sm], name: "arrow-right");
                    b.Text([], _signingIn.Value ? "Opening…" : "Sign in / Create account");
                });
            });
        });
    }

    private void RenderLandingTopBar(UIView view)
    {
        view.Row(["shrink-0 items-center justify-between gap-3 px-4 sm:px-6 xl:px-8 py-3 mx-auto w-full max-w-7xl"], content: view =>
        {
            view.Row(["items-center gap-2 min-w-0"], content: r =>
            {
                r.Button(["lg:hidden w-10 h-10 rounded-xl flex items-center justify-center text-white hover:bg-white/10 cursor-pointer"], ariaLabel: "Open menu",
                    onClick: () => _landingMenuOpen.Value = true, content: b => b.Icon([Icon.Md], name: "menu"));
                r.Box(["lg:hidden w-9 h-9 rounded-xl bg-emerald-300 flex items-center justify-center shrink-0"], content: b =>
                    b.Icon([Icon.Sm, "text-emerald-950"], name: "sprout"));
                r.Text(["lg:hidden text-xl font-bold tracking-tight text-white"], "Omabilo");
            });

            view.Row(["items-center gap-1 sm:gap-3 shrink-0"], content: r =>
            {
                r.Box(["hidden sm:flex flex-row items-center gap-1.5 px-2 text-white/90"], ariaLabel: "Language: English", content: b =>
                {
                    b.Icon([Icon.Sm], name: "globe");
                    b.Text(["text-sm font-semibold"], "EN");
                });
                r.Button(["hidden sm:inline-flex flex-row items-center gap-2 h-10 px-3 rounded-full text-white/90 hover:bg-white/10 cursor-pointer"], onClick: () => LandingGo("help"), content: b =>
                {
                    b.Icon([Icon.Sm], name: "circle-help");
                    b.Text(["text-sm font-medium"], "Help");
                });
                r.Button([MintButton, "h-10 px-4 text-sm"], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in to Omabilo"), content: b =>
                {
                    b.Icon([Icon.Sm], name: "user-round");
                    b.Text([], _signingIn.Value ? "Opening…" : "Sign in");
                });
            });
        });
    }

    private void RenderLandingHero(UIView view)
    {
        view.Grid(["grid grid-cols-1 lg:grid-cols-[1.1fr_1fr] gap-8 items-center"], content: g =>
        {
            g.Column(["gap-5 min-w-0"], content: c =>
            {
                c.Row(["self-start items-center gap-2 rounded-full border border-white/15 bg-white/5 px-4 py-2"], content: r =>
                {
                    r.Icon([Icon.Sm, "text-emerald-300"], name: "leaf");
                    r.Text(["text-sm font-medium text-white"], "Your Financial Companion in Finland");
                });

                c.Heading(style: ["m-0 text-4xl sm:text-5xl 2xl:text-6xl font-bold tracking-tight leading-[1.05] text-white"], level: 1, content: h =>
                {
                    h.Text(["block sm:whitespace-nowrap"], "Make money feel");
                    h.Text(["block text-emerald-300"], "manageable.");
                });

                c.Text(["text-base sm:text-lg leading-relaxed text-white/85 max-w-xl"],
                    "Bring bills, income and plans into one clearer financial picture. Understand what you owe, plan around payday and move towards your goals — all in one place.");

                c.Row(["gap-3 flex-wrap"], content: r =>
                {
                    r.Button([MintButton], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in or create your free Omabilo account"), content: b =>
                    {
                        b.Text([], _signingIn.Value ? "Opening…" : "Get started for free");
                        b.Icon([Icon.Sm], name: "arrow-right");
                    });
                    r.Button([GhostPillButton], onClick: () => LandingGo("how"), content: b =>
                    {
                        b.Icon([Icon.Sm], name: "play");
                        b.Text([], "See how it works");
                    });
                });
            });

            g.Column(["min-w-0 gap-2"], content: RenderLandingLaptop);
        });
    }

    /// <summary>The example dashboard in the hero, drawn rather than photographed. Its numbers are an example and labelled so.</summary>
    private static void RenderLandingLaptop(UIView view)
    {
        view.Box(["relative w-full max-w-lg mx-auto lg:mr-0 pt-4"], ariaLabel: "Example of the Omabilo home screen", content: wrap =>
        {
            wrap.Box(["absolute -inset-6 rounded-full bg-emerald-400/15 blur-3xl"]);
            wrap.Box(["absolute -top-2 -left-4 sm:-left-10 hidden sm:flex text-emerald-400/70 -rotate-12"], content: b => b.Icon(["w-20 h-20"], name: "leaf"));

            wrap.Column(["relative rounded-t-2xl border-[10px] border-b-[14px] border-zinc-900 bg-emerald-950 p-4 gap-3 shadow-2xl",
                "motion-[0:opacity-0_translate-y-[12px],100:opacity-100_translate-y-0] motion-duration-500ms"], content: s =>
            {
                s.Row(["items-center gap-2"], content: r =>
                {
                    r.Box(["w-5 h-5 rounded-md bg-emerald-300 flex items-center justify-center"], content: b => b.Icon(["w-3 h-3 text-emerald-950"], name: "sprout"));
                    r.Text(["text-xs font-semibold text-white/90"], "Omabilo");
                });
                s.Row(["items-center gap-1.5"], content: r =>
                {
                    r.Text(["text-sm sm:text-base font-semibold text-white"], "Good morning!");
                    r.Icon([Icon.Xs, "text-amber-300"], name: "sun");
                });
                s.Grid(["grid grid-cols-3 gap-2"], content: tiles =>
                {
                    foreach (var (label, value) in new[] { ("Next bill", "€62.40"), ("Until payday", "12 days"), ("Available", "€428.60") })
                    {
                        tiles.Column(["rounded-lg bg-white/10 px-2.5 py-2 gap-0.5 min-w-0"], key: $"lt-{label}", content: t =>
                        {
                            t.Text(["text-[10px] sm:text-xs text-white/75 truncate"], label);
                            t.Text(["text-sm sm:text-lg font-bold tabular-nums text-white truncate"], value);
                        });
                    }
                });
                s.Column(["rounded-lg bg-white/5 px-3 py-2.5 gap-2"], content: f =>
                {
                    f.Text(["text-xs font-semibold text-white/85"], "Money flow");
                    f.Box(["h-2.5 w-full rounded-full bg-white/10 overflow-hidden flex flex-row"], content: bar =>
                    {
                        bar.Box(["h-full w-[45%] bg-emerald-400 origin-left motion-[0:scale-x-0,100:scale-x-100] motion-duration-700ms"]);
                        bar.Box(["h-full w-[30%] bg-amber-300 origin-left motion-[0:scale-x-0,100:scale-x-100] motion-duration-700ms"]);
                    });
                });
            });
            wrap.Box(["relative h-3 -mx-[6%] rounded-b-2xl bg-gradient-to-b from-zinc-300 to-zinc-500"]);
        });
        view.Text(["text-xs text-white/55 text-center lg:text-right"], "Example view with sample numbers");
    }

    private static void RenderLandingFeatures(UIView view)
    {
        var features = new[]
        {
            ("file-text", "Bills made clearer", "Understand what you owe and when it's due."),
            ("calendar-days", "Plan around payday", "See upcoming costs and plan what comes next."),
            ("wallet", "Know where you stand", "Keep track of income and spending."),
            ("target", "Make room for goals", "Work towards savings and your next steps."),
        };

        view.Grid(["grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6 lg:gap-0"], content: g =>
        {
            for (var i = 0; i < features.Length; i++)
            {
                var (icon, title, text) = features[i];
                g.Column([$"gap-2 lg:px-6 {(i == 0 ? "lg:pl-0" : "lg:border-l lg:border-white/10")}"], key: $"lf-{icon}", content: c =>
                {
                    c.Icon(["w-8 h-8 text-emerald-300"], name: icon);
                    c.Heading(title, style: ["m-0 text-lg font-semibold text-white"], level: 2);
                    c.Text([Text.BodySm, "text-white/80"], text);
                });
            }
        });
    }

    private void LandingChecks(UIView view, IEnumerable<string> lines, string tick)
    {
        view.Column(["gap-2"], content: list =>
        {
            foreach (var line in lines)
            {
                list.Row(["items-start gap-3"], content: row =>
                {
                    row.Icon([Icon.Sm, tick, "mt-0.5 shrink-0"], name: "check");
                    row.Text([Text.BodySm, "text-white/90"], line);
                });
            }
        });
    }

    private void RenderLandingPricing(UIView view, bool standalone)
    {
        if (standalone)
        {
            view.Column(["gap-2 pt-4"], content: c =>
            {
                c.Heading("Plans & Pricing", style: ["m-0 text-3xl sm:text-4xl font-bold tracking-tight text-white"], level: 1);
                c.Text(["text-base text-white/80 max-w-2xl"], "Start free and stay free as long as you like. Upgrade when you want Omabilo to find your bills for you.");
            });
        }

        // The whole Free and Premium cards are one button each (a clickable Box is a tab stop; Enter and Space press it).
        // The pill inside only looks like a button, so a click never fires twice; SignInAsync also ignores a second press.
        const string PlanCard = "relative overflow-hidden rounded-3xl p-5 sm:p-6 xl:p-5 2xl:p-6 flex flex-col gap-3 cursor-pointer select-none transition duration-150 ease-out " +
            "hover:-translate-y-0.5 hover:shadow-lg active:translate-y-0 active:scale-[0.99] motion-reduce:transition-none motion-reduce:hover:translate-y-0 motion-reduce:active:scale-100 " +
            "focus-visible:outline-2 focus-visible:outline-offset-4";
        var busy = _signingIn.Value;

        // Each card is as tall as its own content (items-start: no stretching to the tallest one). Premium has the
        // longest list, so it gets the most width and wraps less; Custom is the narrow, secondary column
        view.Grid(["grid grid-cols-1 md:grid-cols-2 xl:grid-cols-[0.95fr_1.35fr_0.8fr] items-start gap-4"], content: g =>
        {
            g.Box([PlanCard, "border border-emerald-300/50 bg-emerald-900/40 hover:border-emerald-300 hover:bg-emerald-900/60 focus-visible:outline-emerald-200", busy ? "opacity-80" : ""],
                key: "plan-free", ariaLabel: busy ? "Free plan, €0: opening sign-in…" : "Free plan, €0: get started with Free",
                onClick: () => StartFromLandingAsync("Sign in to start with Omabilo Free"), content: card =>
            {
                card.Row(["items-baseline justify-between gap-3"], content: r =>
                {
                    r.Heading("Free", style: ["m-0 text-2xl font-bold text-white"], level: 2);
                    r.Text(["text-2xl font-bold text-white"], "€0");
                });
                LandingChecks(card, Membership.FreeIncludes, "text-emerald-300");
                card.Row([MintButton, "w-full", busy ? "opacity-60" : ""], content: b =>
                {
                    b.Text([], busy ? "Opening…" : "Get started with Free");
                    b.Icon([Icon.Sm], name: "arrow-right");
                });
            });

            g.Box([PlanCard, "border border-amber-300/60 bg-amber-950/30 hover:border-amber-300 hover:bg-amber-950/50 focus-visible:outline-amber-200", busy ? "opacity-80" : ""],
                key: "plan-premium", ariaLabel: busy ? $"Premium plan, {Membership.PremiumPrice}: opening sign-in…" : $"Premium plan, {Membership.PremiumPrice}: upgrade to Premium",
                onClick: () => StartFromLandingAsync("Sign in, then upgrade to Premium from Settings"), content: card =>
            {
                card.Row(["items-baseline justify-between gap-x-3 gap-y-1 flex-wrap"], content: r =>
                {
                    r.Row(["items-center gap-2"], content: t =>
                    {
                        t.Icon([Icon.Md, "text-amber-300"], name: "crown");
                        t.Heading("Premium", style: ["m-0 text-2xl font-bold text-amber-300"], level: 2);
                    });
                    r.Text(["text-xl font-bold text-white whitespace-nowrap"], Membership.PremiumPrice);
                });
                card.Text(["text-sm font-semibold text-white"], "Everything in Free, plus:");
                LandingChecks(card, Membership.PremiumAdds.Select(Membership.Describe), "text-amber-300");
                card.Row(["inline-flex flex-row items-center justify-center gap-2 h-12 px-6 w-full rounded-full bg-amber-300 text-amber-950 font-semibold transition-colors", busy ? "opacity-60" : ""], content: b =>
                {
                    b.Text([], busy ? "Opening…" : "Upgrade to Premium");
                    b.Icon([Icon.Sm], name: "arrow-right");
                });
                card.Text(["text-xs text-white/65 text-center"], "Sign in first; you choose Premium inside the app and can cancel any time.");
            });

            // No price, features or inquiry channel exist for tailored plans yet, so this card says so and does nothing
            g.Column(["relative overflow-hidden rounded-3xl border border-dashed border-white/30 bg-emerald-950/60 p-5 sm:p-6 xl:p-5 2xl:p-6 gap-3 md:col-span-2 xl:col-span-1"], key: "plan-custom", content: card =>
            {
                card.Row(["items-center justify-between gap-x-3 gap-y-1 flex-wrap"], content: r =>
                {
                    r.Row(["items-center gap-2"], content: t =>
                    {
                        t.Icon([Icon.Md, "text-emerald-200"], name: "building-2");
                        t.Heading("Custom", style: ["m-0 text-2xl font-bold text-white"], level: 2);
                    });
                    r.Text(["rounded-full bg-white/10 px-3 py-1 text-xs font-semibold text-emerald-100"], "Coming soon");
                });
                card.Text(["text-sm text-white/80"], "For businesses, teams or other tailored needs.");
                card.Text(["text-sm text-white/65"], "Omabilo is for personal use today. A custom plan has no price, features or contact form yet, so there is nothing to sign up for here.");
                card.Button(["inline-flex flex-row items-center justify-center gap-2 h-12 px-4 w-full rounded-full border border-white/25 text-sm text-white/60 font-semibold whitespace-nowrap cursor-not-allowed disabled:pointer-events-none"],
                    disabled: true, onClick: () => { }, content: b => b.Text([], "Contact form coming soon"));
                card.Text(["text-xs text-white/65 text-center"], "Free and Premium work for one person's own money right now.");
            });
        });
    }

    private void RenderLandingHow(UIView view)
    {
        view.Column(["gap-2 pt-4"], content: c =>
        {
            c.Heading("How it works", style: ["m-0 text-3xl sm:text-4xl font-bold tracking-tight text-white"], level: 1);
            c.Text(["text-base text-white/80 max-w-2xl"], "Four steps from a pile of bills to a calmer month.");
        });

        var steps = new[]
        {
            ("log-in", "Sign in", "Google, Apple, Microsoft, Finnish bank ID (Signicat), an email code or a passkey. Signing in never opens your email."),
            ("file-up", "Add your bills", "Upload a PDF or paste the text. Connecting Gmail is a separate choice, free for your first 30 days."),
            ("sparkles", "See each bill explained", "Omabilo reads the amount, due date and reference, explains it in plain English, and you can correct anything."),
            ("calendar-days", "Plan around payday", "Reminders on Home before bills are due, and with Premium a plan for what's safe to spend until payday."),
        };

        view.Grid(["grid grid-cols-1 md:grid-cols-2 gap-5"], content: g =>
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var (icon, title, text) = steps[i];
                g.Row(["items-start gap-4 rounded-2xl border border-white/10 bg-white/5 p-5 sm:p-6"], key: $"step-{i}", content: r =>
                {
                    r.Box(["w-11 h-11 shrink-0 rounded-xl bg-emerald-300/15 flex items-center justify-center"], content: b => b.Icon([Icon.Md, "text-emerald-300"], name: icon));
                    r.Column(["gap-1 min-w-0"], content: c =>
                    {
                        c.Text(["text-xs font-semibold uppercase tracking-wider text-emerald-300"], $"Step {i + 1}");
                        c.Heading(title, style: ["m-0 text-lg font-semibold text-white"], level: 2);
                        c.Text([Text.BodySm, "text-white/80"], text);
                    });
                });
            }
        });

        view.Row(["gap-3 flex-wrap"], content: r =>
        {
            r.Button([MintButton], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in or create your free Omabilo account"), content: b =>
            {
                b.Text([], _signingIn.Value ? "Opening…" : "Get started for free");
                b.Icon([Icon.Sm], name: "arrow-right");
            });
            r.Button([GhostPillButton], onClick: () => LandingGo("pricing"), content: b => b.Text([], "Compare plans"));
        });
    }

    private void RenderLandingBusiness(UIView view)
    {
        view.Column(["gap-2 pt-4"], content: c =>
        {
            c.Heading("For Business", style: ["m-0 text-3xl sm:text-4xl font-bold tracking-tight text-white"], level: 1);
            c.Text(["text-base text-white/80 max-w-2xl"], "Omabilo is built for personal money today. Business accounts are not available yet.");
        });

        view.Column(["gap-4 rounded-3xl border border-white/10 bg-white/5 p-6 sm:p-8 max-w-3xl"], content: card =>
        {
            card.Row(["items-center gap-3"], content: r =>
            {
                r.Box(["w-11 h-11 shrink-0 rounded-xl bg-emerald-300/15 flex items-center justify-center"], content: b => b.Icon([Icon.Md, "text-emerald-300"], name: "briefcase-business"));
                r.Heading("Self-employed or a small team?", style: ["m-0 text-xl font-semibold text-white"], level: 2);
            });
            card.Text([Text.Body, "text-white/85"],
                "You can already use a personal account for the bills and income you handle yourself, including invoices you receive. Team access and company bookkeeping are not part of Omabilo yet.");
            card.Button([MintButton, "self-start"], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in or create your free Omabilo account"), content: b =>
            {
                b.Text([], _signingIn.Value ? "Opening…" : "Start with a personal account");
                b.Icon([Icon.Sm], name: "arrow-right");
            });
        });
    }

    private void RenderLandingHelp(UIView view)
    {
        view.Column(["gap-2 pt-4"], content: c =>
        {
            c.Heading("Help & Support", style: ["m-0 text-3xl sm:text-4xl font-bold tracking-tight text-white"], level: 1);
            c.Text(["text-base text-white/80 max-w-2xl"], "Answers to the questions people ask before they start.");
        });

        var faq = new[]
        {
            ("Is Omabilo free?", $"Yes. Free covers adding bills, plain-English explanations, corrections, reminders and Gmail for your first {Membership.GmailTrialDays} days. Premium is {Membership.PremiumPrice}."),
            ("Does signing in read my email?", "No. Signing in only creates your account. Connecting Gmail is a separate choice you make in Settings, it is read-only, and you can disconnect it any time."),
            ("Which accounts can I sign in with?", "Google, Apple, Microsoft, Finnish bank ID (Signicat), an email code or a passkey."),
            ("Where is my data kept?", "Your records are stored in the EU. AI explanations are opt-in and processed in the EU. You can delete all your data from Settings."),
        };

        view.Column(["gap-3 max-w-3xl"], content: list =>
        {
            foreach (var (q, a) in faq)
            {
                list.Column(["gap-1.5 rounded-2xl border border-white/10 bg-white/5 p-5"], key: $"faq-{q}", content: c =>
                {
                    c.Heading(q, style: ["m-0 text-base font-semibold text-white"], level: 2);
                    c.Text([Text.BodySm, "text-white/80"], a);
                });
            }
        });

        view.Button([MintButton, "self-start"], disabled: _signingIn.Value, onClick: () => StartFromLandingAsync("Sign in or create your free Omabilo account"), content: b =>
        {
            b.Text([], _signingIn.Value ? "Opening…" : "Get started for free");
            b.Icon([Icon.Sm], name: "arrow-right");
        });
    }
}
