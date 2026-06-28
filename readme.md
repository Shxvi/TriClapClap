[English](readme.md) | [Русский](readme_ru.md)
---
# triclapclap
A lightweight input overlay application for OBS Studio

## Features
* **Independent Sound Toggles:** Separate controls for local audio (on your PC) and stream audio (via WebSocket) to hear clicks only where you want them.
* **Hot-Reload:** Live reloading of configuration (`config.json`) and external assets (`Assets/`) without restarting the application.
* **WebSocket Server:** Real-time broadcasting of animation frames, hits, and CPS to the browser overlay.

---

## How to use

### Method 1: Browser Source Integration (Recommended)
*Provides perfect alpha-transparency for character assets without chroma key artifacts.*

1. Run `triclapclap.exe`.
2. Add a **Browser Source** in OBS.
3. Check the **Local file** box and select `index.html`.
4. Set the resolution to **Width: 1024**, **Height: 576** *(can be changed inside the HTML file)*.
5. If stream sound is enabled, check **Control audio via OBS** in the source properties.

> 📌 **Note:** To hear sound only on the stream, set `"IsSoundLocalEnabled": false` and `"IsSoundStreamEnabled": true` inside `config.json`.

### Method 2: Window Capture (Alternative)
1. Run `triclapclap.exe`.
2. Add a **Window Capture** source in OBS.
3. Select the `triclapclap` window from the list.
4. Right-click the source -> **Filters**.
5. Add a **Chroma Key** filter to remove the background color defined in your config.

## Building the Application

### Method 1: Via Visual Studio
1. Open the project file (`.csproj`) or solution file (`.sln`) in Visual Studio.
2. Set the build configuration to **Release** in the top toolbar.
3. Build the solution by pressing **`Ctrl + Shift + B`** (or via *Build -> Build Solution*).

### Method 2: Via .NET CLI
Run the following command in the project root directory (where the `.csproj` resides):
```bash
dotnet build -c Release
