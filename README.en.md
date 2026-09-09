<p align="center">
  <a href="README.md">简体中文</a> · <strong>English</strong>
</p>

<div align="center">
  <img src="src/SuperCV.Presentation.Wpf/Assets/SuperCV.Remastered.png" width="108" alt="SuperCV icon" />
  <h1>SuperCV</h1>
  <p><strong>An elegant, lightweight productivity companion</strong></p>
  <p>An AI-powered clipboard that turns content scattered across apps into searchable, editable AI context—ready whenever you need it.</p>
  <p>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D4?style=flat-square" alt="Windows 10 / 11" />
    <img src="https://img.shields.io/badge/Interface-中文%20%2F%20English-4C8BF5?style=flat-square" alt="Chinese and English" />
    <img src="https://img.shields.io/badge/AI-Agent-7C3AED?style=flat-square" alt="AI Agent" />
  </p>
  <p>
    <strong><a href="https://github.com/ShuaiKeAng/OpenSuperCV/releases">Download the latest release</a></strong>
    · <a href="docs/SuperCV_使用说明书.md">Chinese User Guide</a>
    · <a href="docs/SuperCV_User_Guide.md">English User Guide</a>
  </p>
</div>

<p align="center">
  <img src="docs/media/gifs/07-entry-shadow-light.gif" height="400" alt="Card shadows in the light theme" />
  <img src="docs/media/gifs/05-scroll-pin-delete.gif" height="400" alt="Scrolling, pinning, and deleting items" />
  <img src="docs/media/gifs/03-floating-window-expand.gif" height="400" alt="Floating window and expanded window" />
</p>

## Turn your clipboard from a temporary stop into a workspace

SuperCV automatically collects text and images, bringing snippets from web pages, documents, chats, and code editors into one lightweight desktop workspace. Browse recent content at a glance; use search, bookmarks, and workspaces to find it; then paste it back into the app you are using with a click, drag-and-drop, or shortcut.

When content needs more work, AI can translate, summarize, or polish a single item. Within the permissions you choose, the Agent can also create, understand, organize, and edit clipboard content.

<p align="center">
  <img src="docs/media/screenshots/01-main-window.png" width="36%" alt="SuperCV main window" />
</p>

## From copying to reusing, without breaking your flow

1. **Copy as usual**: Copy text or images in any app and SuperCV captures them automatically.
2. **Find it quickly**: Scroll through recent cards, enter keywords, or narrow the list by bookmark, color, or workspace.
3. **Use it right away**: Click an item, drag it, or use a global shortcut to paste it into the target app.
4. **Process it when needed**: Edit an item or ask AI to translate, summarize, polish, organize, and more—then keep using it.

<p align="center">
  <img src="docs/media/gifs/02-click-and-drag-paste.gif" height="260" alt="Click an item and paste it" />
  <img src="docs/media/gifs/10-drag-paste.gif" height="260" alt="Drag an item to another app" />
</p>

## Four core experiences

### Find it again—and find it fast

SuperCV does more than save your latest copies. It gives content a different home based on how often you need it.

- **Workspaces**: Keep content separate by project, client, course, or temporary task. Each workspace has its own ordinary items and long-term history.
- **Bookmarks**: Save frequently used items such as phone numbers, addresses, commands, and canned replies.
- **Pins**: Keep what matters now at the top of the list.
- **Color tags**: Mark categories or priorities so important items stand out.
- **Keyword search**: Search the current workspace from the search box for an exact match.
- **Fuzzy search**: Type `/fs your description` in the search box to discover semantically related items with AI.
- **Text and image filters**: Press the left or right arrow in the search box to quickly filter text or image items.

<p align="center">
  <img src="docs/media/gifs/05-scroll-pin-delete.gif" height="270" alt="Scroll, pin, and delete" />
  <img src="docs/media/gifs/04-switch-workspaces.gif" height="270" alt="Switch workspaces" />
  <img src="docs/media/gifs/06-fuzzy-search.gif" height="270" alt="AI semantic search" />
  <img src="docs/media/gifs/11-filter-text-and-images.gif" height="270" alt="Filter text and image items" />
</p>

<p align="center">
  <img src="docs/media/screenshots/05-pin-and-color-tags.png" height="360" alt="Pins and color tags" />
  <img src="docs/media/screenshots/09-search-filter.png" height="360" alt="Keyword search filter" />
</p>

### Handle every kind of content naturally

From code snippets and rich text to reference images, SuperCV presents every kind of content in a unified card view while retaining the right actions for each.

- Collect Unicode text, legacy text, HTML, RTF, and image items.
- Edit text directly from the context menu, create blank items for notes and temporary information, and undo recent edits.
- Find and replace text directly within clipboard content.
- Preview images, open the original image, or keep an image preview on top of the desktop.
- Export text as TXT or RTF and images as PNG, JPEG, BMP, or TIFF.

<p align="center">
  <img src="docs/media/screenshots/03-edit-text-entry.png" height="360" alt="Edit a text item" />
  <img src="docs/media/screenshots/04-entry-more-actions.png" height="360" alt="More item actions" />
  <img src="docs/media/screenshots/07-image-preview.png" height="360" alt="Preview an original image" />
</p>

<p align="center">
  <img src="docs/media/screenshots/08-pinned-image-preview.png" height="270" alt="Pinned image preview window" />
  <img src="docs/media/gifs/09-pinned-image-preview.gif" height="270" alt="Always-on-top image preview" />
</p>

### AI is right next to your content

Its most powerful feature—AI—can be turned off completely.

After connecting a supported model provider, you can work from a single-item transformation to multi-item organization without moving content to another app.

**Process text with custom prompts**

Click an action on a text item to translate, summarize, polish, explain in simpler terms, or run a custom instruction such as “rewrite this as a brief, polite client reply” or “turn this into a to-do list.”

**Agent-assisted work**

Enter `/ai your question` to open an Agent for the current workspace. It first calls tools to inspect relevant items, then can filter, edit, delete, and create content for tasks such as summarizing materials, locating information, or organizing your workspace.

- Choose from **read-only**, **review and approve**, and **full access** permission levels.
- In review-and-approve mode, confirm edits, new items, deletions, pins, and tags one at a time.
- Expand an execution to see its progress, making content reads and changes more transparent.
- Search the public web when needed; this does not change the Agent's permissions for local content.

When you ask AI from an individual item's entry point, the Agent proactively reads that item. It can still access information from all items in the workspace.

<p align="center">
  <img src="docs/media/screenshots/10-entry-ai-actions.png" height="380" alt="AI text actions on an item" />
  <img src="docs/media/gifs/13-ai-text-transform.gif" height="380" alt="Transform text with an AI instruction" />
</p>

<p align="center">
  <img src="docs/media/screenshots/11-workspace-ai-chat.png" height="340" alt="Workspace AI Agent chat" />
  <img src="docs/media/screenshots/12-entry-ai-chat.png" height="340" alt="Start an AI chat from an item" />
</p>

### Lightweight and at home on your desktop

SuperCV is designed for a quick glance and a single click. The floating window stays compact, while deeper management tools appear only when you need them.

<p align="center">
  <img src="docs/media/gifs/03-floating-window-expand.gif" height="360" alt="SuperCV floating window and expanded window" />
  <img src="docs/media/gifs/01-sidebar-toggle.gif" height="360" alt="Collapse and expand item-side action buttons" />
</p>

<p align="center">
  <img src="docs/media/gifs/12-window-follow.gif" height="250" alt="SuperCV window following" />
</p>

- **Global shortcuts**: Paste recent items by their displayed number or step forward and backward through content; customize the shortcuts to suit your workflow.
- **A familiar entry point**: Optionally take over `Win + V` while keeping your existing habit intact.
- **Runs in the background**: Closing the window keeps SuperCV in the notification area, where it continues recording. Pause capture or quit at any time.
- **Personalize the look**: Choose dark or light mode and multiple themes; adjust font size, opacity, animations, and window-follow speed.
- **Chinese and English UI**: Switch instantly between Chinese and English, with a welcome guide on first launch.

<p align="center">
  <img src="docs/media/gifs/07-entry-shadow-light.gif" height="380" alt="Card depth and shadow in the light theme" />
  <img src="docs/media/gifs/08-entry-shadow-dark.gif" height="380" alt="Card depth and shadow in the dark theme" />
</p>

<p align="center"><strong>Dark themes</strong></p>

<p align="center">
  <img src="docs/media/screenshots/themes/theme-02.png" height="220" alt="Terminal theme: dark mode" />
  <img src="docs/media/screenshots/themes/theme-03.png" height="220" alt="Ember theme: dark mode" />
  <img src="docs/media/screenshots/themes/theme-06.png" height="220" alt="Ocean theme: dark mode" />
  <img src="docs/media/screenshots/themes/theme-10.png" height="220" alt="Plum theme: dark mode" />
  <img src="docs/media/screenshots/themes/theme-12.png" height="220" alt="Monochrome theme: dark mode" />
</p>

<p align="center"><strong>Light themes</strong></p>

<p align="center">
  <img src="docs/media/screenshots/themes/theme-01.png" height="220" alt="Terminal theme: light mode" />
  <img src="docs/media/screenshots/themes/theme-04.png" height="220" alt="Ember theme: light mode" />
  <img src="docs/media/screenshots/themes/theme-05.png" height="220" alt="Ocean theme: light mode" />
  <img src="docs/media/screenshots/themes/theme-09.png" height="220" alt="Plum theme: light mode" />
  <img src="docs/media/screenshots/themes/theme-11.png" height="220" alt="Monochrome theme: light mode" />
</p>

## For every high-frequency copying task

| Scenario | How SuperCV helps |
| --- | --- |
| Writing and office work | Collect research, save templates, polish copy, and paste several items in sequence. |
| Development and operations | Manage code snippets, commands, and configuration, keeping common content close at hand. |
| Learning and reading | Gather key passages, then use search and AI to review and summarize them quickly. |

## Productivity without unclear boundaries

- **AI is entirely optional**: You can still capture, organize, search, and reuse your clipboard without enabling AI.
- **You control permissions**: You choose what the Agent can read or change. Review-and-approve mode is the default guardrail for write actions.
- **Your data is portable**: Export settings, workspaces, history, bookmarks, custom instructions, and image cache as a migration package. Imports are checked first, and the original data is backed up.
- **Treat sensitive content carefully**: When using AI, selected text or content read by the Agent is sent to the model service you configure. Do not provide passwords, tokens, private data, or trade secrets to services you do not trust.

## Get started now

SuperCV supports **Windows 10 and Windows 11**.

1. Download and install the latest version from [Releases](https://github.com/ShuaiKeAng/OpenSuperCV/releases).
2. Start SuperCV and leave **Clipboard capture** enabled.
3. Copy text or images in any app; new content appears automatically in the current workspace.
4. Click an item or use a global shortcut to paste it back into your original app.
5. To use AI, enable a model provider and test the connection in **Settings → AI**.

> New to SuperCV? Read the full [Chinese User Guide](docs/SuperCV_使用说明书.md) or [English User Guide](docs/SuperCV_User_Guide.md).

## Feedback and contributions

For feature requests, questions, or bugs, please join the discussion in [Issues](https://github.com/ShuaiKeAng/OpenSuperCV/issues). When reporting an issue, include the scenario, steps to reproduce, and expected result where possible.

This project is licensed under the repository [LICENSE](LICENSE.txt).
