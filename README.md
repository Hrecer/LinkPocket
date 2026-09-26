# LinkPocket

**Your links, in one place that is actually yours — with a built-in AI agent that does the work.** A standalone, local-first personal knowledge base for Windows: import, organize and create your own links, and hand the tedious parts — batch moves, cleanup, dedupe, restructuring — to an agent that operates your library through the same safe, auditable engine the interface uses.

![Version](https://img.shields.io/badge/version-3.2.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![AI Agent](https://img.shields.io/badge/AI-agent%20built--in-8A2BE2)
![Tools](https://img.shields.io/badge/tools-60%2B%20engine%20commands-2E8B57)
![License](https://img.shields.io/badge/license-MIT-green)

---

## The AI agent — ask, approve, undo

LinkPocket ships with an **AI assistant that can actually operate your library**, not just talk about it. You describe the outcome ("move everything loose at the root into a folder called Inbox", "find duplicates and keep the newest one"), and the agent works through the same engine the UI uses — so every step is real, recorded, and reversible.

**What it can do**

- **Organize at scale** — bulk move, copy, rename and retag; restructure a folder tree; split or dissolve folders, all in one instruction.
- **Clean up** — run duplicate scans, trash the copies you don't want, audit what changed recently.
- **Report before acting** — dry-run batch scripts first, show a step-by-step impact preview, and only then apply.

**Why it is safe to let it act**

- **Every capability is an engine command.** The agent gets no private back door — it calls the same 60+ commands the interface uses, with the same validation, write-gates and audit trail.
- **Three-tier approval.** Read-only calls go straight through; reversible writes are audited; destructive or irreversible operations stop and ask — approval cards show exactly what will be touched, with a per-step impact preview before you say yes.
- **Undo and rewind.** Every batch the agent runs lands on the undo stack. **Rewind a turn** and LinkPocket rolls the data back, trims the conversation to before that turn, and puts your original words back in the composer — the three together are what make a rewind trustworthy.
- **Honest failure.** If a step cannot run, you get the real reason and how many items it affected — never a silent "ok".

**How you drive it**

- **Multi-session workspace** — keep several conversations, start a draft without polluting the list, and reference another session by its ID (`#s-<id>`) to pull its context in.
- **Skills and macros** — save a prompt as a reusable skill, save a batch script as a macro the agent can run again.
- **Streaming with thinking** — reasoning, tool calls and results arrive as they happen, with a context-usage ring that reflects what was actually sent to the model.
- **Web search, where the provider supports it** — when your model provider offers server-side search, the agent can consult the live web and cite its sources; otherwise it says so instead of pretending.
- **Bring your own model** — OpenAI-compatible endpoints, OpenAI Responses, or Anthropic Messages; keys are stored locally and encrypted.

**Local-first applies to the agent too.** There is no LinkPocket cloud. The agent runs locally, talks only to the model provider you configure, and every action goes through the same local engine, database and undo journal as your own clicks.

## Why LinkPocket

- **Standalone, not a browser accessory.** No extension to install and no sync service to trust — your links live in one self-contained library on your machine, whether they came from a browser, an export file, or your own typing.
- **Local first.** No account, no sync service, no telemetry. Everything lives in a single folder next to the app — copy it to a USB stick and your whole library comes along.
- **Built for large libraries.** Tens of thousands of bookmarks stay responsive: virtualized lists, instant folder switching, and background work that never blocks the interface.
- **Nothing destructive happens by accident.** Deleting moves things to a recycle bin, permanent removal and full wipes ask twice, and edits can be undone — for you and for the agent.

## What you can do

### Organize

- Build a folder tree as deep as you like, drag bookmarks and folders anywhere, and reorder items inside a folder.
- Select many items at once and move, copy, delete, or export them in one go.
- Rename in place, jump between recently visited folders with Back / Forward / Up, and edit the path directly from the address bar.

### Find

- Search the title, the address, your notes, or the folder path — separately or together — and see **which field matched** for every hit.
- Smart Lists collect what matters without any setup: recently added, recently visited, recently edited, most visited.
- Jump straight to a bookmark by its ID, or from any list into the folder it lives in.

### Keep and restore

- Deleting a bookmark or a folder sends it to the **Recycle Bin**, together with its original location.
- Restore an item where it came from; restore a whole deleted folder as one unit, or put it somewhere else on purpose.
- If the original folder no longer exists, LinkPocket says so and places the item at the top level instead of guessing.

### Clean up

- The **duplicate finder** groups bookmarks that share the same address, shows you each group, and lets you keep the one you want — the rest go to the Recycle Bin, not into the void.

### Move your data in and out

- Import bookmarks from standard bookmark files exported by any browser; export them back out at any time.
- Export as JSON or CSV when you want to process your library elsewhere.
- **Full backup in one file** — including descriptions, visit counts, favorites and site icons. A backup is verified when written and validated before it is restored, and it can be restored by later versions of the app.

### Work without fear

- Every edit can be undone — and redone. The history panel shows what is on both stacks.
- Visit counts and "last visited" times are tracked as you browse, so Smart Lists keep getting smarter.
- Mark the bookmarks you rely on as favorites.

### Make it feel like yours

- **11 built-in themes**, plus a color studio: give it your own palette and the entire interface — surfaces, text, highlights, borders — is derived from it.
- Import your own font files and use them across the whole app.
- **简体中文 and English**, switched instantly from the settings — or leave it on **Automatic**: an English Windows gets English, any other display language gets Simplified Chinese. Switching never touches your bookmarks, folders or paths.

## Getting started

1. Download the latest release for Windows.
2. Extract the archive anywhere — a normal folder, a USB stick, a synced drive.
3. Run `LinkPocket.exe`.

There is no installer and nothing to configure. Your library (database, settings, site icons and logs) is created in the folder you extracted, so uninstalling means deleting that folder — and moving your library means moving it.

To use the AI agent, add a model provider in **Settings → AI** (OpenAI-compatible endpoint, OpenAI Responses, or Anthropic Messages) and paste your API key. It is stored locally, encrypted; nothing leaves your machine until you send a message.

**Requirements:** Windows 10 or Windows 11 (64-bit).

## Your data and your privacy

- Your bookmarks never leave your machine unless you export them yourself.
- LinkPocket keeps no account and sends no usage data.
- The app uses the network only to serve you: it fetches the title, description and icon of a page **when you add or open a bookmark's details**, and it can look up a site's icon from a third-party icon provider when the site itself does not offer one. Nothing else is requested, and nothing about your library is ever uploaded.
- **AI conversations** go only to the model provider you configure, from your machine, with your key. Conversation history and the agent's action log are stored in your library folder like everything else.

## Interface

![The browse view: folder tree on the left, the bookmark list in the middle, details for the selected bookmark on the right](docs/screenshots/browse.png)

*The browse view — your folder tree on the left, the bookmark list in the middle, details for the selected bookmark on the right.*

## Credits

- Icons: [Pictogrammers Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache License 2.0).

## License

[MIT](LICENSE) © 2026 Hrecer
