#if UNITY_EDITOR
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UltEvents;
using UltSharp.EditorTestingUtilities;
using UltSharpCustomReader;
using UnityEngine;

namespace UltSharp
{
    public partial class ScriptCompiler
    {
        public static void Compile(UltSharpScript scriptComp)
        {
            // Get script data
            scriptComp.RefreshScript();
            SerializedScript script = scriptComp.LastScript;


            // remove last artifact
            if (scriptComp.LastCompiledRoot != null) 
                UnityEngine.Object.DestroyImmediate(scriptComp.LastCompiledRoot);

            if (script == null)
            {
                Debug.LogError($"[UltSharp] Couldn't Identify & Compile UltScript: {scriptComp.ScriptIdentifier} on \"Root/{scriptComp.gameObject.GetPath()}\"!");
                return;
            }

            // make new artifact root
            scriptComp.LastCompiledRoot = new GameObject(script.Name + "_Compiled");
            scriptComp.LastCompiledRoot.transform.SetParent(scriptComp.transform, false);

            // make class variables
            VariableStores = new();
            foreach (var field in script.Fields)
            {
                if (!field.IsStatic)  
                {
                    UltSharpScript.UserVarOverride fieldUserOverride = null;
                    if (!field.IsUltSwap)
                        fieldUserOverride = scriptComp.UserVarOverrides.FirstOrDefault(vo => vo.Name == field.Name);

                    var fieldRoot = new GameObject((field.IsPublic ? "public " : "private ") + field.FieldType.Name + " " + field.Name);
                    fieldRoot.SetActive(false);
                    fieldRoot.transform.SetParent(scriptComp.LastCompiledRoot.transform, false); 
                    VariableStores.Add(field, new CompVar(fieldRoot, field.FieldType, fieldUserOverride == null ? field.GetDefault() : fieldUserOverride.Value.Value));
                }
                else throw new NotImplementedException($"UltScript {script.Name} on {scriptComp.gameObject.GetPath()} Attempted to use a static variable!");
            }
            scriptComp.LastVariableFields = VariableStores.Keys.ToArray();
            scriptComp.LastVariableStores = VariableStores.Values.ToArray();

            // compile methods
            UltMethods.Clear(); UltIOs.Clear(); AllCompiledBranches.Clear();
            List<CompVar> CustomFuncIOs = new();
            foreach (var method in script.Methods)
            {
                var methodRoot = new GameObject(method.Key.MethodName + "()");
                methodRoot.transform.SetParent(scriptComp.LastCompiledRoot.transform, false);

                // associate key methods with events
                UltEventHandle createdEvent;
                switch (method.Key.MethodName)
                {
                    case "OnAwake":
                        {
                            LifeCycleEvents lce = methodRoot.AddComponent<LifeCycleEvents>();
                            createdEvent = new(lce, "_AwakeEvent");
                            break;
                        }
                    case "OnStart":
                        {
                            LifeCycleEvents lce = methodRoot.AddComponent<LifeCycleEvents>();
                            createdEvent = new(lce, "_StartEvent");
                            break;
                        }
                    case "OnEnable":
                        {
                            LifeCycleEvents lce = methodRoot.AddComponent<LifeCycleEvents>();
                            createdEvent = new(lce, "_EnableEvent");
                            break;
                        }
                    case "OnDisable":
                        {
                            LifeCycleEvents lce = methodRoot.AddComponent<LifeCycleEvents>();
                            createdEvent = new(lce, "_DisableEvent");
                            break;
                        }
                    case "OnDestroy":
                        {
                            LifeCycleEvents lce = methodRoot.AddComponent<LifeCycleEvents>();
                            createdEvent = new(lce, "_DestroyEvent");
                            break;
                        }
                    case "Update":
                        {
                            UpdateEvents e = methodRoot.AddComponent<UpdateEvents>();
                            createdEvent = new(e, "_UpdateEvent");
                            break;
                        }
                    case "LateUpdate":
                        {
                            UpdateEvents e = methodRoot.AddComponent<UpdateEvents>();
                            createdEvent = new(e, "_LateUpdateEvent");
                            break;
                        }
                    case "FixedUpdate":
                        {
                            UpdateEvents e = methodRoot.AddComponent<UpdateEvents>();
                            createdEvent = new(e, "_FixedUpdateEvent");
                            break;
                        }
                    default:
                        {
                            UltEventHolder e = methodRoot.AddComponent<UltEventHolder>();
                            createdEvent = new(e, "_Event");

                            CustomFuncIOs.Clear();
                            if (method.Key.Parameters.Length > 0)
                                for (int i = 0; i < method.Key.Parameters.Length; i++)
                                {
                                    SerializedType param = method.Key.Parameters[i];
                                    var newInputRoot = new GameObject("InputParam" + i + $" ({param.Name})");
                                    newInputRoot.SetActive(false);
                                    newInputRoot.transform.SetParent(createdEvent.UltComp.transform, false);
                                    CustomFuncIOs.Add(new CompVar(newInputRoot, param.Type, null));
                                }
                            if (method.Key.ReturnType.Type != typeof(void))
                            {
                                var newLocalRoot = new GameObject($"ReturnValue ({method.Key.ReturnType.Name})");
                                newLocalRoot.SetActive(false);
                                newLocalRoot.transform.SetParent(createdEvent.UltComp.transform, false);
                                CustomFuncIOs.Add(new CompVar(newLocalRoot, method.Key.ReturnType.Type, null));
                            }
                            if (CustomFuncIOs.Count > 0)
                            {
                                UltIOs.Add(method.Key, CustomFuncIOs.ToArray());
                            }

                            break;
                        }
                }
                UltMethods.Add(method.Key.MethodName, createdEvent);
            }

            CurScript = script;
            CurRootScript = scriptComp;

            // Assemble IL into Ults
            foreach (var method in script.Methods)
            {
                UltEventHandle handle = UltMethods[method.Key.MethodName];

                // create locals
                var localVarInfos = script.LocalVars[method.Key];
                LocalVariableStores = new CompVar[localVarInfos.Length];
                for (int i = 0; i < localVarInfos.Length; i++)
                {
                    var newLocalRoot = new GameObject("LocalVar" + i);
                    newLocalRoot.SetActive(false);
                    newLocalRoot.transform.SetParent(handle.UltComp.transform, false);
                    LocalVariableStores[i] = new CompVar(newLocalRoot, localVarInfos[i].Type, null);
                }

                IL2Ult(handle, method.Key, method.Value);
            }

            // at this point, all the methods have been converted into ults.
            // However, our special UltSwapAttribute vars still need their OnChange ults setup.
            foreach (var kvp in VariableStores)
                if (kvp.Key.IsUltSwap)
                    BuildUltSwapEvt(kvp.Value);


            // ensure all pcalls are internally updated
            foreach (var b in AllCompiledBranches)
                foreach (var pcall in b.Event.Event.PersistentCallsList)
                {
                    pcall.FSetMethod(null);
                    foreach (var arg in pcall.PersistentArguments)
                        arg.FSetValue(null); // evil caching
                }

            scriptComp.LastMethodNames = UltMethods.Keys.ToArray();
            scriptComp.LastMethodActions = UltMethods.Values.ToArray();
        }

        private static SerializedScript CurScript;
        private static UltSharpScript CurRootScript;
        private static Dictionary<string, UltEventHandle> UltMethods = new();
        private static Dictionary<SerializedMethod, CompVar[]> UltIOs = new();
        private static Stack<UltRet> Stack = new();
        private static Dictionary<Field, CompVar> VariableStores = new();
        private static CompVar[] LocalVariableStores;
        private static CompVar[] CustomMethodParams;
        private static Dictionary<int, UltRet> Branches = new();
        private static List<Branch> AllCompiledBranches = new();
        private static void IL2Ult(UltEventHandle h, SerializedMethod m, IL[] ils)
        {
            Stack.Clear();
            Branches.Clear();
            Transform mathTransform = null;
            void EnsureMathTransform()
            {
                if (mathTransform == null)
                {
                    mathTransform = new GameObject("MathTransform").transform;
                    mathTransform.SetParent(h.UltComp.transform, false);
                    mathTransform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                }
            }

            // identify IL branches
            List<Branch> branches = new();
            string[] jmpOps = new[] { "brfalse.s", "br.s", "ret" };

            // a branch starts at a label, and extends untill either another label or a jmpOp
            int k = 0;
            while (ils.Length > 0) 
            {
                k++;
                int jmpIdx = Array.FindIndex(ils, il => (il.Labels.Length > 0 && il != ils[0]) || jmpOps.Contains(il.OpCode.ToString()));
                var cut = ils[..(jmpIdx+1)];
                IL forNext = null;
                if (jmpIdx != 0 && cut[jmpIdx].Labels.Length > 0)
                {
                    forNext = ils[jmpIdx];
                    cut[jmpIdx] = new IL(forNext.Labels[0]);
                }

                if (branches.Count == 0)
                    branches.Add(new Branch() { Index = 0, Event = h, ILs = cut });
                else
                {
                    int branchNum = branches.Count;
                    UltEventHandle branchEvt = new UltEventHandle(new GameObject("Branch " + branchNum, typeof(UltEventHolder)).GetComponent<UltEventHolder>(), "_Event");
                    branchEvt.UltComp.transform.SetParent(h.UltComp.transform, false);
                    int? label;
                    if (cut[0].Labels.Length != 0)
                        label = cut[0].Labels[0];
                    else label = null;
                        branches.Add(new Branch() { Index = branches.Sum(b => b.ILs.Length), Label = label, Event = branchEvt, ILs = cut });
                }
                ils = ils[(jmpIdx+1)..];
                if (forNext != null)
                    ils = ils.Prepend(forNext).ToArray();

                if (k > 200) throw new Exception(branches.Count.ToString());
            }

            for (int bIdx = 0; bIdx < branches.Count; bIdx++)
            {
                Branch branch = branches[bIdx];
                h = branch.Event;
                AllCompiledBranches.Add(branch);

                Debug.Log("BRANCH #" + bIdx);
                bool branchEarlyExit = false;
                for (int ilIdx = 0; ilIdx < branch.ILs.Length; ilIdx++)
                {
                    IL il = branch.ILs[ilIdx];
                    string fullIlString = il.OpCode.ToString() + "    " + (il.Operand?.ToString() ?? "null");
                    Debug.Log(fullIlString);
                    string opCodeStr = il.OpCode.ToString();
                    
                    if (opCodeStr.StartsWith("conv")) continue;

                    switch (opCodeStr)
                    {
                        case "box":
                        case "nop":
                        case "castclass":
                            break;
                        case "ldstr":
                            Stack.Push(new UltRet(il.Operand));
                            break;
                        case "call":
                        case "callvirt":
                            var meth = (SerializedMethod)il.Operand;
                            var customMethod = CurScript.Methods.FirstOrDefault(m => m.Key == meth);
                            if (customMethod.Key != null)
                            {
                                //Debug.Log(m.MethodName + " tried to run " + customMethod.Key.MethodName + "!");
                                bool hasRetVal = customMethod.Key.ReturnType.Type != typeof(void);

                                UltEventHandle customMethodHandle = UltMethods[customMethod.Key.MethodName];
                                if (customMethod.Key.Parameters.Length > 0)
                                {
                                    // feed forward params
                                    CompVar[] IO = UltIOs[customMethod.Key];
                                    for (int i = hasRetVal ? (IO.Length - 2) : (IO.Length-1); i >= 0; i--)
                                    {
                                        CompVar compVar = IO[i];
                                        h.AddMethod(compVar.Set, UltRet.Params(compVar.Comp, Stack.Pop()));
                                    }
                                }

                                var targ = Stack.Pop();

                                if (!targ.Self)
                                    throw new NotImplementedException(m.MethodName + " tried to call a Custom method on a non-self instance.");

                                h.AddMethod(FGMethod<UltEventHolder>("Invoke"), UltRet.Params(customMethodHandle.UltComp));

                                
                                if (hasRetVal)
                                {
                                    CompVar retVar = UltIOs[customMethod.Key].Last();
                                    Stack.Push(h.AddMethod(retVar.Get, UltRet.Params(retVar.Comp)));
                                }
                                break;
                            }

                            if (meth.MethodType.Name == "UltBehaviour")
                            {
                                bool hasOverride = true;
                                switch (meth.MethodName)
                                {
                                    case "get_gameObject":
                                        Stack.Push(new UltRet(CurRootScript.gameObject));
                                        break;
                                    case "get_transform":
                                        Stack.Push(new UltRet(CurRootScript.gameObject.transform));
                                        break;
                                    default:
                                        hasOverride = false;
                                        break;
                                }
                                if (hasOverride) break;
                            }

                            MethodInfo call = meth;
                            if (call == FGMethod<Type>("GetTypeFromHandle", TA.R<RuntimeTypeHandle>()))
                                break; // we already handled this in ldtoken

                            // silly workaround - we can't use System.Object.ToString(), but we can use static string.Concat(string arg0)
                            if (call.Name == "ToString" && call.ReturnType == typeof(string) && !call.IsStatic)
                                call = FGMethod<string>("Concat", typeof(string));

                            int paramCount = call.GetParameters().Length + (call.IsStatic ? 0 : 1);
                            UltRet[] inputs = new UltRet[paramCount];
                            int c = 0;
                            if (!call.IsStatic)
                            {
                                inputs[0] = Stack.Pop();
                                c++;
                            }
                            while (c < paramCount)
                                inputs[c++] = Stack.Pop();

                            if (call.ReturnType != typeof(void))
                                Stack.Push(h.AddMethod(call, inputs));
                            else
                                h.AddMethod(call, inputs);
                            break;
                        case "ret":
                            if (m.ReturnType.Type != typeof(void))
                            {
                                CompVar retVar = UltIOs[m].Last();
                                h.AddMethod(retVar.Set, UltRet.Params(retVar.Comp, Stack.Pop()));
                            }
                            branchEarlyExit = true;
                            break;
                        case "ldc.i4.0":
                        case "ldc.i4.1":
                        case "ldc.i4.2":
                        case "ldc.i4.3":
                        case "ldc.i4.4":
                        case "ldc.i4.5":
                        case "ldc.i4.6":
                        case "ldc.i4.7":
                        case "ldc.i4.8":
                            Stack.Push(new UltRet(int.Parse(il.OpCode.ToString().Replace("ldc.i4.", ""))));
                            break;
                        case "ldc.i4.s":
                            Stack.Push(new UltRet(Convert.ToInt32(il.Operand)));
                            break;
                        case "ldc.i4":
                            Stack.Push(new UltRet(il.Operand));
                            break;
                        case "ldc.r4":
                        case "ldc.r8":
                            Stack.Push(new UltRet(il.Operand));
                            break;
                        case "ldarg.0":
                            if (m.IsStatic)
                                throw new NotImplementedException("Loading the first parameter onto the stack for a static function is not yet implemented");
                            else
                                Stack.Push(new UltRet() { Self = true });
                            break;
                        case "ldarg.1":
                        case "ldarg.2":
                        case "ldarg.3":
                        case "ldarg.4":
                        case "ldarg.5":
                            {
                                CompVar @in = UltIOs[m][int.Parse(il.OpCode.ToString().Replace("ldarg.", ""))-1];
                                Stack.Push(h.AddMethod(@in.Get, UltRet.Params(@in.Comp)));
                                break;
                            }
                        case "ldflda":
                        case "ldfld":
                            {
                                var f = (SerializedField)il.Operand;
                                if (f.IsStatic)
                                    throw new NotImplementedException($"{h.UltComp.gameObject.GetPath()} tried to access static field {f.DeclaringType}.{f.FieldName}");

                                var targ = Stack.Pop();
                                if (f.DeclaringType == m.MethodType)
                                {
                                    if (targ.Self)
                                    {
                                        var fieldStorager = VariableStores.FirstOrDefault(vs => vs.Key.Name == f.FieldName && vs.Key.FieldType == f.FieldType);
                                        if (fieldStorager.Key == null || fieldStorager.Value == null)
                                            throw new Exception($"load field for {f.DeclaringType}.{f.FieldName} failed; No matching var found in SerializedScript {m.MethodType}.{m.MethodName}()! @{h.UltComp.gameObject.GetPath()}");

                                        targ.Const = new ConstInput();
                                        targ.Const.Type = PersistentArgumentType.Object;
                                        if (f.RuntimeConstant)
                                        {
                                            targ.Const.Value = fieldStorager.Value.Get.Method.Invoke(fieldStorager.Value.Comp, null);
                                            Stack.Push(targ);
                                        }
                                        else
                                        {
                                            if (f.IsUltSwap) // pcalls that work via ultswaps default to the OnChange gameobject; so post-comp we can build the OnChange events.
                                                Stack.Push(new UltRet(fieldStorager.Value.GetUltSwapEvt().gameObject).OverrideRetType(fieldStorager.Value.Type));
                                            else
                                            {
                                                targ.Const.Value = fieldStorager.Value.Comp;
                                                Stack.Push(h.AddMethod(fieldStorager.Value.Get, targ).OverrideRetType(fieldStorager.Value.Type));
                                            }
                                        }
                                        break;
                                    }
                                    else throw new NotImplementedException($"{h.UltComp.gameObject.GetPath()} tried to access owned field {f.DeclaringType}.{f.FieldName} on non-self instance");
                                }
                                else 
                                {
                                    Stack.Push(AddGetField(h, targ, f));
                                    break;
                                }
                            }
                        case "stfld": // field setter
                            {
                                var f = (SerializedField)il.Operand;
                                if (f.IsStatic)
                                    throw new NotImplementedException($"{h.UltComp.gameObject.GetPath()} tried to set static field {f.DeclaringType}.{f.FieldName}");

                                var val = Stack.Pop();
                                var targ = Stack.Pop();

                                if (f.DeclaringType == m.MethodType)
                                {
                                    if (targ.Self)
                                    {
                                        var fieldStorager = VariableStores.FirstOrDefault(vs => vs.Key.Name == f.FieldName && vs.Key.FieldType == f.FieldType);
                                        if (fieldStorager.Key == null || fieldStorager.Value == null)
                                            throw new Exception($"load field for {f.DeclaringType}.{f.FieldName} failed; No matching var found in SerializedScript {m.MethodType}.{m.MethodName}()! @{h.UltComp.gameObject.GetPath()}");

                                        targ.Const = new ConstInput();
                                        targ.Const.Type = PersistentArgumentType.Object;
                                        if (f.RuntimeConstant)
                                            throw new Exception($"{h.UltComp.gameObject.GetPath()} tried to set owned RuntimeConstant field {f.DeclaringType}.{f.FieldName}");
                                        else
                                        {
                                            targ.Const.Value = fieldStorager.Value.Comp;
                                            Stack.Push(h.AddMethod(fieldStorager.Value.Set, UltRet.Params(targ, val)));

                                            if (f.IsUltSwap) // setting an ultswap means also triggering an event that updates all refs to the value
                                                h.AddMethod(FGMethod<UltEventHolder>("Invoke", TA.R()), UltRet.Params(fieldStorager.Value.GetUltSwapEvt()));
                                        }
                                        break;
                                    }
                                    else throw new NotImplementedException($"{h.UltComp.gameObject.GetPath()} tried to set owned field {f.DeclaringType}.{f.FieldName} on non-self instance");
                                }
                                else // -------------- code for setting non-owned instance fields --------------
                                {
                                    AddSetField(h, targ, val, f);
                                    break;
                                }
                            }
                        case "ldtoken":
                            {
                                Stack.Push(h.AddMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(((SerializedType)il.Operand)._assemblyTypeName, true, true)));
                                break;
                            }
                        case "stloc.0":
                        case "stloc.1":
                        case "stloc.2":
                        case "stloc.3":
                            {
                                var compVar = LocalVariableStores[int.Parse(il.OpCode.ToString().Replace("stloc.", ""))];
                                h.AddMethod(compVar.Set, Stack.Pop(), new UltRet(compVar.Comp) { Self = true });
                                break;
                            }
                        case "ldloc.0":
                        case "ldloc.1":
                        case "ldloc.2":
                        case "ldloc.3":
                            {
                                var compVar = LocalVariableStores[int.Parse(il.OpCode.ToString().Replace("ldloc.", ""))];
                                Stack.Push(h.AddMethod(compVar.Get, new UltRet(compVar.Comp) { Self = true }).OverrideRetType(compVar.Type));
                                break;
                            }
                        case "ldloca.s":
                        case "ldloc.s":
                        case "ldloc":
                            {
                                var compVar = LocalVariableStores[Convert.ToInt32(il.Operand)];
                                Stack.Push(h.AddMethod(compVar.Get, new UltRet(compVar.Comp) { Self = true }).OverrideRetType(compVar.Type));
                                break;
                            }
                        case "stloc.s":
                        case "stloc":
                            {
                                var compVar = LocalVariableStores[Convert.ToInt32(il.Operand)];
                                h.AddMethod(compVar.Set, Stack.Pop(), new UltRet(compVar.Comp) { Self = true });
                                break;
                            }
                        case "add":
                            {
                                // supposed to add the latest two numbers on stack
                                EnsureMathTransform();
                                var a = Stack.Pop();
                                var b = Stack.Pop();

                                // reset math transform
                                h.AddMethod(FGProp<Transform>("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                // translate up A
                                h.AddMethod(FGMethod<Transform>("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), a, new UltRet(mathTransform) { Self = true });
                                // translate up B
                                h.AddMethod(FGMethod<Transform>("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), b, new UltRet(mathTransform) { Self = true });
                                // get total
                                var locPos = h.AddMethod(FGProp<Transform>("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });
                                // convert to float
                                Stack.Push(h.AddMethod(FGMethod<Vector3>("Dot", TA.R<Vector3, Vector3>()), new UltRet(new Vector3(1, 0, 0)), locPos));
                                break;
                            }
                        case "sub":
                            {
                                // supposed to sub the latest two numbers on stack
                                EnsureMathTransform();
                                var a = Stack.Pop();
                                var b = Stack.Pop();

                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), a, new UltRet(mathTransform) { Self = true });
                                var aVector = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });
                                aVector = h.AddMethod(typeof(Vector3).FGetMethod("Scale", TA.R<Vector3, Vector3>()), aVector, new UltRet(new Vector3(-1, 0, 0)));
                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, aVector, new UltRet(mathTransform) { Self = true });

                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), b, new UltRet(mathTransform) { Self = true });
                                var locPos = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });
                                Stack.Push(h.AddMethod(typeof(Vector3).FGetMethod("Dot", TA.R<Vector3, Vector3>()), new UltRet(new Vector3(1, 0, 0)), locPos));
                                break;
                            }
                        case "mul":
                            {
                                // supposed to mul the latest two numbers on stack
                                EnsureMathTransform();
                                var a = Stack.Pop();
                                var b = Stack.Pop();

                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), a, new UltRet(mathTransform) { Self = true });
                                var aVector = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });


                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), b, new UltRet(mathTransform) { Self = true });
                                var bVector = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });

                                aVector = h.AddMethod(typeof(Vector3).FGetMethod("Scale", TA.R<Vector3, Vector3>()), aVector, bVector);
                                Stack.Push(h.AddMethod(typeof(Vector3).FGetMethod("Dot", TA.R<Vector3, Vector3>()), new UltRet(new Vector3(1, 0, 0)), aVector));
                                break;
                            }
                        case "div":
                            {
                                // supposed to div the latest two numbers on stack
                                EnsureMathTransform();
                                var b = Stack.Pop();
                                var a = Stack.Pop();

                                b = h.AddMethod(typeof(Mathf).FGetMethod("Pow", TA.R<float, float>()), new UltRet(-1), b);

                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), a, new UltRet(mathTransform) { Self = true });
                                var aVector = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });

                                h.AddMethod(typeof(Debug).FGetMethod("Log", typeof(object)), aVector);

                                h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()), new UltRet(0f), new UltRet(0f), b, new UltRet(mathTransform) { Self = true });
                                var bVector = h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true });

                                h.AddMethod(typeof(Debug).FGetMethod("Log", typeof(object)), bVector);

                                aVector = h.AddMethod(typeof(Vector3).FGetMethod("Scale", TA.R<Vector3, Vector3>()), aVector, bVector);
                                h.AddMethod(typeof(Debug).FGetMethod("Log", typeof(object)), aVector);
                                Stack.Push(h.AddMethod(typeof(Vector3).FGetMethod("Dot", TA.R<Vector3, Vector3>()), new UltRet(new Vector3(1, 0, 0)), aVector));
                                break;
                            }
                        case "cgt":
                            {
                                var a = Stack.Pop();
                                var b = Stack.Pop();
                                Stack.Push(h.AddMethod(typeof(CompileUtils).FGetMethod("GreaterThan", TA.R<float, float>()), a, b));
                                break;
                            }
                        case "clt":
                            {
                                var a = Stack.Pop();
                                var b = Stack.Pop();
                                Stack.Push(h.AddMethod(typeof(CompileUtils).FGetMethod("LessThan", TA.R<float, float>()), a, b));
                                break;
                            }
                        case "newobj":
                            {
                                var ctor = (SerializedMethod)il.Operand;
                                switch (ctor.ReturnType.Name)
                                {
                                    case "Vector3":
                                        EnsureMathTransform();
                                        h.AddMethod(typeof(Transform).FGetProperty("localPosition").SetMethod, new UltRet(Vector3.zero), new UltRet(mathTransform) { Self = true });
                                        h.AddMethod(typeof(Transform).FGetMethod("Translate", TA.R<float, float, float>()),
                                            Stack.Pop(), Stack.Pop(), Stack.Pop(), new UltRet(mathTransform) { Self = true });
                                        Stack.Push(h.AddMethod(typeof(Transform).FGetProperty("localPosition").GetMethod, new UltRet(mathTransform) { Self = true }));
                                        break;
                                    default:
                                        throw new NotImplementedException("Constructor for " + ctor.ReturnType.Name + " is not implemented! Attempted on: " + h.UltComp.gameObject.GetPath());
                                }
                                break;
                            }
                        case "dup":
                            {
                                var a = Stack.Pop();
                                var b = (UltRet)a.Clone();
                                Stack.Push(a);
                                Stack.Push(b);
                                break;
                            }
                        case "brfalse.s":
                            {
                                // exec Branch with operand as label if false;
                                // else exec next branch
                                var brFalseRoot = new GameObject(fullIlString);
                                brFalseRoot.transform.SetParent(h.UltComp.transform, false);

                                UltEventHandle onTrue = new UltEventHandle(new GameObject("True", typeof(LifeCycleEvents)).GetComponent<LifeCycleEvents>(), "_EnableEvent");
                                onTrue.UltComp.transform.SetParent(brFalseRoot.transform, false);
                                onTrue.UltComp.gameObject.SetActive(false);
                                onTrue.UltComp.gameObject.AddComponent<IfBlockRunner>();

                                UltEventHandle onFalse = new UltEventHandle(new GameObject("False", typeof(LifeCycleEvents)).GetComponent<LifeCycleEvents>(), "_EnableEvent");
                                onFalse.UltComp.transform.SetParent(brFalseRoot.transform, false);
                                onFalse.UltComp.gameObject.SetActive(false);
                                onFalse.UltComp.gameObject.AddComponent<IfBlockRunner>();
                                
                                var nextEvt = branches[bIdx + 1].Event;
                                onTrue.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onTrue.UltComp.gameObject, false));
                                onTrue.AddMethod(FGMethod<UltEventHolder>("Invoke"), new UltRet(nextEvt.UltComp));

                                var jmpEvt = branches.First(b => b.Label == (int)il.Operand).Event;
                                onFalse.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onFalse.UltComp.gameObject, false));
                                onFalse.AddMethod(FGMethod<UltEventHolder>("Invoke"), new UltRet(jmpEvt.UltComp));

                                UltRet condition = Stack.Pop();
                                h.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onTrue.UltComp.gameObject, condition));
                                //h.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onTrue.UltComp.gameObject, false));

                                condition = h.AddMethod(FGMethod<object>("Equals", TA.R<object, object>()), UltRet.Params(condition, false));
                                h.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onFalse.UltComp.gameObject, condition));
                                //h.AddMethod(FGMethod<GameObject>("SetActive", TA.R<bool>()), UltRet.Params(onFalse.UltComp.gameObject, false));

                                break;
                            }
                        case "br.s":
                        case "br":
                        case "MetaGoto":
                            {
                                var b = branches.First(b => b.Label == (int)il.Operand);
                                if (b.ILs[0].OpCode.OpCode.ToString() == "ret")
                                {
                                    branchEarlyExit = true;
                                    break;
                                }

                                var jmpEvt = b.Event;
                                h.AddMethod(FGMethod<UltEventHolder>("Invoke"), new UltRet(jmpEvt.UltComp));
                                break;
                            }
                        case "pop":
                            {
                                Stack.Pop();
                                break;
                            }
                        case "ldnull":
                            {
                                Stack.Push(new UltRet() { Const = new ConstInput() { Type = PersistentArgumentType.Object } });
                                break;
                            }
                        case "ldlen":
                            {
                                var arr = Stack.Pop();
                                Stack.Push(h.AddMethod(FGProp<Array>("Length").GetMethod, arr));
                                break;
                            }
                        default:
                            throw new NotImplementedException("Unexpected OpCode: " + il.OpCode.ToString());

                    }
                    if (branchEarlyExit) break;
                }
            }
        }
        private static void BuildUltSwapEvt(CompVar var)
        {
            UltEventHandle h = new UltEventHandle(var.GetUltSwapEvt(), "_Event");
            var gottenNewValue = h.AddMethod(var.Get, new UltRet(var.Comp));

            void print(object o)
            {
                if (o is UltRet u)
                    h.AddMethod(typeof(Debug).FGetMethod("Log", TA.R<object>()), u);
                else
                    h.AddMethod(typeof(Debug).FGetMethod("Log", TA.R<object>()), new UltRet(o));
            }

            UnityEngine.Object ultMarker = var.GetUltSwapEvt().gameObject;
            foreach (var b in AllCompiledBranches)
            {
                UltEventHandle t = b.Event;
                
                List<(int, bool, List<int>)> pendingSwaps = new();
                for (int i = 0; i < t.Event.PersistentCallsList.Count; i++)
                {
                    bool needsTargSwap = t.Event.PersistentCallsList[i].Target == ultMarker;

                    List<int> swapArgs = t.Event.PersistentCallsList[i].PersistentArguments.Where(a => a.Type == PersistentArgumentType.Object && a.Object == ultMarker)
                        .Select(a => Array.IndexOf(t.Event.PersistentCallsList[i].PersistentArguments, a)).ToList();

                    if (needsTargSwap || swapArgs.Count > 0)
                    {
                        pendingSwaps.Add((i, needsTargSwap, swapArgs));

                        foreach (var idx in swapArgs)
                            t.Event.PersistentCallsList[i].PersistentArguments[idx].FSetObject(var.Value as UnityEngine.Object);

                        if (needsTargSwap)
                            t.Event.PersistentCallsList[i].FSetTarget(var.Value as UnityEngine.Object);
                    }
                }


                if (pendingSwaps.Count == 0) continue;

                // get the event
                var e = AddGetField(h, new UltRet(t.UltComp), t.UltField);
                var pcallList = h.AddMethod(FGProp<UltEventBase>("PersistentCallsList").GetMethod, e);
                int curIEidx = 0;
                var enumerator = h.AddMethod(FGMethod<IEnumerable>("GetEnumerator", TA.R()), pcallList);
                h.AddMethod(FGMethod<IEnumerator>("MoveNext"), enumerator);
                var moveEnumeratorCall = h.Event.PersistentCallsList.Last();
                foreach ((int idx, bool needsTargSwap, List<int> argIdxs) plannedSwap in pendingSwaps)
                {
                    //Debug.Log(plannedSwap.idx + " : " + t.UltComp.gameObject.name + " - " + JsonConvert.SerializeObject(plannedSwap.argIdxs));
                    while (true)
                    {
                        if (curIEidx != plannedSwap.idx)
                        {
                            curIEidx++;
                            var moveCall = new PersistentCall();
                            moveCall.CopyFrom(moveEnumeratorCall);
                            h.Event.PersistentCallsList.Add(moveCall);
                            continue;
                        }

                        var gottenPcall = h.AddMethod(FGProp<IEnumerator>("Current").GetMethod, enumerator);
                        if (plannedSwap.needsTargSwap)
                            AddSetField(h, gottenPcall, gottenNewValue, FGField<PersistentCall>("_Target"));

                        if (plannedSwap.argIdxs.Count > 0)
                        {
                            var argsList = h.AddMethod(FGProp<PersistentCall>("PersistentArguments").GetMethod, gottenPcall);
                            foreach (var argIdx in plannedSwap.argIdxs)
                            {
                                var arg = h.AddMethod(FGMethod<Array>("GetValue", TA.R<int>()), UltRet.Params(argsList, argIdx));
                                AddSetField(h, arg, gottenNewValue, FGField<PersistentArgument>("_Object"));
                                AddSetField(h, arg, gottenNewValue, FGField<PersistentArgument>("_Value"));
                            }
                        }

                        break;
                    }
                }
            }
        }
        private static UltRet AddGetField(UltEventHandle h, UltRet targ, SerializedField f, bool debugPrint = false)
        {
            void print(object o)
            {
                if (!debugPrint) return;
                if (o is UltRet u)
                    h.AddMethod(typeof(Debug).FGetMethod("Log", TA.R<object>()), u);
                else
                    h.AddMethod(typeof(Debug).FGetMethod("Log", TA.R<object>()), new UltRet(o));
            }

            print($"Planning on getting field {f.FieldName} from {f.DeclaringType.Name}");

            // We have to get the MethodInfo of FieldInfo.GetValue, then run it on the reflected FieldInfo to set the value on targ
            // gross and ugly
            var typeofType = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(Type).AssemblyQualifiedName, true, true));
            var typeofTypeArr = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(Type[]).AssemblyQualifiedName, true, true));

            var typeofFieldInfo = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(FieldInfo).AssemblyQualifiedName, true, true));

            var typeArr_string_bindingflags = h.FindOrAddConstMethod(typeof(JsonConvert).FGetMethod("DeserializeObject", TA.R<string, Type>()), UltRet.Params("[\"System.String, mscorlib\", \"System.Reflection.BindingFlags, mscorlib\"]", typeofTypeArr));
            var methodInfo_Type_GetField = h.FindOrAddConstMethod(FGMethod<MemberDescriptor>("FindMethod", TA.R<Type, string, Type[], Type, bool>()),
                UltRet.Params(typeofType, "GetField", typeArr_string_bindingflags, typeofFieldInfo, false));

            var typeofSysObj = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(object).AssemblyQualifiedName, true, true));
            var objArr_fieldname_bindingflag = h.FindOrAddConstMethod(FGMethod<Array>("CreateInstance", TA.R<Type, int>()), UltRet.Params(typeofSysObj, 2));

            // fill above array ...
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_fieldname_bindingflag, 0, f.FieldName));
            var typeofBindingFlags = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(BindingFlags).AssemblyQualifiedName, true, true));
            var anyAccessBindingFlags = h.FindOrAddConstMethod(FGMethod<Enum>("Parse", TA.R<Type, string>()), UltRet.Params(typeofBindingFlags, "60"));
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_fieldname_bindingflag, 1, anyAccessBindingFlags));

            var typeofTargObject = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(f.DeclaringType._assemblyTypeName, true, true));
            Type secureUtils = Type.GetType("System.SecurityUtils, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", true, true);
            var fieldInfo_TargField = h.AddMethod(secureUtils.FGetMethod("MethodInfoInvoke", TA.R<MethodInfo, object, object[]>()), UltRet.Params(methodInfo_Type_GetField, typeofTargObject, objArr_fieldname_bindingflag));

            print("got fieldinfo: ");
            print(fieldInfo_TargField);

            var typeArr_object = h.FindOrAddConstMethod(typeof(JsonConvert).FGetMethod("DeserializeObject", TA.R<string, Type>()), UltRet.Params("[\"System.Object, mscorlib\"]", typeofTypeArr));
            var methodInfo_FieldInfo_GetValue = h.FindOrAddConstMethod(FGMethod<MemberDescriptor>("FindMethod", TA.R<Type, string, Type[], Type, bool>()),
                UltRet.Params(typeofFieldInfo, "GetValue", typeArr_object, typeofSysObj, false));


            var objArr_targObj = h.FindOrAddConstMethod(FGMethod<Array>("CreateInstance", TA.R<Type, int>()), UltRet.Params(typeofSysObj, 1));
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_targObj, 0, targ));

            return h.AddMethod(secureUtils.FGetMethod("MethodInfoInvoke", TA.R<MethodInfo, object, object[]>()), UltRet.Params(methodInfo_FieldInfo_GetValue, fieldInfo_TargField, objArr_targObj));
        }
        private static void AddSetField(UltEventHandle h, UltRet targ, UltRet val, SerializedField f)
        {
            // We have to get the MethodInfo of FieldInfo.SetValue, then run it on the reflected FieldInfo to set the value on targ
            // gross and ugly
            var typeofType = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(Type).AssemblyQualifiedName, true, true));
            var typeofTypeArr = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(Type[]).AssemblyQualifiedName, true, true));

            var typeofFieldInfo = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(FieldInfo).AssemblyQualifiedName, true, true));

            var typeArr_string_bindingflags = h.FindOrAddConstMethod(typeof(JsonConvert).FGetMethod("DeserializeObject", TA.R<string, Type>()), UltRet.Params("[\"System.String, mscorlib\", \"System.Reflection.BindingFlags, mscorlib\"]", typeofTypeArr));
            var methodInfo_Type_GetField = h.FindOrAddConstMethod(FGMethod<MemberDescriptor>("FindMethod", TA.R<Type, string, Type[], Type, bool>()),
                UltRet.Params(typeofType, "GetField", typeArr_string_bindingflags, typeofFieldInfo, false));

            var typeofSysObj = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(object).AssemblyQualifiedName, true, true));
            var objArr_fieldname_bindingflag = h.FindOrAddConstMethod(FGMethod<Array>("CreateInstance", TA.R<Type, int>()), UltRet.Params(typeofSysObj, 2));

            // fill above array ...
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_fieldname_bindingflag, 0, f.FieldName));
            var typeofBindingFlags = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(BindingFlags).AssemblyQualifiedName, true, true));
            var anyAccessBindingFlags = h.FindOrAddConstMethod(FGMethod<Enum>("Parse", TA.R<Type, string>()), UltRet.Params(typeofBindingFlags, "60"));
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_fieldname_bindingflag, 1, anyAccessBindingFlags));

            var typeofTargObject = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(f.DeclaringType._assemblyTypeName, true, true));
            Type secureUtils = Type.GetType("System.SecurityUtils, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", true, true);
            var fieldInfo_TargField = h.AddMethod(secureUtils.FGetMethod("MethodInfoInvoke", TA.R<MethodInfo, object, object[]>()), UltRet.Params(methodInfo_Type_GetField, typeofTargObject, objArr_fieldname_bindingflag));



            var typeArr_object_object = h.FindOrAddConstMethod(typeof(JsonConvert).FGetMethod("DeserializeObject", TA.R<string, Type>()), UltRet.Params("[\"System.Object, mscorlib\", \"System.Object, mscorlib\"]", typeofTypeArr));
            var typeofVoid = h.FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(void).AssemblyQualifiedName, true, true));
            var methodInfo_FieldInfo_SetValue = h.FindOrAddConstMethod(FGMethod<MemberDescriptor>("FindMethod", TA.R<Type, string, Type[], Type, bool>()),
                UltRet.Params(typeofFieldInfo, "SetValue", typeArr_object_object, typeofVoid, false));


            var objArr_targObj_value = h.FindOrAddConstMethod(FGMethod<Array>("CreateInstance", TA.R<Type, int>()), UltRet.Params(typeofSysObj, 2));
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_targObj_value, 0, targ));
            val.OverrideRetType(f.FieldType);
            h.AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(objArr_targObj_value, 1, val));

            h.AddMethod(secureUtils.FGetMethod("MethodInfoInvoke", TA.R<MethodInfo, object, object[]>()), UltRet.Params(methodInfo_FieldInfo_SetValue, fieldInfo_TargField, objArr_targObj_value));
        }
        private class Branch
        {
            public IL[] ILs;
            public int? Label;
            public int Index;
            public UltEventHandle Event;
        }
    }
}
#endif