#if UNITY_EDITOR
using System;
using System.Linq;
using UltEvents;
using UltSharp.EditorTestingUtilities;
using UltSharpCustomReader;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UltSharp
{
    [CustomEditor(typeof(UltSharpScript))]
    class UltSharpScriptInspector : Editor
    {
        public VisualTreeAsset m_InspectorUXML;
        private DropdownField ScriptSelection;
        private UltSharpScript ScriptComponent;
        VisualElement root;
        VisualElement root2;

        public override VisualElement CreateInspectorGUI()
        {
            ScriptComponent = (UltSharpScript)this.target;

            if (!m_InspectorUXML)
                m_InspectorUXML = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UltSharpManager.InPackage ? "Packages/UltSharp/UI/UltSharpScriptInspectorUI.uxml"
                    : "Assets/UltSharp/UI/UltSharpScriptInspectorUI.uxml");
            VisualElement myInspector = m_InspectorUXML.Instantiate();

            root = new VisualElement();
            myInspector.Add(root);
            root.SendToBack();

            root2 = new VisualElement();
            myInspector.Add(root2);
            root2.BringToFront();

            PropsRoot = myInspector.Q("PublicVarsRoot");

            GenerateUI();

            UltSharpManager.ScriptsReloaded += GenerateUI;

            return myInspector;
        }

        public void GenerateUI()
        {
            if (root == null) return;
            root.Clear();

            var topHorizontal = new VisualElement();
            root.Add(topHorizontal);
            topHorizontal.style.flexDirection = FlexDirection.Row;

            ScriptSelection = new DropdownField() { label = "UltScript:" };
            ScriptSelection.choices = UltSharpManager.ILScripts.Keys?.ToList();
            ScriptSelection.value = ScriptComponent.ScriptIdentifier;
            //ScriptSelection.style.color = Color.blue;
            topHorizontal.Add(ScriptSelection);
            ScriptSelection.bindingPath = "ScriptIdentifier";
            ScriptSelection.Bind(new SerializedObject(ScriptComponent));

            ScriptSelection.RegisterValueChangedCallback(e => { if (e.newValue != ScriptComponent.ScriptIdentifier) { GenerateUI(); ScriptComponent.RefreshScript(true); } });


            ScriptComponent.RefreshScript(false);
            if (ScriptComponent.LastScript == null)
            {
                var err = new TextElement() { text = $"Error: Couldn't find UltScript \"{ScriptComponent.ScriptIdentifier}\", Check your UltSharpCustom Project!" };
                err.style.color = Color.red;
                root.Add(err);
                return;
            }

            var CompileBT = new Button();
            CompileBT.text = "Compile/Refresh Ults";
            CompileBT.clicked += () => { ScriptComponent.RefreshScript(true); GenerateUI(); };
            topHorizontal.Add(CompileBT);

            DrawProperties();


            root2.Clear();
            for (int i = 0; i < ScriptComponent.LastMethodNames.Length; i++)
            {
                string name = ScriptComponent.LastMethodNames[i];
                UltEventHandle method = ScriptComponent.LastMethodActions[i];

                Button b = null;


                root2.Add(b = new Button(() =>
                {
                    IfBlockRunner.Active = true;

                    System.Diagnostics.Stopwatch stopWatch = new();
                    stopWatch.Start();
                    method.Event.DynamicInvoke();
                    stopWatch.Stop();
                    IfBlockRunner.Active = false;

                    b.text = $"{ScriptComponent.ScriptIdentifier}.{name}() -> {stopWatch.ElapsedTicks * 0.0001f}ms";
                })
                { text = ScriptComponent.ScriptIdentifier + "." + name + "()" });
            }

        }

        private void OnDestroy()
        {
            UltSharpManager.ScriptsReloaded -= GenerateUI;
        }

        private VisualElement PropsRoot;
        private void DrawProperties()
        {
            PropsRoot.Clear();

            for (int i = 0; i < ScriptComponent.LastVariableStores.Length; i++)
            {
                Field field = ScriptComponent.LastVariableFields[i];
                CompVar store = ScriptComponent.LastVariableStores[i];

                if (field.IsPublic && !field.IsStatic && !field.IsUltSwap && store.Comp)
                {
                    var userOverride = ScriptComponent.UserVarOverrides.FirstOrDefault(vo => vo.Name == field.Name);

                    VisualElement v;
                    PropsRoot.Add(
                        v = GetFieldForType(field, field.Name, store.Comp, store.PropName, userOverride != null ? userOverride.Value.Value : field.DefaultValue)
                    );
                    if (v is BindableElement b)
                    {
                        if (b.binding == null)
                        {
                        }
                    }
                }
            }
        }

        private VisualElement GetFieldForType(Field field, string fieldName, UnityEngine.Object storager, string binding, object @default)
        {
            string _fName = fieldName;
            Type fieldType = field.FieldType;
            void makeOverride(object value)
            {
                var @override = ScriptComponent.UserVarOverrides.FirstOrDefault(vo => vo.Name == _fName);

                if (@override == null)
                {
                    @override = new UltSharpScript.UserVarOverride();
                    @override.Name = _fName;
                    @override.Value = new ConstInput();
                    @override.Value.Type = value?.GetType().ToArgType() ?? PersistentArgumentType.Object;
                    ScriptComponent.UserVarOverrides = ScriptComponent.UserVarOverrides.Append(@override).ToArray();
                }

                @override.Value.Value = value;

                if (field.IsRuntimeStatic)
                    ScriptCompiler.Compile(ScriptComponent);
            }

            if (fieldType == typeof(string))
            {
                var o = new TextField(fieldName, 999999, true, false, 'a');
                o.bindingPath = binding;
                o.BindProperty(new SerializedObject(storager).FindProperty("m_Text"));
                o.value = (string)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(int))
            {
                var o = new IntegerField(fieldName);
                o.bindingPath = binding;
                o.BindProperty(new SerializedObject(storager).FindProperty("m_CullingMask"));
                o.value = (int)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(float))
            {
                var o = new FloatField(fieldName);
                o.bindingPath = binding;
                o.BindProperty(new SerializedObject(storager).FindProperty("m_AspectRatio"));
                o.value = (float)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(bool))
            {
                var o = new Toggle(fieldName);
                o.bindingPath = binding;
                var sObj = new SerializedObject(storager);
                o.BindProperty(sObj.FindProperty("m_Enabled"));
                o.value = (bool)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(Vector2))
            {
                var o = new Vector2Field(fieldName);
                o.bindingPath = binding;
                o.Bind(new SerializedObject(storager));
                o.value = (Vector2)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(Vector3))
            {
                var o = new Vector3Field(fieldName);
                o.bindingPath = binding;
                o.Bind(new SerializedObject(storager));
                o.value = (Vector3)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(Vector4))
            {
                var o = new Vector4Field(fieldName);
                o.bindingPath = binding;
                o.Bind(new SerializedObject(storager));
                o.value = (Vector4)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(Color))
            {
                var o = new ColorField(fieldName);
                o.bindingPath = binding;
                o.Bind(new SerializedObject(storager));
                o.value = (Color)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (fieldType == typeof(Rect))
            {
                var o = new RectField(fieldName);
                o.bindingPath = binding;
                o.Bind(new SerializedObject(storager));
                o.value = (Rect)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
            {
                var o = new ObjectField(fieldName);
                o.objectType = fieldType;
                o.bindingPath = "m_InteractorSource";
                o.Bind(new SerializedObject(storager));
                o.value = (UnityEngine.Object)@default;
                o.RegisterValueChangedCallback((e) => { if (e.previousValue != e.newValue) makeOverride(e.newValue); });
                return o;
            }

            return new TextElement() { text = "Error: No UI for variable of type: " + fieldType.Name };
        }

    }
}
#endif