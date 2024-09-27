using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;

namespace PatchDumperPatcher
{
    public static class PatcherMain
    {
        static readonly List<BaseDetourInfo> _activeDetours = [];
        public static readonly ReadOnlyCollection<BaseDetourInfo> ActiveDetours = _activeDetours.AsReadOnly();

        static bool _activeDetoursDirty = false;
        static void refreshActiveDetours()
        {
            if (!_activeDetoursDirty)
                return;

            _activeDetoursDirty = false;

            _activeDetoursMap.Clear();
            foreach (BaseDetourInfo detourInfo in _activeDetours)
            {
                if (detourInfo.From == null)
                    continue;

                if (!_activeDetoursMap.TryGetValue(detourInfo.From, out List<BaseDetourInfo> detoursList))
                {
                    detoursList = [];
                    _activeDetoursMap.Add(detourInfo.From, detoursList);
                }

                detoursList.Add(detourInfo);
            }
        }

        static readonly Dictionary<MethodBase, List<BaseDetourInfo>> _activeDetoursMap = [];
        static readonly ReadOnlyDictionary<MethodBase, List<BaseDetourInfo>> _readOnlyActiveDetoursMap = new ReadOnlyDictionary<MethodBase, List<BaseDetourInfo>>(_activeDetoursMap);

        public static ReadOnlyDictionary<MethodBase, List<BaseDetourInfo>> ActiveDetoursMap
        {
            get
            {
                refreshActiveDetours();
                return _readOnlyActiveDetoursMap;
            }
        }

        static readonly string[] _hookTypes = [
            typeof(Hook).FullName,
            typeof(ILHook).FullName,
            typeof(Detour).FullName,
            typeof(NativeDetour).FullName
        ];

        static readonly string[] _ignoreAssemblies = [
            typeof(HookEndpointManager).Assembly.GetName().Name
        ];

        static PatcherMain()
        {
            Array.Sort(_hookTypes, StringComparer.OrdinalIgnoreCase);
            Array.Sort(_ignoreAssemblies, StringComparer.OrdinalIgnoreCase);
        }

        public static IEnumerable<string> TargetDLLs { get; } = [];

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        public static void Initialize()
        {
            Util.AppendDelegate(ref Hook.OnDetour, registerHook);
            Util.AppendDelegate(ref Hook.OnUndo, unregisterDetour);

            Util.AppendDelegate(ref ILHook.OnDetour, registerILHook);
            Util.AppendDelegate(ref ILHook.OnUndo, unregisterDetour);

            Util.AppendDelegate(ref Detour.OnDetour, registerDetour);
            Util.AppendDelegate(ref Detour.OnUndo, unregisterDetour);

            Util.AppendDelegate(ref NativeDetour.OnDetour, registerNativeDetour);
            Util.AppendDelegate(ref NativeDetour.OnUndo, unregisterDetour);
        }

        static void registerDetourInfo(BaseDetourInfo detourInfo)
        {
            _activeDetours.Add(detourInfo);
            _activeDetoursDirty = true;
        }

        static bool unregisterDetour(IDetour detour)
        {
            if (_activeDetours.RemoveAll(d => d.Detour == detour) > 0)
            {
                _activeDetoursDirty = true;
            }

            return true;
        }

        static bool registerHook(Hook hook, MethodBase from, MethodBase to, object target)
        {
            registerDetourInfo(new BaseDetourInfo(hook, from, to, findHookOwner()));
            return true;
        }

        static bool registerILHook(ILHook hook, MethodBase method, ILContext.Manipulator manipulator)
        {
            registerDetourInfo(new BaseDetourInfo(hook, method, manipulator.Method, findHookOwner()));
            return true;
        }

        static bool registerDetour(Detour detour, MethodBase from, MethodBase to)
        {
            registerDetourInfo(new BaseDetourInfo(detour, from, to, findHookOwner()));
            return true;
        }

        static bool registerNativeDetour(NativeDetour nativeDetour, MethodBase method, IntPtr from, IntPtr to)
        {
            registerDetourInfo(new NativeDetourInfo(nativeDetour, from, to, findHookOwner()));
            return true;
        }

        static Assembly findHookOwner(StackTrace stackTrace = null)
        {
            stackTrace ??= new StackTrace();

            StackFrame[] frames = stackTrace.GetFrames();

            Assembly ownerAssembly = null;

            for (int i = frames.Length - 1; i >= 0; i--)
            {
                Type callerType = frames[i].GetMethod()?.DeclaringType;

                if (callerType == null || Array.BinarySearch(_hookTypes, callerType.FullName, StringComparer.OrdinalIgnoreCase) < 0)
                    continue;

                i++;
                for (; i < frames.Length; i++)
                {
                    Assembly callingAssembly = frames[i].GetMethod()?.DeclaringType?.Assembly;
                    if (callingAssembly == null)
                        continue;

                    string assemblyName = callingAssembly.GetName().Name;

                    if (Array.BinarySearch(_ignoreAssemblies, assemblyName, StringComparer.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (assemblyName.StartsWith("MMHOOK_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ownerAssembly = callingAssembly;
                    break;
                }

                break;
            }

            if (ownerAssembly != null)
            {
                // Log.Debug($"Selected owner assembly '{ownerAssembly.FullName}' for stack trace {stackTrace}");
            }
            else
            {
                Log.Warning($"Failed to find owner assembly for stack trace {stackTrace}");
            }

            return ownerAssembly;
        }
    }
}
