# SFS Mod Template
_This is a simple mod skeleton to stat making mods in Spacefligth Simulator_

**Note:** If you don't know about programming, c# language and Unity. Please first go learn about it.

## Getting Started

First of all, you need to get Visual Studio Community, you can download it from this [Link](https://visualstudio.microsoft.com/downloads/). When it is installing it will ask you to install workspaces, you need to install `.Net Desktop Development`, you also need to install `.NET Framework 4.8 SDK` in the `Individual Components` tab.

**Note:** Here there is an official Visual Studio installation [guide](https://learn.microsoft.com/en-us/visualstudio/install/install-visual-studio?view=vs-2022)

Next, you need to go to the `Spaceflight Simulator Game\Spaceflight Simulator_Data\Managed` folder and copy the following list of files to the `Dependencies` folder in this project:
- 0Harmony.dll
- Assembly-CSharp.dll
- Newtonsoft.Json.dll
- UniTask.dll
- UnityEngine.CoreModule.dll
- UnityEngine.dll
- UnityEngine.UI.dll
- UnityEngine.UIElementsModule.dll
- UnityEngine.UIModule.dll

Finally, you need to create a copy of `env.props.example` and name it `env.props`. Inside the file you need to change the `ModsFolder` variable to the path where you have your mods folder. This is used to automatically start your game when you compile the Mod. Now you can double click on `SFSMod.sln` and Visual Studio will open the project and if everything is ok and there are no errors you can hit `Run` to start the game with your mod

**Note:** If you want to change the project name to the name of your mod, change "SFSMod" in every file name, folder name and text in this project.

## How to start hacking 

Once you have the project installed and can run it in-game, you can start making your mod. You will need a handy tool called `dnSpy`, this tool is used to view the game code in `Assembly-CSharp.dll` (all SFS functionality is in this file). You can download `dnSpy` at this [link](https://github.com/dnSpy/dnSpy/releases). To use it, you just need to open `Assembly-CSharp.dll` and dnspy will start reading the file and show you all the game code. The game code is useful to see how the game works and to see where you need to tweak to add your mods.

Modifications are done by `Harmony patch`, so you don't need to modify `Assembly-CSharp.dll` to add your modification. to learn about `Harmony` at this [link](https://harmony.pardeike.net/articles/intro.html).

The entry point (first thing to load into your mod) is the `MyMod.cs` file, you can start writing your code here. I added comments to guide you and links that can help you. If you have questions about something, you can write about it in the `Discussions` tab in this repository or go to [SFS dicord server](https://discordapp.com/invite/hwfWm2d) and write in modding channel.