#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UltEvents;
using UltSharpCustomReader;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.UI;

namespace UltSharp
{
    public static class CompileUtils
    {
        public static void FSetMethodName(this PersistentCall call, string methodName)
        {
            call.FSetMethod(null);
            _mn.SetValue(call, methodName);
        }
        public static void FSetMethod(this PersistentCall call, MethodInfo method) => _m.SetValue(call, method);
        public static void FSetArgs(this PersistentCall call, params PersistentArgument[] args) => _pargs.SetValue(call, args);
        public static void FSetString(this PersistentArgument arg, string str) => _pargstr.SetValue(arg, str);
        public static string FGetString(this PersistentArgument arg) => (string)_pargstr.GetValue(arg);
        public static void FSetArgType(this PersistentArgument arg, PersistentArgumentType t) => _pargt.SetValue(arg, t);
        public static void FSetInt(this PersistentArgument arg, int i) => _pargi.SetValue(arg, i);
        public static void FSetObject(this PersistentArgument arg, UnityEngine.Object obj) => _pargo.SetValue(arg, obj);
        public static void FSetValue(this PersistentArgument arg, object v) => _pargv.SetValue(arg, v);
        public static void FSetTarget(this PersistentCall call, UnityEngine.Object t) => _callTarg.SetValue(call, t);

        private static FieldInfo _mn = typeof(PersistentCall).FGetField("_MethodName");
        private static FieldInfo _m = typeof(PersistentCall).FGetField("_Method");
        private static FieldInfo _pargs = typeof(PersistentCall).FGetField("_PersistentArguments");
        private static FieldInfo _pargstr = typeof(PersistentArgument).FGetField("_String");
        private static FieldInfo _pargt = typeof(PersistentArgument).FGetField("_Type");
        private static FieldInfo _pargi = typeof(PersistentArgument).FGetField("_Int");
        private static FieldInfo _pargo = typeof(PersistentArgument).FGetField("_Object");
        private static FieldInfo _pargv = typeof(PersistentArgument).FGetField("_Value");
        private static FieldInfo _callTarg = typeof(PersistentCall).FGetField("_Target");

        public static FieldInfo FGetField(this Type t, string fieldName) => t.GetField(fieldName, UltEventUtils.AnyAccessBindings);
        public static MethodInfo FGetMethod(this Type t, string methodName, params Type[] types) => t.GetMethod(methodName, UltEventUtils.AnyAccessBindings, null, types, null);
        public static PropertyInfo FGetProperty(this Type t, string propName) => t.GetProperty(propName, UltEventUtils.AnyAccessBindings);

        // Used to configure a PersistentArgument so that it accepts Type t
        public static PersistentArgumentType ToArgType(this Type t)
        {
            if (t == typeof(bool)) return PersistentArgumentType.Bool;
            if (t == typeof(string)) return PersistentArgumentType.String;
            if (t == typeof(int)) return PersistentArgumentType.Int;
            if (t.IsEnum) return PersistentArgumentType.Enum;
            if (t == typeof(float)) return PersistentArgumentType.Float;
            if (t == typeof(Vector2)) return PersistentArgumentType.Vector2;
            if (t == typeof(Vector3)) return PersistentArgumentType.Vector3;
            if (t == typeof(Vector4)) return PersistentArgumentType.Vector4;
            if (t == typeof(Quaternion)) return PersistentArgumentType.Quaternion;
            if (t == typeof(Color)) return PersistentArgumentType.Color;
            if (t == typeof(Color32)) return PersistentArgumentType.Color32;
            if (t == typeof(Rect)) return PersistentArgumentType.Rect;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return PersistentArgumentType.Object;
            else return PersistentArgumentType.None; // Return Values & Params are setup later, usually
        }
        public static Type ToType(this PersistentArgumentType t)
        {
            switch (t)
            {
                case PersistentArgumentType.Bool:
                    return typeof(bool);
                case PersistentArgumentType.String:
                    return typeof(string);
                case PersistentArgumentType.Int:
                    return typeof(int);
                case PersistentArgumentType.Enum:
                    return typeof(Enum);
                case PersistentArgumentType.Float:
                    return typeof(float);
                case PersistentArgumentType.Vector2:
                    return typeof(Vector2);
                case PersistentArgumentType.Vector3:
                    return typeof(Vector3);
                case PersistentArgumentType.Vector4:
                    return typeof(Vector4);
                case PersistentArgumentType.Quaternion:
                    return typeof(Quaternion);
                case PersistentArgumentType.Color:
                    return typeof(Color);
                case PersistentArgumentType.Color32:
                    return typeof(Color32);
                case PersistentArgumentType.Rect:
                    return typeof(Rect);
                case PersistentArgumentType.Object:
                    return typeof(UnityEngine.Object);
                case PersistentArgumentType.Parameter:
                    return typeof(System.Object);
                case PersistentArgumentType.ReturnValue:
                    return typeof(System.Object);
            }
            throw new NotImplementedException();
        }

        public static string GetPath(this GameObject obj)
        {
            string path = obj.name;
            Transform current = obj.transform;

            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }
        public static void PrintILs(this IL[] ils)
        {
            foreach (var il in ils)
            {
                string operand = il.Operand?.ToString() ?? "null";
                if (il.Operand is byte[] b)
                    operand = SerializedOpCode.RawDeserialize<int>(b, 0).ToString();

                string labels = "[ ";
                foreach (var l in il.Labels)
                    labels += l + " ";
                labels += "]";

                Debug.Log($"{labels}{il.OpCode}    {operand}");
            }
        }

        private static Assembly _xrAssmb;
        public static Assembly XRAssembly => _xrAssmb ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(ass => ass.FullName.StartsWith("Unity.XR.Interaction.Toolkit"));
        public static Type GetExtType(string name, Assembly ass = null)
        {
            foreach (Type t in ass.GetTypes())
                try
                {
                    if (t.Name == name) return t;
                }
                catch (TypeLoadException) { }
            return null;
        }
        public static Dictionary<Type, (Type, PropertyInfo)> CompStoragers = new Dictionary<Type, (Type, PropertyInfo)>()
        {
            { typeof(UnityEngine.Object), (GetExtType("XRInteractorAffordanceStateProvider", XRAssembly), GetExtType("XRInteractorAffordanceStateProvider", XRAssembly).GetProperty("interactorSource", UltEventUtils.AnyAccessBindings)) },
            { typeof(float), (typeof(AspectRatioFitter), typeof(AspectRatioFitter).GetProperty("aspectRatio", UltEventUtils.AnyAccessBindings)) },
            { typeof(Material[]), (typeof(MeshRenderer), typeof(MeshRenderer).GetProperty("sharedMaterials", UltEventUtils.AnyAccessBindings)) },
            { typeof(bool), (typeof(Mask), typeof(Mask).GetProperty("enabled")) },
            { typeof(Vector3), (typeof(PositionConstraint), typeof(PositionConstraint).GetProperty(nameof(PositionConstraint.translationOffset))) },
            { typeof(string), (typeof(Text), typeof(Text).GetProperty("text", UltEventUtils.AnyAccessBindings)) },
            { typeof(int), (typeof(MeshRenderer), typeof(Renderer).GetProperty("rendererPriority", UltEventUtils.AnyAccessBindings)) },
            { typeof(Vector2), (typeof(RectTransform), typeof(RectTransform).GetProperty("sizeDelta", UltEventUtils.AnyAccessBindings)) }
        };


        // todo: make mbh swap these to SLZ's ingame variant
        public static bool GreaterThan(float a, float b) {  return a > b; }
        public static bool LessThan(float a, float b) {  return a < b; }
        public static void ArrayItemSetter1(Array array, int idx, object obj) => array.SetValue(obj, idx);
    }

    public static class TA
    {
        public static Type[] R() => new Type[0];
        public static Type[] R<A>() => new Type[] { typeof(A) };
        public static Type[] R<A, B>() => new Type[] { typeof(A), typeof(B) };
        public static Type[] R<A, B, C>() => new Type[] { typeof(A), typeof(B), typeof(C) };
        public static Type[] R<A, B, C, D>() => new Type[] { typeof(A), typeof(B), typeof(C), typeof(D) };
        public static Type[] R<A, B, C, D, E>() => new Type[] { typeof(A), typeof(B), typeof(C), typeof(D), typeof(E) };
        public static Type[] R<A, B, C, D, E, F>() => new Type[] { typeof(A), typeof(B), typeof(C), typeof(D), typeof(E), typeof(F) };
        public static Type[] R<A, B, C, D, E, F, G>() => new Type[] { typeof(A), typeof(B), typeof(C), typeof(D), typeof(E), typeof(F), typeof(G) };
        public static Type[] R<A, B, C, D, E, F, G, H>() => new Type[] { typeof(A), typeof(B), typeof(C), typeof(D), typeof(E), typeof(F), typeof(G), typeof(H) };

    }

    public partial class ScriptCompiler
    {
        public static MethodInfo FGMethod<T>(string name, params Type[] types)
        {
            var o = typeof(T).GetMethod(name, UltEventUtils.AnyAccessBindings, null, types, null);
            if (o == null)
            {
                foreach (var member in typeof(T).GetMembers(UltEventUtils.AnyAccessBindings))
                {
                    if (member is MethodInfo m && m.Name == name)
                    {
                        var @params = m.GetParameters();
                        if (@params.Length != types.Length) continue;
                        bool match = true;
                        for (int i = 0; i < @params.Length; i++)
                        {
                            if (@params[i].ParameterType != types[i])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                            return m;
                    }
                }

                string er = $"Unable to FGMethod: {typeof(T).Name}.{name}, with params: ";
                foreach (var t in types)
                {
                    er += t.AssemblyQualifiedName + ", ";
                }
                throw new Exception(er);
            }
            return o;
        }
        public static PropertyInfo FGProp<T>(string name) => typeof(T).GetProperty(name, UltEventUtils.AnyAccessBindings);
        public static FieldInfo FGField<T>(string name) => typeof(T).GetField(name, UltEventUtils.AnyAccessBindings);
    }
}
#endif