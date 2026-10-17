<!-- This file is automatically updated by ikon tool commands. User edits are preserved only below the ikon-user-content-below marker. -->

# IKON App Development Guidelines

## Quick start

1. Converting an existing app? Its screens become C#: read the `convert` playbook first. Else read `## By kind of app` in `docs/Ikon.Agent.Docs/patterns/_index.md` now and the patterns your row names; `ikon docs --search <feature>` finds the doc for any other feature.
2. Pick a visual direction (below) and who shares an instance (`app-structure` guide): the scaffold's `SessionIdentity(string? UserId)` gives each user their own, `SessionIdentity()` one for all, `SessionIdentity(string? Room)` one per `?room=`; a local run hosts one (`?room=` and `--param` do not split it). Private app: `RequireSignIn = true`, no `guest`; any account signs in, so staff-only screens check a role. An instance holds 200 clients; more need `[Sharded]` (`scaling` guide).
3. The scaffold runs as is: `ikon build`, `ikon run --detach`, then `ikon browse --steps "observe"` ("Running and checking the app").

## Architecture

An IKON app is a C# app that runs in the cloud and streams reactive UI to browsers; an instance keeps running when every client disconnects. Nearly everything is C#; touch `frontend-node` only for custom React components (`frontend-fundamentals` guide). Packages: `dotnet add app/<Project>/<Project>.csproj package <Id>`, `npm install <name>` in `frontend-node`.

`ikon new` creates a cloud app that a local run uses too: AI calls are billed to its space, and a local run reads and writes the deployed app's databases, files and persistent state. A deployed instance has 1 CPU and 512 MB (the app uses about 300 MB). Its image has .NET and `ffmpeg`, no Python and no extra fonts (`app-container` guide). Its databases and files stay in the EU; the `region-pinned-ai` pattern covers where AI calls run.

**AI services** (platform-held keys, except Rodin's): `Emerge.Run<T>()` for LLM text and typed output, `Emerge.Refine` for a draft improved in rounds; `ImageGenerator`, `SpeechGenerator`, `SpeechRecognizer`, `VideoGenerator`, `MusicGenerator`, `EmbeddingGenerator`, `WebSearcher`, `Classifier` (moderation, free), `DepthEstimator`, `ImageSegmenter`, `VideoSegmenter`, `MeshGenerator`. Each has a static one-shot on a default model (`ImageGenerator.GenerateAsync(prompt)`, bytes via `GetDataAsync()`), and `new Xxx(model)` with its `XxxConfig` for options (the `ai-*` guides).

**UI** is declared in C# inside `UI.Root([Page.Default], content: view => { ... })`. Components are methods on `view` (`UIView`, aliased `IView`; there is no `ViewContext`, CS0246); nested content lambdas get their own `view`.

**Styling:** Crosswind, IKON's utility classes in C# string arrays: a superset of Tailwind plus semantic theme classes, keyframe motion and variant groups.
Use **semantic theme classes**, which follow light/dark and the `IkonTheme`, and the **`Ikon.Parallax.Theming` tokens** built from them (`Button.PrimaryMd`, `Text.H1`, `Layout.Page`; `styling-and-motion` lists them), in one array: `[Button.PrimaryMd, "mt-4"]`. Fixed palette classes (`bg-amber-400`) are for looks that ignore the theme; a colour the palette lacks goes in the theme as a role, not a hex class (IKON013).

Animations are keyframe steps, each `time:utilities` joined by `_`: `"motion-[0:opacity-0_translate-y-[8px],100:opacity-100_translate-y-0] motion-duration-300ms"`. There are no named animations and no `motion-preset-*` (IKON022).

**A `style:` array is exactly what renders.** With none a component gets its themed default; with one, only those classes (`view.Button(["px-6"], …)` is a bare button; a layout-only one is IKON025). `"default"` first keeps the themed default under yours: `view.Button(["default", "px-6"], …)`. Token composites (`Button.OutlineMd`) are complete: add classes after, never `"default"` before. `view.Card` pads `content:` (style padding doubles it, IKON026; `contentStyle: ["p-0"]` for edge to edge); a `Card.*` token on a Column adds none (IKON034).

**Theming:** `new IkonTheme { ["primary"] = "amber-400", ["background"] = "zinc-950", … }` at the `UI` initializer. The only named properties are `Mode`, `DarkMode` (or `Dark`) and `Light`. Status colours other than `["destructive"]` are not keys (`theming` guide).

## Running and checking the app

From the app root, `ikon run --detach` starts the app and returns once it is ready; a plain
`ikon run` blocks. `build/app/artifacts/bin/<App>/debug/ikon-server-info.json`
has the URLs. `ikon stop` saves persistent state and ends the run. A `.cs` edit under `app/<App>/` hot-reloads
the run (a compile error keeps the previous build, an `OnStarting` that throws leaves
none, and `browse` prints the error;
`ikon status --wait` returns once the reload has applied or failed); a `.csproj`
or `ikon-config.toml` change needs `ikon stop` and a new run. Check the app as text before reaching for a browser:

```bash
ikon browse --steps "observe"  # screen text + [id] action handles
ikon browse --steps "fill Message hello; tap Send; wait-absent 60s Thinking; wait 5s --new Sent"
ikon browse --steps 'tap Host; capture code /Room (\w+)/; as bob: fill Room code ${code}; tap Join'
```

`ikon browse --help` lists the steps and options. Acting steps print only what changed; `click`,
`eval` and `console` reach custom React components; `observe` lists a canvas or custom node as one line; its `content` prop adds a text summary. `look` (a screenshot) is for pixels only. A run starts at `/`: `--path /room/7` opens another page. `wait 15s Sent` returns once the text shows, or if it showed before
the last tap, once the tap's handlers return. `--new` waits for new text, `wait 15s` alone for those
handlers, and `sleep 15s` pauses. A bare number is milliseconds. Each run is a new
client (new ids, empty `ClientReactive` state) that ends with the run, and so does what the app keeps only while a client is connected (a room): a script names controls by label
and plays a whole scenario in one run. Where the shell strips quotes (PowerShell), use
`--steps-file scratch/steps.txt`. `hold`, `mic` and `--fake-media-file` (a .wav loops into
the microphone, or plays once with `--mic-once`; a .y4m is the camera) test a voice or camera app.
`look`, `upload`, `mic` and `audio` need the page, which `--text-only` and an `as` user lack
(`as bob: observe` reads bob).

A script worth keeping is a **check gate**, `.ikon/gates/<name>.toml`: `ikon test` runs it against
the local run or the deployed app, and
`ikon task set <task-id> --reproduction <name>` records it as a defect's reproduction.

`--app-id <space>` or `--target <name>` makes `browse` and `test` drive the **deployed** app;
without them they drive the local run, else the linked deployed app. The page signs in as you when
the app offers the method you used for `ikon login`; `--as-guest` opens it as a new guest, and on a
local run `--as-user <name>` acts as a named test user (`--role`), and `as <name>+<role>:` as another up to the
next `as` (`as me:` is the run's own). A deployed app takes no `as` users: run one
`ikon browse --text-only --guest-name <name>` per user in parallel, each ending in a `sleep`. When
`[Auth] Methods` lists "global", every guest is one shared user, which a deployed app's page cannot
join: use `--text-only` there, a local run for `look`, or list "guest". `click <name>` is a real mouse click.

`ikon link --target staging` makes a staging app; `--target staging` aims `deploy`, `browse`,
`test` and `logs` at it. `ikon deployment rollback` undoes a deploy.

## The ikon tool's commands

`ikon help --json` covers every command, how the app and environment are chosen, and
the shape of `--format json` output (`ikon help <command> --json`: one). An unknown command a doc names
was renamed; see the help. `ikon deploy` commits and pushes first (`--no-save`: neither). Settings are in `ikon-config.toml`,
each explained by the comment above it: `ikon-config.development.toml` for an app on the development
platform (`run` and `deploy` read that one file), `ikon-config.<name>.toml` for `--target <name>`.
On Windows, read and edit files with your file tools; PowerShell 5.1's `cat`/`Get-Content`/`Set-Content`
garble non-ASCII text.

Root folders (`app-structure`): `app/`, `frontend-node/`, `frontend-flutter/`, `data/`,
`public/`, `branding/` (replace the placeholder `logo.png`, then `ikon icon`), `emails/`, `legal/`, `router/`, `schema/`, `tools/<runtime>/` (programs the
app runs; source in `tools/source/`), `container/`, `devices/` (programs on other
machines), `scripts/` (scripts the app keeps), `scratch/` (git-ignored steps files and throwaway
scripts, not `/tmp`), `reference/`, `test/`, `release-notes/`, `.ikon/gates/`, generated
`build/` and `docs/`.

## What the app must contain

Before writing UI, name the KIND of product and build what its ordinary workflow needs from the first build, and nothing unrequested: the `product-design` guide lists it per kind (records get search, sort, filters and export; a board a non-drag move; a review queue Approve · Reject · Request changes).
Every visible control works or is disabled with a visible reason; changes, AI-made ones included, can be undone; a filter never loses records; and anything reaching outside the app (`app.Payments`, `app.Email`, `app.Telephony`, `app.Notifications`, a publish, a permanent delete) happens only when a person presses a control or turns on a setting naming it, never as a side effect of an AI turn.

## How the app must look

Every app ships polished on the first build, unasked. Before the first `view.*` call pick a visual direction in one sentence (feel, palette, type, radius, density) and set it with `new IkonTheme { … }`, `["primary"]` included (IKON015).
Then: one primary action per screen in the brand colour; a real type scale; `Card.Elevated` for what matters and one flat colour per card (IKON011); generous padding in a max-width `Layout.Page`; a tinted background; motion only where state changes; designed empty, loading and error states (`EmptyState.*`, `Skeleton.*`, `Alert.*`); every viewport: an `h-screen` root, a `ScrollArea` in a `flex-1 min-h-0` parent, touch targets ≥ 40px.

## Common Pitfalls

The two most common compile failures:
1. Collection state uses the reactive collections: `ReactiveList<T>`, `ReactiveDictionary<TKey,TValue>`, `ReactiveHashSet<T>` (+ `Client*`/`User*`/`Mount*`/`Persistent*`/`PersistentUser*` variants). A `Reactive<List<T>>` is IKON002.
2. There is NO `view.Keyed(...)` (CS1061). Render list items directly in the loop; `key:` is a named parameter, needed only where a stateful component must keep or drop its state.

### Reactive state

More in the `reactive-state` guide ("Rules that are easy to get wrong").

- **Re-render is implicit.** Reading `_x.Value` in a UI lambda registers the dependency, and only the diff is streamed. There is NO `view.Dynamic`/`Watch`/`Bind`/`Observe` (CS1061).
- **Reactives are `private readonly` fields constructed with the initial value**: `private readonly ClientReactive<int> _count = new(0);`, collections `= new();`. No `app` argument, no `app.ClientReactive(...)` (CS1503/CS7036). A `ClientReactive` of a mutable type needs `ClientReactive.Create(_ => new Settings())`.
- **Mutate collections on the reactive** (`_todos.Add(item)`, `.RemoveAll(match)`, `.ReplaceWhere(match, t => t with { … })`, `.ReplaceAll(items)`, `.Update(list => …)` that returns new items or edits the copy, `_map[key] = v`) and enumerate it directly. `.Value` is read-only (`.Value.Add` is CS1061).
- **Scope by lifetime**: `Reactive<T>` is shared by all clients; view state (the open screen or dialog, an in-flight flag) is `ClientReactive`, per browser tab even in a per-user instance; typed text, drafts and the open record `UserReactive` (a reload is a new client). `Persistent*` is ONE value for the app, `PersistentSession*` one per `SessionIdentity`, `PersistentUser*` one per user; they autosave in seconds (await `app.SavePersistentStateAsync()` before "Saved"); records that can't be lost go in a database; renamed fields: `reactive-state` guide.
- **`_x.Value` is the current client's** in a UI lambda or handler. Outside a scope (`Main`, a timer, an endpoint) name the target: `_x.SetFor(cid, value)` (list: `AddFor`, `ReplaceWhereFor`), `_x.ValueFor(cid)` or `ReactiveScope.Use(new ClientScope(cid))`; `cid` is `ReactiveScope.ClientId` captured in a scope, and a `User*` one takes the user id string.
- The user is `ReactiveScope.UserId` (`UserIdOrNull`), not `app.SessionIdentity.UserId` (yours in a local run); `app.CurrentClientContext` (nullable; no `Context.Current`) has `IsAnonymous` and `Timezone`, the email and roles are in `new ClientProfiles(app)`, a field (`profiles-and-roles` guide).
- **Render only reads.** A reactive written inside a render lambda, or by work it starts, is not propagated (IKON031): load a screen's data in the handler that opens it (`reactive-state` guide).

### AI services

- **`Emerge` is static, and `await Emerge.Run<T>(…)` returns a non-null `T`** (no `IEmerge`): `var result = await Emerge.Run<MyResult>(LLMModel.Claude55Sonnet, pass => { pass.Command = userPrompt; });`. The prompt goes in `pass.Command`, never positionally (CS1660). `.FinalAsync()` gives `(T? Result, KernelContext)`. Images, PDFs and prior turns go in a `KernelContext`, a struct.
- **`Emerge.AskAsync` is prompt-first**: `AskAsync(userPrompt)` or `AskAsync(userPrompt, LLMModel.Claude45Haiku)` (model-first is CS1503). It returns a `string`; `AskAsync<T>(prompt)` a `T` (a `public record`/`class`, never a `struct`).
- **Models.** `LLMModel.Default` is `Claude45Haiku`, cheap and fast, and what the one-shots run on. For reasoning and chat name `Claude55Sonnet`, for the hardest tasks `Claude55Opus` (`ai-models` guide).
- **AI calls never return null — catch instead of guarding.** The services throw `AIException` (`InsufficientCreditsException` when credits run out), Emerge any exception (`EmergenceStoppedException` for no result): `catch (AIException)` around the services, `catch (Exception)` around Emerge.
- **The catch shows a human sentence and a retry, never the exception** (not `ex.Message` or a model id), keeping the inputs so Retry is one press.
- **Disclose AI** with `view.AiDisclosure(AiDisclosureKind.Interaction)` where a conversation with an AI starts, and `kind: AiDisclosureKind.GeneratedContent` beside what a model made. Show model text with `view.Markdown(text)`.

### The view-call shape

More in the `ui-components` guide ("Call-shape details").

- **The leading `string[]` style array is positional; name everything else.** `view.Column(["flex gap-4"], content: v => …)`, `view.Button(["px-4"], text: "Save", onClick: async () => …)`. Never also write `style:` (CS1744), never a named arg before a positional (CS8323). Lambdas are always named (IKON003).
- **The display string is `text:`** on Text, Button, Link and ActionButton (not `value:`/`label:`).
- **Form controls take `bind:`** (TextField/TextArea/Select/RadioGroup a `Reactive<string>`, Checkbox/Switch `Reactive<bool>`, Slider `Reactive<double>`) or `value:` + `onValueChange:` (Slider's take a list); a `value:` alone is read-only (IKON027). `view.Select` takes `options:` of `new SelectOption(value, label)`; `view.RadioGroup` takes `content:` with a `RadioGroupItem(value:, label:)` per option.
- **Handlers.** `onSubmit:` is `Func<string, Task>`, always `async`; `onClick:` takes any parameterless lambda. Only `Box`, `Button`, `Card`, `Image`, `Link`, `TableRow` (in a `Table`) and `ToolbarButton` take `onClick:` (charts one with `ChartClickArgs`); a clickable list row is `view.Box(["flex flex-row …"], onClick: …)`. `onOpenChange` is `Func<bool, Task>`: `async open => …`.
- **`ariaLabel:`** (on controls, `Box` and `Icon`; not `Text` or `Badge`, CS1739) replaces the visible text for screen readers and `ikon browse`: set it only where there is none, or start it with that text (`$"Delete {item.Name}"` on a per-item button; IKON028). Image takes `alt:`, RadioGroup `label:`.
- **The style array is classes, not CSS**; there is no `inlineStyle:`. A runtime value is an arbitrary value: `$"w-[{pct:0.#}%]"`.
- **Never name an app type, method or property like a style class** (`Page`, `Text`, `Card`; IKON030): it hides the `Theming` class (CS0117/CS0119/CS1929).
- **Dialog, AlertDialog and Alert** take `title:`/`description:`/`content:`, not `text:`. A field's `label:` is a VISIBLE label above it (with none, give `ariaLabel:` or `placeholder:`; IKON033); on Checkbox/Switch/Toggle it is the clickable trailing text.
- **Buttons bake variant and size into one constant** (`Button.PrimaryMd`, `Button.ErrorMd`, `Button.OutlineErrorMd`); no `Button.Danger*`, no `variant:`/`size:`. Even one token goes in an array: `[Button.PrimaryMd]`.
- **There is no** `Text.Xs`, `Tabs.Default` (`Tabs.List`/`Trigger`/`Content`), `Destructive` or `AlertStatus` (`tone: SemanticTone.Error`/`Warning`/`Info`/`Success` on Alert and Badge), `text:` on `ToggleGroupItem` (a `Text` in `content:`) or `title:` on `StatCard` (`label:`).
- **Copy, share and download are `view.ActionButton`** with `options:` `new CopyToClipboardActionOptions { Text = … }`, `ShareActionOptions` or `DownloadFileActionOptions`. There is no `ClientFunctions.CopyToClipboardAsync`/`RunJavaScriptAsync`. The app's link (to share, or for `view.QR`) is `app.PublicUrl` (no `app.Origin`), with a query `app.JoinUrl(new { room })`.

### Custom frontend and real-time

- **Native `view.*` first** (`view.RichTextEditor`; `view.CodeEditor` has no highlighting). For a highlighting editor, a map, a 3D view, a chart library or a signature pad, never ship a worse approximation: follow the `custom-react-node-embed` pattern (`leaflet-map` for a geographic map) and write all four of its parts at once. A floor plan or big picture with markers is `view.PanZoom`.
- **Crosswind classes in a `.tsx` file do nothing**; they exist only in C# style arrays. Style a custom component's elements with inline `style={{ … }}`, an imported CSS file or `var(--ikon-…)`, and its root from C#.
- **Blank page after a clean build**: give the root `h-screen`, paint on `bg-background`/`bg-card` under a committed theme, and if a red "Unregistered node type" shows, register the custom module.
- **Real-time MULTI-USER surfaces** (shared whiteboard, multiplayer game) are a custom canvas plus a `schema/<Name>.tp` message: `view.*` has no client-to-client channel, and per-frame `Reactive` diffs flood the connection (`realtime-game-loop` pattern). Single-user drawing is `view.ImageEditorCanvas`.

### Audio, files, media

- **Audio is mixed SERVER-side** and played through one `private Audio Audio { get; } = new(app);` (`Play`, `Speak`, `PlayLive`, microphones), never browser Web Audio (a custom node may analyse its own `<video>`). **Video** (cameras, screens) is one `private Video Video { get; } = new(app);` playing on named surfaces: clients render `view.VideoSurface(surface: "stage")` (`audio-video` guide).
- **A `FileUpload` that keeps the file** puts it in asset storage: `onUploadStart: async args => new FileUploadResult { AssetUri = new AssetUri(AssetClass.CloudFile, path, app.GlobalState.SpaceId) }`, then `onUploadComplete` gets `args.AssetUri`; a small file read once needs no `onUploadStart` and is at `args.LocalTempFilePath` (`media-analysis` guide).
- **Never copy video or audio into the container** (disk and RAM are too small): `ffprobe`/`ffmpeg` read `(await Asset.Instance.GetMetadataAsync(uri)).Url` over HTTP.
- **App files: root `public/` (served by path, `src: "/hero.png"`) and `data/` (private, no URL), `app.Files.Public` and `app.Files.Data` at runtime**; a written file's `src` comes from `GetUrlAsync(path)`, never from its path. A private image: `private-generated-media` pattern. Never read through `AppContext.BaseDirectory` (`app-structure` guide).

### App structure and platform

- **Keep the scaffold's shape**: `[App] public partial class MyApp(IApp<SessionIdentity, ClientParameters> app)` and its two records.
- **`Main()` returns after `UI.Root(...)`**: a Main that never returns (`await Task.Delay(Timeout.Infinite)`) fails the boot.
- **Lifecycle**, wired in `Main()`: `app.OnStarting(async () => …)`, `app.OnStopping`, `app.OnClientJoined(async ctx => …)` (or `(ctx, parameters)`), `app.OnClientLeft`, `app.OnMessageReceived(async msg => …)`, `app.Navigation.PathChangedAsync += async e => …` (`e.Path`), not `(sender, args)` (CS1593).
- **A hot reload starts a new instance** and runs `Main`/`OnStarting` again: reactives are restored, plain fields (engines, caches, counters) are not. In `OnStarting` rebuild them and settle restored in-flight state. End loops on `app.StoppingToken` (`app-structure` guide).
- **Test data goes in `app.Seed`**, run only on a staging copy (`ikon link --target staging --new-app`); rows the live app needs, in `app.OnStarting` when the table is empty. `ikon bundle`/`deploy` run `Main`, `OnStarting` and a capture client's `OnClientJoined` against the real databases to capture a boot snapshot: guard side effects with `SnapshotCapture.IsCaptureProcess`, and the capture client's with `ctx.IsSnapshot`.
- **A value presented as unique is GENERATED** (room and invite codes, ids) from `Random.Shared` or `Guid`. Demo rows look real, not `Item 1`.
- **Cloud servers run in UTC**: store UTC and show times in the viewer's `ctx.Timezone` (a shared screen shows the place's zone; `app-structure` guide).
- **Log with `Log.Instance.Info/Warning/Error/Debug(…)`** (`logging` guide), not `app.Logger`.
- **A "Sign in" button drawn in C#** calls `ClientFunctions.LoginShowAsync(reason)`; `LoginAsync(provider)` is OAuth only.
- **Open a database connection PER operation**: `await using var conn = await app.DatabaseAsync("mydb"); await conn.OpenAsync();` in each method, never a field (`databases` guide). A database `"X"` needs `ikon db create --name X`; the built-in one needs nothing. `QueryAsync`/`ExecuteAsync` (CS1061) need package `Dapper` and `global using Dapper;` in `GlobalUsings.cs`.
- **Don't invent a `using Ikon.*;`** (CS0234), but a parent does not import its nested namespaces (CS0246). Outside `GlobalUsings.cs`: `Ikon.Common.Core.Protocol` (`TriggerEventType`, `VideoCodec`; `Action` clashes: CS0104), `Ikon.App.Cells`, `Ikon.App.Mcp`, `Ikon.App.Triggers`, `Ikon.AI.Retrieving`, `Ikon.AI.Reranking`, `Ikon.AI.Database`, `Ikon.AI.Storage`, `Ikon.AI.Emergence.Structured`, `Ikon.AI.Emergence.Tree`, `Ikon.Crosswind`, `System.Globalization`, `System.Net`, `System.Net.Http.Json`, `System.Diagnostics`, `System.Text.RegularExpressions`.
- **A real `Ikon.*` namespace failing with CS0234 needs its package**: line 2 of its `api/<namespace>.md` names it.
- **Keep the entry point and the global namespace.** Never delete the top-level `return await App.Run(args);` (CS5001) or move it to a `Program.cs`. Never add a `namespace` to app files: inside `namespace Ikon.App.<Anything>`, `App` is the `Ikon.App` namespace (CS0234/CS0305/CS8956).

### Color tokens and theming

- **`bg-primary` is NOT the brand fill** but the PAGE SURFACE (white in light, near-black in dark); the theme key `["primary"]` sets the brand colour that the `brand` classes use. Brand fill is `bg-brand-solid`, brand text `text-brand-secondary`, brand border `border-brand`; neutrals are `bg-background`/`bg-card`, `text-foreground`, `border-secondary`.
- **A hardcoded palette, or an always-dark app, needs `Mode = ThemeMode.Fixed`** (no `ThemeMode.Dark`), or dark-preference clients get dark-scheme text on your light surfaces; for both schemes set `DarkMode`.
- **Text colour goes on the Text**, after its token: `[Text.Body, "text-white"]`. Only the role tokens take a numeric shade (`brand-100`, `error-700`); `text-tertiary-600` is IKON006.
- **Tailwind class names are v4's**: `shadow-xs`/`shadow-sm`/`rounded-xs`/`rounded-sm` where v3 wrote `shadow-sm`/`shadow`/`rounded-sm`/`rounded`, `outline-hidden`, `ring-3`, `bg-black/50` for `bg-opacity-50` (`styling-and-motion` guide).

### C# syntax

More in the `csharp-primer` guide (CS8917, CS9176, raw strings, curly quotes, `using` locals).

- **Escape inner quotes in `$"…"`** (`$"Add card to \"{col}\""`): one unescaped `"` cascades into CS1002/CS1010/CS1026 across the file; fix that quote, not the lines the errors point at. Many quotes → `$"""…"""`.

## API Reference Guides

Each agent guide is `docs/Ikon.Agent.Docs/guides/<name>.md`, one subject per guide:

- **accessibility**: names, headings, landmarks and focus for screen readers
- **agent-stages-and-history**: stage machines, pass records, journals and checkpoints of agent threads
- **agent-threads**: long-running agent threads, sub-agents, artifacts, budgets, asking the user
- **ai-advanced**: text-to-SQL, AI database and storage
- **ai-image**: image generation, upscaling, depth maps, segmentation and 3D meshes
- **ai-models**: choosing an LLM model; KernelContext
- **ai-speech-and-audio**: text to speech, speech recognition, sound effects and music
- **ai-video**: video generation and enhancement; playing a video file
- **ai-web-and-data**: web search and scraping, classification, decisions, OCR, embeddings, retrieval
- **app-api-reference**: where the signatures of IApp and the host services are
- **app-container**: what the cloud server image has, and bringing other programs, fonts and packages
- **app-structure**: the app class, identity, lifecycle, files, legal pages, navigation, client functions, join-URL QR
- **asset-system**: asset storage: cloud files and JSON, metadata, expiry, concurrency
- **audio-video**: playing and capturing audio and video, effects, calls, broadcasts, synthesis
- **charts**: pie, bar and line charts
- **component-vocabulary**: the enums, records and event arguments components take
- **consent**: asking for and recording end-user consent
- **costs**: the app's AI costs in credits, per day and per usage event
- **crosswind-reference**: Crosswind styling recipes: layouts, gradients, overlays, effects
- **csharp-primer**: modern C# idioms and the syntax mistakes that fail a build
- **databases**: Postgres, SQL with Dapper, EF Core, pgvector, migrations and seed data
- **email**: sending and receiving email
- **emergence-strategies**: advanced Emerge patterns: map-reduce, tree search, critics, ensembles
- **emergence**: Emerge.Run<T>: structured output, tools, chat history, cancellation
- **endpoints-webhooks**: HTTP endpoints, public HTML pages, webhooks, MCP tools, raw sockets and cron methods
- **frontend-fundamentals**: the React frontend: custom components, sign-in, embedding, branding
- **function-registry**: functions the client and LLM tools can call
- **location**: one-off and continuous GPS location
- **logging**: logging from an app
- **media-analysis**: uploading large media and analysing it with ffmpeg
- **motion-reference**: the full motion and keyframe syntax
- **notifications**: push and in-app notifications, inbox, channels
- **parallax-overview**: the Parallax UI library as a whole, SEO and share cards
- **payments**: payments, subscriptions, offers and entitlements
- **pipelines-reference**: the pipeline API
- **pipelines**: background processing pipelines
- **platform-runtime-types**: TOML, run type, name conversions, exceptions
- **product-design**: what each kind of app must contain and how it must look
- **profiles-and-roles**: user profiles, roles and custom attributes
- **reactive-state**: shared, per-client, per-user and persistent state; scopes
- **scaling**: instances, shards and cells for many users
- **secrets**: secrets and credentials
- **sso**: enterprise single sign-on
- **styling-and-motion**: Crosswind classes, theme tokens, colour tokens and motion
- **tailwind-reference**: every Tailwind utility Crosswind supports
- **telephony**: SMS and phone calls
- **theming**: IkonTheme: palettes, dark mode and design tokens
- **ui-api-reference**: the parameters every component shares; where signatures are
- **ui-components**: every UI component with examples: layout, inputs, overlays, drag and drop
- **utilities**: retries, embedded resources, fuzzy matching, logger providers


## Playbooks

A whole job's procedure, `docs/Ikon.Agent.Docs/playbooks/<slug>/SKILL.md` with its files beside it; read it before planning:

- **convert**: Convert, port or migrate an existing web app (Lovable, Replit, Next.js, React and Tailwind) into an IKON app from a folder, zip, repository link or live site, or when a brief names an export under reference/.


## API Signatures

Exact signatures of every public platform type are in `docs/Ikon.Agent.Docs/api/`, one file per namespace (`Ikon.App.md`; `~2`, `~3` parts where one needs several, listed in `_index.md`), generated from the assemblies, so they win when a guide disagrees. Grep them by type or member name before guessing a signature (CS1739, CS1061). A type's line is indented two spaces and its members four: `OpenDatabaseAsync` is on `AppServices`, not on `app`. Grep can print `[Omitted long matching line]` for a long signature; read the file at that line instead.


## Code Patterns

`docs/Ikon.Agent.Docs/patterns/_index.md` groups patterns from production IKON apps by task, each compiled and render tested: adapt the closest one rather than writing a screen or a flow from scratch.

<!-- ikon-user-content-below -->
