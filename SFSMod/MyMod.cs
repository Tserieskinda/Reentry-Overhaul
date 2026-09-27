using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using System.Collections.Generic;
using UnityEngine;

namespace SFSMod
{
    public class MyMod : Mod
    {
        public static MyMod Main;

        public override string ModNameID => "reentryoverhaul";
        public override string DisplayName => "Reentry Overhaul";
        public override string Author => "Tserieskinda";
        public override string MinimumGameVersionNecessary => "0.3.7";
        public override string ModVersion => "1.2.0";
        public override string IconLink => "https://raw.githubusercontent.com/Tserieskinda/Reentry-Overhaul/main/icon";
        public override string Description => "A simple mod that adds reentry effects adjustments, enjoy the pre existing one or make your own by F3 button.";

        public override Dictionary<string, string> Dependencies
        {
            get
            {
                return this._dependencies;
            }
        }

        private Dictionary<string, string> _dependencies =
            new Dictionary<string, string>
            {
                { "UITools", "1.0" }
            };

        public override void Early_Load()
        {
            Main = this;

            Debug.Log("Running Early load code");

            Harmony harmony =
                new Harmony(ModNameID);

            harmony.PatchAll();

            SceneHelper.OnWorldSceneLoaded +=
                this.OnWorld;

            SceneHelper.OnBuildSceneLoaded +=
                this.OnBuild;
        }

        public override void Load()
        {
            Debug.Log("Running Load code");

            Settings.Setup();

            SFSMod.Patches.ReentryConsoleCommands.Register();

            SFSMod.Patches.ReentryVisualsUI.Initialize();
        }

        private void OnWorld()
        {
            Debug.Log("On World");
        }

        private void OnBuild()
        {
            Debug.Log("On Build");
        }
    }
}