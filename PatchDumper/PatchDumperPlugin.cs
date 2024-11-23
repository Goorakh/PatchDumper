using BepInEx;
using Mono.Cecil;
using PatchDumperPatcher;
using RoR2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace PatchDumper
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class PatchDumperPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "Gorakh";
        public const string PluginName = "PatchDumper";
        public const string PluginVersion = "1.0.0";

        internal static PatchDumperPlugin Instance { get; private set; }

        void Awake()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            Log.Init(Logger);

            Instance = SingletonHelper.Assign(Instance, this);

            stopwatch.Stop();
            Log.Info_NoCallerPrefix($"Initialized in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        }

        void OnDestroy()
        {
            Instance = SingletonHelper.Unassign(Instance, this);
        }

        [ConCommand(commandName = "dump_hooks")]
        static void CCDumpHooks(ConCommandArgs args)
        {
            AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Hooks", new Version(0, 0, 0, 0)), "Hooks.dll", new ModuleParameters
            {
                Kind = ModuleKind.Dll
            });

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                asm.MainModule.AssemblyReferences.Add(AssemblyNameReference.Parse(assembly.FullName));
            }

            PatchDumperProcessor patchProcessor = new PatchDumperProcessor(asm);
            foreach (KeyValuePair<MethodBase, List<BaseDetourInfo>> kvp in PatcherMain.ActiveDetoursMap)
            {
                patchProcessor.AddDetours(kvp.Key, kvp.Value);
            }

            patchProcessor.Validate();

            patchProcessor.OutputAssembly.Write(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Instance.Info.Location), "Hooks.dll"));
        }
    }
}

