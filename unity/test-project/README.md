# Lola Retro test project

A small Unity project to try the Unity package (`unity/lola-retro`) from this repository. It uses the package from
the folder next to it (`Packages/manifest.json`: `file:../../lola-retro`), so a change to the package shows at once,
and it shows the package's **Demo** sample, imported the way a user imports it.

## Open it

1. Unity Hub → **Projects → Add → Add project from disk** → choose this `test-project` folder. If the Hub asks
   for an editor version, choose your Unity 6 (6000.0 or newer).
2. On the first start Unity imports everything and imports the Demo (**Package Manager → Lola Retro → Samples →
   Demo**), then opens its scene `Assets/Samples/Lola Retro/…/Demo/Lola Retro Demo` (the Console says *Lola Retro
   test: demo imported*).
3. Press **Play**.

## What is in the Demo

- **Game camera (console resolution)** renders the picture into `Console.renderTexture`, exactly the picture's
  size (256×224 for the platformer): every texel is one console pixel.
- **Display camera (Lola Retro)** renders nothing itself and shows that texture through Lola Retro (**Input**).
  Its Inspector has every setting; change them while the game runs.
- A ball bounces over the picture, one pixel per frame (for the afterglow and the composite dots that move).
- The sprites use the Demo's own sprite shader, so the same scene works in the Built-in render pipeline and URP.

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
with a plain Universal Renderer and uses it, then adds the Lola Retro renderer feature the way the Inspector's
**Fix it** button does; **Use the Built-in render pipeline** goes back. **Import the demo** imports the Demo again.
**Make the demo** (for the package's developers) builds the Demo's scene and assets from its sources in the
package (`unity/lola-retro/Samples~/Demo`) and puts them back there with Unity's `.meta` files.

## From the command line

The editor scripts can render the scene into a PNG, or make a web build, without opening the editor
(`Unity` is the editor's executable, for example
`/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity`):

```
Unity -batchmode -projectPath unity/test-project -executeMethod LolaRetroTestCapture.Capture -lolaOut out.png -quit
Unity -batchmode -projectPath unity/test-project -buildTarget WebGL -executeMethod LolaRetroTestBuild.WebGL -quit
```

The capture takes `-lolaSize 1440x1080`, `-lolaFrames 3`, `-lolaPipeline urp|builtin` (switching the pipeline
first; `urp` needs URP installed), `-lolaPreset`, `-lolaFlat`, `-lolaPicture`, `-lolaTube` and `-lolaSet field=value`
(any setting, more than once); `tools/make_store_images.py` uses it. `LolaRetroTestChecks.ShaderMessages` compiles
every pass of the shader and lists the compiler's errors and warnings. Making the Demo from the command line takes
two runs: `-executeMethod LolaRetroTestSetup.MakeDemoCopy`, then `LolaRetroTestSetup.MakeDemoBuild`. The web build is in `Builds/WebGL`, uncompressed, for any local web
server (`python3 -m http.server -d unity/test-project/Builds/WebGL`); `-lolaDevelopment` makes a development
build, which logs what the game camera rendered.

## If something is wrong

Errors in the Console (red) and anything that looks wrong on the screen: please send them, with the Unity
version, the render pipeline and the graphics API (**Edit → Project Settings → Player → Other Settings**).
