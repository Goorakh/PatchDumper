using BepInEx;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using PatchDumper.Content;
using PatchDumperPatcher;
using RoR2;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PatchDumper
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class PatchDumperPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "Gorakh";
        public const string PluginName = "PatchDumper";
        public const string PluginVersion = "1.0.0";

        static readonly Assembly _harmonyAssembly = typeof(Harmony).Assembly;

        internal static PatchDumperPlugin Instance { get; private set; }

        internal ContentPackProvider ContentPackProvider { get; private set; }

        void Awake()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            Log.Init(Logger);

            Instance = SingletonHelper.Assign(Instance, this);

            ContentPackProvider = new ContentPackProvider();
            ContentPackProvider.Register();

            LanguageFolderHandler.Register(System.IO.Path.GetDirectoryName(Info.Location));

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
            DefaultAssemblyResolver assemblyResolver = new DefaultAssemblyResolver();
            assemblyResolver.AddSearchDirectory(System.IO.Path.Combine(Application.dataPath, "Managed"));

            AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Hooks", new Version(0, 0, 0, 0)), "Hooks.dll", new ModuleParameters
            {
                Kind = ModuleKind.Dll,
                AssemblyResolver = assemblyResolver,
            });

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                asm.MainModule.AssemblyReferences.Add(AssemblyNameReference.Parse(assembly.FullName));
            }

            TypeReference attributeTypeRef = asm.MainModule.ImportReference(typeof(Attribute));
            TypeReference voidTypeRef = asm.MainModule.ImportReference(typeof(void));
            TypeReference stringTypeRef = asm.MainModule.ImportReference(typeof(string));

            TypeDefinition patchInfoAttribute = new TypeDefinition("", "PatchInfoAttribute", Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit, attributeTypeRef);

            MethodDefinition patchedInfoAttributeConstructor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.FamANDAssem | Mono.Cecil.MethodAttributes.Family | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, voidTypeRef);

            patchedInfoAttributeConstructor.Parameters.Add(new ParameterDefinition("type", Mono.Cecil.ParameterAttributes.None, stringTypeRef));

            patchedInfoAttributeConstructor.Parameters.Add(new ParameterDefinition("owner", Mono.Cecil.ParameterAttributes.None, stringTypeRef));

            patchInfoAttribute.Methods.Add(patchedInfoAttributeConstructor);

            asm.MainModule.Types.Add(patchInfoAttribute);

            Dictionary<string, TypeDefinition> addedHookContainerTypes = [];

            foreach (KeyValuePair<MethodBase, List<BaseDetourInfo>> kvp in PatcherMain.ActiveDetoursMap)
            {
                MethodBase hookedMethod = kvp.Key;
                List<BaseDetourInfo> detourInfoList = kvp.Value;

                List<BaseDetourInfo> ilHooks = new List<BaseDetourInfo>(detourInfoList.Count);
                List<BaseDetourInfo> onHooks = new List<BaseDetourInfo>(detourInfoList.Count);
                List<BaseDetourInfo> detours = new List<BaseDetourInfo>(detourInfoList.Count);
                List<BaseDetourInfo> nativeDetours = new List<BaseDetourInfo>(detourInfoList.Count);

                foreach (BaseDetourInfo detourInfo in detourInfoList)
                {
                    List<BaseDetourInfo> detourList;
                    switch (detourInfo.Detour)
                    {
                        case Hook:
                            detourList = onHooks;
                            break;
                        case ILHook:
                            detourList = ilHooks;
                            break;
                        case Detour:
                            detourList = detours;
                            break;
                        case NativeDetour:
                            detourList = nativeDetours;
                            break;
                        default:
                            Log.Warning($"Unhandled detour type '{detourInfo.Detour}'");
                            detourList = null;
                            break;
                    }

                    detourList?.Add(detourInfo);
                }

                Log.Message($"Adding hook method {hookedMethod.FullDescription()}");

                List<Type> declaringTypes = [];

                Type declaringType = hookedMethod.DeclaringType;
                do
                {
                    declaringTypes.Insert(0, declaringType);
                    declaringType = declaringType.DeclaringType;
                }
                while (declaringType != null);

                string typeNamespace = declaringTypes[0].Namespace;
                string typeName = string.Join("::", declaringTypes.Select(t => t.Name));

                string typeKey = string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";

                if (!addedHookContainerTypes.TryGetValue(typeKey, out TypeDefinition hookContainerType))
                {
                    const string HOOK_CONTAINER_ROOT_NAMESPACE = "Hook";

                    string containerNamespace;
                    if (!string.IsNullOrEmpty(typeNamespace))
                    {
                        containerNamespace = $"{HOOK_CONTAINER_ROOT_NAMESPACE}.{typeNamespace}";
                    }
                    else
                    {
                        containerNamespace = HOOK_CONTAINER_ROOT_NAMESPACE;
                    }

                    hookContainerType = new TypeDefinition(containerNamespace, typeName, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Abstract);
                    addedHookContainerTypes.Add(typeKey, hookContainerType);
                    asm.MainModule.Types.Add(hookContainerType);
                }

                void addPatchInfoAttribute(MethodDefinition method, string type, BaseDetourInfo detourInfo)
                {
                    void addAttribute(string type, string owner)
                    {
                        CustomAttribute attribute = new CustomAttribute(patchedInfoAttributeConstructor);
                        attribute.ConstructorArguments.Add(new CustomAttributeArgument(stringTypeRef, type));
                        attribute.ConstructorArguments.Add(new CustomAttributeArgument(stringTypeRef, owner));
                        method.CustomAttributes.Add(attribute);
                    }

                    if (detourInfo.Owner == _harmonyAssembly)
                    {
                        void addPatchAttribute(string type, Patch patch)
                        {
                            addAttribute(type, patch.PatchMethod?.DeclaringType?.Assembly?.FullName);
                        }

                        Patches patches = Harmony.GetPatchInfo(hookedMethod);
                        if (patches != null)
                        {
                            bool anyAttributeAdded = false;

                            foreach (Patch prefix in patches.Prefixes)
                            {
                                addPatchAttribute("HarmonyPrefix", prefix);
                                anyAttributeAdded = true;
                            }

                            foreach (Patch postfix in patches.Postfixes)
                            {
                                addPatchAttribute("HarmonyPostfix", postfix);
                                anyAttributeAdded = true;
                            }

                            foreach (Patch transpiler in patches.Transpilers)
                            {
                                addPatchAttribute("HarmonyTranspiler", transpiler);
                                anyAttributeAdded = true;
                            }

                            foreach (Patch finalizer in patches.Finalizers)
                            {
                                addPatchAttribute("HarmonyFinalizer", finalizer);
                                anyAttributeAdded = true;
                            }

                            foreach (Patch ilManipulator in patches.ILManipulators)
                            {
                                addPatchAttribute("HarmonyManipulator", ilManipulator);
                                anyAttributeAdded = true;
                            }

                            if (anyAttributeAdded)
                            {
                                return;
                            }
                        }
                    }

                    addAttribute(type, detourInfo.Owner?.FullName);
                }

                MethodDefinition ilModifiedMethod = null;
                if (ilHooks.Count > 0)
                {
                    using DynamicMethodDefinition dynMethod = new DynamicMethodDefinition(hookedMethod);
                    ilModifiedMethod = dynMethod.Definition;

                    ilModifiedMethod.Name = hookedMethod.Name;
                    ilModifiedMethod.DeclaringType = hookContainerType;
                    ilModifiedMethod.CustomAttributes.Clear();

                    ILContext context = new ILContext(ilModifiedMethod)
                    {
                        ReferenceBag = new RuntimeILReferenceBag()
                    };

                    for (int i = 0; i < ilHooks.Count; i++)
                    {
                        BaseDetourInfo detourInfo = ilHooks[i];

                        ILHook ilHook = (ILHook)detourInfo.Detour;
                        addPatchInfoAttribute(ilModifiedMethod, "ILHook", detourInfo);

                        context.Invoke(ilHook.Manipulator);
                    }

                    importMethodReferences(asm.MainModule, ilModifiedMethod);
                }

                MethodDefinition createNullTargetStub(int index)
                {
                    return new MethodDefinition($"{hookedMethod.Name}_{index}_STUB_NULLTARGET", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, voidTypeRef);
                }

                MethodDefinition[] onHookMethods = new MethodDefinition[onHooks.Count];
                for (int i = 0; i < onHooks.Count; i++)
                {
                    BaseDetourInfo detourInfo = onHooks[onHooks.Count - 1 - i];

                    Hook onHook = (Hook)detourInfo.Detour;

                    MethodDefinition hookMethod;
                    if (detourInfo.To == null)
                    {
                        hookMethod = createNullTargetStub(i);
                    }
                    else
                    {
                        using DynamicMethodDefinition dynMethod = new DynamicMethodDefinition(detourInfo.To);
                        hookMethod = dynMethod.Definition;

                        hookMethod.Name = $"{hookedMethod.Name}_{i}";
                        hookMethod.DeclaringType = hookContainerType;
                        hookMethod.CustomAttributes.Clear();

                        importMethodReferences(asm.MainModule, hookMethod);
                    }

                    addPatchInfoAttribute(hookMethod, "Hook", detourInfo);

                    onHookMethods[i] = hookMethod;

                    hookContainerType.Methods.Add(hookMethod);
                }

                MethodDefinition[] nativeDetourMethods = new MethodDefinition[nativeDetours.Count];
                for (int i = 0; i < nativeDetours.Count; i++)
                {
                    BaseDetourInfo detourInfo = nativeDetours[nativeDetours.Count - 1 - i];

                    NativeDetour nativeDetour = (NativeDetour)detourInfo.Detour;

                    MethodDefinition detourMethod;
                    if (detourInfo.To == null)
                    {
                        detourMethod = createNullTargetStub(i);
                    }
                    else
                    {
                        using DynamicMethodDefinition dynMethod = new DynamicMethodDefinition(detourInfo.To);
                        detourMethod = dynMethod.Definition;

                        detourMethod.Name = $"{hookedMethod.Name}_{i}";
                        detourMethod.DeclaringType = hookContainerType;
                        detourMethod.CustomAttributes.Clear();

                        importMethodReferences(asm.MainModule, detourMethod);
                    }

                    addPatchInfoAttribute(detourMethod, "NativeDetour", detourInfo);

                    nativeDetourMethods[i] = detourMethod;

                    hookContainerType.Methods.Add(detourMethod);
                }

                /*
                MethodDefinition[] detourMethods = new MethodDefinition[detours.Count];
                for (int i = 0; i < detours.Count; i++)
                {
                    DetourInfo detourInfo = detours[detours.Count - 1 - i];

                    Detour detour = (Detour)detourInfo.Detour;

                    MethodDefinition detourMethod;
                    if (detourInfo.To == null)
                    {
                        detourMethod = createNullTargetStub(i);
                    }
                    else
                    {
                        using DynamicMethodDefinition dynMethod = new DynamicMethodDefinition(detourInfo.To);
                        detourMethod = dynMethod.Definition;

                        detourMethod.Name = $"{hookedMethod.Name}_{i}";
                        detourMethod.DeclaringType = hookContainerType;
                        detourMethod.CustomAttributes.Clear();

                        importMethodReferences(asm.MainModule, detourMethod);
                    }
                
                    addPatchInfoAttribute(detourMethod, "Detour", detourInfo);

                    detourMethods[i] = detourMethod;

                    hookContainerType.Methods.Add(detourMethod);
                }
                */

                if (ilModifiedMethod != null)
                {
                    hookContainerType.Methods.Add(ilModifiedMethod);
                }
            }

            asm.Write(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Instance.Info.Location), "Hooks.dll"));
        }

        static void importMethodReferences(ModuleDefinition module, MethodDefinition method)
        {
            method.ReturnType = module.ImportReference(method.ReturnType);

            foreach (ParameterDefinition parameter in method.Parameters)
            {
                parameter.ParameterType = module.ImportReference(parameter.ParameterType);
            }

            foreach (CustomAttribute attribute in method.CustomAttributes)
            {
                attribute.Constructor = module.ImportReference(attribute.Constructor);
            }

            foreach (VariableDefinition localVariable in method.Body.Variables)
            {
                localVariable.VariableType = module.ImportReference(localVariable.VariableType);
            }

            foreach (ExceptionHandler exceptionHandler in method.Body.ExceptionHandlers)
            {
                if (exceptionHandler.CatchType != null)
                {
                    exceptionHandler.CatchType = module.ImportReference(exceptionHandler.CatchType);
                }
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is IMetadataTokenProvider metadataTokenProvider)
                {
                    instruction.Operand = module.ImportReference(metadataTokenProvider);
                }
            }
        }
    }
}

