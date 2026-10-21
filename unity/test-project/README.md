# Lola Retro test project

A small Unity 2D project to try the Unity package (`unity/lola-retro`) from this repository: the test
pictures of `test-images/` as sprites, shown through Lola Retro. It uses the package from the folder next to it
(`Packages/manifest.json`: `file:../../lola-retro`), so a change to the package shows at once.

## Open it

1. Unity Hub → **Projects → Add → Add project from disk** → choose this `test-project` folder. If the Hub asks
   for an editor version, choose your Unity 6 (6000.0 or newer).
2. On the first start Unity imports everything and builds the scene `Assets/LolaRetroTest/LolaRetroTest.unity`
   (the Console says *Lola Retro test: scene built*). Open it if it is not open.
3. Press **Play**.

## What is in the scene

- **Game camera (console resolution)** renders the picture into `Console.renderTexture`, exactly the picture's
  size (256×224 for the platformer): every texel is one console pixel.
- **Display camera (Lola Retro)** renders nothing itself and shows that texture through Lola Retro (**Input**).
  Its Inspector has every setting; change them while the game runs.
- A ball bounces over the picture, one pixel per frame (for the afterglow and the composite dots that move).

## Keys (Play mode)

| Key | |
|---|---|
| Left / Right | previous / next preset |
| C | next cable (RF, composite, S-Video, RGB, TV show) |
| F | flat screen on / off |
| T | next test picture |
| M | the console's picture from the Input texture, or from the display camera's own image |
| Space | ball on / off |
| H | help on / off |

## Render pipelines

The project starts in the Built-in render pipeline. **Lola Retro Test → Use URP** installs URP, makes a URP asset
with a Universal Renderer that has the Lola Retro renderer feature, and uses it; **Use the Built-in render
pipeline** goes back. **Rebuild the test scene** makes the scene again.

## From the command line

The editor scripts can render the scene into a PNG, or make a web build, without opening the editor
(`Unity` is the editor's executable, for example
`/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity`):

```
Unity -batchmode -projectPath unity/test-project -executeMethod LolaRetroTestCapture.Capture -lolaOut out.png -quit
Unity -batchmode -projectPath unity/test-project -buildTarget WebGL -executeMethod LolaRetroTestBuild.WebGL -quit
```

The capture takes `-lolaSize 1440x1080`, `-lolaFrames 3` and `-lolaPipeline urp|builtin` (switching the
pipeline first; `urp` needs URP installed). The web build is in `Builds/WebGL`, uncompressed, for any local web
server (`python3 -m http.server -d unity/test-project/Builds/WebGL`); `-lolaDevelopment` makes a development
build, which logs what the game camera rendered.

## If something is wrong

Errors in the Console (red) and anything that looks wrong on the screen: please send them, with the Unity
version, the render pipeline and the graphics API (**Edit → Project Settings → Player → Other Settings**).
