#if UNITY_EDITOR
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UltEvents;
using UltSharpCustomReader;
using UnityEngine;
using static UltSharp.ScriptCompiler;

namespace UltSharp
{
    [Serializable]
    public class UltEventHandle
    {
        public UltEventHandle(Component c, string f)
        {
            UltComp = c;
            UltField = c.GetType().GetField(f, UltEventUtils.AnyAccessBindings);

            if (UltField.GetValue(UltComp) == null)
                UltField.SetValue(UltComp, Activator.CreateInstance(UltField.FieldType));
        }

        public Component UltComp;
        public FieldInfo UltField 
        {
            get => _sf;
            set => _sf = value;
        }
        [SerializeField] SerializedField _sf;
        public UltEventBase Event
        {
            get 
            {
                UltEventBase e = (UltEventBase)UltField.GetValue(UltComp);
                if (e.PersistentCallsList == null)
                    _pcallAccess.SetValue(e, new List<PersistentCall>());
                return e;
            }
        }
        private static FieldInfo _pcallAccess = typeof(UltEventBase).GetField("_PersistentCalls");


        public UltRet AddMethod(MethodInfo m, params UltRet[] inputs)
        {
            inputs = inputs.Reverse().ToArray();

            if (!m.IsStatic)
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(m.DeclaringType))
                {
                    if (inputs[0].Const == null)
                        return AddReflectionMethod(m, inputs);

                    return AddConstMethod(m, inputs);
                } else
                    return AddReflectionMethod(m, inputs);
            } else return AddConstMethod(m, inputs);
        }
        private UltRet AddConstMethod(MethodInfo m, UltRet[] inputs) // handles static calls and instance calls where the instance is RuntimeConstant
        {
            // make call
            PersistentCall c = new PersistentCall();
            Event.PersistentCallsList.Add(c);
            c.FSetMethodName(m.IsStatic ? UltEventUtils.GetFullyQualifiedName(m) : m.Name);

            if (!m.IsStatic)
            {
                // set target
                c.FSetTarget((UnityEngine.Object)inputs[0].Const.Value);
                inputs = inputs[1..];
            }

            // init args
            var @params = m.GetParameters();
            c.FSetArgs(new PersistentArgument[@params.Length]);
            for (int i = 0; i < @params.Length; i++)
            {
                ParameterInfo p = @params[i];
                PersistentArgument arg = c.PersistentArguments[i] = new PersistentArgument();

                // ints auto-cast to floats; floats do not auto-cast to ints. C# compiler expects auto-cast, so here's a fix.
                if (p.ParameterType == typeof(int) && inputs[i].RetType == typeof(float))
                {
                    Event.PersistentCallsList.Remove(c);

                    // convert float to int
                    inputs[i] = AddMethod(typeof(Mathf).FGetMethod("FloorToInt", typeof(float)), inputs[i]);

                    Event.PersistentCallsList.Add(c);
                }
                if (inputs[i].Const != null && p.ParameterType == typeof(bool) && inputs[i].RetType == typeof(int))
                {
                    inputs[i].Const.Type = PersistentArgumentType.Bool; // the C# compiler marks bools as ints, ez workaround
                }
                if (inputs[i].Const != null && p.ParameterType == typeof(object))
                {
                    inputs[i].Const.Type = inputs[i].Type;
                }

                arg.FSetString(p.ParameterType.AssemblyQualifiedName);
                arg.FSetArgType(p.ParameterType.ToArgType());
            }

            // connect args / set const args
            for (int i = 0; i < @params.Length; i++)
            {
                ParameterInfo p = @params[i];
                PersistentArgument arg = c.PersistentArguments[i];
                UltRet input = inputs[i];

                if (input.Const != null) // constant
                {
                    arg.FSetArgType(input.Type);
                    string t = arg.FGetString();
                    arg.Value = input.Const.Value;
                    arg.FSetArgType(input.RetType.ToArgType());
                    if (input.Const.Type != PersistentArgumentType.String)
                        arg.FSetString(t);
                }
                else if (input.H != null && input.Call != null)
                {
                    if (input.H == this)
                    {
                        arg.FSetArgType(PersistentArgumentType.ReturnValue);
                        arg.FSetInt(input.Index);
                    }
                    else throw new NotImplementedException($"Input to {m.Name} for param {i}:({p.ParameterType.Name} {p.Name}) requires evt data-transfer!");
                }
                else throw new Exception($"Input to {m.Name} for param {i}:({p.ParameterType.Name} {p.Name}) has no const or ret val!");
            }

            // for some reason, PersistentCall doesn't allways setup correctly on-invoke.
            // we gotta use a getter to finish setup
            var got = c.Method;

            return new UltRet(this, c);
        }
        private UltRet AddReflectionMethod(MethodInfo m, UltRet[] inputs) // handles non-constant instance calls
        {
            // we gotta re-do all the prior reflection, our way
            // first lets get the MethodInfo, within the ult-context

            // get method Declaring Type
            UltRet declaringType = FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(m.DeclaringType.AssemblyQualifiedName, true, true));
            if (m.IsGenericMethod)
                throw new NotImplementedException(UltComp.gameObject.GetPath() + " tried to run a generic method: " + m);
            else
            {
                // we'll use System.ComponentModel.MemberDescriptor.FindMethod(Type, string, Type[], Type, bool) -> MethodInfo

                // we need to get the input types as an array (via JsonConvert), and the return Type
                var returnType = FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(m.ReturnType.AssemblyQualifiedName, true, true));
                var tArr = FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(Type[]).AssemblyQualifiedName, true, true));

                string jsonInputTypes = "[";
                ParameterInfo[] mParams = m.GetParameters();
                if (mParams.Length > 0)
                    for (int i = 0; i < mParams.Length; i++)
                    {
                        ParameterInfo p = mParams[i];
                        if (i != (mParams.Length-1))
                            jsonInputTypes += $"\"{p.ParameterType.AssemblyQualifiedName}\", ";
                        else jsonInputTypes += $"\"{p.ParameterType.AssemblyQualifiedName}\"]";
                    }
                else jsonInputTypes += "]";

                var inputTypes = FindOrAddConstMethod(typeof(JsonConvert).FGetMethod("DeserializeObject", TA.R<string, Type>()), UltRet.Params(jsonInputTypes, tArr));

                var ultedMethodInfo =
                    FindOrAddConstMethod(FGMethod<System.ComponentModel.MemberDescriptor>("FindMethod", TA.R<Type, string, Type[], Type, bool>()),
                                            UltRet.Params(declaringType, m.Name, inputTypes, returnType, false));

                // now that we've ultified the method, we'll use System.SecurityUtils.MethodInfoInvoke(MethodInfo, object, object[])

                // gotta make and fill an object[] with our items
                var objType = FindOrAddConstMethod(FGMethod<Type>("GetType", TA.R<string, bool, bool>()), UltRet.Params(typeof(object).AssemblyQualifiedName, true, true));
                var ultedParamsArray = FindOrAddConstMethod(FGMethod<Array>("CreateInstance", TA.R<Type, int>()), UltRet.Params(objType, inputs.Length - 1));

                for (int i = 1; i < inputs.Length; i++)
                    AddMethod(typeof(CompileUtils).GetMethod("ArrayItemSetter1", TA.R<Array, int, object>()), UltRet.Params(ultedParamsArray, i-1, inputs[i]));

                // hope we get a replacement to this next patch ...
                Type secureUtils = Type.GetType("System.SecurityUtils, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", true, true);
                return AddMethod(secureUtils.FGetMethod("MethodInfoInvoke", TA.R<MethodInfo, object, object[]>()), UltRet.Params(ultedMethodInfo, inputs[0], ultedParamsArray));

                //AddMethod(FGMethod<Debug>("Log", TA.R<string>()), ultedMethodInfo);
            }
        }

        public UltRet FindOrAddConstMethod(MethodInfo m, params UltRet[] inputs)
        {
            var origarr = inputs;
            if (m.IsStatic)
            {
                inputs = inputs.Reverse().ToArray();
                for (int i1 = 0; i1 < Event.PersistentCallsList.Count; i1++)
                {
                    PersistentCall pcall = Event.PersistentCallsList[i1];
                    if (pcall.Method == m)
                    {
                        bool samey = true;
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            PersistentArgument pArg = pcall.PersistentArguments[i];
                            UltRet @const = inputs[i];

                            bool equal;
                            if (@const.Const != null)
                            {
                                pArg.FSetValue(null);
                                equal = (pArg.Value == null && @const.Const.Value == null);
                                if (!equal) equal = pArg.Value.Equals(@const.Const.Value);
                            } else
                                equal = @const.Index == pArg.ReturnedValueIndex;

                            if (!equal)
                            {
                                samey = false;
                                break;
                            }
                        }
                        if (!samey) continue;
                        else return new UltRet(this, pcall);
                    }
                }
            } else
            {
                var t = inputs[0].Const.ConstObject;
                inputs = inputs.Reverse().ToArray()[1..];
                for (int i1 = 0; i1 < Event.PersistentCallsList.Count; i1++)
                {
                    PersistentCall pcall = Event.PersistentCallsList[i1];
                    if (pcall.Method == m && pcall.Target == t)
                    {
                        bool samey = true;
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            PersistentArgument pArg = pcall.PersistentArguments[i];
                            UltRet @const = inputs[i];

                            bool equal;
                            if (@const.Const != null)
                            {
                                pArg.FSetValue(null);
                                equal = (pArg.Value == null && @const.Const.Value == null);
                                if (!equal) equal = pArg.Value.Equals(@const.Const.Value);
                            }
                            else
                                equal = @const.Index == pArg.ReturnedValueIndex;

                            if (!equal)
                            {
                                samey = false;
                                break;
                            }
                        }
                        if (!samey) continue;
                        else return new UltRet(this, pcall);
                    }
                }
            }
            return AddMethod(m, origarr);
        }


        public static MethodInfo Cooltest() => ScriptCompiler.FGMethod<Transform>("SetParent", TA.R<Transform, bool>());

        public override bool Equals(object obj)
        {
            if (obj is UltEventHandle h) return h == this;
            else return base.Equals(obj);
        }
        public override int GetHashCode()
        {
            return (UltComp.GetHashCode() + UltField.GetHashCode())/2;
        }
        public static bool operator ==(UltEventHandle a, UltEventHandle b)
        {
            if (ReferenceEquals(a, null))
                return ReferenceEquals(b, null);
            if (ReferenceEquals(b, null))
                return false;

            return a.UltComp == b.UltComp && a.UltField == b.UltField;
        }
        public static bool operator !=(UltEventHandle a, UltEventHandle b) => !(a == b);
    }

    [Serializable]
    public class UltRet : ICloneable
    {
        public static UltRet[] Params(params object[] args) // takes in hard-set params for ultevents, reverses them for useability
        {
            UltRet[] o = new UltRet[args.Length];
            for (int i = 0; i < args.Length; i++)
                if (args[(args.Length-1) - i] is UltRet ur)
                    o[i] = ur;
                else o[i] = new UltRet(args[(args.Length - 1) - i]);
            return o;
        }

        public UltRet(UltEventHandle handle, PersistentCall call)
        {
            H = handle; Call = call;
        }
        public UltRet(object @const)
        {
            Const = new ConstInput();
            Const.Type = @const.GetType().ToArgType();
            Const.Value = @const;
        }
        public UltRet() { }

        public Type RetType => ReturnTypeOverride ?? 
            (Call?.Method.GetReturnType() ?? Const?.Type.ToType() ?? H.Event.GetType().GetMethods(UltEventUtils.AnyAccessBindings).First(m => m.Name == "Invoke").GetParameters()[Param.Value].ParameterType);
        public Type ReturnTypeOverride;
        public PersistentArgumentType Type => ReturnTypeOverride?.ToArgType() ?? (Const != null ? Const.Type : Param.HasValue ? PersistentArgumentType.Parameter : PersistentArgumentType.ReturnValue);
        public UltEventHandle H;
        public PersistentCall Call;
        public int? Param;
        public bool Self;
        public int Index => Param.HasValue ? Param.Value : H.Event.PersistentCallsList.IndexOf(Call);

        public ConstInput Const;

        public object Clone()
        {
            var c = (UltRet)this.MemberwiseClone();
            if (c.Const != null)
                c.Const = (ConstInput)Const.Clone();
            return c;
        }
        public UltRet OverrideRetType(Type t)
        {
            ReturnTypeOverride = t;
            return this;
        }
    }

    [Serializable]
    public class ConstInput : ICloneable
    {
        public object Value
        {
            get
            {
                switch (Type)
                {
                    case PersistentArgumentType.Bool:
                        return this.ConstBool;
                    case PersistentArgumentType.String:
                        return this.ConstString;
                    case PersistentArgumentType.Int:
                        return this.ConstInt;
                    case PersistentArgumentType.Float:
                        return this.ConstFloat;
                    case PersistentArgumentType.Vector2:
                        return this.ConstVector2;
                    case PersistentArgumentType.Vector3:
                        return this.ConstVector3;
                    case PersistentArgumentType.Vector4:
                        return this.ConstVector4;
                    case PersistentArgumentType.Quaternion:
                        return this.ConstQuaternion;
                    case PersistentArgumentType.Color:
                        return this.ConstColor;
                    case PersistentArgumentType.Color32:
                        return this.ConstColor32;
                    case PersistentArgumentType.Rect:
                        return this.ConstRect;
                    case PersistentArgumentType.Object:
                        return this.ConstObject;
                    default:
                        throw new NotImplementedException();
                }
            }
            set
            {
                switch (Type)
                {
                    case PersistentArgumentType.Bool:
                        this.ConstBool = (bool)value; return;
                    case PersistentArgumentType.String:
                        this.ConstString = (string)value; return;
                    case PersistentArgumentType.Int:
                        this.ConstInt = (int)value; return;
                    case PersistentArgumentType.Float:
                        this.ConstFloat = (float)value; return;
                    case PersistentArgumentType.Vector2:
                        this.ConstVector2 = (Vector2)value; return;
                    case PersistentArgumentType.Vector3:
                        this.ConstVector3 = (Vector3)value; return;
                    case PersistentArgumentType.Vector4:
                        this.ConstVector4 = (Vector4)value; return;
                    case PersistentArgumentType.Quaternion:
                        this.ConstQuaternion = (Quaternion)value; return;
                    case PersistentArgumentType.Color:
                        this.ConstColor = (Color)value; return;
                    case PersistentArgumentType.Color32:
                        this.ConstColor32 = (Color32)value; return;
                    case PersistentArgumentType.Rect:
                        this.ConstRect = (Rect)value; return;
                    case PersistentArgumentType.Object:
                        this.ConstObject = (UnityEngine.Object)value; return;
                    default:
                        throw new NotImplementedException($"No Const Type for {value}!");
                }
            }
        }
        public PersistentArgumentType   Type;

        public string ConstString;
        public int ConstInt;
        public bool ConstBool;
        public float ConstX;
        public float ConstY;
        public float ConstZ;
        public float ConstW;
        public UnityEngine.Object ConstObject;

        public float ConstFloat { get => ConstX; set => ConstX = value; }
        public Vector2 ConstVector2 { get => new(ConstX, ConstY); set { ConstX = value.x; ConstY = value.y; } }
        public Vector3 ConstVector3 { get => new(ConstX, ConstY, ConstZ); set { ConstX = value.x; ConstY = value.y; ConstZ = value.z; } }
        public Vector4 ConstVector4 { get => new(ConstX, ConstY, ConstZ, ConstW); set { ConstX = value.x; ConstY = value.y; ConstZ = value.z; ConstW = value.w; } }
        public Quaternion ConstQuaternion { get => new(ConstX, ConstY, ConstZ, ConstW); set { ConstX = value.x; ConstY = value.y; ConstZ = value.z; ConstW = value.w; } }
        public Color ConstColor { get => new(ConstX, ConstY, ConstZ, ConstW); set { ConstX = value.r; ConstY = value.g; ConstZ = value.b; ConstW = value.a; } }
        public Color32 ConstColor32 { get => new((byte)(ConstInt & 0xFF), (byte)((ConstInt >> 8) & 0xFF), (byte)((ConstInt >> 16) & 0xFF), (byte)((ConstInt >> 24) & 0xFF)); 
            set { ConstInt = (value.r & (value.g << 8) & (value.b << 16) & (value.a << 24)); } }
        public Rect ConstRect { get => new(ConstX, ConstY, ConstZ, ConstW); set { ConstX = value.x; ConstY = value.y; ConstZ = value.width; ConstW = value.height; } }

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }

    [Serializable]
    public class CompVar
    {
        public CompVar(GameObject root, Type type, object defaultValue)
        {
            foreach (KeyValuePair<Type, (Type storager, PropertyInfo accessor)> cs in CompileUtils.CompStoragers)
            {
                if (cs.Key.IsAssignableFrom(type))
                {
                    var varInfo = cs.Value;
                    Comp = root.AddComponent(varInfo.storager);
                    PropName = varInfo.accessor.Name;
                    Get = varInfo.accessor.GetMethod;
                    Set = varInfo.accessor.SetMethod;
                    Type = type;
                    if (defaultValue != null)
                        Set.Method.Invoke(Comp, new object[] { defaultValue });
                    return;
                }
            }
            throw new NotImplementedException($"Attempted to store a {type.Name} variable on {root.GetPath()} ");
        }

        public Component Comp;
        public string PropName;
        public SerializedMethod Get;
        public SerializedMethod Set;
        public SerializedType Type;
        public object Value
        {
            get => Get.Method.Invoke(Comp, null);
            set => Set.Method.Invoke(Comp, new object[] { value });
        }

        private Component _ultswapEvt;
        public Component GetUltSwapEvt()
        {
            if (_ultswapEvt) return _ultswapEvt;
            _ultswapEvt = new GameObject("OnChange", typeof(UltEventHolder)).GetComponent<UltEventHolder>();
            _ultswapEvt.transform.SetParent(Comp.transform, false);
            return _ultswapEvt;
        }
    }
}
#endif