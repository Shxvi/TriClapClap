[English](readme.md) | [Русский](readme_ru.md)
---
# triclapclap
A lightweight input overlay application for OBS Studio.

## Features
* **Hot-Reload:** Live reloading of configuration (`config.json`) and external assets (`Assets/`) without restarting the app.
* **WebSocket Server:** Real-time broadcasting of animation frames and counters for seamless Browser Source integration.

---
## How to use
### Method 1: Browser Source Integration (Recommended)
*Provides perfect alpha-transparency for character assets without chroma key artifacts.*

1. Run `triclapclap.exe`.
2. Add a **Browser Source** in OBS.
3. Check the **Local file** box and select `index.html`.
4. Set the resolution to **Width: 1024**, **Height: 576** *(can be adjusted by editing the HTML file)*.

> 📌 **Note:** The provided `index.html` is a baseline template. Feel free to modify its CSS layout and design as needed.

### Method 2: Window Capture (Alternative)
1. Run `triclapclap.exe`.
2. Add a **Window Capture** source in OBS.
3. Select the `triclapclap` window from the list.
4. Right-click the source -> **Filters**.
5. Add a **Chroma Key** filter to clear the background color defined in your config.

## Building the Application

### Method 1: Via Visual Studio
1. Open the project file (`.csproj`) or solution file (`.sln`) in Visual Studio.
2. Set the build configuration to **Release** in the top toolbar.
3. Build the solution by pressing **`Ctrl + Shift + B`** (or via *Build -> Build Solution*).

### Method 2: Via .NET CLI
Run the following command in the project root directory (where the `.csproj` resides):
```bash
dotnet build -c Release
```
