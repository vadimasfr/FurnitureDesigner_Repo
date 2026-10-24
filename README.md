# Furniture Designer

PRO100-style furniture and kitchen designer with an AI assistant (Claude).

| Project | What it is |
|---|---|
| `src/FurnitureDesigner.Core` | Model, editor with undo, templates, import (DXF/OBJ), reports |
| `src/FurnitureDesigner.Ai` | AI assistant: changes the project through Claude tool use |
| `src/FurnitureDesigner.Web` | Web app (Blazor Server + three.js): runs on any OS, in Docker, or hosted |
| `src/FurnitureDesigner.App` | Windows desktop app (WPF) |
| `tests/FurnitureDesigner.Core.Tests` | Unit tests |

## Run the web app locally

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/FurnitureDesigner.Web
```

Open http://localhost:5080 (add `?sample` to start with a sample kitchen).
For the AI tab, set `ANTHROPIC_API_KEY` in your environment first.

## Run in Docker

Needs Docker (Desktop). Copy `.env.example` to `.env`, fill in `ANTHROPIC_API_KEY`, then:

```sh
docker compose up --build
```

Open http://localhost:8080. Stop with `Ctrl+C` (or `docker compose down`).

## Configuration

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | API key for the AI assistant. Without it the rest of the app still works. |
| `APP_PASSWORD` | When set, the site asks for this password first. Set it before putting the app on the internet, so strangers cannot use your API key. |
| `PORT` | Port to listen on (set automatically by hosts such as Railway). Defaults to 8080 in Docker. |

## Projects and files

Each browser tab has its own project on the server. *Save* downloads a `.fdp` file and *Open* uploads one,
so files stay on your computer; they are the same format the desktop app uses.

## Desktop app (Windows)

```sh
dotnet run --project src/FurnitureDesigner.App
```
