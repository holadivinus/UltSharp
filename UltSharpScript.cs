#if UNITY_EDITOR
using System;
using System.Linq;
using UltSharpCustomReader;
using UnityEngine;

namespace UltSharp
{
    [ExecuteAlways]
    public class UltSharpScript : MonoBehaviour
    {
        public string ScriptIdentifier = "ExampleUserBehaviour";

        public GameObject LastCompiledRoot;
        public SerializedScript LastScript;

        public Field[] LastVariableFields = new Field[0];
        public CompVar[] LastVariableStores = new CompVar[0];

        public string[] LastMethodNames = new string[0];
        public UltEventHandle[] LastMethodActions = new UltEventHandle[0];

        public UserVarOverride[] UserVarOverrides = new UserVarOverride[0];

        public void RefreshScript(bool recompile = false)
        {
            if (UltSharpManager.ILScripts.TryGetValue(ScriptIdentifier, out var newScript))
            {
                LastScript = newScript;
                if (recompile)
                    ScriptCompiler.Compile(this);
            }
            else
            {
                LastScript = null;
            }
        }

        [Serializable]
        public class UserVarOverride
        {
            public string Name;
            public ConstInput Value;
        }

        [ContextMenu("Print OpCodes")]
        void prinn()
        {
            foreach (var il in this.LastScript.Methods.First().Value)
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
    }
}
#endif