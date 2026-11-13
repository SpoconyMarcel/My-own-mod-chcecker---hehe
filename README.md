# ModChecker

BepInEx plugin for Gorilla Tag. / Plugin BepInEx do Gorilla Tag.

## Controls
| Button | Action |
|---|---|
| Y (left) | Toggle checker + menu |
| X (left) | Switch EN / PL |
| Right trigger on a player | Select target |
| Right trigger on a menu button | Press button (Language, Lobby hop, Test rig, Mute, Reason, Report) |

Shows: nick, color, FPS, platform, join time, number of legal / illegal mods.
Legal / illegal key lists are editable in `BepInEx/config/com.example.modchecker.cfg`.

## Build
1. Copy these DLLs from `Gorilla Tag_Data/Managed` into `libs/`:
   Assembly-CSharp, UnityEngine, UnityEngine.CoreModule, UnityEngine.PhysicsModule,
   UnityEngine.TextRenderingModule, UnityEngine.XRModule, PhotonUnityNetworking, PhotonRealtime
2. `dotnet build ModChecker/ModChecker.csproj -c Release`
3. Put `ModChecker.dll` into `BepInEx/plugins`.

## GitHub Actions
The workflow builds on every push. Game DLLs are copyrighted, so keep the repo **private** if you commit `libs/`,
or let the workflow fetch them from your own private storage.


## Test rig
The "Spawn test rig" button places a standing dummy player 2 m in front of you with fake data and fake mods.
It works offline, so you can test the checker, mute and report buttons without being in a lobby.
Report on the test rig is simulated and sends nothing.
