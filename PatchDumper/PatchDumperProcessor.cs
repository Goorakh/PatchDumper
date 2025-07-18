﻿using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using PatchDumperPatcher;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PatchDumper
{
    class PatchDumperProcessor
    {
        static readonly Assembly _harmonyAssembly = typeof(Harmony).Assembly;

        public readonly AssemblyDefinition OutputAssembly;

        readonly TypeReference _attributeTypeRef;
        readonly TypeReference _voidTypeRef;
        readonly TypeReference _stringTypeRef;

        readonly MethodDefinition _patchInfoAttributeConstructor;

        readonly Dictionary<Type, TypeDefinition> _cachedContainerTypes = [];

        public PatchDumperProcessor(AssemblyDefinition outputAssembly)
        {
            OutputAssembly = outputAssembly;

            ModuleDefinition mainModule = OutputAssembly.MainModule;
            _attributeTypeRef = mainModule.ImportReference(typeof(Attribute));
            _voidTypeRef = mainModule.ImportReference(typeof(void));
            _stringTypeRef = mainModule.ImportReference(typeof(string));

            TypeDefinition patchInfoAttribute = new TypeDefinition("", "PatchInfoAttribute", Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit, _attributeTypeRef);

            _patchInfoAttributeConstructor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.FamANDAssem | Mono.Cecil.MethodAttributes.Family | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, _voidTypeRef);

            _patchInfoAttributeConstructor.Parameters.Add(new ParameterDefinition("type", Mono.Cecil.ParameterAttributes.None, _stringTypeRef));

            _patchInfoAttributeConstructor.Parameters.Add(new ParameterDefinition("owner", Mono.Cecil.ParameterAttributes.None, _stringTypeRef));

            patchInfoAttribute.Methods.Add(_patchInfoAttributeConstructor);

            OutputAssembly.MainModule.Types.Add(patchInfoAttribute);
        }

        TypeDefinition getOrCreateContainerType(Type type)
        {
            if (type.IsGenericType)
                type = type.GetGenericTypeDefinition();

            if (_cachedContainerTypes.TryGetValue(type, out TypeDefinition cachedContainerType))
                return cachedContainerType;

            string typeName = type.Name;

            // Remove generic type naming
            int backtickIndex = typeName.IndexOf('`');
            if (backtickIndex >= 0)
            {
                typeName = typeName.Remove(backtickIndex);
            }

            const Mono.Cecil.TypeAttributes CONTAINER_TYPE_ATTRIBUTES = Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.Abstract;

            Type declaringType = type.DeclaringType;

            TypeDefinition containerType;
            if (declaringType != null)
            {
                TypeDefinition declaringTypeContainer = getOrCreateContainerType(declaringType);

                containerType = new TypeDefinition("", typeName, CONTAINER_TYPE_ATTRIBUTES);

                declaringTypeContainer.NestedTypes.Add(containerType);
            }
            else
            {
                const string HOOK_CONTAINER_ROOT_NAMESPACE = "Hook";

                string containerNamespace = type.Namespace;
                if (string.IsNullOrEmpty(containerNamespace))
                {
                    containerNamespace = HOOK_CONTAINER_ROOT_NAMESPACE;
                }
                else
                {
                    containerNamespace = $"{HOOK_CONTAINER_ROOT_NAMESPACE}.{containerNamespace}";
                }

                containerType = new TypeDefinition(containerNamespace, typeName, CONTAINER_TYPE_ATTRIBUTES);

                OutputAssembly.MainModule.Types.Add(containerType);
            }

            _cachedContainerTypes.Add(type, containerType);
            return containerType;
        }

        public void AddDetours(MethodBase method, List<BaseDetourInfo> detours)
        {
            if (method == null || detours == null)
                return;

            int detourCount = detours.Count;
            if (detourCount <= 0)
                return;

            List<BaseDetourInfo> hookDetourInfos = new List<BaseDetourInfo>(detourCount);
            List<BaseDetourInfo> ilHookDetourInfos = new List<BaseDetourInfo>(detourCount);
            List<BaseDetourInfo> nativeDetourInfos = new List<BaseDetourInfo>(detourCount);
            List<BaseDetourInfo> detourDetourInfos = new List<BaseDetourInfo>(detourCount);

            foreach (BaseDetourInfo detourInfo in detours)
            {
                switch (detourInfo.Detour)
                {
                    case Hook:
                        hookDetourInfos.Add(detourInfo);
                        break;
                    case ILHook:
                        ilHookDetourInfos.Add(detourInfo);
                        break;
                    case NativeDetour:
                        nativeDetourInfos.Add(detourInfo);
                        break;
                    case Detour:
                        detourDetourInfos.Add(detourInfo);
                        break;
                }
            }

            foreach (BaseDetourInfo hookDetourInfo in detours)
            {
                if (hookDetourInfo.Detour.TryGetUnderlyingDetour(out Detour underlyingDetour))
                {
                    detourDetourInfos.RemoveAll(d => d.Detour == underlyingDetour);
                }
            }

            sortDetours<ILHook>(ilHookDetourInfos);
            sortDetours<Detour>(detourDetourInfos);

            addILHookMethods(method, ilHookDetourInfos);

#if DEBUG
            Log.Debug_NoCallerPrefix($"{method.FullDescription()} detours:");
            foreach (BaseDetourInfo detour in detours)
            {
                Log.Debug_NoCallerPrefix($"    {detour.Detour.GetType().Name}: {detour.To?.FullDescription()}");
            }
#endif
        }

        void addILHookMethods(MethodBase method, List<BaseDetourInfo> ilHookDetourInfos)
        {
            if (!method.HasMethodBody())
            {
                Log.Error($"Method has no body, cannot dump il hooks: {method.FullDescription()}");
                return;
            }

            if (ilHookDetourInfos.Count <= 0)
                return;

            Log.Info($"Dumping IL hooks for {method.FullDescription()}:");

            TypeDefinition containerType = getOrCreateContainerType(method.DeclaringType);

            using DynamicMethodDefinition dmd = new DynamicMethodDefinition(method);
            MethodDefinition methodDefinition = dmd.Definition;
            methodDefinition.Name = method.Name;
            methodDefinition.DeclaringType = containerType;
            methodDefinition.CustomAttributes.Clear();

            IILReferenceBag referenceBag = new RuntimeILReferenceBag();
            
            foreach (BaseDetourInfo detourInfo in ilHookDetourInfos)
            {
                ILHook ilHook = (ILHook)detourInfo.Detour;

                Log.Info($"    - Applying manipulator {ilHook.Manipulator.Method.FullDescription()}");

                using ILContext context = new ILContext(methodDefinition);
                context.ReferenceBag = referenceBag;

                try
                {
                    context.Invoke(ilHook.Manipulator);
                }
                catch (Exception e)
                {
                    Log.Error_NoCallerPrefix($"Exception invoking hook ILManipulator {ilHook.Manipulator.Method.FullDescription()}: {e}");
                    continue;
                }
            }

            addPatchInfoAttributes(methodDefinition, ilHookDetourInfos);

            importMethodReferences(methodDefinition);

            containerType.Methods.Add(methodDefinition);
        }

        void addPatchInfoAttributes(MethodDefinition method, List<BaseDetourInfo> detourInfos)
        {
            foreach (BaseDetourInfo detourInfo in detourInfos)
            {
                addPatchInfoAttribute(method, detourInfo);
            }
        }

        void addPatchInfoAttribute(MethodDefinition method, BaseDetourInfo detourInfo)
        {
            void addAttribute(string type, string owner)
            {
                if (string.IsNullOrEmpty(type))
                    type = string.Empty;

                if (string.IsNullOrEmpty(owner))
                    owner = string.Empty;

                CustomAttribute attribute = new CustomAttribute(_patchInfoAttributeConstructor);
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(_stringTypeRef, type));
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(_stringTypeRef, owner));
                method.CustomAttributes.Add(attribute);
            }

            if (detourInfo.Owner == _harmonyAssembly)
            {
                void addPatchAttribute(string type, Patch patch)
                {
                    addAttribute(type, patch.PatchMethod?.DeclaringType?.Assembly?.FullName);
                }

                Patches patches = Harmony.GetPatchInfo(detourInfo.From);
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

            addAttribute(detourInfo.Detour.GetType().Name, detourInfo.Owner?.FullName);
        }

        void importMethodReferences(MethodDefinition method)
        {
            method.ReturnType = OutputAssembly.MainModule.ImportReference(method.ReturnType);

            foreach (ParameterDefinition parameter in method.Parameters)
            {
                parameter.ParameterType = OutputAssembly.MainModule.ImportReference(parameter.ParameterType);
            }

            foreach (CustomAttribute attribute in method.CustomAttributes)
            {
                attribute.Constructor = OutputAssembly.MainModule.ImportReference(attribute.Constructor);
            }

            foreach (VariableDefinition localVariable in method.Body.Variables)
            {
                localVariable.VariableType = OutputAssembly.MainModule.ImportReference(localVariable.VariableType);
            }

            foreach (ExceptionHandler exceptionHandler in method.Body.ExceptionHandlers)
            {
                if (exceptionHandler.CatchType != null)
                {
                    exceptionHandler.CatchType = OutputAssembly.MainModule.ImportReference(exceptionHandler.CatchType);
                }
            }

            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case IMetadataTokenProvider metadataTokenProvider:
                        instruction.Operand = OutputAssembly.MainModule.ImportReference(metadataTokenProvider);
                        break;
                    case MethodBase methodInfo when methodInfo.DeclaringType != null:
                        instruction.Operand = OutputAssembly.MainModule.ImportReference(methodInfo);
                        break;
                }
            }
        }

        static void sortDetours<TDetour>(List<BaseDetourInfo> detourInfos) where TDetour : ISortableDetour
        {
            if (detourInfos == null || detourInfos.Count <= 1)
                return;

            List<TDetour> detours = new List<TDetour>(detourInfos.Count);
            foreach (BaseDetourInfo detourInfo in detourInfos)
            {
                detours.Add((TDetour)detourInfo.Detour);
            }

            DetourSorter<TDetour>.Sort(detours);

            for (int i = 0; i < detours.Count; i++)
            {
                TDetour detour = detours[i];

                int detourIndex = detourInfos.FindIndex(i, d => ReferenceEquals(d.Detour, detour));
                if (detourIndex == -1)
                    throw new InvalidOperationException("Failed to find detour in list, collection was modified");

                if (detourIndex != i)
                {
                    (detourInfos[i], detourInfos[detourIndex]) = (detourInfos[detourIndex], detourInfos[i]);
                }
            }
        }

        public void Validate()
        {
            foreach (ModuleDefinition module in OutputAssembly.Modules)
            {
                List<TypeDefinition> invalidTypes = [];

                foreach (TypeDefinition type in module.GetAllTypes())
                {
                    List<MethodDefinition> invalidMethods = [];

                    foreach (MethodDefinition method in type.Methods)
                    {
                        bool methodInvalid = false;
                        
                        foreach (Instruction instruction in method.Body.Instructions)
                        {
                            if (instruction.OpCode.FlowControl == FlowControl.Call)
                            {
                                if (instruction.Operand is MethodBase methodOperand)
                                {
                                    try
                                    {
                                        instruction.Operand = module.ImportReference(methodOperand);
                                        Log.Info($"Converted invalid call operand {methodOperand.FullDescription()} to {instruction.Operand} at {instruction.Offset:X4} in {method.FullName}");
                                    }
                                    catch (Exception e)
                                    {
                                        Log.Error($"Failed to convert invalid call operand {methodOperand.FullDescription()} at {instruction.Offset:X4} in {method.FullName}: {e}");
                                    }
                                }

                                if (instruction.Operand is not IMethodSignature)
                                {
                                    Log.Error($"Invalid call operand {instruction.Operand} ({instruction.Operand?.GetType()?.FullName ?? "null"}) at {instruction.Offset:X4} in {method.FullName}");
                                    methodInvalid = true;
                                }
                            }

                            switch (instruction.OpCode.OperandType)
                            {
                                case OperandType.InlineField:
                                case OperandType.InlineMethod:
                                case OperandType.InlineTok:
                                case OperandType.InlineType:

                                    if (instruction.Operand is FieldInfo fieldOperand)
                                    {
                                        try
                                        {
                                            instruction.Operand = module.ImportReference(fieldOperand);
                                            Log.Info($"Converted invalid inline field operand {fieldOperand.DeclaringType.FullName}.{fieldOperand.Name} to {instruction.Operand} at {instruction.Offset:X4} in {method.FullName}");
                                        }
                                        catch (Exception e)
                                        {
                                            Log.Error($"Failed to convert invalid inline field operand {fieldOperand.DeclaringType.FullName}.{fieldOperand.Name} at {instruction.Offset:X4} in {method.FullName}: {e}");
                                        }
                                    }
                                    else if (instruction.Operand is MethodBase methodOperand)
                                    {
                                        try
                                        {
                                            instruction.Operand = module.ImportReference(methodOperand);
                                            Log.Info($"Converted invalid inline method operand {methodOperand.FullDescription()} to {instruction.Operand} at {instruction.Offset:X4} in {method.FullName}");
                                        }
                                        catch (Exception e)
                                        {
                                            Log.Error($"Failed to convert invalid inline method operand {methodOperand.FullDescription()} at {instruction.Offset:X4} in {method.FullName}: {e}");
                                        }
                                    }
                                    else if (instruction.Operand is Type typeOperand)
                                    {
                                        try
                                        {
                                            instruction.Operand = module.ImportReference(typeOperand);
                                            Log.Info($"Converted invalid inline type operand {typeOperand.FullDescription()} to {instruction.Operand} at {instruction.Offset:X4} in {method.FullName}");
                                        }
                                        catch (Exception e)
                                        {
                                            Log.Error($"Failed to convert invalid inline type operand {typeOperand.FullDescription()} at {instruction.Offset:X4} in {method.FullName}: {e}");
                                        }
                                    }
                                    else if (instruction.Operand is not IMetadataTokenProvider)
                                    {
                                        Log.Debug($"Unknown inline operand {instruction.Operand} ({instruction.Operand?.GetType().FullName}) at {instruction.Offset:X4} in {method.FullName}");
                                    }

                                    if (instruction.Operand is not IMetadataTokenProvider)
                                    {
                                        Log.Error($"Invalid {instruction.OpCode.OperandType} ({instruction.OpCode.Name}) operand {instruction.Operand} ({instruction.Operand?.GetType()?.FullName ?? "null"}) at {instruction.Offset:X4} in {method.FullName}");
                                        methodInvalid = true;
                                    }

                                    break;
                            }

                            if (instruction.Operand is IMemberDefinition memberDefinition)
                            {
                                if (memberDefinition.DeclaringType == null)
                                {
                                    Log.Error($"Invalid member reference in method {method.FullName} at {instruction.Offset:X4} ({memberDefinition})");
                                    methodInvalid = true;
                                }
                            }
                        }

                        if (methodInvalid)
                        {
                            invalidMethods.Add(method);
                        }
                    }

                    foreach (MethodDefinition method in invalidMethods)
                    {
                        type.Methods.Remove(method);
                    }
                }
            }
        }
    }
}

