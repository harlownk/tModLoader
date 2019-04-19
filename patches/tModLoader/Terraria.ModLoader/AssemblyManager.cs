﻿using log4net;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Terraria.ModLoader.IO;
using Terraria.Utilities;

namespace Terraria.ModLoader
{
	//todo: further documentation
	internal static class AssemblyManager
	{
		private class LoadedMod
		{
			public TmodFile modFile;
			public BuildProperties properties;
			public string Name => modFile.name;

			public readonly List<LoadedMod> dependencies = new List<LoadedMod>();
			public readonly List<LoadedMod> dependents = new List<LoadedMod>();
			//list of weak dependencies that are not currently loaded
			//weak dependencies assume loadIndex 0 when they come into being
			public readonly ISet<string> weakDependencies = new HashSet<string>();

			public Assembly assembly;
			public List<Assembly> assemblies = new List<Assembly>();
			public long bytesLoaded = 0;

			private int loadIndex;
			private bool eacEnabled;

			private bool _needsReload = true;
			private bool NeedsReload {
				get => _needsReload;
				set {
					if (value && !_needsReload) loadIndex++;
					_needsReload = value;
				}
			}

			public bool HasEaC => File.Exists(properties.eacPath);

			private string AssemblyName => eacEnabled ? Name : Name + '_' + loadIndex;
			private string DllName(string dll) => eacEnabled ? dll : Name + '_' + dll + '_' + loadIndex;
			private string WeakDepName(string depName) => eacEnabled ? depName : depName + "_0";

			public void SetMod(LocalMod mod) {
				if (modFile == null ||
					modFile.version != mod.modFile.version ||
					!modFile.hash.SequenceEqual(mod.modFile.hash))
					SetNeedsReload();

				modFile = mod.modFile;
				properties = mod.properties;
			}

			private void SetNeedsReload() {
				NeedsReload = true;
				eacEnabled = false;

				foreach (var dep in dependents)
					dep.SetNeedsReload();
			}

			public void AddDependency(LoadedMod dep) {
				dependencies.Add(dep);
				dep.dependents.Add(this);
			}

			public bool CanEaC => eacEnabled ||
				!loadedAssemblies.ContainsKey(modFile.name) && dependencies.All(dep => dep.CanEaC);

			public void EnableEaC() {
				if (eacEnabled)
					return;

				SetNeedsReloadUnlessEaC();
				eacEnabled = true;

				//all dependencies need to have unmodified names
				foreach (var dep in dependencies)
					dep.EnableEaC();
			}

			private void SetNeedsReloadUnlessEaC() {
				if (!eacEnabled)
					NeedsReload = true;

				foreach (var dep in dependents)
					dep.SetNeedsReloadUnlessEaC();
			}

			public void UpdateWeakRefs() {
				foreach (var loaded in dependencies.Where(dep => weakDependencies.Remove(dep.Name))) {
					if (eacEnabled && !loaded.eacEnabled)
						loaded.EnableEaC();
					else if (loaded.AssemblyName != WeakDepName(loaded.Name))
						SetNeedsReload();
				}
			}

			public void LoadAssemblies() {
				if (!NeedsReload)
					return;

				try {
					using (modFile.EnsureOpen()) {
						foreach (var dll in properties.dllReferences)
							LoadAssembly(EncapsulateReferences(modFile.GetBytes("lib/" + dll + ".dll")));

						if (eacEnabled && HasEaC) //load the unmodified dll and EaC pdb
							assembly = LoadAssembly(modFile.GetModAssembly(), File.ReadAllBytes(properties.eacPath));
						else {
							var pdb = GetModPdb(out var imageDebugHeader);
							assembly = LoadAssembly(EncapsulateReferences(modFile.GetModAssembly(), imageDebugHeader), pdb);
						}
						NeedsReload = false;
					}
				}
				catch (Exception e) {
					e.Data["mod"] = Name;
					throw;
				}
			}

			private byte[] GetModPdb(out ImageDebugHeader header) {
				var fileName = modFile.GetModAssemblyFileName();

				// load a separate debug header to splice into the assembly (dll provided was precompiled and references non-cecil symbols)
				if (modFile.HasFile(fileName + ".cecildebugheader"))
					header = ImageDebugHeaderFile.Read(modFile.GetBytes(fileName + ".cecildebugheader"));
				else
					header = null;

				if (FrameworkVersion.Framework == Framework.Mono)
					fileName += ".mdb";
				else
					fileName = Path.ChangeExtension(fileName, "pdb");

				return modFile.GetBytes(fileName);
			}

			private byte[] EncapsulateReferences(byte[] code, ImageDebugHeader debugHeader = null) {
				if (eacEnabled && debugHeader == null)
					return code;

				var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(code), new ReaderParameters {
					AssemblyResolver = CecilAssemblyResolver
				});
				asm.Name.Name = EncapsulateName(asm.Name.Name);

				//randomize the module version id so that the debugger can detect it as a different module (even if it has the same content)
				if (FrameworkVersion.Framework == Framework.NetFramework)
					asm.MainModule.Mvid = Guid.NewGuid();

				foreach (var mod in asm.Modules)
					foreach (var asmRef in mod.AssemblyReferences)
						asmRef.Name = EncapsulateName(asmRef.Name);

				var ms = new MemoryStream();
				asm.Write(ms, new WriterParameters {
					SymbolWriterProvider = new DebugHeaderWriterProvider(debugHeader ?? asm.MainModule.GetDebugHeader())
				});
				cecilAssemblyResolver.RegisterAssembly(asm);
				return ms.ToArray();
			}

			private string EncapsulateName(string name) {
				if (name == Name)
					return AssemblyName;

				if (properties.dllReferences.Contains(name))
					return DllName(name);

				if (weakDependencies.Contains(name))
					return WeakDepName(name);

				foreach (var dep in dependencies) {
					var _name = dep.EncapsulateName(name);
					if (_name != name)
						return _name;
				}

				return name;
			}

			private Assembly LoadAssembly(byte[] code, byte[] pdb = null) {
				var asm = Assembly.Load(code, pdb);
				assemblies.Add(asm);
				loadedAssemblies[asm.GetName().Name] = asm;
				assemblyBinaries[asm.GetName().Name] = code;
				hostModForAssembly[asm] = this;
				bytesLoaded += code.LongLength + (pdb?.LongLength ?? 0);
				if (pdb != null && FrameworkVersion.Framework == Framework.Mono) {
					var cecilAssemblyDef = cecilAssemblyResolver.Resolve(asm.GetName().ToReference());
					MdbManager.RegisterMdb(cecilAssemblyDef.MainModule, pdb);
				}
				return asm;
			}
		}

		internal static void AssemblyResolveEarly(ResolveEventHandler handler) {
			var backingFieldName = FrameworkVersion.Framework == Framework.NetFramework ? "_AssemblyResolve" : "AssemblyResolve";
			var f = typeof(AppDomain).GetField(backingFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
			var a = (ResolveEventHandler)f.GetValue(AppDomain.CurrentDomain);
			f.SetValue(AppDomain.CurrentDomain, null);

			AppDomain.CurrentDomain.AssemblyResolve += handler;
			AppDomain.CurrentDomain.AssemblyResolve += a;
		}

		private static readonly IDictionary<string, LoadedMod> loadedMods = new Dictionary<string, LoadedMod>();
		private static readonly IDictionary<string, Assembly> loadedAssemblies = new ConcurrentDictionary<string, Assembly>();
		private static readonly IDictionary<string, byte[]> assemblyBinaries = new ConcurrentDictionary<string, byte[]>();
		private static readonly IDictionary<Assembly, LoadedMod> hostModForAssembly = new ConcurrentDictionary<Assembly, LoadedMod>();

		private static TerrariaCecilAssemblyResolver cecilAssemblyResolver = new TerrariaCecilAssemblyResolver();
		public static IAssemblyResolver CecilAssemblyResolver => cecilAssemblyResolver;

		private static bool assemblyResolveEventsAdded;
		internal static void InitAssemblyResolvers() {
			if (assemblyResolveEventsAdded)
				return;
			assemblyResolveEventsAdded = true;

			AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
				string name = new AssemblyName(args.Name).Name;

				if (name == "Terraria")
					return Assembly.GetExecutingAssembly();

				Assembly a;
				loadedAssemblies.TryGetValue(name, out a);
				return a;
			};

			// allow mods which reference embedded assemblies to reference a different version and be safely upgraded
			AssemblyResolveEarly((sender, args) => {
				var name = new AssemblyName(args.Name);
				if (Array.Find(typeof(Program).Assembly.GetManifestResourceNames(), s => s.EndsWith(name.Name + ".dll")) == null)
					return null;

				var existing = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == name.Name);
				if (existing != null) {
					Logging.tML.Warn($"Upgraded Reference {name.Name} -> Version={name.Version} -> {existing.GetName().Version}");
					return existing;
				}

				return null;
			});
		}

		private static void RecalculateReferences() {
			foreach (var mod in loadedMods.Values) {
				mod.dependencies.Clear();
				mod.dependents.Clear();
			}

			foreach (var mod in loadedMods.Values)
				foreach (var depName in mod.properties.RefNames(true))
					if (loadedMods.ContainsKey(depName))
						mod.AddDependency(loadedMods[depName]);
					else
						mod.weakDependencies.Add(depName);

			foreach (var mod in loadedMods.Values)
				mod.UpdateWeakRefs();
		}

		private static Mod Instantiate(LoadedMod mod) {
			try {
				Type modType = mod.assembly.GetTypes().SingleOrDefault(t => t.IsSubclassOf(typeof(Mod)));
				if (modType == null)
					throw new Exception(mod.Name + " does not have a class extending Mod. Mods need a Mod class to function.") {
						HelpLink = "https://github.com/blushiemagic/tModLoader/wiki/Basic-tModLoader-Modding-FAQ#sequence-contains-no-matching-element-error"
					};

				var m = (Mod)Activator.CreateInstance(modType);
				m.File = mod.modFile;
				m.Code = mod.assembly;
				m.Logger = LogManager.GetLogger(m.Name);
				m.Side = mod.properties.side;
				m.DisplayName = mod.properties.displayName;
				m.tModLoaderVersion = mod.properties.buildVersion;
				return m;
			}
			catch (Exception e) {
				e.Data["mod"] = mod.Name;
				throw;
			}
			finally {
				MemoryTracking.Update(mod.Name).code += mod.bytesLoaded;
			}
		}

		internal static List<Mod> InstantiateMods(List<LocalMod> modsToLoad) {
			InitAssemblyResolvers();

			var modList = new List<LoadedMod>();
			foreach (var loading in modsToLoad) {
				if (!loadedMods.TryGetValue(loading.Name, out LoadedMod mod))
					mod = loadedMods[loading.Name] = new LoadedMod();

				mod.SetMod(loading);
				modList.Add(mod);
			}

			RecalculateReferences();

			//as far as we know, mono doesn't support edit and continue anyway
			if (Debugger.IsAttached && FrameworkVersion.Framework == Framework.NetFramework) {
				ModCompile.activelyModding = true;
				foreach (var mod in modList.Where(mod => mod.HasEaC && mod.CanEaC))
					mod.EnableEaC();
			}

			try {
				//load all the assemblies in parallel.
				Interface.loadMods.SetLoadStage("tModLoader.MSSandboxing", modsToLoad.Count);
				int i = 0;
				Parallel.ForEach(modList, mod => {
					Interface.loadMods.SetCurrentMod(i++, mod.Name);
					mod.LoadAssemblies();
				});

				//Assemblies must be loaded before any instantiation occurs to satisfy dependencies
				Interface.loadMods.SetLoadStage("tModLoader.MSInstantiating");
				MemoryTracking.Checkpoint();
				return modList.Select(Instantiate).ToList();
			}
			catch (AggregateException ae) {
				ae.Data["mods"] = ae.InnerExceptions.Select(e => (string)e.Data["mod"]).ToArray();
				throw;
			}
		}

		private static string GetModAssemblyFileName(this TmodFile modFile, bool? xna = null) {
			var variant = modFile.HasFile($"{modFile.name}.All.dll") ? "All" : (xna ?? PlatformUtilities.IsXNA) ? "XNA" : "FNA";
			var fileName = $"{modFile.name}.{variant}.dll";
			if (!modFile.HasFile(fileName)) // legacy compatibility
				fileName = modFile.HasFile("All.dll") ? "All.dll" : (xna ?? FrameworkVersion.Framework == Framework.NetFramework) ? "Windows.dll" : "Mono.dll";

			return fileName;
		}

		internal static byte[] GetModAssembly(this TmodFile modFile, bool? xna = null) => modFile.GetBytes(modFile.GetModAssemblyFileName(xna));

		internal static IEnumerable<Assembly> GetModAssemblies(string name) => loadedMods[name].assemblies;

		public static bool GetAssemblyOwner(Assembly assembly, out string modName) {
			if (hostModForAssembly.TryGetValue(assembly, out var mod)) {
				modName = mod.Name;
				return true;
			}

			modName = null;
			return false;
		}

		internal static bool FirstModInStackTrace(StackTrace stack, out string modName) {
			for (int i = 0; i < stack.FrameCount; i++) {
				StackFrame frame = stack.GetFrame(i);
				var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
				if (assembly != null && GetAssemblyOwner(assembly, out modName))
					return true;
			}

			modName = null;
			return false;
		}

		internal static AssemblyNameReference ToReference(this AssemblyName name) =>
			new AssemblyNameReference(name.Name, name.Version);

		private class TerrariaCecilAssemblyResolver : DefaultAssemblyResolver
		{
			public TerrariaCecilAssemblyResolver() {
				RegisterAssembly(ModuleDefinition.ReadModule(Assembly.GetExecutingAssembly().Location).Assembly);
			}

			public new void RegisterAssembly(AssemblyDefinition asm) {
				lock (this) //locking on this is not recommended but fine in this case
					base.RegisterAssembly(asm);
			}

			public override AssemblyDefinition Resolve(AssemblyNameReference name)
			{
				try {
					return base.Resolve(name);
				}
				catch (AssemblyResolutionException) {
					var asm = FallbackResolve(name);
					if (asm == null)
						throw new AssemblyResolutionException(name);

					RegisterAssembly(asm);
					return asm;
				}
			}

			private AssemblyDefinition FallbackResolve(AssemblyNameReference name)
			{
				string resourceName = name.Name + ".dll";
				resourceName = Array.Find(typeof(Program).Assembly.GetManifestResourceNames(), element => element.EndsWith(resourceName));
				if (resourceName != null) {
					Logging.tML.DebugFormat("Generating ModuleDefinition for {0}", name);
					using (var stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName))
						return AssemblyDefinition.ReadAssembly(stream, new ReaderParameters(ReadingMode.Immediate));
				}

				if (assemblyBinaries.TryGetValue(name.Name, out var modAssemblyBytes)) {
					Logging.tML.DebugFormat("Generating ModuleDefinition for {0}", name);
					using (var stream = new MemoryStream(modAssemblyBytes))
						return AssemblyDefinition.ReadAssembly(stream, new ReaderParameters(ReadingMode.Immediate));
				}

				return null;
			}
		}

		internal class DebugHeaderWriterProvider : ISymbolWriterProvider, ISymbolWriter
		{
			private ImageDebugHeader header;

			public DebugHeaderWriterProvider(ImageDebugHeader header) {
				this.header = header;
			}

			public ISymbolWriter GetSymbolWriter(ModuleDefinition module, string fileName) => this;
			public ISymbolWriter GetSymbolWriter(ModuleDefinition module, Stream symbolStream) => this;

			public ImageDebugHeader GetDebugHeader() => header;

			public ISymbolReaderProvider GetReaderProvider() => throw new NotImplementedException();
			public void Write(MethodDebugInformation info) => throw new NotImplementedException();
			public void Dispose() { }
		}
	}
}
