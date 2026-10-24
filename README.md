# Furniture Designer

Furniture and kitchen designer with parametric built-in wardrobes and an AI assistant (Claude).

| Project | What it is |
|---|---|
| `src/FurnitureDesigner.Core` | Model, editor with undo, templates, parametric wardrobe, import (DXF/OBJ), reports |
| `src/FurnitureDesigner.Ai` | AI assistant: changes the project through Claude tool use |
| `src/FurnitureDesigner.Web` | Web app (Blazor Server + three.js): runs on any OS, in Docker, or hosted |
| `src/FurnitureDesigner.App` | Windows desktop app (WPF) |
| `tests/FurnitureDesigner.Core.Tests` | Unit tests |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: Docker Desktop, only to run the app in a container (see [Docker](#docker))
- Optional: an Anthropic API key for the AI tab (see [API key](#api-key-for-the-ai-tab))

## Quick start: the run script

From the repository root:

```powershell
.\run.ps1
```

It stops a copy of the app that is already running, builds the solution, runs the unit tests, starts the
web app and opens <http://localhost:5080/?sample> in the browser. If the build or a test fails, it stops and
shows the error. Stop the app with `Ctrl+C`; after changing code, just run `.\run.ps1` again.

If Windows says *"running scripts is disabled on this system"*, use `.\run.cmd` instead (same options).

| Option | What it does |
| --- | --- |
| `-SkipTests` | Faster start: skip the unit tests |
| `-Desktop` | Start the Windows desktop (WPF) app instead |
| `-Docker` | Run the web app in Docker → <http://localhost:8080> |
| `-NoBrowser` | Do not open the browser |

```powershell
.\run.ps1 -SkipTests
.\run.ps1 -Desktop
.\run.ps1 -Docker
```

Do not run the script and `dotnet watch` at the same time: both use port 5080 (the script stops the other copy).

## Other ways to run

### Command line

```sh
dotnet run --project src/FurnitureDesigner.Web      # web app → http://localhost:5080/?sample
dotnet watch --project src/FurnitureDesigner.Web    # same, reloads when files change (no debugger)
dotnet run --project src/FurnitureDesigner.App      # Windows desktop app
dotnet test                                         # unit tests
```

### Visual Studio

1. Open `FurnitureDesigner.slnx`.
2. Right-click **FurnitureDesigner.Web** → **Set as Startup Project**.
3. Press **F5** (debug) or **Ctrl+F5** (without debugger). The browser opens with the sample kitchen.

To run the desktop app as well, use **Solution → Configure Startup Projects → Multiple startup projects**.

### VS Code

1. Install the **C# Dev Kit** extension (VS Code suggests it when you open the folder).
2. Open **Run and Debug** (`Ctrl+Shift+D`), pick **Web app** or **Desktop app (WPF)** and press **F5**.
   Breakpoints work in all C# code; stop with `Shift+F5`.
3. Other tasks: `Ctrl+Shift+P` → **Tasks: Run Task** → *build*, *watch web app*, *test*, *docker compose up*.

The 3D view's JavaScript (`wwwroot/js/viewport.js`) is debugged in the browser: `F12` → *Sources*.

## API key for the AI tab

The assistant reads `ANTHROPIC_API_KEY` from the environment; the rest of the app works without it.
Set it once for your Windows user, then restart Visual Studio / VS Code / the terminal:

```powershell
[Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "User")
```

Never put the key in `launchSettings.json` or any other file in the repository.

## Docker

Not needed for everyday development (the run script, Visual Studio and VS Code are faster and can debug).
Use it to check the container, e.g. after changing the `Dockerfile` or before deploying (hosts such as
Railway build the same `Dockerfile`).

Copy `.env.example` to `.env` and fill in `ANTHROPIC_API_KEY`, then:

```sh
docker compose up --build      # or: .\run.ps1 -Docker
```

Open <http://localhost:8080>. Stop with `Ctrl+C` (or `docker compose down`).

## Configuration

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | API key for the AI assistant. Without it the rest of the app still works. |
| `APP_PASSWORD` | When set, the site asks for this password first. Set it before putting the app on the internet, so strangers cannot use your API key. |
| `PORT` | Port to listen on (set automatically by hosts such as Railway). Defaults to 8080 in Docker. |

## Using the app

### Projects and files

Each browser tab has its own project on the server. *Save* downloads a `.fdp` file and *Open* uploads one,
so files stay on your computer; they are the same format the desktop app uses. To bring in a PRO100 design,
export it from PRO100 as 3D DXF (or OBJ) and use *File → Import*.

### Built-in wardrobe (parametric)

A wardrobe that fills a niche wall to wall. You describe the niche and its sections; every board, front,
cut-list line and cost is generated and updates when anything changes.

1. **Properties → Room**: set the room to your niche, e.g. *Width* `2818`, and press Enter.
2. **Catalog → Built-in**: double-click **Built-in wardrobe** (or drag it into the room). It fills the back
   wall and the **Properties** tab opens with its designer.
3. **Niche**: width, height, depth, *gap to walls* (for niches that are not quite square), plinth and
   *top storage* (a separate box on top with its own doors; `0` = none). *Fit to room* resizes it to the room.
4. **Materials**: carcass, fronts, back and front style. Board thickness comes from the material.
5. **Layout**: click a zone in the front diagram to edit its section:
   - *Width*: a number, or empty to share the remaining space
   - *Doors*: Auto / None / 1–3
   - *Presets*: Shelves, Hanging, Long hanging, Drawers + hanging
   - *Zones* (listed top to bottom): shelves, hanging rail, drawers or open, each with a count and a height
     (empty = share the remaining height)
   - *◀ ▶* move the section, *Select* selects its boards in 3D, *+ Section* adds one on the right
6. Every change is one undo step (`Ctrl+Z`). Sizes that cannot fit are scaled, with a warning.
7. **Convert to plain boards** stops the regeneration so the boards can be edited one by one.

Parts get meaningful names ("Section 2 drawer 3 front", "Top storage Divider 1") in the structure tree and the
cut list; hanging rails are drawn but not listed as boards to cut.

### Controls

Press `F1` in the app for the full list. The most used:

| Input | Action |
|---|---|
| Left click / drag | Select an item / move it on the floor (`Shift`: up/down) |
| Right drag · middle drag · wheel | Orbit · pan · zoom |
| `S` `P` `I` `M` | Select, paint, eyedropper, measure tool |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Ctrl+S` / `Ctrl+O` | Save (download) / open |
| `F5` `F6` `F7` `F8` · `Home` | 3D, front, top, left view · zoom to room |
