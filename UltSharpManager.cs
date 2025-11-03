#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using UltEvents;
using UltSharp.EditorTestingUtilities;
using UltSharpCustomReader;
using UnityEditor;
using UnityEngine;

namespace UltSharp
{
    [InitializeOnLoad]
    public static class UltSharpManager
    {
        private static string CustomProjFolder => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "UltSharpCustom").Replace("/", "\\");
        private static string ThisProjFolder => Path.Combine(Application.dataPath, "UltSharp").Replace("/", "\\");
        private static string CustomAssemblyFolder => Path.Combine(CustomProjFolder, "bin", "x64", "Debug").Replace("/", "\\");
        private static string CustomAssemblyPath => Path.Combine(CustomAssemblyFolder, "UltSharpCustom.dll").Replace("/", "\\");
        private static string ReaderAssemblyPath => Path.Combine(ThisProjFolder, "UltSharpCustomReader.dll").Replace("/","\\");
        private static string HarmonyAssemblyPath => Path.Combine(ThisProjFolder, "0Harmony2.dll").Replace("/", "\\");
        private static string CustomProjZip => Path.Combine(ThisProjFolder, "UltSharpCustom.zip").Replace("/", "\\");
        private static string ScriptAssembliesPath => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "ScriptAssemblies");
        private static string UnityAssembliesPath => Path.Combine(Directory.GetParent(EditorApplication.applicationPath).FullName, "Data", "Managed", "UnityEngine");

        // on Unity Load
        static UltSharpManager()
        {
            if (!Directory.Exists(CustomProjFolder)) 
                return;

            // build hook.
            Type buildHookType = Type.GetType("MarrowBuildHook.MarrowBuildHook, MarrowBuildHook");
            if (buildHookType != null)
            {
                Debug.Log("can hookk"); 
                var softCallbacks = (List<Action<IEnumerable<GameObject>>>)buildHookType.GetField("ExternalGameObjectProcesses").GetValue(null);
                softCallbacks.Add((numer) =>
                {
                    foreach (var gameObject in numer)
                    {
                        foreach (var scr in gameObject.GetComponentsInChildren<UltSharpScript>(true))
                            UnityEngine.Object.DestroyImmediate(scr);
                        foreach (var scr in gameObject.GetComponentsInChildren<IfBlockRunner>(true))
                            UnityEngine.Object.DestroyImmediate(scr);

                        foreach (var script in gameObject.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (script == null) continue;

                            List<FieldInfo> fields = null;
                            if (!TypeToUltFields.TryGetValue(script.GetType(), out fields))
                            {
                                fields = new List<FieldInfo>();
                                foreach (var f in script.GetType().GetFields((BindingFlags)60))
                                {
                                    if (typeof(IUltEventBase).IsAssignableFrom(f.FieldType))
                                        fields.Add(f);
                                }

                                Type @base = script.GetType();
                                while (@base != typeof(MonoBehaviour))
                                {
                                    @base = @base.BaseType;
                                    foreach (var f in @base.GetFields((BindingFlags)60))
                                    {
                                        if (typeof(IUltEventBase).IsAssignableFrom(f.FieldType))
                                            fields.Add(f);
                                    }
                                }

                                TypeToUltFields[script.GetType()] = fields;
                            }

                            foreach (var ultField in fields)
                            {
                                var evt = (UltEventBase)ultField.GetValue(script);
                                if (evt?.PersistentCallsList != null)
                                    foreach (var pcall in evt.PersistentCallsList)
                                    {
                                        if (pcall.MethodName == "UltSharp.CompileUtils, UltSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null.ArrayItemSetter1")
                                            pcall.FSetMethodName("System.Linq.Expressions.Interpreter.CallInstruction, System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e.ArrayItemSetter1");
                                        else if (pcall.MethodName == "UltSharp.CompileUtils, UltSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null.GreaterThan")
                                            pcall.FSetMethodName("SLZ.Bonelab.VoidLogic.MathUtilities, Assembly-CSharp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null.IsApproximatelyEqualToOrGreaterThan");
                                        else if (pcall.MethodName == "UltSharp.CompileUtils, UltSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null.LessThan")
                                            pcall.FSetMethodName("SLZ.Bonelab.VoidLogic.MathUtilities, Assembly-CSharp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null.IsApproximatelyEqualToOrLessThan");
                                    }
                            }

                        }
                    }
                });
            }
            
            init();
        }
        private static Dictionary<Type, List<FieldInfo>> TypeToUltFields = new();

        private static bool hasInit;
        private static void init()
        {
            if (hasInit) return;
            hasInit = true;

            // load up existing user compiled code
            HandleAssembly();

            // Watch for updates in user's compiled code
            var watcher = new FileSystemWatcher(CustomAssemblyFolder);

            watcher.NotifyFilter = NotifyFilters.Attributes
                                 | NotifyFilters.CreationTime
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.FileName
                                 | NotifyFilters.LastAccess
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Security
                                 | NotifyFilters.Size;

            watcher.Changed += OnChanged;
            watcher.Created += OnCreated;

            watcher.Filter = "UltSharpCustom.dll";
            watcher.EnableRaisingEvents = true;

            EditorApplication.update += Update;
        }


        // FileSystemWatcher runs on a background thread, so we'll move that to the foreground with this stuff
        private static ConcurrentQueue<Action> mainThread = new();
        private static void Update()
        {
            while (mainThread.TryDequeue(out var action))
                action.Invoke();
        }
        private static void OnChanged(object sender, FileSystemEventArgs e) => mainThread.Enqueue(HandleAssembly);
        private static void OnCreated(object sender, FileSystemEventArgs e) => mainThread.Enqueue(HandleAssembly);


        
        private static AppDomain LoadedDomain;
        private static void HandleAssembly() // handles the fetching of user-code from the hotswapped dll
        {
            if (LoadedDomain != null) // teardown old user code
            {
                AppDomain.Unload(LoadedDomain);
                LoadedDomain = null;
                ILScripts.Clear(); 
            }

            if (!File.Exists(CustomAssemblyPath))
                return;

            // make a new domain
            try
            {
                LoadedDomain = AppDomain.CreateDomain("UserUltAssemblies", null, new AppDomainSetup() { ApplicationBase = CustomAssemblyFolder, ShadowCopyFiles = "true" });
                LoadedDomain.Load(new AssemblyName() { CodeBase = HarmonyAssemblyPath }); // add harmony
                LoadedDomain.Load(new AssemblyName() { CodeBase = ReaderAssemblyPath });  // add user code reader
                
                var userCodeReader = (UltSharpReflector)LoadedDomain.CreateInstanceAndUnwrap("UltSharpCustomReader, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", typeof(UltSharpReflector).FullName);

                // make the "user code reader" send us their compiled code
                // we use the "user code reader" so that we never have to load user code into unity for-realsies, since that's laggy
                ILScripts = userCodeReader.SerializeScripts(CustomAssemblyPath).ToDictionary(s => s.Name);

                foreach (var item in ILScripts)
                    Debug.Log("Parsed UltScript: " + item.Key); 

                ScriptsReloaded.Invoke();
                 
                foreach (var script in Resources.FindObjectsOfTypeAll<UltSharpScript>())
                {
                    if (!script) continue;
                    try
                    {
                        script.LastScript = null;
                        script.RefreshScript(true);
                        if (script.LastScript == null) Debug.LogError("Failed to load UltScript: " + script.ScriptIdentifier);
                        Debug.Log($"Compiled UltScript \"{script.LastScript.Name}\" on GameObject \"{script.gameObject.GetPath()}\"");
                    }
                    catch (Exception ex) 
                    {
                        Debug.LogError($"Failed to Compile UltScript \"{script.LastScript?.Name}\"! Error:");
                        Debug.LogException(ex);
                    }
                }
                foreach (var insp in Resources.FindObjectsOfTypeAll<UltSharpScriptInspector>())
                    insp.GenerateUI();
            }
            catch (BadImageFormatException ex) 
            { 
                Debug.LogException(ex);
                Debug.Log(ex.FusionLog);
            }
        }
        

        //                   script name  script
        public static Dictionary<string, SerializedScript> ILScripts = new();
        public static Action ScriptsReloaded = delegate { };
         

        [MenuItem("UltSharp/Open Project Folder")]
        private static void OpenProjFolder()
        {
            if (!Directory.Exists(CustomProjFolder))
            {
                Directory.CreateDirectory(CustomProjFolder);
                ZipFile.ExtractToDirectory(CustomProjZip, CustomProjFolder);
                string csProj = File.ReadAllText(Path.Combine(CustomProjFolder, "UltSharpCustom.csproj"));
                string addedRefs = "";
                string refFormat = "    <Reference Include=\"%NAME%\">\n" +
                                   "      <HintPath>%REF_PATH%</HintPath>\n" +
                                   "    </Reference>\n";

                foreach (var item in Directory.EnumerateFiles(ScriptAssembliesPath, "*.dll"))
                {
                    string newEntry = refFormat.Replace("%REF_PATH%", item.Replace("/", "\\")).Replace("%NAME%", Path.GetFileNameWithoutExtension(item));
                    addedRefs += newEntry;
                }
                foreach (var item in Directory.EnumerateFiles(UnityAssembliesPath, "*.dll"))
                {
                    string newEntry = refFormat.Replace("%REF_PATH%", item.Replace("/", "\\")).Replace("%NAME%", Path.GetFileNameWithoutExtension(item));
                    addedRefs += newEntry;
                }
                csProj = csProj.Replace("%MOREHERE%", addedRefs);
                File.WriteAllText(Path.Combine(CustomProjFolder, "UltSharpCustom.csproj"), csProj);

                init();
            }


            System.Diagnostics.Process.Start("explorer.exe", "/select," + Path.Combine(CustomProjFolder, "UltSharpCustom.sln"));

        }
    }
}

#endif