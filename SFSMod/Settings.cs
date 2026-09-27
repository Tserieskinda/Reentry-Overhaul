using ModLoader;
using SFS.Input;
using SFS.IO;
using SFS.Parsers.Json;
using UnityEngine;

namespace SFSMod
{
	public class Settings : ModKeybindings
	{


		public static Settings Main;

		public static void Setup()
		{
			// Again for the singleton pattern. To use keybindings values anywhere in your mod.
			Main = SetupKeybindings<Settings>(MyMod.Main);
		}

		public override void CreateUI()
		{
			// this sections load default keybindings
			Settings keySettings = new Settings();

			// this creates UI elements in the Settings menu
			base.CreateUI_Text("Reentry Overhaul");
			base.CreateUI_Keybinding(Test, keySettings.Test, "Test Key");

		}

		// define your key bindings like this
		public KeybindingsPC.Key Test = KeyCode.T;
	}
}
