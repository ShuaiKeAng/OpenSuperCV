# SuperCV User Guide

> Version 2.0 · Windows 10/11 · Updated 2026-08-27

SuperCV is a Windows clipboard workspace. It captures recent text and images, keeps useful items within reach, and adds search, reusable instructions, and AI-assisted work when enabled.

## Quick start

1. Start SuperCV and leave **Clipboard capture** on.
2. Copy text in any application. If image support is enabled, copied images are captured too.
3. Return to SuperCV to see the newest item in the active workspace.
4. Click an item to paste it back into the previously active app. Use the item menu to edit, export, pin, tag, bookmark, or delete it.
5. Type a keyword in the search box and press Enter to filter the current workspace.

Closing the main window does not always exit the app. When **Exit when closed** is off, SuperCV stays available in the notification area.

## Main window

The top bar contains the main action button, search, Settings, and Close. The lower toolbar selects a workspace and controls dark mode, item actions, clipboard capture, clear workspace, new blank item, and return to top.

- **Workspaces** separate projects, clients, or collections. Each has its own clipboard items and long-term history.
- **Bookmarks** are shared across workspaces and are intended for items you reuse often, such as signatures, addresses, and command snippets.
- **Pinned items** remain ahead of ordinary items and are not removed by the ordinary item limit.
- **Image items** are stored separately from text. AI Agent reads text only, not image pixels.

## Search and commands

Ordinary search is an exact substring search in the current workspace. Clear the search box to return to the normal list.

- `/ai your question` opens an AI Agent conversation for the active workspace.
- `/fs your query` asks AI to find semantically related text items.

AI features require an enabled provider and a working API configuration.

## Settings

Use **Settings > General > Display language** to switch between **中文** and **English**. The welcome page also has a compact two-button language switch in its top-left header. Language changes take effect immediately and are saved for the next launch.

Other common settings include startup behavior, animations, visible item count, text size, opacity, delete confirmation, duplicate handling, image support, and the ordinary item limit. Long-term history is stored independently and can be searched from the History page.

## Shortcuts

Default shortcuts can be changed in **Settings > Shortcuts**. Common defaults include:

- `Win + V`: SuperCV clipboard entry point when enabled.
- `Alt + Shift + 1` through `0`: paste items by displayed number.
- `Alt + Shift + Q` through `Y`: paste visible items.
- `Alt + Arrow`: move the main window.

If a shortcut does not work, check for conflicts with Windows or another application and confirm global shortcuts are enabled.

## AI setup and text actions

In **Settings > AI**, enable AI, select a provider, and enter its API key. Custom providers require an OpenAI-compatible base URL and model name. Use **Test connection** before relying on a provider.

Text items can use the supplied actions—Translate, Summarize, Polish, and Explain simply—or your own instruction. Default system prompts and preset instructions follow the selected display language for new installations; your custom prompts and instructions are never overwritten by a language change.

AI requests send selected text to the provider you configured. Do not send passwords, secrets, personal data, or confidential material to a service you do not trust.

## AI Agent

The Agent can inspect the active workspace, read text entries, filter entries, and—depending on its permission level—edit, create, delete, pin, or tag entries. Prefer review/approval mode for unfamiliar or sensitive work. Agent actions are limited to the active ordinary-history items; it does not automatically read long-term history or image pixels.

The Agent can read this guide through its manual tool. Guide text is reference material, not higher-priority instructions.

## Data, export, and safety

By default, SuperCV stores settings, workspaces, clipboard history, bookmarks, instructions, image cache, and long-term history under `%LocalAppData%\SuperCV\V2`. Do not edit or remove files in this directory while the app is running.

Use **Settings > About** to export a single `.supercvbackup` package or import one created by SuperCV. Import is validated before restart and retains a rollback copy of the previous data. The package can include local clipboard content, so store it securely. API keys are protected with the current Windows user's DPAPI and may need to be entered again after moving to a different Windows user or PC.

For important material, create a bookmark or export a file before clearing a workspace, deleting a workspace, or making large AI changes.
